using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Tests;

/// <summary>
/// The Growth tab's go-live readiness panel: five checks (accounts verified,
/// unlock armed, manual stake cap, governor cap, webhook) answering "what is
/// still between me and real trading?" at a glance. Verdicts are derived
/// from hub + settings state, so these tests pin the derivation: a locked
/// unlock, an unverified account, a disabled cap, or a missing governor cap
/// must each flip exactly the right check to red — and nothing else.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class GrowthViewModelReadinessTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;
    private readonly ManualRealMoneyGate _gate;

    public GrowthViewModelReadinessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_readiness_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
        _gate = _hub.ManualGate;
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private AppSettings _settings = new(); // the test's settings "store"

    private GrowthViewModel CreateVm() => new(
        _hub, new GrowthPlanStore(), () => _settings,
        new DashboardViewModel(new DerivClient(), _hub), tracker: null);

    private static void AssertCheck(GrowthViewModel vm, string name, bool expectedMet, string? textPart = null)
    {
        var check = vm.ReadinessChecks.Single(c => c.Name == name);
        Assert.Equal(expectedMet, check.IsMet);
        if (textPart is not null)
        {
            Assert.Contains(textPart, check.Text);
        }
    }

    [Fact]
    public void FreshSetup_WithNoRealAccounts_IsGreenOnAccountLegs()
    {
        var vm = CreateVm();

        Assert.Equal(5, vm.ReadinessChecks.Count);
        // Demo-only setups are already "ready" on the account legs: demo
        // needs no verification and no unlock. The gate itself enforces this
        // every start; the panel only mirrors the real-trading state.
        AssertCheck(vm, "Accounts", true, "no real accounts");
        AssertCheck(vm, "Unlock", true, "no real accounts");
    }

    [Fact]
    public void DefaultManualCap_IsCapped_AndBlankDisables()
    {
        // The shipped default: bounded manual trades out of the box.
        AssertCheck(CreateVm(), "Manual cap", true, "capped at 10");

        // Blank/0 in the Settings tab is an explicit opt-out — flagged red.
        _settings = new AppSettings { ManualMaxStake = 0m };
        AssertCheck(CreateVm(), "Manual cap", false, "disabled");
    }

    [Fact]
    public void UnverifiedRealAccount_FailsAccountsCheck_FailingClosed()
    {
        var config = new AccountConfig { Label = "Real1", ApiToken = "t", IsDemo = false, BrainKey = "Growth" };
        _hub.AddAccount(config);

        var vm = CreateVm();

        // ApiVerifiedVirtual is null (never connected) — the panel must say
        // so and stay red, matching the gate's fail-closed verdict.
        AssertCheck(vm, "Accounts", false, "0/1 verified");
        Assert.False(vm.IsReadyForReal);
        Assert.Equal("Accounts", vm.FirstBlocker);
    }

    [Fact]
    public void VerifiedRealAccount_UnarmedUnlock_FailsUnlockCheck()
    {
        var config = new AccountConfig { Label = "Real1", ApiToken = "t", IsDemo = false, BrainKey = "Growth" };
        var connection = _hub.AddAccount(config);
        connection.ApiVerifiedVirtual = false; // the API verified REAL

        var vm = CreateVm();

        AssertCheck(vm, "Accounts", true, "1 real account(s) verified");
        AssertCheck(vm, "Unlock", false, "locked");
        Assert.Equal("Unlock", vm.FirstBlocker);
    }

    [Fact]
    public void ArmingTheUnlock_TurnsUnlockCheckGreen_AndRefreshesFirstBlocker()
    {
        var config = new AccountConfig { Label = "Real1", ApiToken = "t", IsDemo = false, BrainKey = "Growth" };
        var connection = _hub.AddAccount(config);
        connection.ApiVerifiedVirtual = false;
        var vm = CreateVm();

        Assert.True(_hub.TryArmUnlock(config.Id)); // the panel's arm path
        vm.TestRebuildReadinessChecks(); // the panel's own post-arm rebuild

        AssertCheck(vm, "Unlock", true, "armed");

        // Complete the remaining dials (governor cap + webhook) — every
        // check green means ready, with no blocker line.
        vm.PortfolioDrawdownCapText = "2.50"; // the typed edit re-checks live
        _settings = new AppSettings { WebhookUrl = "https://discord.example/hook" };
        vm.TestRebuildReadinessChecks();

        Assert.True(vm.IsReadyForReal);
        Assert.Null(vm.FirstBlocker);
    }

    [Fact]
    public void GovernorLeg_TracksCapText_AndLatchedTrip()
    {
        // Fresh plan store: no cap configured — the governor leg is red.
        var vm = CreateVm();
        AssertCheck(vm, "Governor", false, "off");

        vm.PortfolioDrawdownCapText = "2.50"; // typed edit re-checks live
        AssertCheck(vm, "Governor", true, "cap active (2.50)");

        // A latched governor refuses every start until re-armed — the check
        // mirrors it even with the cap present.
        _hub.TestRaiseGovernorTripped(-3m);
        AssertCheck(vm, "Governor", false, "latched");
        Assert.False(vm.IsReadyForReal);
    }

    [Fact]
    public void WebhookLeg_TracksSettings()
    {
        AssertCheck(CreateVm(), "Webhook", false, "not configured");

        _settings = new AppSettings { WebhookUrl = "https://discord.example/hook" };
        AssertCheck(CreateVm(), "Webhook", true, "configured");
    }

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
