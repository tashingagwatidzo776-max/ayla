using Tf.Deriv;

namespace Tf.Core.Tests;

/// <summary>
/// Integration tests for the token-bucket RateLimiter: verifies that a burst
/// of rapid-fire requests is throttled to the configured per-second rate and
/// that slots refill as the sliding window drains.
/// </summary>
[Trait("Category", "Unit")]
public class RateLimiterTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task WithinLimit_AllowsAllRequestsImmediately()
    {
        var limiter = new RateLimiter(maxRequestsPerSecond: 5);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 5; i++)
        {
            await limiter.WaitForSlotAsync();
        }

        sw.Stop();
        Assert.True(limiter.CurrentCount == 5, $"expected 5 in window, got {limiter.CurrentCount}");
        Assert.True(sw.Elapsed < Grace,
            $"5 requests within the limit took {sw.Elapsed.TotalMilliseconds:0}ms — expected immediate passage");
    }

    [Fact]
    public async Task RapidFireBurst_ThrottlesToConfiguredRate()
    {
        const int limit = 4;
        const int total = 12;
        var limiter = new RateLimiter(limit);
        var granted = new List<DateTimeOffset>(total);

        for (var i = 0; i < total; i++)
        {
            await limiter.WaitForSlotAsync();
            granted.Add(DateTimeOffset.UtcNow);
        }

        Assert.Equal(total, granted.Count);

        // Timing bounds are computed from the bucket's own contract: with the
        // first burst passing immediately, the remaining (total - limit)
        // grants must be spaced at least 1/limit apart, so the run takes at
        // least that long. A no-op limiter passes all 12 in a few milliseconds.
        var minSpacingMs = 1000.0 / limit;
        var minTotalMs = (total - limit) * minSpacingMs;

        var elapsed = granted[^1] - granted[0];
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(minTotalMs * 0.8),
            $"all {total} requests completed in {elapsed.TotalMilliseconds:0}ms — " +
            $"expected ≥{minTotalMs * 0.8:0}ms when throttled to {limit}/s");

        // First `limit` grants pass immediately; the (limit+1)-th had to wait
        // for the window to drain.
        var overflowDelay = granted[limit] - granted[0];
        Assert.True(overflowDelay >= TimeSpan.FromMilliseconds(minSpacingMs * 0.5),
            $"request {limit + 1} passed after only {overflowDelay.TotalMilliseconds:0}ms — " +
            "the bucket did not throttle the overflow");
    }

    [Fact]
    public async Task ConcurrentWaiters_TotalGrantsNeverExceedLimit()
    {
        const int limit = 3;
        const int workers = 15;
        var limiter = new RateLimiter(limit);
        var grants = new System.Collections.Concurrent.ConcurrentBag<DateTimeOffset>();

        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(async () =>
            {
                await limiter.WaitForSlotAsync();
                grants.Add(DateTimeOffset.UtcNow);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(workers, grants.Count);

        var ordered = grants.OrderBy(t => t).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var inWindow = ordered.Count(t => (t - ordered[i]).Duration() < TimeSpan.FromSeconds(1));
            Assert.True(inWindow <= limit,
                $"concurrent burst admitted {inWindow} > {limit} requests in one window");
        }
    }

    [Fact]
    public async Task SlotsRefill_AfterWindowDrains()
    {
        var limiter = new RateLimiter(maxRequestsPerSecond: 2);

        // Drain the bucket.
        await limiter.WaitForSlotAsync();
        await limiter.WaitForSlotAsync();
        Assert.Equal(2, limiter.CurrentCount);

        // Immediately exhausted — the next call must block, proving the
        // bucket is empty until the window drains.
        var next = limiter.WaitForSlotAsync();
        var finishedFast = next == Task.CompletedTask || next.IsCompletedSuccessfully;
        Assert.False(finishedFast, "third request should have been throttled while the bucket was empty");

        await next;

        // After the refill the counter reflects a fresh slot.
        var count = limiter.CurrentCount;
        Assert.InRange(count, 1, 2);
    }

    [Fact]
    public async Task SustainedLoad_MaintainsAverageRateNearLimit()
    {
        const int limit = 5;
        var limiter = new RateLimiter(limit);
        var granted = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            await limiter.WaitForSlotAsync();
            granted++;
        }
        sw.Stop();

        var perSecond = granted / sw.Elapsed.TotalSeconds;
        // A token bucket never exceeds the rate in any 1s window (verified
        // above), but the average over an arbitrary interval can exceed the
        // nominal limit slightly due to partial seconds at both edges — allow
        // 25% slack. A no-op limiter would grant orders of magnitude more.
        Assert.True(perSecond <= limit * 1.25,
            $"{granted} grants in {sw.Elapsed.TotalSeconds:0.0}s = {perSecond:0.0}/s — exceeded the {limit}/s bucket");
        Assert.True(perSecond >= limit * 0.6,
            $"{granted} grants in {sw.Elapsed.TotalSeconds:0.0}s = {perSecond:0.0}/s — bucket over-throttles below {limit * 0.6:0}/s");
    }

    [Fact]
    public async Task Cancellation_ThrowsWhenCancelledWhileWaiting()
    {
        var limiter = new RateLimiter(maxRequestsPerSecond: 1);

        await limiter.WaitForSlotAsync(); // exhaust the single slot

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.WaitForSlotAsync(cts.Token));
    }
}
