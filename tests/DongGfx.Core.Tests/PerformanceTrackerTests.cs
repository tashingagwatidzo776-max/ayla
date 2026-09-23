using System.Globalization;
using DongGfx.Core.Analytics;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

/// <summary>
/// The intraday P&L series behind the Growth tab's curve chart: one point per
/// settled trade, running cumulative totals, per-account partitioning, and
/// JSON persistence round-trips.
/// </summary>
[Trait("Category", "Unit")]
public class PerformanceTrackerTests : IDisposable
{
    private readonly string _dir;

    public PerformanceTrackerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_tracker_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Trade MakeTrade(Guid accountId, decimal profit, int minutesOffset) => new(
        Id: Guid.NewGuid(),
        Symbol: "EURUSD",
        Stake: 1.00m,
        Profit: profit,
        SettledAt: DateTimeOffset.UtcNow.Date.AddMinutes(minutesOffset),
        Source: TradeSource.Fx,
        AccountId: accountId,
        AccountName: "Acc");

    [Fact]
    public void GetIntradayPnl_BuildsCumulativeCurve()
    {
        var tracker = new PerformanceTracker(_dir);
        var account = Guid.NewGuid();

        tracker.RecordTrade(MakeTrade(account, +0.90m, 1));
        tracker.RecordTrade(MakeTrade(account, -1.00m, 2));
        tracker.RecordTrade(MakeTrade(account, +0.50m, 3));

        var points = tracker.GetIntradayPnl(account);

        Assert.Equal(3, points.Count);
        Assert.Equal(+0.90m, points[0].CumulativePnl);
        Assert.Equal(-0.10m, points[1].CumulativePnl);
        Assert.Equal(+0.40m, points[2].CumulativePnl);
        Assert.Equal(-1.00m, points[1].TradePnl);
    }

    [Fact]
    public void GetIntradayPnl_PartitionsByAccount()
    {
        var tracker = new PerformanceTracker(_dir);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        tracker.RecordTrade(MakeTrade(a, +0.90m, 1));
        tracker.RecordTrade(MakeTrade(b, -1.00m, 2));

        Assert.Single(tracker.GetIntradayPnl(a));
        Assert.Single(tracker.GetIntradayPnl(b));
        Assert.Equal(+0.90m, tracker.GetIntradayPnl(a)[0].CumulativePnl);
        Assert.Equal(-1.00m, tracker.GetIntradayPnl(b)[0].CumulativePnl);
    }

    [Fact]
    public void GetIntradayPnl_UnknownAccount_ReturnsEmpty()
    {
        var tracker = new PerformanceTracker(_dir);

        Assert.Empty(tracker.GetIntradayPnl(Guid.NewGuid()));
    }

    [Fact]
    public void PnlSeries_SurvivesSaveAndReload()
    {
        var tracker = new PerformanceTracker(_dir);
        var account = Guid.NewGuid();
        tracker.RecordTrade(MakeTrade(account, +0.90m, 1));
        tracker.RecordTrade(MakeTrade(account, +0.90m, 2));
        tracker.Save();

        var reloaded = new PerformanceTracker(_dir);

        var points = reloaded.GetIntradayPnl(account);
        Assert.Equal(2, points.Count);
        Assert.Equal(1.80m, points[1].CumulativePnl);
    }
}
