using System.IO;
using Tf.App.ViewModels;
using Tf.Core.Analytics;
using Tf.Core.Models;

namespace Tf.App.Tests;

/// <summary>
/// Tests for the Performance dashboard view model: summary/strategy
/// ranking/equity-curve building on top of a real PerformanceTracker fed
/// with controlled trades.
/// </summary>
[Trait("Category", "Unit")]
public class PerformanceViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly PerformanceTracker _tracker;

    public PerformanceViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_perfvm_{Guid.NewGuid():N}");
        _tracker = new PerformanceTracker(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Trade Trade(decimal profit, string source, string accountName, DateTimeOffset settledAt) => new(
        Guid.NewGuid(), "frxEURUSD",
        profit >= 0 ? Direction.Rise : Direction.Fall,
        1.00m, "USD", 1.17, 1700000300, $"C-{Guid.NewGuid():N}",
        profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
        profit, 1.165, 1700000600, settledAt,
        Guid.NewGuid(), accountName, source);

    [Fact]
    public void Refresh_EmptyTracker_ShowsZeroedSummary()
    {
        var vm = new PerformanceViewModel(_tracker);

        vm.RefreshCommand.Execute(null);

        Assert.Contains("Trades: 0", vm.SummaryText);
        Assert.Contains("Accounts: 0", vm.SummaryText);
        Assert.Empty(vm.EquityCurve);
        Assert.Equal("", vm.EquityCurvePoints);
    }

    [Fact]
    public void Refresh_WithTrades_BuildsSummaryRankingAndCurve()
    {
        var today = DateTimeOffset.Now;
        var growth = Guid.NewGuid();
        _tracker.RecordTrade(Trade(0.90m, "Growth", "Alpha", today));
        _tracker.RecordTrade(Trade(-1.00m, "Growth", "Alpha", today));
        _tracker.RecordTrade(Trade(0.50m, "Manual", "Beta", today));

        var vm = new PerformanceViewModel(_tracker);
        vm.RefreshCommand.Execute(null);

        Assert.Contains("Trades: 3", vm.SummaryText);
        Assert.Contains("+0.4", vm.SummaryText); // net 0.90 − 1.00 + 0.50

        // Strategies ranked by profit: Manual (+0.50) beats Growth (−0.10).
        Assert.Equal("Manual", vm.StrategyComparison[0].Strategy);
        Assert.True(vm.StrategyComparison[0].IsBest);
        Assert.Contains("Best", vm.StrategyComparison[0].Rank);
        Assert.Equal("Growth", vm.StrategyComparison[1].Strategy);

        // One equity point per account-day (each helper trade has its own
        // account id): running P&L walks 0.90 → −0.10 → 0.40.
        Assert.Equal(3, vm.EquityCurve.Count);
        Assert.Equal(0.40m, vm.EquityCurve[^1].Value);
        Assert.False(string.IsNullOrWhiteSpace(vm.EquityCurvePoints));
        Assert.Contains(",", vm.EquityCurvePoints);
    }

    [Fact]
    public void Refresh_AccountFilter_FiltersDailyHistory()
    {
        var today = DateTimeOffset.Now;
        var alpha = Guid.NewGuid();
        _tracker.RecordTrade(new Trade(
            Guid.NewGuid(), "frxEURUSD", Direction.Rise, 1m, "USD", 1.17, 1, "C-1",
            ContractStatus.Won, 0.9m, 1.165, 2,            today, alpha, "Alpha", "Growth"));
        _tracker.RecordTrade(Trade(-0.5m, "Growth", "Beta", today));

        var vm = new PerformanceViewModel(_tracker);
        vm.RefreshCommand.Execute(null);
        Assert.Equal(2, vm.DailyHistory.Count); // ALL

        vm.SelectedAccountId = alpha.ToString();
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.DailyHistory);
        Assert.Equal(0.9m, vm.DailyHistory[0].Profit);
    }

    [Fact]
    public void Refresh_SingleDay_NoPolyline()
    {
        _tracker.RecordTrade(Trade(0.9m, "Growth", "Alpha", DateTimeOffset.Now));

        var vm = new PerformanceViewModel(_tracker);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.EquityCurve);
        Assert.Equal("", vm.EquityCurvePoints); // needs > 1 point
    }
}
