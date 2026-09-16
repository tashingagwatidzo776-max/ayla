using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Tf.Core.Analytics;

/// <summary>
/// Bounded, thread-safe collector for operational telemetry samples: cycle
/// latencies and error events. Ring-buffer semantics — once <see cref="MaxSamples"/>
/// is reached, new samples evict the oldest, so a long-running session exports
/// its most recent telemetry rather than only the first cycles from app start.
/// </summary>
public sealed class MetricsCollector : IReadOnlyCollection<MetricSample>
{
    /// <summary>Ring-buffer capacity: the most recent samples kept per collector.</summary>
    public const int MaxSamples = 500;

    private const string ErrorName = "cycle_error";

    private readonly Queue<MetricSample> _samples = new();
    private readonly object _lock = new();
    private int _errorCount;

    /// <summary>Records a latency sample (milliseconds) attributed to an account.</summary>
    public void RecordLatency(double milliseconds, string? account, DateTimeOffset at) =>
        Add(new MetricSample("cycle_latency_ms", milliseconds, at, account));

    /// <summary>Records one error event attributed to an account.</summary>
    public void RecordError(string? account, DateTimeOffset at) =>
        Add(new MetricSample(ErrorName, 1, at, account));

    /// <summary>Latency samples only, oldest first.</summary>
    public IReadOnlyList<MetricSample> Latency
    {
        get
        {
            lock (_lock)
            {
                var list = new List<MetricSample>(_samples.Count);
                foreach (var s in _samples)
                {
                    if (s.Name != ErrorName)
                    {
                        list.Add(s);
                    }
                }
                return list;
            }
        }
    }

    /// <summary>Error samples only, oldest first.</summary>
    public IReadOnlyList<MetricSample> Errors
    {
        get
        {
            lock (_lock)
            {
                var list = new List<MetricSample>();
                foreach (var s in _samples)
                {
                    if (s.Name == ErrorName)
                    {
                        list.Add(s);
                    }
                }
                return list;
            }
        }
    }

    /// <summary>True once at least one error has been recorded.</summary>
    public bool HasErrors
    {
        get { lock (_lock) { return _errorCount > 0; } }
    }

    public int Count
    {
        get { lock (_lock) { return _samples.Count; } }
    }

    public IEnumerator<MetricSample> GetEnumerator()
    {
        lock (_lock)
        {
            return new List<MetricSample>(_samples).GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Add(MetricSample sample)
    {
        lock (_lock)
        {
            _samples.Enqueue(sample);
            if (sample.Name == ErrorName)
            {
                _errorCount++;
            }
            while (_samples.Count > MaxSamples)
            {
                var dropped = _samples.Dequeue();
                if (dropped.Name == ErrorName)
                {
                    _errorCount--;
                }
            }
        }
    }
}
