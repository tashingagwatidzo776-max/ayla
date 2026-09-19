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
/// Headless tests for the dashboard's growth drill-down: the account picker
/// fed by running runners and the trade store, and the per-account session
/// history (today's W/L, trailing streak, P/L, stake range, last sessions,
/// running win rate) composed purely from store trades. Sessions are
/// delimited by the daily reset, matching how the runner scopes a session.
/// </summary>
[Trait("Category", "Unit")]
public class DashboardDrillDownTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;
    private readonly DashboardViewModel _dashboard;

    public DashboardDrillDownTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_drilldown_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
        _dashboard = new DashboardViewModel(new DerivClient(), _hub, trades: _store);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Trade GrowthTrade(decimal profit, string accountName, Guid? accountId,
        decimal stake = 1.00m, DateTimeOffset? settledAt = null) => new(
        Guid.NewGuid(), "frxEURUSD",
        profit >= 0 ? Direction.Rise : Direction.Fall,
        stake, "USD", 1.17, 1700000300, $"C-{Guid.NewGuid():N}",
        profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
        profit, 1.165, 1700000600, settledAt ?? Noon(),
        accountId, accountName, TradeSource.Growth);

    /// <summary>Noon UTC anchors timestamps mid-day so a test run near
    /// 00:00 UTC can never split "today" across two dates.</summary>
    private static DateTimeOffset Noon(int daysAgo = 0) =>
        DateTimeOffset.UtcNow.Date.AddDays(-daysAgo).AddHours(12);

    [Fact]
    public void NoSelection_ShowsPrompt()
    {
        Assert.Equal("Select an account to see its session history.",
            DashboardViewModel.ComposeAccountDetails(null, null, []));
    }

    [Fact]
    public void SelectedAccountWithoutTrades_ShowsEmptyMessage()
    {
        var text = DashboardViewModel.ComposeAccountDetails("Acct", Guid.NewGuid(), []);
        Assert.Contains("Acct", text);
        Assert.Contains("no settled growth trades yet", text);
    }

    [Fact]
    public void TodaySummary_ShowsCountsStreakPnlAndStakeRange()
    {
        var id = Guid.NewGuid();
        var trades = new[]
        {
            GrowthTrade(1.00m, "Acct", id, stake: 1.00m, settledAt: Noon()),
            GrowthTrade(2.00m, "Acct", id, stake: 1.50m, settledAt: Noon().AddMinutes(-5)),
            GrowthTrade(-0.50m, "Acct", id, stake: 0.50m, settledAt: Noon().AddMinutes(-10)),
        };

        var text = DashboardViewModel.ComposeAccountDetails("Acct", id, trades);

        Assert.Contains("today: 3 trades", text);
        Assert.Contains("2W/1L", text);
        Assert.Contains("2-win streak", text); // the two most recent settle as wins
        Assert.Contains("+$2.50", text);
        Assert.Contains("stake $0.5–$1.5", text);
    }

    [Fact]
    public void TrailingLosses_ShowAsLossStreak()
    {
        var id = Guid.NewGuid();
        var trades = new[]
        {
            GrowthTrade(-1.00m, "Acct", id, settledAt: Noon()),          // most recent
            GrowthTrade(1.00m, "Acct", id, settledAt: Noon().AddMinutes(-5)),
        };

        var text = DashboardViewModel.ComposeAccountDetails("Acct", id, trades);

        Assert.Contains("1-loss streak", text);
    }

    [Fact]
    public void PriorSessions_ListedNewestFirst_WithWinRate()
    {
        var id = Guid.NewGuid();
        var trades = new[]
        {
            GrowthTrade(1.00m, "Acct", id, settledAt: Noon()),
            GrowthTrade(2.00m, "Acct", id, settledAt: Noon(1)),
            GrowthTrade(-1.00m, "Acct", id, settledAt: Noon(1).AddHours(-1)),
        };

        var text = DashboardViewModel.ComposeAccountDetails("Acct", id, trades);

        Assert.Contains("today: 1 trades", text);
        Assert.Contains("2 trades", text);          // yesterday's session line
        Assert.Contains("P/L +$1.00", text);        // yesterday: +2 -1
        Assert.Contains("running win rate: 66.7% over 3 trades", text);
    }

    [Fact]
    public void OtherAccounts_TradesAreNotMixedIn()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var trades = new[]
        {
            GrowthTrade(1.00m, "Acct", mine),
            GrowthTrade(50.00m, "Other", other),
        };

        var text = DashboardViewModel.ComposeAccountDetails("Acct", mine, trades);

        Assert.Contains("1 trades", text);
        Assert.Contains("+$1", text);
        Assert.DoesNotContain("$50", text);
    }

    [Fact]
    public void NullIdAccount_MatchesOnlyNullIdTrades()
    {
        // The single-account store uses null ids; a null-id selection must
        // match them but never the multi-account id-tagged trades.
        var trades = new[]
        {
            GrowthTrade(1.00m, "Solo", null),
            GrowthTrade(2.00m, "Tagged", Guid.NewGuid()),
        };

        var text = DashboardViewModel.ComposeAccountDetails("Solo", null, trades);

        Assert.Contains("1 trades", text);
        Assert.Contains("+$1", text);
    }

    [Fact]
    public void ManualTrades_DoNotCreateChoices_ButGrowthTradesDo()
    {
        _store.Add(GrowthTrade(1.00m, "Growth Acct", Guid.NewGuid(), settledAt: Noon()));
        _store.Add(new Trade(Guid.NewGuid(), "frxEURUSD", Direction.Rise, 1.00m, "USD",
            1.17, 1700000300, $"C-{Guid.NewGuid():N}", ContractStatus.Won, 1.00m,
            1.165, 1700000600, Noon(),
            Guid.NewGuid(), "Manual Acct", TradeSource.Manual));

        Assert.Contains("Growth Acct", _dashboard.AccountSummaries);
        Assert.DoesNotContain("Manual Acct", _dashboard.AccountSummaries);
        Assert.True(_dashboard.HasGrowthAccounts);
        Assert.Equal("Growth Acct", _dashboard.SelectedAccount); // auto-selected
        Assert.Contains("today: 1 trades", _dashboard.AccountDetailsText);
    }

    [Fact]
    public void RunnerBecomesSelectable_EvenWithoutTrades()
    {
        var connection = _hub.AddAccount(new AccountConfig
        {
            Label = "Runner Acct", ApiToken = "tok", IsDemo = true, BrainKey = "Growth"
        });
        var runner = new GrowthRunner(connection, _store,
            () => new AppSettings(), () => false, _journal);
        _hub.ObserveRunner(runner);
        _hub.TestRaiseGrowthActivity(runner, "Session Runner Acct: starting");

        Assert.Contains("Runner Acct", _dashboard.AccountSummaries);
        Assert.Equal("Runner Acct", _dashboard.SelectedAccount);
        Assert.Contains("no settled growth trades yet", _dashboard.AccountDetailsText);
    }

    [Fact]
    public void NewSettlement_RefreshesDetailsForSelectedAccount()
    {
        var id = Guid.NewGuid();
        _store.Add(GrowthTrade(1.00m, "Acct", id, settledAt: Noon()));
        Assert.Contains("+$1", _dashboard.AccountDetailsText);

        // The next settlement flows through TradeAdded and the details grow.
        _store.Add(GrowthTrade(2.00m, "Acct", id, settledAt: Noon()));

        Assert.Contains("+$3", _dashboard.AccountDetailsText);
    }

    [Fact]
    public void NoTradesAndNoRunners_HasGrowthAccountsFalse()
    {
        Assert.False(_dashboard.HasGrowthAccounts);
        Assert.Empty(_dashboard.AccountSummaries);
    }

    private sealed class MemoryVault : IAccountVault
    {
        public IReadOnlyList<AccountConfig> Load() => new List<AccountConfig>();
        public void Save(IReadOnlyList<AccountConfig> configs) { }
    }
}
