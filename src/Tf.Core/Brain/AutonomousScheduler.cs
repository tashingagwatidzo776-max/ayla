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
    private readonly TimeProvider _timeProvider;

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
        int maxConsecutiveFailures = 3,
        TimeProvider? timeProvider = null)
    {
        _failureBackoff = failureBackoff ?? TimeSpan.FromSeconds(5);
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
        _brain = brain;
        _settings = settings;
        _tickWindow = tickWindow ?? throw new ArgumentNullException(nameof(tickWindow));
        _riskContext = riskContext ?? throw new ArgumentNullException(nameof(riskContext));
        _recentLessons = recentLessons ?? throw new ArgumentNullException(nameof(recentLessons));
        _onCycle = onCycle;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>Granularity at which long waits re-check the kill switch —
    /// engaging the switch must interrupt the inter-cycle interval (and the
    /// failure backoff) within about one slice, not wait out the whole wait.</summary>
    private static readonly TimeSpan SwitchCheckSlice = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits for <paramref name="total"/>, but returns early — and lets the
    /// caller's loop-top checks act — as soon as the kill switch engages,
    /// the scheduler is stopped (generation bump), or the token cancels.
    /// A plain <c>Task.Delay</c> here would keep sleeping through a kill
    /// switch until the full interval elapsed.
    /// </summary>
    private async Task DelayRespectingSwitchAsync(TimeSpan total, CancellationToken ct, int generation)
    {
        var deadline = _timeProvider.GetUtcNow() + total;
        while (true)
        {
            if (generation != Volatile.Read(ref _generation) ||
                _riskContext().KillSwitchEngaged)
            {
                return;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var slice = remaining > SwitchCheckSlice ? SwitchCheckSlice : remaining;
            try
            {
                await DelayAsync(slice, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Waits for <paramref name="delay"/> via the injected provider's timer
    /// facility (<c>TimeProvider.Delay</c> is not available on net8.0). The
    /// one-shot timer resolves the wait; cancellation throws
    /// <see cref="OperationCanceledException"/> as callers expect.
    /// </summary>
    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = _timeProvider.CreateTimer(
            state => ((TaskCompletionSource)state!).TrySetResult(), resolved,
            delay, Timeout.InfiniteTimeSpan);
        await resolved.Task.WaitAsync(ct);
    }

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
        _nextAllowedDecision = _timeProvider.GetUtcNow();

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

            var now = _timeProvider.GetUtcNow();
            if (now < _nextAllowedDecision)
            {
                await DelayRespectingSwitchAsync(_nextAllowedDecision - now, ct, generation);
                continue;
            }

            try
            {
                var result = await _brain.RunCycleAsync(
                    _tickWindow() ?? Array.Empty<Tick>(), risk, _recentLessons(),
                    allowTrading: settings.AutonomyEnabled, ct);

                _onCycle?.Invoke(result);
                ConsecutiveFailures = 0;
                _nextAllowedDecision = _timeProvider.GetUtcNow().Add(interval);
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
                    await DelayRespectingSwitchAsync(_failureBackoff, ct, generation);
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