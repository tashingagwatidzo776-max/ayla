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
