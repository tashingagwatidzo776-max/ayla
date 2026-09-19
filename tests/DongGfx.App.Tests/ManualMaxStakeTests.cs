using System.IO;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Tests;

/// <summary>
/// The ManualMaxStake ceiling: the Trades tab's manual trade is the one
/// path that bypasses the risk engine's stake checks (the brain and growth
/// paths are bounded by MaxStake and the session plan ladder), so the user
/// ceiling must refuse an over-cap stake BEFORE any proposal is requested —
/// a mistyped stake must not reach a real account.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class ManualMaxStakeTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;

    public ManualMaxStakeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_mcap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TradesViewModel CreateVm(Func<AppSettings> settings) => new(
        new DerivClient(), _store, settings, new DashboardViewModel(new DerivClient()));

    [Fact]
    public async Task ManualTrade_RefusesStakeAboveTheCeiling_BeforeAnyNetworkCall()
    {
        var vm = CreateVm(() => new AppSettings
        {
            IsDemo = true, Stake = 10m, ManualMaxStake = 5m,
            Symbol = "frxEURUSD", Currency = "USD", DurationMinutes = 5
        });

        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);

        // The cap fires before the connectivity guard — the client in this
        // test is not even connected, proving no network leg ran.
        Assert.Contains("manual max", vm.StatusMessage);
        Assert.Contains("5", vm.StatusMessage);
        Assert.Empty(_store.Trades);
    }

    [Fact]
    public async Task ManualTrade_AtOrBelowTheCeiling_PassesTheCapCheck()
    {
        var vm = CreateVm(() => new AppSettings
        {
            IsDemo = true, Stake = 5m, ManualMaxStake = 5m, // exactly at cap
            Symbol = "frxEURUSD", Currency = "USD", DurationMinutes = 5
        });

        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);

        // The next guard is the API token — reaching it proves the cap
        // checked and passed.
        Assert.Contains("No API token", vm.StatusMessage);
    }

    [Fact]
    public async Task ManualTrade_ZeroCeiling_DisablesTheExtraLimit()
    {
        var vm = CreateVm(() => new AppSettings
        {
            IsDemo = true, Stake = 10m, ManualMaxStake = 0m, // 0 = no extra limit
            Symbol = "frxEURUSD", Currency = "USD", DurationMinutes = 5
        });

        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);

        Assert.Contains("No API token", vm.StatusMessage); // cap did not block
    }

    [Fact]
    public void Settings_RoundTrip_NullsAndClampsTheCeiling()
    {
        var settingsService = new Infrastructure.SettingsService();
        var vm = new SettingsViewModel(settingsService, new DerivClient(),
            new DashboardViewModel(new DerivClient()));

        // A stored 0 (disabled) edits as an empty box and stays 0.
        vm.Load(new AppSettings { ManualMaxStake = 0m });
        Assert.Null(vm.ManualMaxStake);
        Assert.Equal(0m, vm.BuildSettings().ManualMaxStake);

        // A stored value edits as a number and round-trips.
        vm.Load(new AppSettings { ManualMaxStake = 7.5m });
        Assert.Equal(7.5m, vm.ManualMaxStake);
        Assert.Equal(7.5m, vm.BuildSettings().ManualMaxStake);

        // A negative typed by the user is dropped on build.
        vm.ManualMaxStake = -3m;
        Assert.Equal(0m, vm.BuildSettings().ManualMaxStake);
    }
}
