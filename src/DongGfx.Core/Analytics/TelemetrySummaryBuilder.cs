using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Analytics;

/// <summary>
/// Computes the live telemetry panel snapshot (cycle count, latency mean/
/// p95/max, error count) from MetricsCollector samples. Headless so the
/// Performance view model's periodic refresh is unit-testable without WPF.
/// </summary>
public static class TelemetrySummaryBuilder
{
    /// <summary>A ready-to-display telemetry snapshot.</summary>
    public sealed record TelemetrySummary(
        int CycleCount,
        double MeanLatencyMs,
        double P95LatencyMs,
        double MaxLatencyMs,
        int ErrorCount);

    /// <summary>Builds the snapshot from latency and error samples.
    /// Returns null when no telemetry has been collected yet.</summary>
    public static TelemetrySummary? Build(
        IReadOnlyList<MetricSample> latency, IReadOnlyList<MetricSample> errors)
    {
        if (latency.Count == 0 && errors.Count == 0)
        {
            return null;
        }

        var values = latency.Select(s => s.Value).OrderBy(v => v).ToList();
        return new TelemetrySummary(
            CycleCount: latency.Count,
            MeanLatencyMs: values.Count > 0 ? values.Average() : 0,
            P95LatencyMs: values.Count > 0 ? Percentile(values, 0.95) : 0,
            MaxLatencyMs: values.Count > 0 ? values[^1] : 0,
            ErrorCount: errors.Count);
    }

    /// <summary>Formats the snapshot for the panel ("cycles 12 · latency
    /// mean 142.5ms · p95 310ms · max 480ms · errors 1") — or "no telemetry
    /// yet" for the empty case.</summary>
    public static string Format(TelemetrySummary? summary) => summary is null
        ? "no telemetry yet"
        : $"cycles {summary.CycleCount} · latency mean {summary.MeanLatencyMs:0.#}ms"
          + $" · p95 {summary.P95LatencyMs:0.#}ms · max {summary.MaxLatencyMs:0.#}ms"
          + $" · errors {summary.ErrorCount}";

    private static double Percentile(List<double> sorted, double p)
    {
        var idx = (int)System.Math.Ceiling(p * sorted.Count) - 1;
        return sorted[System.Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
