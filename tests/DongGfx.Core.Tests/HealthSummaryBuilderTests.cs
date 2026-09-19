using DongGfx.Core.Analytics;

namespace DongGfx.Core.Tests;

/// <summary>
/// Tests for the headless Health tab summary builder (extracted from the
/// HealthViewModel): counters, row formatting, overall status precedence,
/// and the cache summary line.
/// </summary>
[Trait("Category", "Unit")]
public class HealthSummaryBuilderTests
{
    private static AccountHealthInput Account(
        string name,
        bool connected = false,
        bool degraded = false,
        bool paused = false,
        bool hasRunner = false,
        int ticks = 0) => new(
        Guid.NewGuid(), name, name[..Math.Min(8, name.Length)].ToUpperInvariant() + "0000",
        connected ? "Connected" : "Not connected", connected ? "100 USD" : "demo",
        connected, degraded, paused, hasRunner, degraded ? "Open (5 failures)" : "",
        ticks, 400, 1234, "frxEURUSD", "Growth");

    [Fact]
    public void Build_EmptyAccounts_ReportsNoAccountsConnected()
    {
        var summary = HealthSummaryBuilder.Build(Array.Empty<AccountHealthInput>(), 0, 0);

        Assert.Equal("No accounts connected", summary.OverallStatus);
        Assert.Equal(0, summary.TotalAccounts);
        Assert.Empty(summary.Rows);
        Assert.Equal("No tick data cached yet", summary.CacheStats);
    }

    [Fact]
    public void Build_AllConnected_ReportsAllConnected()
    {
        var summary = HealthSummaryBuilder.Build(
            new[] { Account("Alpha", connected: true), Account("Beta", connected: true) }, 2, 4000);

        Assert.Equal("✓ All 2 accounts connected", summary.OverallStatus);
        Assert.Equal(2, summary.ConnectedCount);
        Assert.Equal(0, summary.RunningEngines); // connected but not running
        Assert.Equal(2, summary.TotalAccounts);
    }

    [Fact]
    public void Build_DegradedBeatsConnected_InOverallStatus()
    {
        var summary = HealthSummaryBuilder.Build(
            new[] { Account("Alpha", connected: true), Account("Beta", degraded: true) }, 1, 10);

        Assert.Contains("1 degraded", summary.OverallStatus);
        Assert.Equal(1, summary.DegradedCount);
    }

    [Fact]
    public void Build_PartiallyConnected_ShowsRatio()
    {
        var summary = HealthSummaryBuilder.Build(
            new[] { Account("Alpha", connected: true), Account("Beta") }, 1, 10);

        Assert.Equal("1/2 connected", summary.OverallStatus);
    }

    [Fact]
    public void Build_Rows_FormatBuffersAndCounters()
    {
        var summary = HealthSummaryBuilder.Build(
            new[] { Account("Gamma", connected: true, paused: true, hasRunner: true, ticks: 120) }, 1, 1234);

        var row = Assert.Single(summary.Rows);
        Assert.Equal("120/400", row.TickBuffer);
        Assert.Equal(0.30, row.TickBufferFill, 5);
        Assert.Equal("1,234", row.CachedTicks);
        Assert.True(row.HasRunner);
        Assert.True(row.IsPaused);
        Assert.Equal("Growth", row.Brain);
        Assert.Equal(1, summary.RunningEngines);
        Assert.Equal(1, summary.PausedCount);
    }

    [Fact]
    public void Build_CacheStats_FormatsSymbolsAndTicks()
    {
        var summary = HealthSummaryBuilder.Build(
            new[] { Account("Alpha", connected: true) }, 3, 17500);

        Assert.Equal("17,500 ticks cached across 3 symbol(s)", summary.CacheStats);
    }
}
