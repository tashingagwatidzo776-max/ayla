using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Controls;
using Tf.App.Services;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DerivClient _client;
    private readonly MultiAccountHub? _hub;
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

    /// <summary>Combined net P&amp;L across all growth accounts (portfolio row).</summary>
    [ObservableProperty]
    private string combinedGrowthPnlText = "$0.00";

    /// <summary>Per-account risk-rail summary lines for the dashboard card.</summary>
    public ObservableCollection<string> RiskRailAlerts { get; } = new();

    /// <summary>The live chart view (attached by MainWindow; VM stays UI-agnostic).</summary>
    public TickChartControl? Chart { get; set; }

    public DashboardViewModel(DerivClient client, MultiAccountHub? hub = null,
        Infrastructure.NotificationService? notifications = null,
        Infrastructure.WebhookService? webhook = null)
    {
        _client = client;
        _hub = hub;
        _notifications = notifications;
        _webhook = webhook;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _client.StatusChanged += OnStatusChanged;
        _client.TickReceived += OnTickReceived;
        _client.BalanceUpdated += OnBalanceUpdated;
        _client.ErrorReceived += OnErrorReceived;

        if (_hub is not null)
        {
            _hub.GrowthActivity += OnGrowthActivity;
            _hub.GrowthActivity += OnGrowthActivityForGrowthStatus;
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

    /// <summary>Refreshes the growth status when a runner starts or stops.</summary>
    private void OnRestartStateChangedForGrowthStatus(Guid _, int __, bool ___) =>
        OnUiThread(UpdateGrowthStatusFromHub);

    private void OnAccountsChangedForSummary() => OnUiThread(RefreshPortfolioSummary);

    private void OnGrowthActivity(Services.GrowthRunner _, string __) =>
        OnUiThread(RefreshPortfolioSummary);

    private void OnGovernorTripped(decimal net) => OnUiThread(() =>
    {
        IsGovernorLatched = true;
        IsGovernorWarned = false;
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