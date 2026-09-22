using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Controls;
using DongGfx.App.Services;
using DongGfx.Core;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DerivClient _client;
    private readonly MultiAccountHub? _hub;
    private readonly TradeStore? _trades;
    private readonly Dispatcher _dispatcher;
    private readonly Infrastructure.NotificationService? _notifications;
    private readonly Infrastructure.WebhookService? _webhook;

    [ObservableProperty]
    private string statusText = "Disconnected";

    /// <summary>
    /// Live growth-runner state: one line per running growth runner (its last
    /// activity), or an idle line when nothing trades. Kept current from the
    /// hub's events; the dashboard "Growth:" pill binds to this. The
    /// autonomous brain's state has its own pill (Brain tab VM).
    /// </summary>
    [ObservableProperty]
    private string growthStatus = "Idle — no growth runners";

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string symbolText = "—";

    [ObservableProperty]
    private string lastPriceText = "—";

    [ObservableProperty]
    private string tickCountText = "0 ticks";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isKillSwitchEngaged;

    /// <summary>True while the portfolio drawdown governor is latched after a trip.</summary>
    [ObservableProperty]
    private bool isGovernorLatched;

    /// <summary>True while the pre-trip warning is active (combined daily
    /// drawdown at 80% of the portfolio cap; cleared by a trip or re-arm).</summary>
    [ObservableProperty]
    private bool isGovernorWarned;

    /// <summary>Detail line of the last reported bankroll auto-publish
    /// failure (null while the publish path is healthy or not wired up) —
    /// a broken export must not hide behind silent retries: the trend
    /// page's money axis would silently freeze.</summary>
    [ObservableProperty]
    private string? bankrollPublishFailureText;

    /// <summary>True while the bankroll auto-publish is in a reported-failed
    /// state (edge-triggered by the publisher, not recomputed per refresh).</summary>
    public bool IsBankrollPublishFailing => BankrollPublishFailureText is not null;

    /// <summary>Combined net P&amp;L across all growth accounts (portfolio row).</summary>
    [ObservableProperty]
    private string combinedGrowthPnlText = "$0.00";

    /// <summary>
    /// The account chosen in the dashboard's growth drill-down; its session
    /// history, stakes and win streak are composed into AccountDetailsText.
    /// </summary>
    [ObservableProperty]
    private string? selectedAccount;

    partial void OnSelectedAccountChanged(string? value) => UpdateAccountDetails();

    /// <summary>One line per account that has growth history (drill-down choices).</summary>
    public ObservableCollection<string> AccountSummaries { get; } = new();

    /// <summary>True once any account has growth history or a running runner;
    /// gates the drill-down selector's visibility.</summary>
    public bool HasGrowthAccounts => AccountSummaries.Count > 0;

    /// <summary>The selected account's drill-down: session history, stakes,
    /// win streak — composed from the trade store.</summary>
    [ObservableProperty]
    private string accountDetailsText = "Select an account to see its session history.";

    /// <summary>Account display name → the store id its growth trades carry.</summary>
    private readonly Dictionary<string, Guid?> _growthAccountIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-account risk-rail summary lines for the dashboard card.</summary>
    public ObservableCollection<string> RiskRailAlerts { get; } = new();

    /// <summary>The live chart view (attached by MainWindow; VM stays UI-agnostic).</summary>
    public TickChartControl? Chart { get; set; }

    public DashboardViewModel(DerivClient client, MultiAccountHub? hub = null,
        Infrastructure.NotificationService? notifications = null,
        Infrastructure.WebhookService? webhook = null,
        TradeStore? trades = null)
    {
        _client = client;
        _hub = hub;
        _trades = trades;
        _notifications = notifications;
        _webhook = webhook;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _client.StatusChanged += OnStatusChanged;
        _client.TickReceived += OnTickReceived;
        _client.BalanceUpdated += OnBalanceUpdated;
        _client.ErrorReceived += OnErrorReceived;

        // New settlements refresh the drill-down for the selected account.
        if (_trades is not null)
        {
            _trades.TradeAdded += OnTradeAddedForDetails;
        }

        if (_hub is not null)
        {
            _hub.GrowthActivity += OnGrowthActivity;
            _hub.GrowthActivity += OnGrowthActivityForGrowthStatus;
            _hub.GrowthActivity += OnGrowthActivityForDetails;
            _hub.PortfolioGovernorTripped += OnGovernorTripped;
            _hub.PortfolioGovernorWarning += OnGovernorWarning;
            _hub.GovernorRearmed += OnGovernorRearmed;
            _hub.RestartStateChanged += OnRestartStateChanged;
            _hub.RestartStateChanged += OnRestartStateChangedForGrowthStatus;
            _hub.AccountsChanged += OnAccountsChangedForSummary;

            // A governor latch restored from the journal fires its event
            // before this VM exists — seed the rail and alert on launch.
            if (_hub.IsGovernorTripped)
            {
                IsGovernorLatched = true;
            }

            RefreshPortfolioSummary();
        }
    }

    /// <summary>
    /// Rebuilds GrowthStatus from the hub's growth runners: per runner, the
    /// account name, live P/L and progress toward the session target (from
    /// the session engine when one exists), falling back to the latest
    /// activity line otherwise — or a single idle line when none are running.
    /// Pure with respect to the hub snapshot, so it is unit-testable
    /// (GrowthActivity fires per brain cycle — this recomputes from state,
    /// never appends, so bursts cannot duplicate or corrupt the text).
    /// </summary>
    internal void UpdateGrowthStatusFromHub()
    {
        GrowthStatus = ComposeGrowthStatus(
            _hub?.AllRunners.Values.Select(r => r.Snapshot).ToArray());
    }

    /// <summary>
    /// Refreshes the growth drill-down after any growth activity line: new
    /// runners become selectable, and a selected runner's live session shows
    /// its running state. Cheap by design — one store read per refresh.
    /// </summary>
    private void OnGrowthActivityForDetails(Services.GrowthRunner _, string __) =>
        OnUiThread(() =>
        {
            RebuildAccountChoices();
            UpdateAccountDetails();
        });

    /// <summary>
    /// Rebuilds the selected account's drill-down text from the trade store:
    /// today's session (trade count, win streak, running P/L, current and
    /// peak stake), the last five sessions, and the running win rate.
    /// Sessions are delimited by the daily reset (the engine starts fresh at
    /// StartBudget each day), so history reads as one session per day —
    /// which is exactly how the runner itself scopes a bankroll session.
    /// </summary>
    internal void UpdateAccountDetails()
    {
        // All settled growth trades across accounts. ForAccount(null) would be
        // wrong here: it selects the primary (null-id) account, not "all".
        var allGrowth = (_trades?.Trades ?? [])
            .Where(t => t.Source == TradeSource.Growth)
            .ToArray();
        AccountDetailsText = ComposeAccountDetails(
            SelectedAccount, _growthAccountIds.GetValueOrDefault(SelectedAccount ?? ""), allGrowth);
    }

    /// <summary>Pure composer for the drill-down text; internal for tests.</summary>
    internal static string ComposeAccountDetails(
        string? account, Guid? accountId, IReadOnlyList<Trade> growthTrades)
    {
        if (account is null)
        {
            return "Select an account to see its session history.";
        }

        var mine = growthTrades
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.SettledAt)
            .ToArray();

        if (mine.Length == 0)
        {
            return $"{account}: no settled growth trades yet.";
        }

        var sb = new StringBuilder();
        sb.Append(account).Append(" — ");

        // Sessions: one per day (the engine's daily reset is the boundary).
        var sessions = mine
            .GroupBy(t => t.SettledAt.Date)
            .OrderByDescending(g => g.Key)
            .ToArray();

        var today = sessions[0].ToArray();
        var wins = today.Count(t => t.IsWin);
        var streak = 0;
        for (var i = today.Length - 1; i >= 0 && today[i].IsWin == today[^1].IsWin; i--)
        {
            streak++;
        }
        var streakText = today[^1].IsWin ? $"{streak}-win streak" : $"{streak}-loss streak";
        var pnl = today.Sum(t => t.Profit);
        var stakes = today.Select(t => t.Stake).ToArray();
        sb.Append($"today: {today.Length} trades · {wins}W/{today.Length - wins}L · {streakText} · ")
          .Append($"P/L {(pnl >= 0 ? "+" : "−")}${Math.Abs(pnl):0.00} · stake ${stakes.Min():0.##}–${stakes.Max():0.##}");

        // Last five prior sessions, most recent first.
        foreach (var s in sessions.Skip(1).Take(5))
        {
            var list = s.ToArray();
            var sPnl = list.Sum(t => t.Profit);
            sb.Append($"\n  {s.Key:MMM d}: {list.Length} trades · {list.Count(t => t.IsWin)}W/{list.Length - list.Count(t => t.IsWin)}L · ")
              .Append($"P/L {(sPnl >= 0 ? "+" : "−")}${Math.Abs(sPnl):0.00}");
        }

        var totalWins = mine.Count(t => t.IsWin);
        var winRate = (double)totalWins * 100 / mine.Length;
        sb.Append($"\n  running win rate: {winRate:0.#}% over {mine.Length} trades");
        return sb.ToString();
    }

    /// <summary>Pure composer for the growth pill text; internal for tests.</summary>
    internal static string ComposeGrowthStatus(GrowthRunnerSnapshot[]? runners)
    {
        var running = (runners ?? [])
            .Where(r => r.IsRunning)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (running.Length == 0)
        {
            return "Idle — no growth runners";
        }

        return string.Join("  •  ", running.Select(DescribeRunner));
    }

    private static string DescribeRunner(GrowthRunnerSnapshot r)
    {
        if (r.Bankroll is null || r.StartBankroll is null || r.Target is null)
        {
            // No engine yet (just started): the activity line is the best truth.
            return $"{r.Name}: {r.Activity}";
        }

        var bankroll = r.Bankroll.Value;
        var start = r.StartBankroll.Value;
        var target = r.Target.Value;

        var pnl = bankroll - start;
        var pnlText = pnl >= 0 ? $"+${pnl:0.##}" : $"-${Math.Abs(pnl):0.##}";

        var span = target - start;
        var pct = span <= 0
            ? 100m
            : Math.Clamp((bankroll - start) / span * 100m, 0m, 100m);

        var targetText = pct >= 100m ? "target reached" : $"{pct:0.#}% to target";
        return $"{r.Name} {pnlText} · ${bankroll:0.##} · {targetText}";
    }

    /// <summary>Refreshes the growth status after any growth activity line.</summary>
    private void OnGrowthActivityForGrowthStatus(Services.GrowthRunner _, string __) =>
        OnUiThread(UpdateGrowthStatusFromHub);

    /// <summary>Refreshes the drill-down choices after a settlement (a new
    /// account's first growth trade makes it selectable).</summary>
    private void OnTradeAddedForDetails(Trade trade)
    {
        if (trade.Source == TradeSource.Growth)
        {
            OnUiThread(() =>
            {
                RebuildAccountChoices();
                UpdateAccountDetails();
            });
        }
    }

    /// <summary>Refreshes the growth status when a runner starts or stops.</summary>
    private void OnRestartStateChangedForGrowthStatus(Guid _, int __, bool ___) =>
        OnUiThread(UpdateGrowthStatusFromHub);

    private void OnAccountsChangedForSummary() => OnUiThread(RefreshPortfolioSummary);

    /// <summary>
    /// Rebuilds the drill-down account list: every growth runner plus every
    /// account that has settled growth trades in the store, with its store id.
    /// Auto-selects the first account once any exist, so the panel is alive
    /// without a manual click.
    /// </summary>
    private void RebuildAccountChoices()
    {
        _growthAccountIds.Clear();

        foreach (var runner in _hub?.AllRunners.Values ?? Enumerable.Empty<Services.GrowthRunner>())
        {
            _growthAccountIds.TryAdd(runner.Connection.DisplayName, runner.Connection.Config.Id);
        }

        foreach (var trade in _trades?.Trades ?? Enumerable.Empty<Trade>())
        {
            if (trade.Source == TradeSource.Growth && trade.AccountName is not null)
            {
                _growthAccountIds.TryAdd(trade.AccountName, trade.AccountId);
            }
        }

        var names = _growthAccountIds.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.SequenceEqual(AccountSummaries, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        AccountSummaries.Clear();
        foreach (var name in names)
        {
            AccountSummaries.Add(name);
        }

        OnPropertyChanged(nameof(HasGrowthAccounts));
        SelectedAccount ??= names.FirstOrDefault();
    }

    private void OnGrowthActivity(Services.GrowthRunner _, string __) =>
        OnUiThread(RefreshPortfolioSummary);

    private void OnGovernorTripped(decimal net) => OnUiThread(() =>
    {
        IsGovernorLatched = true;
        IsGovernorWarned = false;

        // Cross-venue: a governor trip is a portfolio-level stop — the FX
        // brain halts and MT5 flattens too (same semantics as the kill switch).
        try
        {
            _ = Ioc.Default.GetRequiredService<TerminalViewModel>().FxEmergencyFlattenAsync("governor trip");
        }
        catch (InvalidOperationException)
        {
            // no TerminalViewModel registered (tests) — hub already stopped Deriv-side runners
        }

        RefreshPortfolioSummary();
    });

    private void OnGovernorWarning(decimal used) => OnUiThread(() =>
    {
        IsGovernorWarned = true;
        RefreshPortfolioSummary();
    });

    private void OnGovernorRearmed() => OnUiThread(() =>
    {
        IsGovernorLatched = false;
        IsGovernorWarned = false;
        RefreshPortfolioSummary();
    });

    private void OnRestartStateChanged(Guid _, int __, bool ___) =>
        OnUiThread(RefreshPortfolioSummary);

    /// <summary>Bankroll auto-publish broke: latch the rail alert (the rail
    /// change machinery fires the toast + webhook so the user is alerted no
    /// matter which tab they are on). Any-thread safe — the publisher raises
    /// from its timer thread.</summary>
    internal void OnBankrollPublishFailed(string detail) => OnUiThread(() =>
    {
        BankrollPublishFailureText =
            $"Bankroll export not publishing — the trend page's money axis is stale ({detail})";
        RefreshPortfolioSummary();
    });

    /// <summary>Bankroll auto-publish recovered after a reported failure:
    /// release the rail alert (the rail machinery posts the all-clear).</summary>
    internal void OnBankrollPublishRecovered(string detail) => OnUiThread(() =>
    {
        BankrollPublishFailureText = null;
        RefreshPortfolioSummary();
    });

    /// <summary>
    /// Rebuilds the dashboard's portfolio summary: combined growth P&amp;L
    /// plus one alert line per latched risk rail (governor, kill switch,
    /// account circuit breakers, exhausted auto-restarts). Any rail that
    /// newly engages fires a toast and a webhook so the user is alerted no
    /// matter which tab is open — not just for the governor.
    /// </summary>
    private void RefreshPortfolioSummary()
    {
        if (_hub is null)
        {
            return;
        }

        var net = _hub.CombinedGrowthNetPnl();
        CombinedGrowthPnlText = $"{(net >= 0 ? "+" : "−")}${Math.Abs(net):0.00}";

        // This runs on every growth activity line (each brain cycle) — skip
        // the rebuild (and its flicker) unless the rails actually changed.
        var alerts = BuildRiskRailAlerts();
        if (alerts.SequenceEqual(RiskRailAlerts))
        {
            return;
        }

        var previous = RiskRailAlerts.ToList();

        RiskRailAlerts.Clear();
        foreach (var alert in alerts)
        {
            RiskRailAlerts.Add(alert);
        }

        NotifyOnRiskRailChange(previous, alerts);

        // Same feed keeps the dashboard's growth-status line current.
        UpdateGrowthStatusFromHub();
        RebuildAccountChoices();
        UpdateAccountDetails();
    }

    /// <summary>Builds one alert line per currently latched risk rail.</summary>
    private List<string> BuildRiskRailAlerts()
    {
        var alerts = new List<string>();
        if (IsGovernorLatched)
        {
            alerts.Add("Portfolio drawdown governor latched — re-arm on the Growth tab");
        }
        else if (IsGovernorWarned)
        {
            alerts.Add("Portfolio drawdown warning — 80% of the governor cap used");
        }

        if (IsKillSwitchEngaged)
        {
            alerts.Add("Global kill switch engaged");
        }

        // Bankroll auto-publish failure is edge-triggered (latched on the
        // publisher's failure event, released on recovery), not recomputed —
        // a missing git remote is not visible in any state RefreshPortfolioSummary polls.
        if (BankrollPublishFailureText is { } publishFailure)
        {
            alerts.Add(publishFailure);
        }

        if (_hub is null)
        {
            return alerts;
        }

        foreach (var connection in _hub.Accounts)
        {
            if (connection.IsDegraded)
            {
                alerts.Add($"{connection.DisplayName}: circuit breaker open");
            }

            if (_hub.IsGivenUp(connection.Config.Id))
            {
                alerts.Add($"{connection.DisplayName}: auto-restarts exhausted — restart on the Growth tab");
            }
        }

        return alerts;
    }

    /// <summary>Fires the toast/webhook alerts for rails that newly engaged
    /// (and one all-clear when the last rail releases).</summary>
    private void NotifyOnRiskRailChange(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var engaged = current.Where(a => !previous.Contains(a)).ToList();
        if (engaged.Count > 0)
        {
            var change = string.Join("; ", engaged);
            var summary = current.Count == 0 ? "no rails latched" : string.Join("; ", current);
            _notifications?.NotifyRiskRailEngaged(change, summary);
            _webhook?.PostRiskRail("⚠ Risk rail engaged", $"{change} — {summary}");
        }
        else if (previous.Count > 0 && current.Count == 0)
        {
            _webhook?.PostRiskRail("✅ Risk rails clear", "all latched risk rails have been released");
        }
    }

    public void SetSymbol(string symbol) => OnUiThread(() => SymbolText = symbol);

    public void AddHistory(IReadOnlyList<Tick> ticks)
    {
        OnUiThread(() =>
        {
            Chart?.Clear();
            if (ticks.Count > 0)
            {
                Chart?.AddTicks(ticks);
                LastPriceText = ticks[^1].Quote.ToString("0.00000");
                TickCountText = $"{ticks.Count} ticks";
            }
        });
    }

    [RelayCommand]
    private void ToggleKillSwitch()
    {
        IsKillSwitchEngaged = !IsKillSwitchEngaged;
        if (IsKillSwitchEngaged)
        {
            StatusText = "KILL SWITCH ENGAGED — all feeds stopped";
            _ = _client.DisconnectAsync();

            // Halt every account connection and growth runner immediately.
            if (_hub is not null)
            {
                _ = _hub.DisconnectAllAsync();
            }

            // Cross-venue: stop the FX brain and flatten every open MT5
            // position so the kill switch covers the MT5 leg too.
            // (Ioc-guarded: tests construct this VM without the container.)
            try
            {
                _ = Ioc.Default.GetRequiredService<TerminalViewModel>().FxEmergencyFlattenAsync("kill switch");
            }
            catch (InvalidOperationException)
            {
                // no TerminalViewModel registered (tests) — Deriv-side stop above still applies
            }
        }
        else
        {
            StatusText = "Disconnected — press Connect to resume";
        }

        RefreshPortfolioSummary();
    }

    private void OnStatusChanged(ConnectionStatus status) => OnUiThread(() =>
    {
        if (IsKillSwitchEngaged)
        {
            return;
        }

        IsConnected = status is ConnectionStatus.Connected or ConnectionStatus.Reconnecting;
        StatusText = status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Reconnecting => "Reconnecting…",
            ConnectionStatus.Error => "Error",
            _ => "Disconnected"
        };
    });

    private int _tickCount;

    private void OnTickReceived(Tick tick) => OnUiThread(() =>
    {
        LastPriceText = tick.Quote.ToString("0.00000");
        _tickCount++;
        TickCountText = $"{_tickCount} ticks";
        Chart?.AddTicks(new[] { tick });
    });

    private void OnBalanceUpdated(AccountBalance balance) => OnUiThread(() =>
    {
        BalanceText = $"{balance.Balance:0.##} {balance.Currency}";
        if (!string.IsNullOrEmpty(balance.LoginId))
        {
            SymbolText = SymbolText == "—" ? balance.LoginId : $"{SymbolText} · {balance.LoginId}";
        }
    });

    private void OnErrorReceived(string message) => OnUiThread(() =>
    {
        if (message.StartsWith("Connection lost"))
        {
            StatusText = message;
        }
    });

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}