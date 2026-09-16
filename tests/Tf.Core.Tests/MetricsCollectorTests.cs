using Tf.Core.Analytics;

namespace Tf.Core.Tests;

/// <summary>
/// Tests for the bounded telemetry collector: latency/error bucketing,
/// oldest-eviction at capacity, and thread-safety under concurrent writers.
/// </summary>
[Trait("Category", "Unit")]
public class MetricsCollectorTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyCollector_HasNoSamplesAndNoErrors()
    {
        var collector = new MetricsCollector();

        Assert.Empty(collector);
        Assert.False(collector.HasErrors);
        Assert.Empty(collector.Latency);
        Assert.Empty(collector.Errors);
    }

    [Fact]
    public void RecordLatency_LandsInLatencyBucket_WithAccountAttribution()
    {
        var collector = new MetricsCollector();
        collector.RecordLatency(123.5, "Alpha", T);

        var sample = Assert.Single(collector.Latency);
        Assert.Equal("cycle_latency_ms", sample.Name);
        Assert.Equal(123.5, sample.Value);
        Assert.Equal("Alpha", sample.Account);
        Assert.Equal(T, sample.At);
        Assert.Empty(collector.Errors);
        Assert.Single(collector);
    }

    [Fact]
    public void RecordError_LandsInErrorBucket_AndSetsHasErrors()
    {
        var collector = new MetricsCollector();
        collector.RecordError("Beta", T);

        var sample = Assert.Single(collector.Errors);
        Assert.Equal("cycle_error", sample.Name);
        Assert.Equal(1, sample.Value);
        Assert.Equal("Beta", sample.Account);
        Assert.True(collector.HasErrors);
        Assert.Empty(collector.Latency);
    }

    [Fact]
    public void BucketsStaySeparated_AfterMixedRecording()
    {
        var collector = new MetricsCollector();
        collector.RecordLatency(10, "Alpha", T);
        collector.RecordError("Alpha", T);
        collector.RecordLatency(20, "Beta", T);
        collector.RecordError("Beta", T);
        collector.RecordLatency(30, "Alpha", T);

        Assert.Equal(2, collector.Errors.Count);
        Assert.Equal(3, collector.Latency.Count);
        Assert.Equal(5, collector.Count);
    }

    [Fact]
    public void CapacityExceeded_NewestSamplesWin_OldestEvicted()
    {
        var collector = new MetricsCollector();
        for (var i = 0; i < MetricsCollector.MaxSamples + 20; i++)
        {
            collector.RecordLatency(i, "Alpha", T);
        }
        collector.RecordError("Alpha", T); // recent — must survive

        Assert.Equal(MetricsCollector.MaxSamples, collector.Count);
        Assert.Equal(MetricsCollector.MaxSamples - 1, collector.Latency.Count);
        Assert.Single(collector.Errors);
        Assert.True(collector.HasErrors);

        // The 500-slot window spans latencies 21..519 (499 samples) plus the
        // newest error — one latency more was displaced than the naive math.
        Assert.Equal(21, collector.Latency[0].Value);
        Assert.Equal(MetricsCollector.MaxSamples + 19, collector.Latency[^1].Value);
    }

    [Fact]
    public void CapacityExceeded_AllErrorsEvicted_HasErrorsResets()
    {
        var collector = new MetricsCollector();
        for (var i = 0; i < 10; i++)
        {
            collector.RecordError("Alpha", T);
        }
        Assert.True(collector.HasErrors);

        for (var i = 0; i < MetricsCollector.MaxSamples; i++)
        {
            collector.RecordLatency(i, "Alpha", T);
        }

        // Every error was pushed out by newer latency samples.
        Assert.True(collector.HasErrors is false, "old errors must be evicted with their samples");
        Assert.Empty(collector.Errors);
        Assert.Equal(MetricsCollector.MaxSamples, collector.Latency.Count);
    }

    [Fact]
    public async Task ConcurrentWriters_NeverExceedCapacity_NorLoseCounts()
    {
        var collector = new MetricsCollector();
        const int writers = 4, perWriter = 200; // 800 total > capacity

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                if (i % 2 == 0)
                {
                    collector.RecordLatency(i, $"W{w}", T);
                }
                else
                {
                    collector.RecordError($"W{w}", T);
                }
            }
        })));

        Assert.Equal(MetricsCollector.MaxSamples, collector.Count);
        Assert.Equal(MetricsCollector.MaxSamples, collector.Latency.Count + collector.Errors.Count);
        Assert.True(collector.HasErrors);
    }
}
