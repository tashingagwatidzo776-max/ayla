using System.IO;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.App.ViewModels;
using Tf.Core;
using Tf.Core.Logging;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.Tests;

/// <summary>
/// Headless tests for the dashboard's growth-status line: the "Growth:" pill
/// must mirror the hub's running growth runners (their last activity), fall
/// back to an idle line when nothing trades, and never go stale when a
/// runner stops. Driven through the hub's TestRaise* seams — a real activity
/// line needs a live trading session (covered by the E2E tests).
/// </summary>
[Trait("Category", "Unit")]
public class DashboardGrowthStatusTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;
    private readonly DashboardViewModel _dashboard;

    public DashboardGrowthStatusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_dashstatus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
        _dashboard = new DashboardViewModel(new DerivClient(), _hub);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private GrowthRunner MakeRunner(string label, string token)
    {
        var connection = _hub.AddAccount(new AccountConfig
        {
            Label = label,
            ApiToken = token,
            IsDemo = true,
            BrainKey = "Growth"
        });
        return new GrowthRunner(connection, _store,
            () => new AppSettings(), () => false, _journal);
    }

    [Fact]
    public void InitialState_ShowsIdleLine()
    {
        Assert.Equal("Idle — no growth runners", _dashboard.GrowthStatus);
    }

    [Fact]
    public void RunningRunner_ShowsItsLastActivity()
    {
        var runner = MakeRunner("Dash Alpha", "dash-alpha");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true, "Session Dash Alpha: bankroll $5.00 → target $5.50");

        // A fresh activity line is what flips the status in production (the
        // start path raises one too), so emulate both here.
        _hub.TestRaiseGrowthActivity(runner, "Session Dash Alpha: bankroll $5.00 → target $5.50");

        Assert.Contains("Dash Alpha", _dashboard.GrowthStatus);
        Assert.Contains("bankroll $5.00", _dashboard.GrowthStatus);
    }

    [Fact]
    public void StoppedRunner_RevertsToIdleLine()
    {
        var runner = MakeRunner("Dash Beta", "dash-beta");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true, "Session Dash Beta: bankroll $5.00 → target $5.50");
        _hub.TestRaiseGrowthActivity(runner, "Session Dash Beta: bankroll $5.00 → target $5.50");
        Assert.Contains("Dash Beta", _dashboard.GrowthStatus);

        // Stop the runner; the next activity line must flip the pill back to
        // idle — otherwise the dashboard would show a brain that isn't there.
        runner.TestSetRunningState(false, "Stopped.");
        _hub.TestRaiseGrowthActivity(runner, "Stopped.");

        Assert.Equal("Idle — no growth runners", _dashboard.GrowthStatus);
    }

    [Fact]
    public void TwoRunningRunners_ShowBothLines()
    {
        var a = MakeRunner("Dash Two A", "dash-two-a");
        var b = MakeRunner("Dash Two B", "dash-two-b");
        _hub.ObserveRunner(a);
        _hub.ObserveRunner(b);
        a.TestSetRunningState(true, "Session Dash Two A: bankroll $5.00 → target $5.50");
        b.TestSetRunningState(true, "Session Dash Two B: bankroll $9.00 → target $9.90");

        _hub.TestRaiseGrowthActivity(a, "Session Dash Two A: bankroll $5.00 → target $5.50");
        _hub.TestRaiseGrowthActivity(b, "Session Dash Two B: bankroll $9.00 → target $9.90");

        Assert.Contains("Dash Two A", _dashboard.GrowthStatus);
        Assert.Contains("Dash Two B", _dashboard.GrowthStatus);
    }

    [Fact]
    public void NoHub_ShowsIdleLine()
    {
        var dashboard = new DashboardViewModel(new DerivClient());
        dashboard.UpdateGrowthStatusFromHub();
        Assert.Equal("Idle — no growth runners", dashboard.GrowthStatus);
    }

    private sealed class MemoryVault : IAccountVault
    {
        public IReadOnlyList<AccountConfig> Load() => [];
        public void Save(IReadOnlyList<AccountConfig> accounts) { }
    }
}
