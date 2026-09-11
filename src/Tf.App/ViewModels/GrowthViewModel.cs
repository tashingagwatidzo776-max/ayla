using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.App.ViewModels;

/// <summary>
/// The deterministic Growth brain fleet. Each connected demo account runs its
/// own session that aims to grow a $5-equivalent bankroll: small fixed-risk
/// stakes, a short recovery ladder, a daily profit target, and a hard floor.
/// </summary>
public sealed partial class GrowthViewModel : ObservableObject
{
    private readonly MultiAccountHub _hub;
    private readonly GrowthPlanStore _planStore;
    private readonly Func<AppSettings> _settings;
    private readonly DashboardViewModel _dashboard;
    private readonly PerformanceTracker? _tracker;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private decimal budget = 5.00m;

    [ObservableProperty]
    private int riskPercent = 20;

    [ObservableProperty]
    private int targetPercent = 100;

    [ObservableProperty]
    private int floorPercent = 40;

    [ObservableProperty]
    private int recoverySteps = 3;

    [ObservableProperty]
    private int intervalMinutes = 1;

    [ObservableProperty]
    private int failureBackoffSeconds = 5;

    [ObservableProperty]
    private int maxAutoRestarts = 3;

    [ObservableProperty]
    private double restartBaseDelaySeconds = 5.0;

    [ObservableProperty]
    private double restartBackoffFactor = 3.0;

    [ObservableProperty]
    private string portfolioDrawdownCapText = "";

    /// <summary>Combined net P&amp;L across every growth account, for the portfolio row.</summary>
    [ObservableProperty]
    private string combinedPnlText = "$0.00";

    /// <summary>True while the portfolio drawdown governor is latched after a trip.</summary>
    [ObservableProperty]
    private bool isGovernorTripped;

    /// <summary>Banner text shown while the governor is latched (empty = hidden).</summary>
    [ObservableProperty]
    private string governorBannerText = "";

    [ObservableProperty]
    private string statusMessage = "Growth plan loaded.";

    public ObservableCollection<GrowthRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();

    /// <summary>One banner per account whose auto-restart budget is exhausted.</summary>
    public ObservableCollection<GaveUpBanner> GaveUpBanners { get; } = new();

    public GrowthViewModel(MultiAccountHub hub, GrowthPlanStore planStore,
        Func<AppSettings> settings, DashboardViewModel dashboard,
        PerformanceTracker? tracker = null)
    {
        _hub = hub;
        _planStore = planStore;
        _settings = settings;
        _dashboard = dashboard;
        _tracker = tracker;
        _dispatcher = Dispatcher.CurrentDispatcher;

        LoadPlan();
        RebuildRows();

        _hub.AccountsChanged += OnAccountsChanged;
        _hub.GrowthActivity += OnGrowthActivity;
        _hub.GrowthActivity += OnPortfolioRefresh;
        _hub.RestartStateChanged += OnRestartStateChanged;
        _hub.PortfolioGovernorTripped += OnGovernorTripped;
        _hub.GovernorRearmed += OnGovernorRearmed;
        RefreshPortfolioPnl();

        // The hub restores a latched governor from the journal before this VM
        // exists — surface the breach immediately instead of waiting for an
        // event that already fired.
        if (_hub.IsGovernorTripped)
        {
            ShowGovernorBanner(_hub.GovernorTrippedNet ?? 0m);
            IsGovernorTripped = true;
        }
    }

    public bool AutonomyEnabled => _settings().AutonomyEnabled;

    private Controls.PnlCurveControl? _pnlChart;

    /// <summary>
    /// Feeds the Growth tab's P&amp;L curve with each account's cumulative
    /// daily P&amp;L (from the <see cref="PerformanceTracker"/>), refreshed on
    /// every new settled growth trade.
    /// </summary>
    public void AttachPnlChart(Controls.PnlCurveControl chart)
    {
        _pnlChart = chart;
        _hub.GrowthActivity -= OnActivityRefreshChart;
        _hub.GrowthActivity += OnActivityRefreshChart;
        RefreshPnlChart();
    }

    private void OnActivityRefreshChart(GrowthRunner _, string __) => OnUiThread(RefreshPnlChart);

    private void RefreshPnlChart()
    {
        var chart = _pnlChart;
        if (chart is null || _tracker is null)
        {
            return;
        }

        var series = new List<(string, IReadOnlyList<(DateTimeOffset, decimal)>)>();
        foreach (var row in Rows)
        {
            var points = _tracker.GetIntradayPnl(row.Connection.Config.Id);
            row.PnlSeries = points.Select(p => p.CumulativePnl).ToArray();
            if (points.Count > 0)
            {
                series.Add((row.Connection.DisplayName,
                    points.Select(p => (p.At, p.CumulativePnl)).ToList()));
            }
        }

        chart.SetSeries(series);
    }

