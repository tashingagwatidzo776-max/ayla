using DongGfx.Core.Analytics;

namespace DongGfx.Core.Tests;

/// <summary>
/// Tests for the headless telemetry panel builder: empty → null, latency
/// stats (mean/p95/max) and error counts from MetricsCollector samples.
/// </summary>
[Trait("Category", "Unit")]
public class TelemetrySummaryBuilderTests
{
    [Fact]
    public void Build_NoSamples_ReturnsNull()
    {
        Assert.Null(TelemetrySummaryBuilder.Build([], []));
    }

    [Fact]
    public void Build_LatencySamples_ComputesCountMeanAndMax()
    {
        var at = DateTimeOffset.UtcNow;
        var latency = new[]
        {
            new MetricSample("cycle_latency_ms", 100, at, "A"),
            new MetricSample("cycle_latency_ms", 200, at, "A"),
            new MetricSample("cycle_latency_ms", 300, at, "A"),
        };

        var summary = TelemetrySummaryBuilder.Build(latency, []);

        Assert.NotNull(summary);
        Assert.Equal(3, summary.CycleCount);
        Assert.Equal(200, summary.MeanLatencyMs, 5);
        Assert.Equal(300, summary.MaxLatencyMs, 5);
        Assert.Equal(0, summary.ErrorCount);
    }

    [Fact]
    public void Build_P95_SlottedPercentile()
    {
        // 20 samples 1..20 → 19th ordered value (ceil(0.95*20)=19) is 19.
        var at = DateTimeOffset.UtcNow;
        var latency = Enumerable.Range(1, 20)
            .Select(i => new MetricSample("cycle_latency_ms", (double)i, at, "A"))
            .ToArray();

        var summary = TelemetrySummaryBuilder.Build(latency, []);

        Assert.NotNull(summary);
        Assert.Equal(19, summary.P95LatencyMs, 5);
    }

    [Fact]
    public void Build_ErrorsOnly_YieldsErrorCountWithoutLatency()
    {
        var at = DateTimeOffset.UtcNow;
        var errors = new[]
        {
            new MetricSample("cycle_error", 1, at, "A"),
            new MetricSample("cycle_error", 1, at, "B"),
        };

        var summary = TelemetrySummaryBuilder.Build([], errors);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.CycleCount);
        Assert.Equal(2, summary.ErrorCount);
        Assert.Equal(0, summary.MeanLatencyMs);
    }

    [Fact]
    public void Format_Empty_SaysNoTelemetryYet()
    {
        Assert.Equal("no telemetry yet", TelemetrySummaryBuilder.Format(null));
    }

    [Fact]
    public void Format_WithSummary_ContainsKeyFigures()
    {
        var summary = new TelemetrySummaryBuilder.TelemetrySummary(
            CycleCount: 12, MeanLatencyMs: 142.5, P95LatencyMs: 310, MaxLatencyMs: 480, ErrorCount: 1);

        var text = TelemetrySummaryBuilder.Format(summary);

        Assert.Contains("cycles 12", text);
        Assert.Contains("142.5ms", text);
        Assert.Contains("p95 310ms", text);
        Assert.Contains("errors 1", text);
    }
}
