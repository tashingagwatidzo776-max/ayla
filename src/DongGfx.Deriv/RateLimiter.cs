namespace DongGfx.Deriv;

/// <summary>
/// Simple token-bucket rate limiter for WebSocket API calls. Prevents
/// hitting Deriv's rate limits when multiple growth engines fire
/// simultaneously. Thread-safe.
/// </summary>
public sealed class RateLimiter
{
    private readonly int _maxRequestsPerSecond;
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly object _sync = new();
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates a rate limiter. Deriv's WebSocket limit is roughly 60
    /// requests per minute per connection. Pass a custom
    /// <see cref="TimeProvider"/> to drive the clock deterministically in
    /// tests (production uses <see cref="TimeProvider.System"/>).
    /// </summary>
    public RateLimiter(int maxRequestsPerSecond = 5, TimeProvider? timeProvider = null)
    {
        _maxRequestsPerSecond = maxRequestsPerSecond;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Wait until a request slot is available. Returns immediately if
    /// under the limit; otherwise blocks until a slot opens.
    /// </summary>
    public async Task WaitForSlotAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var waitMs = 10;
            lock (_sync)
            {
                var now = _time.GetUtcNow();

                // Remove timestamps older than 1 second.
                while (_timestamps.Count > 0 && (now - _timestamps.Peek()).TotalSeconds >= 1)
                {
                    _timestamps.Dequeue();
                }

                if (_timestamps.Count < _maxRequestsPerSecond)
                {
                    _timestamps.Enqueue(now);
                    return; // slot available
                }

                // Calculate wait time until the oldest request expires.
                var oldest = _timestamps.Peek();
                waitMs = (int)(1000 - (now - oldest).TotalMilliseconds) + 10;
                if (waitMs <= 0) waitMs = 10;

                // Release lock before awaiting.
            }

            // Sleep until the window actually drains instead of polling at a
            // fixed tick: waking late relative to the drain skews grant
            // cadence and wastes rate budget, which shows up as a lower
            // sustained average than the nominal limit. An injected clock
            // makes this fully deterministic in tests.
            var tcs = new TaskCompletionSource();
            using var timer = _time.CreateTimer(
                _ => tcs.TrySetResult(), null, TimeSpan.FromMilliseconds(waitMs), Timeout.InfiniteTimeSpan);
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Current number of requests in the sliding window.</summary>
    public int CurrentCount
    {
        get
        {
            lock (_sync)
            {
                var now = _time.GetUtcNow();
                while (_timestamps.Count > 0 && (now - _timestamps.Peek()).TotalSeconds >= 1)
                    _timestamps.Dequeue();
                return _timestamps.Count;
            }
        }
    }
}
