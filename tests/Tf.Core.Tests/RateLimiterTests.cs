using Tf.Deriv;

namespace Tf.Core.Tests;

/// <summary>
/// Deterministic tests for the token-bucket RateLimiter. The limiter takes an
/// injected <see cref="TimeProvider"/>, so every test drives the shared
/// <see cref="TestVirtualClock"/> — grant admission, window bounds, drain
/// precision and cancellation are all verified without a single wall-clock
/// bound, so nothing here can flake under CI load. Correctness (never more
/// than `limit` grants per 1s of virtual time) is enforced by the limiter's
/// own window, which reads the fake clock.
/// </summary>
[Trait("Category", "Unit")]
public class RateLimiterTests
{
    // ─── Grant admission ─────────────────────────────────

    [Fact]
    public async Task WithinLimit_AllowsAllRequestsImmediately()
    {
        var clock = new TestVirtualClock();
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
        var clock = new TestVirtualClock();
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
        var clock = new TestVirtualClock();
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
        var clock = new TestVirtualClock();
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
        var clock = new TestVirtualClock();
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
        var clock = new TestVirtualClock();
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
