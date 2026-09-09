using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>
/// Autonomous decision loop grounded on the kill switch. When started, it runs
/// a full cycle (context → LLM → risk → trade) on a configurable interval.
/// Stops immediately when the kill switch is engaged (re-read every cycle).
/// Each completed cycle is surfaced via <see cref="onCycle"/> so the UI can
/// show decisions as they happen.
/// </summary>
public sealed class AutonomousScheduler : IAsyncDisposable
{
    private readonly TradingBrain _brain;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<Tick>> _tickWindow;
    private readonly Func<RiskContext> _riskContext;
    private readonly Func<IReadOnlyList<string>> _recentLessons;
    private readonly Action<BrainCycleResult>? _onCycle;

    private CancellationTokenSource? _cts;
    private DateTimeOffset _nextAllowedDecision;

    public AutonomousScheduler(
        TradingBrain brain,
        Func<AppSettings> settings,
        Func<IReadOnlyList<Tick>> tickWindow,
        Func<RiskContext> riskContext,
        Func<IReadOnlyList<string>> recentLessons,
        Action<BrainCycleResult>? onCycle = null)
    {
        _brain = brain;
        _settings = settings;
        _tickWindow = tickWindow ?? throw new ArgumentNullException(nameof(tickWindow));
        _riskContext = riskContext ?? throw new ArgumentNullException(nameof(riskContext));
        _recentLessons = recentLessons ?? throw new ArgumentNullException(nameof(recentLessons));
        _onCycle = onCycle;
    }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts the loop. No-op if already running. The loop exits immediately
    /// when <see cref="RiskContext.KillSwitchEngaged"/> becomes true; call
    /// <see cref="StartAsync"/> again once the kill switch is reset.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _nextAllowedDecision = DateTimeOffset.UtcNow;

        try
        {
            await LoopAsync(_cts.Token);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = _settings();
            var interval = TimeSpan.FromMinutes(
                Math.Max(1, settings.DecisionIntervalMinutes));

            var risk = _riskContext();
            if (risk.KillSwitchEngaged)
            {
                return; // exit immediately when engagement is detected
            }

            var now = DateTimeOffset.UtcNow;
            if (now < _nextAllowedDecision)
            {
                var remaining = _nextAllowedDecision - now;
                try
                {
                    await Task.Delay(remaining, ct);
                }
                catch (OperationCanceledException)
                {
                    // cancelled while waiting
                }
                continue;
            }

            try
            {
                var result = await _brain.RunCycleAsync(
                    _tickWindow() ?? Array.Empty<Tick>(), risk, _recentLessons(),
                    allowTrading: settings.AutonomyEnabled, ct);

                _onCycle?.Invoke(result);
                _nextAllowedDecision = DateTimeOffset.UtcNow.Add(interval);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Back off briefly after a failed cycle so a broken LLM or
                // broker connection doesn't spin the loop hot.
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}