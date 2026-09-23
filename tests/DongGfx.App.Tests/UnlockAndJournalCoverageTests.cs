using System.IO;
using System.Reflection;
using DongGfx.App.Controls;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The real-money unlock staleness watch: an unlock armed longer than
/// ArmStalenessHours journals REAL_MONEY_UNLOCK_STALE and fires the rail
/// toast — once per full threshold, repeating while the unlock stays armed;
/// 0 disables, and a disarmed/disabled state resets the repeat counter.
/// The Journal tab's filter pipeline (category, search, dates, clear) is
/// driven against a temp-dir journal. The tick chart's data path (add,
/// trim to MaxPoints, clear) rounds it out.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class UnlockAndJournalCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tf_unk_{Guid.NewGuid():N}");
    private readonly TradeJournal _journal;
    private readonly ManualRealMoneyGate _gate = new();
    private readonly CaptureNotifier _notifier = new();

    private sealed class CaptureNotifier : NotificationService
    {
        public List<(string Title, string Body)> Sent { get; } = new();

        public CaptureNotifier()
        {
            MinInterval = TimeSpan.Zero;   // rate-limit must not eat test toasts
        }

        internal override void SendToast(string title, string body, string severity) =>
            Sent.Add((title, body));
    }

    /// <summary>WPF controls require an STA thread; run the body there and
    /// rethrow any failure on the test thread.</summary>
    private static void RunInSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    public UnlockAndJournalCoverageTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private UnlockStalenessMonitor NewMonitor(double stalenessHours)
    {
        var settings = new AppSettings { ArmStalenessHours = (int)stalenessHours };
        return new UnlockStalenessMonitor(
            _gate, _journal, _notifier,
            new WebhookService(TimeSpan.FromSeconds(60)) { WebhookUrl = null, MinInterval = TimeSpan.Zero },
            () => settings);
    }

    private void ArmHoursAgo(double hours)
    {
        _gate.Arm();
        typeof(ManualRealMoneyGate)
            .GetField("_armedAt", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_gate, DateTimeOffset.UtcNow.AddHours(-hours));
    }

    private static bool StaleEntry(TradeJournal journal)
    {
        journal.Flush();
        return journal.GetRecent(count: 50).Any(e =>
            e.Category == "REAL_MONEY_UNLOCK_STALE");
    }

    // ── UnlockStalenessMonitor ────────────────────────────────────────

    [Fact]
    public void Disabled_Or_Disarmed_Alerts_Nothing_And_Resets()
    {
        var monitor = NewMonitor(stalenessHours: 0);
        ArmHoursAgo(100);   // ancient, but the alert is disabled

        monitor.Tick();

        Assert.False(StaleEntry(_journal));
        Assert.Empty(_notifier.Sent);

        _gate.Reset();      // disarmed with a live threshold — also silent
        var monitor2 = NewMonitor(stalenessHours: 4);
        monitor2.Tick();
        Assert.False(StaleEntry(_journal));
    }

    [Fact]
    public void Freshly_Armed_Within_Threshold_Is_Silent()
    {
        var monitor = NewMonitor(stalenessHours: 4);
        ArmHoursAgo(1);

        monitor.Tick();

        Assert.False(StaleEntry(_journal));
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public void Stale_Unlock_Alerts_Once_Per_Threshold_Then_Repeats_On_The_Next()
    {
        var monitor = NewMonitor(stalenessHours: 4);
        ArmHoursAgo(9);     // index 2 → one alert covering thresholds 1–2

        monitor.Tick();

        Assert.True(StaleEntry(_journal));
        var toast = Assert.Single(_notifier.Sent);
        Assert.Contains("Risk rail engaged", toast.Title);
        Assert.Contains("Real-money unlock stale", toast.Body);
        Assert.Contains("threshold 4h", toast.Body);

        monitor.Tick();     // same threshold → no repeat
        Assert.False(StaleEntry(_journal) && _journal.GetRecent(count: 50).Count(e => e.Category == "REAL_MONEY_UNLOCK_STALE") > 1);
        Assert.Single(_notifier.Sent);

        ArmHoursAgo(13);    // now past threshold 3 → the rail repeats
        monitor.Tick();
        Assert.Equal(2, _notifier.Sent.Count);
    }

    [Fact]
    public void Re_Arm_After_Reset_Alerts_Again()
    {
        var monitor = NewMonitor(stalenessHours: 4);
        ArmHoursAgo(9);
        monitor.Tick();
        Assert.Single(_notifier.Sent);

        _gate.Reset();      // disarm resets the repeat counter
        monitor.Tick();

        ArmHoursAgo(5);     // fresh stale arm → alerts again
        monitor.Tick();
        Assert.Equal(2, _notifier.Sent.Count);
    }

    // ── JournalViewModel filters ──────────────────────────────────────

    private JournalViewModel NewVm()
    {
        _journal.Log(Guid.Empty, "FX_ORDER", "buy market 0.1 XAUUSDmicro");
        _journal.Log(Guid.Empty, "FX_RISK", "drawdown warning");
        _journal.Log(Guid.Empty, "MT5_ORDER", "close 42 → TRADE_RETCODE_DONE");
        _journal.Flush();
        return new JournalViewModel(_journal, () => false);
    }

    [Fact]
    public void Refresh_Loads_Rows_And_Category_Filter_Narrows()
    {
        var vm = NewVm();

        vm.RefreshCommand.Execute(null);
        Assert.Equal(3, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.NotEmpty(e.Timestamp));
        Assert.Contains("3", vm.StatsText);

        vm.FilterCategory = "FX_ORDER";
        vm.RefreshCommand.Execute(null);
        var row = Assert.Single(vm.Entries);
        Assert.Equal("FX_ORDER", row.Category);
        Assert.Contains("XAUUSDmicro", row.Details);
    }

    [Fact]
    public void Search_Filters_Details_And_Category_Text()
    {
        var vm = NewVm();

        vm.SearchText = "retcode";      // matches details, case-insensitive
        vm.RefreshCommand.Execute(null);
        Assert.Equal("MT5_ORDER", Assert.Single(vm.Entries).Category);

        vm.SearchText = "fx_";          // matches the category itself
        vm.RefreshCommand.Execute(null);
        Assert.Equal(2, vm.Entries.Count);

        vm.SearchText = "no-such-token";
        vm.RefreshCommand.Execute(null);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Date_Filter_And_ClearFilter_Restore_The_View()
    {
        var vm = NewVm();

        vm.FilterDateFrom = DateTimeOffset.Now.AddDays(1);   // everything is older
        vm.RefreshCommand.Execute(null);
        Assert.Empty(vm.Entries);

        vm.FilterDateFrom = null;
        vm.FilterCategory = "FX_RISK";
        vm.RefreshCommand.Execute(null);
        Assert.Single(vm.Entries);

        vm.ClearFilterCommand.Execute(null);
        Assert.Equal("ALL", vm.FilterCategory);
        Assert.Null(vm.SelectedAccountId);
        Assert.Equal(3, vm.Entries.Count);   // refreshed with the cleared filter
    }

    [Fact]
    public void EntryAdded_AutoRefreshes_The_View()
    {
        var vm = new JournalViewModel(_journal, () => false);
        vm.RefreshCommand.Execute(null);
        Assert.Empty(vm.Entries);

        _journal.Log(Guid.Empty, "FX_ORDER", "auto-refresh probe");
        _journal.Flush();
        Thread.Sleep(100);   // the EntryAdded handler marshals through the dispatcher

        vm.RefreshCommand.Execute(null);
        Assert.Contains(vm.Entries, e => e.Details.Contains("auto-refresh probe"));
    }

    // ── TickChartControl data path ────────────────────────────────────

    [Fact]
    public void TickChart_Adds_Trims_To_MaxPoints_And_Clears()
    {
        RunInSta(() =>
        {
            var chart = new TickChartControl { MaxPoints = 10 };

            var ticks = Enumerable.Range(0, 25).Select(i => new Tick(
                "XAUUSDmicro", 2650 + i, 2650.1 + i, 2650 + i, 1000 + i, 2)).ToList();
            chart.AddTicks(ticks);

            Assert.Equal(10, chart.Ticks.Count);
            Assert.Equal(2665.0, chart.Ticks[0].Quote, 5);   // oldest 15 trimmed

            chart.Clear();
            Assert.Empty(chart.Ticks);
        });
    }
}
