namespace Tf.Deriv;

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

    /// <summary>
    /// Creates a rate limiter. Deriv's WebSocket limit is roughly 60
    /// requests per minute per connection.
    /// </summary>
    public RateLimiter(int maxRequestsPerSecond = 5)
    {
        _maxRequestsPerSecond = maxRequestsPerSecond;
    }

    /// <summary>
    /// Wait until a request slot is available. Returns immediately if
    /// under the limit; otherwise blocks until a slot opens.
    /// </summary>
    public async Task WaitForSlotAsync(CancellationToken ct = default)
    {
        while (true)
        {
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;

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
                var waitMs = (int)(1000 - (now - oldest).TotalMilliseconds) + 10;
                if (waitMs <= 0) waitMs = 10;

                // Release lock before awaiting.
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Current number of requests in the sliding window.</summary>
    public int CurrentCount
    {
        get
        {
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                while (_timestamps.Count > 0 && (now - _timestamps.Peek()).TotalSeconds >= 1)
                    _timestamps.Dequeue();
                return _timestamps.Count;
            }
        }
    }
}
