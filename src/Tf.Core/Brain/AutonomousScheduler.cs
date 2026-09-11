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
    private readonly TimeSpan _failureBackoff;
    private readonly int _maxConsecutiveFailures;

    private CancellationTokenSource? _cts;
    private DateTimeOffset _nextAllowedDecision;

    public AutonomousScheduler(
        TradingBrain brain,
        Func<AppSettings> settings,
        Func<IReadOnlyList<Tick>> tickWindow,
        Func<RiskContext> riskContext,
        Func<IReadOnlyList<string>> recentLessons,
        Action<BrainCycleResult>? onCycle = null,
        TimeSpan? failureBackoff = null,
        int maxConsecutiveFailures = 3)
    {
        _failureBackoff = failureBackoff ?? TimeSpan.FromSeconds(5);
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
        _brain = brain;
        _settings = settings;
        _tickWindow = tickWindow ?? throw new ArgumentNullException(nameof(tickWindow));
        _riskContext = riskContext ?? throw new ArgumentNullException(nameof(riskContext));
        _recentLessons = recentLessons ?? throw new ArgumentNullException(nameof(recentLessons));
        _onCycle = onCycle;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Failed cycles since the last successful one (reset on start).</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Cap after which the loop exits instead of retrying forever.</summary>
    public int MaxConsecutiveFailures => _maxConsecutiveFailures;

    // Bumped by every Stop(). The loop carries the generation it started
    // under and exits when they no longer match — this closes the race where
    // Stop() runs before StartAsync creates the cancellation source (Stop
    // then has nothing to cancel and the loop would run uncancellable).
    private int _generation;

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
        ConsecutiveFailures = 0;
        var generation = Interlocked.Increment(ref _generation);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _nextAllowedDecision = DateTimeOffset.UtcNow;

        try
        {
            await LoopAsync(_cts.Token, generation);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Stop()
    {
        // Bump the generation first: a loop that starts after this point
        // (the Stop-before-Start race) sees the mismatch and exits at once.
        Interlocked.Increment(ref _generation);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct, int generation)
    {
        while (!ct.IsCancellationRequested && generation == Volatile.Read(ref _generation))
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
                ConsecutiveFailures = 0;
                _nextAllowedDecision = DateTimeOffset.UtcNow.Add(interval);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Back off after a failed cycle (default 5s, configurable via
                // the constructor) so a broken LLM or broker connection
                // doesn't spin the loop hot. After too many consecutive
                // failures, stop retrying entirely — the runner surfaces the
                // exit and may restart the session itself.
                ConsecutiveFailures++;
                if (ConsecutiveFailures >= _maxConsecutiveFailures)
                {
                    return;
                }

                try
                {
                    await Task.Delay(_failureBackoff, ct);
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