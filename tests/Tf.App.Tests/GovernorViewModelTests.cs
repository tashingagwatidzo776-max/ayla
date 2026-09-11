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
/// Headless unit tests for the governor/portfolio state surfaced in the UI:
/// the Growth tab's combined-P&amp;L row, latched-governor banner and Re-arm
/// command, plus the Dashboard's portfolio summary (combined P&amp;L and the
/// latched risk-rail alerts). The hub's governor events are raised through
/// the internal test seams — driving a real trip needs full settlements and
/// is covered by the integration tests.
/// </summary>
[Trait("Category", "Unit")]
public class GovernorViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;

    public GovernorViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_govvm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Trade GrowthTrade(decimal profit) => new(
        Guid.NewGuid(), "frxEURUSD",
        profit >= 0 ? Direction.Rise : Direction.Fall,
        1.00m, "USD", 1.17, 1700000300, $"C-{Guid.NewGuid():N}",
        profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
        profit, 1.165, 1700000600, DateTimeOffset.UtcNow,
        Guid.NewGuid(), "Gov Test", TradeSource.Growth);

    private GrowthViewModel CreateGrowthVm() => new(
        _hub, new GrowthPlanStore(), () => new AppSettings { AutonomyEnabled = true },
        new DashboardViewModel(new DerivClient(), _hub), tracker: null);

    private DashboardViewModel CreateDashboardVm() => new(new DerivClient(), _hub);

    // ─── Growth tab: combined P&L + banner + re-arm ────────

    [Fact]
    public void GrowthViewModel_ShowsCombinedPnl_FromSeededTrades()
    {
        _store.Add(GrowthTrade(+0.90m));
        _store.Add(GrowthTrade(-1.00m));

        var vm = CreateGrowthVm();

        // Combined net −$0.10 → signed, absolute-value formatting.
        Assert.Equal("−$0.10", vm.CombinedPnlText);
    }

    [Fact]
    public void GrowthViewModel_GovernorTrip_ShowsBanner_AndRearmCommandClearsIt()
    {
        var vm = CreateGrowthVm();
        Assert.False(vm.IsGovernorTripped);
        Assert.Equal("", vm.GovernorBannerText);

        _hub.TestRaiseGovernorTripped(-2.00m);

        Assert.True(vm.IsGovernorTripped);
        Assert.Contains("−$2", vm.GovernorBannerText);
        Assert.Contains("re-arm", vm.GovernorBannerText, StringComparison.OrdinalIgnoreCase);

        // The command must flow through the hub's real re-arm (journal +
        // GovernorRearmed event), which clears the VM's banner state.
        vm.RearmGovernorCommand.Execute(null);

        Assert.False(vm.IsGovernorTripped);
        Assert.Equal("", vm.GovernorBannerText);
    }

    // ─── Dashboard: portfolio summary + risk rails ─────────

    [Fact]
    public void DashboardViewModel_ShowsCombinedPnl_AndBuildsRiskRailAlerts()
    {
        _hub.AddAccount(new AccountConfig { Label = "Gov A", ApiToken = "t-a", IsDemo = true, BrainKey = "Growth" });
        _hub.AddAccount(new AccountConfig { Label = "Gov B", ApiToken = "t-b", IsDemo = true, BrainKey = "Growth" });
        _store.Add(GrowthTrade(+0.90m));
        _store.Add(GrowthTrade(-1.00m));

        var vm = CreateDashboardVm();

        Assert.Contains("0.10", vm.CombinedGrowthPnlText);
        Assert.Empty(vm.RiskRailAlerts);

        vm.ToggleKillSwitchCommand.Execute(null);

        Assert.Contains(vm.RiskRailAlerts, a => a.Contains("kill switch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DashboardViewModel_GovernorLatch_AppearsAndClears()
    {
        var vm = CreateDashboardVm();

        _hub.TestRaiseGovernorTripped(-2.00m);

        Assert.True(vm.IsGovernorLatched);
        Assert.Contains(vm.RiskRailAlerts, a => a.Contains("governor latched", StringComparison.OrdinalIgnoreCase));

        _hub.TestRaiseGovernorRearmed();

        Assert.False(vm.IsGovernorLatched);
        Assert.DoesNotContain(vm.RiskRailAlerts, a => a.Contains("governor latched", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>In-memory vault so the tests never touch %APPDATA%.</summary>
    private sealed class MemoryVault : IAccountVault
    {
        public List<AccountConfig> Items { get; } = new();

        public IReadOnlyList<AccountConfig> Load() => Items.ToArray();

        public void Save(IReadOnlyList<AccountConfig> accounts)
        {
            Items.Clear();
            Items.AddRange(accounts);
        }
    }
}
