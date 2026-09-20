using DongGfx.Core.Models;

namespace DongGfx.Core.Brain;

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
    private readonly Action<double>? _onCycleLatencyMs;
    private readonly Action<string>? _onCycleError;

    // Recognizes "market is closed" rejections (returns the clamped reopen
    // instant) and reports them. Market-closed is an expected weekend/holiday
    // state — the engine idles until reopen instead of accumulating failures
    // toward a stop-and-restart storm.
    private readonly Func<Exception, DateTimeOffset?>? _marketClosedProbe;
    private readonly Action<DateTimeOffset>? _onMarketClosed;

    /// <summary>Optional no-auth market-open probe consulted during a
    /// market-closed idle: true = the market is open again, wake the engine
    /// before the API-quoted reopen time. Backed by the public feed so it
    /// needs no token and survives re-auth churn. Null = the quoted reopen
    /// instant is trusted as-is (pre-existing behavior).</summary>
    private readonly Func<CancellationToken, Task<bool>>? _marketOpenProbe;

    /// <summary>How often the market-open probe may run during one idle.
    /// The reopen quote is already clamped to ≤30 min, so a probe a minute
    /// keeps the feed traffic negligible.</summary>
    internal static readonly TimeSpan MarketOpenProbeInterval = TimeSpan.FromMinutes(1);

    /// <summary>UTC instant the market-closed idle was ended by the no-auth
    /// public-feed probe (null when the idle is still running, ended by the
    /// quoted reopen, or no probe is wired). Read by the runner to journal
    /// the wake cause — the evidence that the probe, not the quote, woke
    /// the engine early.</summary>
    public DateTimeOffset? WokeByProbeUtc { get; private set; }

    /// <summary>The UTC reopen instant the API quoted when the market-closed
    /// idle was scheduled (null when idle isn't running). Compared against
    /// <see cref="WokeByProbeUtc"/> to prove the probe woke the engine early.</summary>
    public DateTimeOffset? LastReopenQuoteUtc { get; private set; }

    /// <summary>Consumes the probe-wake evidence: returns the wake instant
    /// (or null) and clears it, so the caller journals the wake cause
    /// exactly once per market-closed idle.</summary>
    public DateTimeOffset? ConsumeWakeEvidence()
    {
        var at = WokeByProbeUtc;
        WokeByProbeUtc = null;
        return at;
    }

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
        TimeProvider? timeProvider = null,
        Action<double>? onCycleLatencyMs = null,
        Action<string>? onCycleError = null,
        Func<Exception, DateTimeOffset?>? marketClosedProbe = null,
        Action<DateTimeOffset>? onMarketClosed = null,
        Func<CancellationToken, Task<bool>>? marketOpenProbe = null)
    {
        _failureBackoff = failureBackoff ?? TimeSpan.FromSeconds(5);
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
        _onCycleLatencyMs = onCycleLatencyMs;
        _onCycleError = onCycleError;
        _marketClosedProbe = marketClosedProbe;
        _onMarketClosed = onMarketClosed;
        _marketOpenProbe = marketOpenProbe;
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

    /// <summary>Waits out a market-closed window, kill-switch aware, and
    /// — when a market-open probe is wired — consults the no-auth feed at
    /// <see cref="MarketOpenProbeInterval"/> between kill-switch slices:
    /// the feed saying "open" ends the idle immediately, even when Deriv's
    /// quoted reopen clock is skewed. Without a probe this is exactly the
    /// pre-existing quoted-reopen wait.</summary>
    private async Task DelayUntilReopenAsync(DateTimeOffset reopen, CancellationToken ct, int generation)
    {
        if (_marketOpenProbe is null)
        {
            await DelayRespectingSwitchAsync(reopen - _timeProvider.GetUtcNow(), ct, generation);
            return;
        }

        WokeByProbeUtc = null;

        var nextProbe = _timeProvider.GetUtcNow();
        while (true)
        {
            if (generation != Volatile.Read(ref _generation)
                || _riskContext().KillSwitchEngaged)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            if (now >= reopen)
            {
                return; // quoted reopen due — the loop re-asks the market
            }

            if (now >= nextProbe)
            {
                nextProbe = now + MarketOpenProbeInterval;
                try
                {
                    if (await _marketOpenProbe(ct))
                    {
                        // Feed-authoritative open: wake now and record the
                        // cause (vs the quoted reopen) for the UI/journal.
                        _nextAllowedDecision = _timeProvider.GetUtcNow();
                        WokeByProbeUtc = _timeProvider.GetUtcNow();
                        return;
                    }
                }
                catch
                {
                    // Feed unreachable is expected sometimes (it is a
                    // convenience probe) — the quoted reopen still fires.
                }
            }

            var remaining = reopen - _timeProvider.GetUtcNow();
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

            var cycleStart = _timeProvider.GetTimestamp();
            try
            {
                var result = await _brain.RunCycleAsync(
                    _tickWindow() ?? Array.Empty<Tick>(), risk, _recentLessons(),
                    allowTrading: settings.AutonomyEnabled, ct);

                _onCycleLatencyMs?.Invoke(_timeProvider.GetElapsedTime(cycleStart).TotalMilliseconds);
                _onCycle?.Invoke(result);
                ConsecutiveFailures = 0;
                _nextAllowedDecision = _timeProvider.GetUtcNow().Add(interval);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Telemetry first: the failure path also surfaces cycle latency
                // (attempt → failure) and the exception type for error counts.
                _onCycleLatencyMs?.Invoke(_timeProvider.GetElapsedTime(cycleStart).TotalMilliseconds);

                // Market-closed is an expected state, not a failure: idle
                // until the (clamped) reopen instant instead of burning the
                // failure budget toward an engine stop that auto-restart
                // would only repeat. Checked before the error telemetry so
                // weekends don't show up as error storms in metrics.
                var reopen = _marketClosedProbe?.Invoke(ex);
                if (reopen is not null)
                {
                    ConsecutiveFailures = 0;
                    _nextAllowedDecision = reopen.Value;
                    LastReopenQuoteUtc = reopen.Value;
                    _onMarketClosed?.Invoke(reopen.Value);

                    // Wait out the closed window here (kill-switch aware)
                    // with the optional open probe between slices: when the
                    // no-auth feed says the market is live again the engine
                    // resumes immediately instead of waiting out Deriv's
                    // possibly-skewed quoted clock.
                    await DelayUntilReopenAsync(reopen.Value, ct, generation);
                    continue;
                }

                _onCycleError?.Invoke(ex.GetType().Name);

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