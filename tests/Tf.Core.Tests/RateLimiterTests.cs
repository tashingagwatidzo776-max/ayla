using Tf.Deriv;

namespace Tf.Core.Tests;

/// <summary>
/// Deterministic tests for the token-bucket RateLimiter. The limiter takes an
/// injected <see cref="TimeProvider"/>, so every test drives a virtual clock
/// that moves only when the test advances it — grant admission, window
/// bounds, drain precision and cancellation are all verified without a
/// single wall-clock bound, so nothing here can flake under CI load.
/// Correctness (never more than `limit` grants per 1s of virtual time) is
/// enforced by the limiter's own window, which reads the fake clock.
/// </summary>
[Trait("Category", "Unit")]
public class RateLimiterTests
{
    // ─── Virtual clock ───────────────────────────────────

    /// <summary>
    /// A TimeProvider whose time stands still until <see cref="Advance"/> is
    /// called. One-shot timers created through it fire, in due-time order,
    /// while the clock is advanced — limiter waits resolve exactly when the
    /// test decides time has passed, never on the real clock. Timer
    /// callbacks run outside the internal lock so completions can propagate
    /// (the limiter uses run-continuations-asynchronously, so nothing
    /// re-enters this class while a callback runs).
    /// </summary>
    private sealed class VirtualClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<FakeTimer> _timers = new();
        private long _nowMs;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return DateTimeOffset.UnixEpoch.AddMilliseconds(_nowMs);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            lock (_sync)
            {
                timer.DueMs = dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero
                    ? -1
                    : _nowMs + (long)dueTime.TotalMilliseconds;
                _timers.Add(timer);
            }
            return timer;
        }

        /// <summary>Advance fake time by <paramref name="ms"/> milliseconds,
        /// firing every timer that comes due along the way (earliest first,
        /// registration order on ties). Time never moves backwards even if a
        /// callback advances the clock further.</summary>
        public void Advance(long ms)
        {
            lock (_sync) _nowMs = Math.Max(_nowMs, 0);
            var target = CurrentMs() + ms;
            while (true)
            {
                FakeTimer? due;
                lock (_sync)
                {
                    due = null;
                    foreach (var t in _timers)
                    {
                        if (!t.Enabled || t.DueMs < 0 || t.DueMs > target) continue;
                        if (due is null || t.DueMs < due.DueMs) due = t;
                    }
                    if (due is null)
                    {
                        _nowMs = Math.Max(_nowMs, target);
                        return;
                    }
                    _nowMs = Math.Max(_nowMs, due.DueMs);
                }
                due.Fire(); // outside the lock: callbacks may schedule new timers
            }
        }

        public long CurrentMs()
        {
            lock (_sync) return _nowMs;
        }

        public int RegisteredTimers { get { lock (_sync) return _timers.Count; } }
        public int FiredTimers { get { lock (_sync) return _timers.Count(t => t.DueMs < 0); } }
    }

    /// <summary>A one-shot timer that fires only when the clock advances past
    /// its due time. The limiter never re-arms or periods its timer, but
    /// <see cref="Change"/> is implemented honestly anyway.</summary>
    private sealed class FakeTimer : ITimer
    {
        private readonly VirtualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _sync = new();

        public long DueMs = -1;
        private bool _cancelled;

        public FakeTimer(VirtualClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public bool Enabled
        {
            get { lock (_sync) return !_cancelled && DueMs >= 0; }
        }

        public void Fire()
        {
            lock (_sync)
            {
                if (_cancelled || DueMs < 0) return;
                DueMs = -1; // one-shot: firing consumes it
            }
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                if (_cancelled) return false;
                DueMs = dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero
                    ? -1
                    : _clock.CurrentMs() + (long)dueTime.TotalMilliseconds;
                return true;
            }
        }

        public void Dispose() { lock (_sync) _cancelled = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    // ─── Grant admission ─────────────────────────────────

    [Fact]
    public async Task WithinLimit_AllowsAllRequestsImmediately()
    {
        var clock = new VirtualClock();
        var limiter = new RateLimiter(maxRequestsPerSecond: 5, clock);

        for (var i = 0; i < 5; i++)
        {
            await limiter.WaitForSlotAsync();
        }

        // All 5 fit in the empty window — no clock advance was needed.
        Assert.Equal(5, limiter.CurrentCount);
        Assert.Equal(0, clock.CurrentMs());
    }

    [Fact]
    public async Task RapidFireBurst_QueuesThenReleasesAtDrains()
    {
        const int limit = 4;
        const int total = 12;
        var clock = new VirtualClock();
        var limiter = new RateLimiter(limit, clock);
        var grants = new List<long>(total);

        // All requests are issued up front: exactly one burst passes free,
        // the rest queue until the test advances past a window drain.
        var waits = new List<Task>();
        for (var i = 0; i < total; i++)
        {
            var task = limiter.WaitForSlotAsync();
            if (task.IsCompletedSuccessfully) grants.Add(clock.CurrentMs());
            else waits.Add(task);
        }
        Assert.Equal(limit, grants.Count);
        Assert.Equal(total - limit, waits.Count);

        // Each drain releases exactly one burst. After advancing, pump the
        // queued continuations (they are posted, not run inline) before
        // observing task status — a synchronous check races the queue.
        while (waits.Count > 0)
        {
            clock.Advance(1010); // 1s window + 10ms scheduling guard
            var drainMs = clock.CurrentMs();
            await PumpAsync();
            var stillWaiting = waits.Where(w => !w.IsCompletedSuccessfully).ToList();
            Assert.True(stillWaiting.Count < waits.Count, "an advance released no waiters");
            foreach (var w in waits.Where(w => w.IsCompletedSuccessfully)) grants.Add(drainMs);
            waits = stillWaiting;
        }

        // Sliding-window bound on the recorded grant times: grant i+limit
        // must come no earlier than 1s after grant i.
        Assert.Equal(total, grants.Count);
        grants.Sort();
        for (var i = 0; i + limit < grants.Count; i++)
        {
            var spacing = grants[i + limit] - grants[i];
            Assert.True(spacing >= 1000,
                $"grants {i} and {i + limit} were {spacing}ms apart — window exceeded {limit}");
        }
    }

    [Fact]
    public async Task ConcurrentWaiters_TotalGrantsNeverExceedLimit()
    {
        const int limit = 3;
        const int workers = 15;
        var clock = new VirtualClock();
        var limiter = new RateLimiter(limit, clock);
        var all = new List<Task>();

        // 15 waiters outstanding simultaneously; each either passes into the
        // free burst or queues against the bucket.
        for (var i = 0; i < workers; i++)
        {
            all.Add(limiter.WaitForSlotAsync());
        }

        // Drain-driven: each advance releases one burst. Pump the queued
        // continuations after each advance, then check statuses and the
        // limiter's own window (read off the fake clock) for the grant cap.
        for (var round = 0; round < workers; round++)
        {
            if (all.All(t => t.IsCompletedSuccessfully)) break;
            clock.Advance(1010);
            await PumpAsync();
            Assert.True(limiter.CurrentCount <= limit,
                $"window held {limiter.CurrentCount} grants — exceeded {limit}");
        }

        Assert.All(all, t => Assert.True(t.IsCompletedSuccessfully, "a waiter never resolved"));
        Assert.Equal(workers, all.Count);
    }

    /// <summary>
    /// Replaces the old wall-clock sustained-load test: a paced caller sees
    /// exact grant timestamps on the virtual clock. Both bounds are exact —
    /// the run must span at least the burst-free lower bound, and no grant
    /// pair one window apart may be closer than 1s.
    /// </summary>
    [Fact]
    public async Task SustainedLoad_WindowBoundNeverExceeded()
    {
        const int limit = 5;
        const int total = 20;
        var clock = new VirtualClock();
        var limiter = new RateLimiter(limit, clock);
        var grantedMs = new List<long>(total);

        for (var i = 0; i < total; i++)
        {
            var task = limiter.WaitForSlotAsync();
            if (!task.IsCompletedSuccessfully) clock.Advance(1010);
            await task;
            grantedMs.Add(clock.CurrentMs());
        }

        Assert.Equal(total, grantedMs.Count);
        var span = grantedMs[^1] - grantedMs[0];
        var minSpan = (long)(1000.0 * (total - limit) / limit);
        Assert.True(span >= minSpan,
            $"{total} grants completed in {span}ms — under the {minSpan}ms burst-free lower bound");

        for (var i = 0; i + limit < grantedMs.Count; i++)
        {
            var spacing = grantedMs[i + limit] - grantedMs[i];
            Assert.True(spacing >= 1000,
                $"grants {i} and {i + limit} were {spacing}ms apart — window exceeded {limit}");
        }
    }

    // ─── Refill & cancellation ───────────────────────────

    [Fact]
    public async Task SlotsRefill_AfterWindowDrains()
    {
        var clock = new VirtualClock();
        var limiter = new RateLimiter(maxRequestsPerSecond: 2, clock);

        // Drain the bucket.
        await limiter.WaitForSlotAsync();
        await limiter.WaitForSlotAsync();
        Assert.Equal(2, limiter.CurrentCount);

        // Immediately exhausted — the next call must block, proving the
        // bucket is empty until the window drains.
        var next = limiter.WaitForSlotAsync();
        Assert.False(next.IsCompletedSuccessfully,
            "third request should have been throttled while the bucket was empty");

        // Advancing to the drain (+ the 10ms guard) releases it with no
        // wall-clock waiting at all.
        clock.Advance(1010);
        await next;

        Assert.InRange(limiter.CurrentCount, 1, 2);
    }

    [Fact]
    public async Task Cancellation_ThrowsWhenCancelledWhileWaiting()
    {
        var clock = new VirtualClock();
        var limiter = new RateLimiter(maxRequestsPerSecond: 1, clock);

        await limiter.WaitForSlotAsync(); // exhaust the single slot

        using var cts = new CancellationTokenSource();
        var wait = limiter.WaitForSlotAsync(cts.Token);
        Assert.False(wait.IsCompletedSuccessfully, "should have been throttled");

        cts.Cancel(); // cancelled while fake time stands still
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        // The limiter is uncorrupted: a fresh caller is served at the drain.
        var retry = limiter.WaitForSlotAsync();
        clock.Advance(1010);
        await retry;
    }

    /// <summary>Pumps queued await continuations (xUnit's sync context
    /// defers them) so tasks completed by a clock advance become observable.</summary>
    private static async Task PumpAsync()
    {
        for (var i = 0; i < 10; i++) await Task.Yield();
    }
}
