using System.Globalization;
using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Brain;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Tests;

/// <summary>
/// Headless tests for the dashboard's growth-status pill: per running runner
/// it must show account name, live P/L and progress toward the session
/// target (from the runner's real session engine), fall back to the activity
/// line when no engine exists yet, fall back to the idle line when nothing
/// runs, and never go stale when a runner stops. Driven through the hub's
/// TestRaise* seams and the runner's TestAttachEngine seam — a live trading
/// session is covered by the E2E tests.
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
    public void RunningRunnerWithoutEngine_ShowsActivityLine()
    {
        var runner = MakeRunner("Dash Alpha", "dash-alpha");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true, "Session Dash Alpha: bankroll $5.00 → target $5.50");

        _dashboard.UpdateGrowthStatusFromHub();

        // No engine attached: the activity line is the best available truth.
        Assert.Contains("Dash Alpha", _dashboard.GrowthStatus);
        Assert.Contains("bankroll $5.00", _dashboard.GrowthStatus);
    }

    [Fact]
    public void RunningRunnerWithEngine_ShowsPnlAndProgressToTarget()
    {
        var runner = MakeRunner("Dash Alpha", "dash-alpha");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true);
        // Real engine, real math: $5 start, $10 target (DailyTargetFraction 1.0),
        // one win: bankroll 5 + 2 = 7 → P/L +$2, 40% of the $5 span to target.
        var engine = new GrowthSessionEngine(GrowthPlan.Default, 5.00m);
        engine.ApplySettlement(won: true, netProfit: 2.00m);
        runner.TestAttachEngine(engine);

        _dashboard.UpdateGrowthStatusFromHub();

        Assert.Contains("Dash Alpha", _dashboard.GrowthStatus);
        Assert.Contains("+$2", _dashboard.GrowthStatus);
        Assert.Contains("$7", _dashboard.GrowthStatus);
        Assert.Contains("40% to target", _dashboard.GrowthStatus);
        Assert.DoesNotContain("Idle", _dashboard.GrowthStatus);
    }

    [Fact]
    public void LosingRunner_ShowsNegativePnlAndClampedFloor()
    {
        var runner = MakeRunner("Dash Bear", "dash-bear");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true);
        // Two losses from $5: 5 - 1 - 2 = 2 → P/L -$3. (The floor clamps the
        // engine's own state at 2 = 0.4 × 5; the composer's display clamp is
        // exercised by the composer tests below.)
        var engine = new GrowthSessionEngine(GrowthPlan.Default, 5.00m);
        engine.ApplySettlement(won: false, netProfit: -1.00m);
        engine.ApplySettlement(won: false, netProfit: -2.00m);
        runner.TestAttachEngine(engine);

        _dashboard.UpdateGrowthStatusFromHub();

        Assert.Contains("Dash Bear", _dashboard.GrowthStatus);
        Assert.Contains("-$3", _dashboard.GrowthStatus);
        Assert.Contains("$2", _dashboard.GrowthStatus);
    }

    [Fact]
    public void TargetReached_ShowsReachedNotPercentage()
    {
        var runner = MakeRunner("Dash Winner", "dash-winner");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true);
        // One big win: 5 + 6 = 11 ≥ 10 → TargetHit flips inside the engine.
        var engine = new GrowthSessionEngine(GrowthPlan.Default, 5.00m);
        engine.ApplySettlement(won: true, netProfit: 6.00m);
        runner.TestAttachEngine(engine);

        _dashboard.UpdateGrowthStatusFromHub();

        Assert.Contains("target reached", _dashboard.GrowthStatus);
        Assert.Contains("+$6", _dashboard.GrowthStatus);
    }

    [Fact]
    public void TwoRunners_BothShown_WithPerRunnerPnl()
    {
        var a = MakeRunner("Dash Two A", "dash-two-a");
        var b = MakeRunner("Dash Two B", "dash-two-b");
        _hub.ObserveRunner(a);
        _hub.ObserveRunner(b);
        a.TestSetRunningState(true);
        b.TestSetRunningState(true);

        var engA = new GrowthSessionEngine(GrowthPlan.Default, 5.00m);
        engA.ApplySettlement(won: true, netProfit: 1.50m);
        a.TestAttachEngine(engA);

        // B has no engine: falls back to its activity line.
        b.TestSetRunningState(true, "Session Dash Two B: bankroll $5.00 → target $5.50");

        _dashboard.UpdateGrowthStatusFromHub();

        Assert.Contains("Dash Two A", _dashboard.GrowthStatus);
        Assert.Contains("+$1.5", _dashboard.GrowthStatus);
        Assert.Contains("Dash Two B", _dashboard.GrowthStatus);
        Assert.Contains("bankroll $5.00", _dashboard.GrowthStatus);
        Assert.Contains("  •  ", _dashboard.GrowthStatus); // per-runner separator
    }

    [Fact]
    public void StoppedRunner_RevertsToIdleLine()
    {
        var runner = MakeRunner("Dash Beta", "dash-beta");
        _hub.ObserveRunner(runner);
        runner.TestSetRunningState(true, "Session Dash Beta: bankroll $5.00 → target $5.50");
        _hub.TestRaiseGrowthActivity(runner, "Session Dash Beta: bankroll $5.00 → target $5.50");
        Assert.Contains("Dash Beta", _dashboard.GrowthStatus);

        // Stop the runner; the next refresh must flip the pill back to idle —
        // otherwise the dashboard would show a brain that isn't there.
        runner.TestSetRunningState(false, "Stopped.");
        _hub.TestRaiseGrowthActivity(runner, "Stopped.");

        Assert.Equal("Idle — no growth runners", _dashboard.GrowthStatus);
    }

    [Fact]
    public void NoHub_ShowsIdleLine()
    {
        var hubLess = new DashboardViewModel(new DerivClient());
        hubLess.UpdateGrowthStatusFromHub();
        Assert.Equal("Idle — no growth runners", hubLess.GrowthStatus);
    }

    [Fact]
    public void Composer_NullRunners_ShowsIdleLine()
    {
        Assert.Equal("Idle — no growth runners",
            DashboardViewModel.ComposeGrowthStatus(null));
    }

    [Fact]
    public void Composer_EmptyRunners_ShowsIdleLine()
    {
        Assert.Equal("Idle — no growth runners",
            DashboardViewModel.ComposeGrowthStatus([]));
    }

    [Fact]
    public void Composer_NegativeSpan_ShowsTargetReached()
    {
        // Degenerate plan (target ≤ start): progress is capped at 100%.
        var snapshot = new GrowthRunnerSnapshot("Deg", true, "line",
            Bankroll: 9m, StartBankroll: 5m, Target: 5m);
        var text = DashboardViewModel.ComposeGrowthStatus([snapshot]);
        Assert.Contains("target reached", text);
    }

    [Fact]
    public void Composer_ProgressClampedTo100()
    {
        // Bankroll above target but TargetHit not yet observed on the engine:
        // the composer's own clamp must cap the display at 100%.
        var snapshot = new GrowthRunnerSnapshot("Over", true, "line",
            Bankroll: 12m, StartBankroll: 5m, Target: 10m);
        var text = DashboardViewModel.ComposeGrowthStatus([snapshot]);
        Assert.Contains("target reached", text);
        Assert.DoesNotContain("%", text.Replace("target reached", ""));
    }

    [Fact]
    public void Composer_RunnersSortedByName_CaseInsensitive()
    {
        var z = new GrowthRunnerSnapshot("zeta", true, "line", 5m, 5m, 10m);
        var a = new GrowthRunnerSnapshot("Alpha", true, "line", 5m, 5m, 10m);
        var text = DashboardViewModel.ComposeGrowthStatus([z, a]);
        Assert.True(text.IndexOf("Alpha", StringComparison.Ordinal)
                   < text.IndexOf("zeta", StringComparison.Ordinal));
    }

    private sealed class MemoryVault : IAccountVault
    {
        public IReadOnlyList<AccountConfig> Load() => [];
        public void Save(IReadOnlyList<AccountConfig> accounts) { }
    }
}