    private void LoadPlan()
    {
        var plan = _planStore.Load();
        Budget = plan.StartBudget;
        RiskPercent = (int)Math.Round(plan.RiskFraction * 100);
        TargetPercent = (int)Math.Round(plan.DailyTargetFraction * 100);
        FloorPercent = (int)Math.Round(plan.FloorFraction * 100);
        RecoverySteps = plan.MaxRecoverySteps;
        IntervalMinutes = plan.IntervalMinutes;
        FailureBackoffSeconds = (int)Math.Round(plan.FailureBackoffSeconds);
        MaxAutoRestarts = plan.MaxAutoRestarts;
        RestartBaseDelaySeconds = plan.RestartBaseDelaySeconds;
        RestartBackoffFactor = plan.RestartBackoffFactor;
        PortfolioDrawdownCapText = plan.PortfolioDailyDrawdownCap?.ToString("0.##") ?? "";
    }

    private GrowthPlan BuildPlan() => new(
        StartBudget: Math.Max(0.50m, Budget),
        RiskFraction: Math.Clamp(RiskPercent / 100.0, 0.01, 0.5),
        MaxRecoverySteps: Math.Clamp(RecoverySteps, 1, 5),
        DailyTargetFraction: Math.Clamp(TargetPercent / 100.0, 0.05, 10.0),
        FloorFraction: Math.Clamp(FloorPercent / 100.0, 0.10, 0.9),
        MinStake: 1.00m,
        IntervalMinutes: Math.Clamp(IntervalMinutes, 1, 60),
        CooldownMinutesAfterLoss: 1,
        FailureBackoffSeconds: Math.Clamp(FailureBackoffSeconds, 1, 120),
        MaxAutoRestarts: Math.Clamp(MaxAutoRestarts, 0, 10),
        RestartBaseDelaySeconds: Math.Clamp(RestartBaseDelaySeconds, 0.5, 600.0),
        RestartBackoffFactor: Math.Clamp(RestartBackoffFactor, 1.0, 10.0),
        PortfolioDailyDrawdownCap: GrowthPlan.TryCreateDrawdownCap(PortfolioDrawdownCapText));

    [RelayCommand]
    private void SavePlan()
    {
        var plan = BuildPlan();
        _planStore.Save(plan);
        StatusMessage =
            $"Plan saved: grow a ${plan.StartBudget:0.##} bankroll · {plan.RiskFraction:P0} risk · " +
            $"+{plan.DailyTargetFraction:P0} target · {(1 - plan.FloorFraction):P0} max drawdown · " +
            $"{plan.MaxRecoverySteps}-step ladder.";
    }

    [RelayCommand]
    private void StartAll()
    {
        var autonomy = AutonomyEnabled;
        if (!autonomy)
        {
            StatusMessage =
                "Autonomy is OFF (Settings → Autonomy). Growth engines will run decisions only — " +
                "no trades are placed until you enable it.";
        }

        var plan = BuildPlan();
        var started = 0;
        foreach (var connection in _hub.Accounts)
        {
            if (!connection.IsConnected)
            {
                continue;
            }

            var runner = _hub.StartGrowth(connection, plan, _settings, () => _dashboard.IsKillSwitchEngaged);
            if (runner is not null)
            {
                var row = Rows.FirstOrDefault(r => r.Connection == connection);
                if (row is not null)
                {
                    row.Runner = runner;
                }
                started++;
            }
        }

        StatusMessage = started == 0
            ? "No connected demo accounts to run — connect accounts on this tab first."
            : $"Growth engine started on {started} account(s). Watch the activity log below.";
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        await _hub.StopGrowthAllAsync();
        foreach (var row in Rows)
        {
            row.Runner = null;
        }

        StatusMessage = "All growth engines stopped.";
        OnUiThread(() => ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — stopped all engines"));
    }

    private void OnAccountsChanged()
    {
        OnUiThread(RebuildRows);
    }

    private void OnGrowthActivity(GrowthRunner runner, string line)
    {
        OnUiThread(() =>
        {
            ActivityLog.Insert(0, $"{runner.Connection.DisplayName}: {line}");
            if (ActivityLog.Count > 400)
            {
                ActivityLog.RemoveAt(ActivityLog.Count - 1);
            }
        });
    }

    /// <summary>
    /// Refreshes the portfolio row from the hub's combined growth net —
    /// called on every growth activity line (settlements, stops, trips).
    /// </summary>
    private void OnPortfolioRefresh(GrowthRunner _, string __) => OnUiThread(RefreshPortfolioPnl);

    private void RefreshPortfolioPnl()
    {
        var net = _hub.CombinedGrowthNetPnl();
        CombinedPnlText = $"{(net >= 0 ? "+" : "−")}${Math.Abs(net):0.00}";
    }

    private void OnGovernorTripped(decimal net)
    {
        OnUiThread(() =>
        {
            IsGovernorTripped = true;
            var signedNet = ShowGovernorBanner(net);
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — portfolio drawdown cap breached ({signedNet})");
        });
    }

    /// <summary>Builds the latched-governor banner and status from the net
    /// that breached the cap (shared by live trips and the launch-time seed);
    /// returns the signed net text used in the banner.</summary>
    private string ShowGovernorBanner(decimal net)
    {
        var signedNet = $"{(net >= 0 ? "+" : "−")}${Math.Abs(net):0.##}";
        GovernorBannerText = $"Portfolio drawdown governor engaged — combined net {signedNet} " +
                             "breached the cap. All engines stopped until you re-arm.";
        StatusMessage = $"🛑 Portfolio drawdown governor: combined net {signedNet} breached " +
                        "the cap — all engines stopped. Re-arm to resume.";
        return signedNet;
    }

    private void OnGovernorRearmed()
    {
        OnUiThread(() =>
        {
            IsGovernorTripped = false;
            GovernorBannerText = "";
            StatusMessage = "Portfolio governor re-armed — engines may start again.";
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — portfolio governor re-armed");
            RefreshPortfolioPnl();
        });
    }

    /// <summary>
    /// Manual re-arm after the portfolio drawdown governor tripped: clears
    /// the latch and re-baselines the daily drawdown so engines may start.
    /// </summary>
    [RelayCommand]
    private void RearmGovernor()
    {
        _hub.RearmGovernor();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var connection in _hub.Accounts)
        {
            var row = new GrowthRowViewModel(connection);
            var attempt = _hub.RestartAttempts.TryGetValue(connection.Config.Id, out var a) ? a : 0;
            var gaveUp = _hub.IsGivenUp(connection.Config.Id);
            row.IsGaveUp = gaveUp;
            row.RestartText = DescribeRestartState(attempt, gaveUp);
            Rows.Add(row);
        }

        GaveUpBanners.Clear();
        foreach (var row in Rows.Where(r => r.IsGaveUp))
        {
            GaveUpBanners.Add(new GaveUpBanner(row.Connection.Config.Id, row.Connection.DisplayName));
        }
    }

