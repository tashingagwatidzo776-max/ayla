using System.IO;
using Tf.App.Infrastructure;
using Tf.App.ViewModels;
using Tf.Core.Analytics;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.Tests;

/// <summary>
/// Tests for the monitoring toggles: AppSettings round-trips the digest
/// switch; SettingsViewModel loads/builds it; the Performance dashboard's
/// live telemetry panel reflects collector samples.
/// </summary>
[Trait("Category", "Unit")]
public class MonitoringSettingsTests
{
    [Fact]
    public void DigestToggle_RoundTripsThroughViewModel()
    {
        var settingsService = new SettingsService();
        var client = new DerivClient();
        var dashboard = new DashboardViewModel(client);

        var vm = new SettingsViewModel(settingsService, client, dashboard);

        var off = vm.BuildSettings();
        off.MetricsDigestEnabled = false;
        Assert.False(off.MetricsDigestEnabled);

        vm.Load(new AppSettings { MetricsDigestEnabled = false });
        Assert.False(vm.MetricsDigestEnabled);
        Assert.False(vm.BuildSettings().MetricsDigestEnabled);

        vm.Load(new AppSettings { MetricsDigestEnabled = true });
        Assert.True(vm.MetricsDigestEnabled);
        Assert.True(vm.BuildSettings().MetricsDigestEnabled);
    }

    [Fact]
    public void TelemetryPanel_WithCollector_ShowsLiveStatsAndErrors()
    {
        var collector = new MetricsCollector();
        collector.RecordLatency(100, "Alpha", DateTimeOffset.UtcNow);
        collector.RecordLatency(300, "Alpha", DateTimeOffset.UtcNow);
        collector.RecordError("Alpha", DateTimeOffset.UtcNow);

        var vm = new PerformanceViewModel(
            new PerformanceTracker(Path.Combine(Path.GetTempPath(), $"tf_mon_{Guid.NewGuid():N}")),
            metrics: collector);
        vm.RefreshTelemetry();

        Assert.Contains("cycles 2", vm.TelemetrySummaryText);
        Assert.Contains("p95 300ms", vm.TelemetrySummaryText);
        Assert.Contains("errors 1", vm.TelemetrySummaryText);
        Assert.Equal(1, vm.TelemetryErrorCount);
    }

    [Fact]
    public void TelemetryPanel_NoCollector_StaysEmpty()
    {
        var vm = new PerformanceViewModel(
            new PerformanceTracker(Path.Combine(Path.GetTempPath(), $"tf_mon_{Guid.NewGuid():N}")));

        vm.RefreshTelemetry();

        Assert.Equal("no telemetry yet", vm.TelemetrySummaryText);
        Assert.Equal(0, vm.TelemetryErrorCount);
    }
}
