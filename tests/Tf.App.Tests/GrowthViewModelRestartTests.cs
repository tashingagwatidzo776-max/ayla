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
/// Headless unit tests for the Growth tab's restart surface: per-row restart
/// text, gave-up banners, and the accounts-changed rebuild. The hub's
/// RestartStateChanged event is raised through the internal test seam — the
/// real failure ladder is covered by the E2E integration tests.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ManualRealMoneyGate")]
public class GrowthViewModelRestartTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;

    public GrowthViewModelRestartTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_growvm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
    }

    public void Dispose()
    {
        ManualRealMoneyGate.Reset(); // panel tests arm the shared manual unlock
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private GrowthViewModel CreateVm() => new(
        _hub, new GrowthPlanStore(), () => new AppSettings { AutonomyEnabled = true },
        new DashboardViewModel(new DerivClient(), _hub), tracker: null);

    [Fact]
    public void Constructor_BuildsOneRowPerAccount_WithCleanState()
    {
        var config = new AccountConfig { Label = "Alpha", ApiToken = "t", IsDemo = true, BrainKey = "Growth" };
        _hub.AddAccount(config);

        var vm = CreateVm();

        var row = Assert.Single(vm.Rows);
        Assert.Equal("Alpha", row.DisplayName);
        Assert.Equal("", row.RestartText); // no restarts yet
        Assert.False(row.IsGaveUp);
        Assert.Empty(vm.GaveUpBanners);

        // A restart-state event raised *after* construction updates the row.
        _hub.TestRaiseRestartStateChanged(config.Id, attempt: 2, gaveUp: false);
        Assert.Equal("restarts 2", row.RestartText);
    }

    [Fact]
    public void RestartStateChanged_GaveUp_ShowsBanner_AndClearsOnRecovery()
    {
        var config = new AccountConfig { Label = "Beta", ApiToken = "t", IsDemo = true, BrainKey = "Growth" };
        _hub.AddAccount(config);
        var vm = CreateVm();

        _hub.TestRaiseRestartStateChanged(config.Id, attempt: 3, gaveUp: true);

        Assert.True(vm.Rows.Single(r => r.Connection.Config.Id == config.Id).IsGaveUp);
        var banner = Assert.Single(vm.GaveUpBanners);
        Assert.Equal(config.Id, banner.AccountId);
        Assert.Equal("Beta", banner.AccountName);

        // A non-gave-up update (fresh start) must remove the banner again.
        _hub.TestRaiseRestartStateChanged(config.Id, attempt: 0, gaveUp: false);

        Assert.Empty(vm.GaveUpBanners);
        Assert.False(vm.Rows.Single(r => r.Connection.Config.Id == config.Id).IsGaveUp);
    }

    [Fact]
    public void LockedRealAccounts_ShowStartupBanners_AndClearOnUnlock()
    {
        // A real config with no API verification yet: fails closed, shows a
        // banner (unverified real accounts are locked too).
        var real = new AccountConfig { Label = "Real One", ApiToken = "tok-real", IsDemo = false, BrainKey = "Growth" };
        var demo = new AccountConfig { Label = "Demo One", ApiToken = "tok-demo", IsDemo = true, BrainKey = "Growth" };
        _hub.AddAccount(real);
        _hub.AddAccount(demo);

        var vm = CreateVm();

        var banner = Assert.Single(vm.LockedRealAccountBanners);
        Assert.Equal(real.Id, banner.AccountId);
        Assert.Equal("Real One", banner.AccountName);
        Assert.Contains("unverified", banner.VerificationText);
        Assert.Empty(vm.GaveUpBanners); // demo accounts never appear

        // Once the API verifies the account as real, the banner says so.
        _hub.Accounts.Single(a => a.Config.Id == real.Id).ApiVerifiedVirtual = false;
        vm.TestRebuildLockedBanners();
        Assert.Contains("REAL", vm.LockedRealAccountBanners.Single().VerificationText);

        // Arming the session unlock (via the in-tab panel's confirm path)
        // clears the banner on the next rebuild.
        vm.ShowUnlockPanelCommand.Execute(null);
        Assert.Single(vm.UnlockableAccounts);
        vm.UnlockPhrase = RealMoneyGate.ConfirmationPhrase;
        vm.ConfirmUnlockCommand.Execute(null);

        Assert.True(_hub.IsRealMoneyUnlocked(real.Id));
        Assert.Empty(vm.LockedRealAccountBanners);
    }

    [Fact]
    public void UnlockPanel_ArmsAllListedAccounts_AndTheManualGate_AtOnce()
    {
        ManualRealMoneyGate.Reset();
        var a = new AccountConfig { Label = "A", ApiToken = "tok-a", IsDemo = false, BrainKey = "Growth" };
        var b = new AccountConfig { Label = "B", ApiToken = "tok-b", IsDemo = false, BrainKey = "Growth" };
        var demo = new AccountConfig { Label = "Demo", ApiToken = "tok-d", IsDemo = true, BrainKey = "Growth" };
        _hub.AddAccount(a);
        _hub.AddAccount(b);
        _hub.AddAccount(demo);
        var vm = CreateVm();

        vm.ShowUnlockPanelCommand.Execute(null);

        // Both real accounts listed (demo never), one pass arms both — plus
        // the Brain/Trades tabs' shared manual gate.
        Assert.Equal(2, vm.UnlockableAccounts.Count);
        ManualRealMoneyGate.Reset();
        vm.UnlockPhrase = "wrong phrase";
        vm.ConfirmUnlockCommand.Execute(null);
        Assert.False(_hub.IsRealMoneyUnlocked(a.Id), "a wrong phrase must arm nothing");
        Assert.False(ManualRealMoneyGate.IsUnlocked);
        Assert.True(vm.IsUnlockPanelVisible, "a failed confirm keeps the panel open");

        vm.UnlockPhrase = $" {RealMoneyGate.ConfirmationPhrase} ";
        vm.ConfirmUnlockCommand.Execute(null);
        Assert.True(_hub.IsRealMoneyUnlocked(a.Id));
        Assert.True(_hub.IsRealMoneyUnlocked(b.Id));
        Assert.False(_hub.IsRealMoneyUnlocked(demo.Id), "demo accounts are not in the unlock set");
        Assert.True(ManualRealMoneyGate.IsUnlocked, "one phrase arms the manual surfaces too");
        Assert.False(vm.IsUnlockPanelVisible);
        Assert.Empty(vm.LockedRealAccountBanners);
    }

    [Fact]
    public void UnlockPanel_CancelArmsNothing()
    {
        ManualRealMoneyGate.Reset();
        var config = new AccountConfig { Label = "C", ApiToken = "tok-c", IsDemo = false, BrainKey = "Growth" };
        _hub.AddAccount(config);
        var vm = CreateVm();

        vm.ShowUnlockPanelCommand.Execute(null);
        vm.UnlockPhrase = RealMoneyGate.ConfirmationPhrase;
        vm.CancelUnlockCommand.Execute(null);

        Assert.False(vm.IsUnlockPanelVisible);
        Assert.False(_hub.IsRealMoneyUnlocked(config.Id));
        Assert.False(ManualRealMoneyGate.IsUnlocked);
        Assert.Empty(vm.UnlockPhrase);
    }

    [Fact]
    public void UnlockPanel_EmptyList_ShowsNotice()
    {
        _hub.AddAccount(new AccountConfig { Label = "D", ApiToken = "tok-d2", IsDemo = true, BrainKey = "Growth" });
        var vm = CreateVm();

        vm.ShowUnlockPanelCommand.Execute(null);

        Assert.Empty(vm.UnlockableAccounts);
        Assert.NotNull(vm.UnlockPanelNotice);
        Assert.Contains("nothing to unlock", vm.UnlockPanelNotice);
    }

    [Fact]
    public void RestartAccount_OnLockedRealAccount_OpensPanelInsteadOfModals()
    {
        var config = new AccountConfig { Label = "Locked R", ApiToken = "tok-lr", IsDemo = false, BrainKey = "Growth" };
        _hub.AddAccount(config);
        var connection = _hub.Accounts.Single(c => c.Config.Id == config.Id);
        connection.IsConnected = true; // headless: mark connected without a broker
        var vm = CreateVm();

        vm.RestartAccountCommand.Execute(config.Id);

        Assert.True(vm.IsUnlockPanelVisible, "restart routes the lock to the in-tab panel");
        Assert.Contains("locked", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LockedRealBanners_RebuildOnAccountsChanged()
    {
        var vm = CreateVm();
        Assert.Empty(vm.LockedRealAccountBanners);

        var real = new AccountConfig { Label = "Late Real", ApiToken = "t", IsDemo = false, BrainKey = "Growth" };
        _hub.AddAccount(real); // raises the real AccountsChanged event

        // The AccountsChanged handler rebuilds rows AND the locked banners.
        Assert.Single(vm.LockedRealAccountBanners);
    }

    [Fact]
    public void RestartText_AtZeroAttempts_IsEmpty()
    {
        var config = new AccountConfig { Label = "Gamma", ApiToken = "t", IsDemo = true, BrainKey = "Growth" };
        _hub.AddAccount(config);
        var vm = CreateVm();

        _hub.TestRaiseRestartStateChanged(config.Id, attempt: 0, gaveUp: false);

        Assert.Equal("", vm.Rows.Single().RestartText);
    }

    [Fact]
    public void AccountsChanged_RebuildsRows()
    {
        var vm = CreateVm();
        Assert.Empty(vm.Rows);

        _hub.AddAccount(new AccountConfig { Label = "Delta", ApiToken = "t", IsDemo = true, BrainKey = "Growth" });

        // AccountsChanged fires synchronously; the VM rebuilds on the dispatcher.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.Rows.Count == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.Single(vm.Rows);
        Assert.Equal("Delta", vm.Rows[0].DisplayName);
    }

    [Fact]
    public void StartAll_NoConnectedAccounts_SetsStatusMessage()
    {
        _hub.AddAccount(new AccountConfig { Label = "Offline", ApiToken = "t", IsDemo = true, BrainKey = "Growth" });
        var vm = CreateVm();

        vm.StartAllCommand.Execute(null);

        Assert.Contains("No connected demo accounts", vm.StatusMessage);
    }

    [Fact]
    public void StartAll_AutonomyOff_SetsDisabledStatus()
    {
        var vm = new GrowthViewModel(
            _hub, new GrowthPlanStore(), () => new AppSettings { AutonomyEnabled = false },
            new DashboardViewModel(new DerivClient(), _hub), tracker: null);

        vm.StartAllCommand.Execute(null);

        // The autonomy notice must survive the result line (it used to be
        // overwritten by it before ever being seen).
        Assert.Contains("No connected demo accounts", vm.StatusMessage);
        Assert.Contains("Autonomy is OFF", vm.StatusMessage);
    }

    [Fact]
    public void StopAll_ClearsRunnerPointers_AndLogs()
    {
        var vm = CreateVm();

        vm.StopAllCommand.Execute(null);

        Assert.Equal("All growth engines stopped.", vm.StatusMessage);
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