    private void OnRestartStateChanged(Guid accountId, int attempt, bool gaveUp)
    {
        OnUiThread(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Connection.Config.Id == accountId);
            if (row is not null)
            {
                row.IsGaveUp = gaveUp;
                row.RestartText = DescribeRestartState(attempt, gaveUp);
            }

            var existing = GaveUpBanners.FirstOrDefault(b => b.AccountId == accountId);
            if (gaveUp && existing is null && row is not null)
            {
                GaveUpBanners.Add(new GaveUpBanner(accountId, row.Connection.DisplayName));
            }
            else if (!gaveUp && existing is not null)
            {
                GaveUpBanners.Remove(existing);
            }
        });
    }

    private static string DescribeRestartState(int attempt, bool gaveUp) => gaveUp
        ? $"gave up — {attempt} restarts used"
        : attempt > 0 ? $"restarts {attempt}" : "";

    /// <summary>
    /// Manually restarts one engine after the auto-restart budget was
    /// exhausted; also clears the gave-up state via the hub.
    /// </summary>
    [RelayCommand]
    private void RestartAccount(Guid accountId)
    {
        var connection = _hub.Accounts.FirstOrDefault(a => a.Config.Id == accountId);
        if (connection is null || !connection.IsConnected)
        {
            StatusMessage = "Cannot restart — the account is not connected.";
            return;
        }

        var runner = _hub.StartGrowth(connection, BuildPlan(), _settings, () => _dashboard.IsKillSwitchEngaged);
        var row = Rows.FirstOrDefault(r => r.Connection == connection);
        if (row is not null && runner is not null)
        {
            row.Runner = runner;
        }

        StatusMessage = runner is null
            ? "Cannot restart — the account must be a connected demo account."
            : $"Restarted the growth engine on {connection.DisplayName}.";
    }

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
}    /// <summary>One row in the growth grid; nests the account and its live runner.</summary>
public sealed partial class GrowthRowViewModel : ObservableObject
{
    /// <summary>Intraday cumulative P&amp;L series for the row's sparkline
    /// (one point per settled trade, from the PerformanceTracker).</summary>
    [ObservableProperty]
    private IReadOnlyList<decimal> pnlSeries = Array.Empty<decimal>();

    [ObservableProperty]
    private GrowthRunner? runner;

    [ObservableProperty]
    private string restartText = "";

    [ObservableProperty]
    private bool isGaveUp;

    public GrowthRowViewModel(AccountConnection connection)
    {
        Connection = connection;
    }

    public AccountConnection Connection { get; }
    public string DisplayName => Connection.DisplayName;
    public string LoginIdText => Connection.LoginIdText;
    public string StatusText => Connection.StatusText;
    public string BalanceText => Connection.BalanceText;
    public string SymbolText => Connection.Config.Symbol;
    public string BudgetText => Connection.Config.StartBudget.ToString("0.##");
    public bool IsConnected => Connection.IsConnected;
}

/// <summary>One gave-up banner in the growth tab; carries the restart target.</summary>
public sealed record GaveUpBanner(Guid AccountId, string AccountName);
