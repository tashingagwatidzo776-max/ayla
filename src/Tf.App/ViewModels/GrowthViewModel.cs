using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Infrastructure;
using Tf.App.Services;
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
    private string statusMessage = "Growth plan loaded.";

    public ObservableCollection<GrowthRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();

    public GrowthViewModel(MultiAccountHub hub, GrowthPlanStore planStore,
        Func<AppSettings> settings, DashboardViewModel dashboard)
    {
        _hub = hub;
        _planStore = planStore;
        _settings = settings;
        _dashboard = dashboard;
        _dispatcher = Dispatcher.CurrentDispatcher;

        LoadPlan();
        RebuildRows();

        _hub.AccountsChanged += OnAccountsChanged;
        _hub.GrowthActivity += OnGrowthActivity;
    }

    public bool AutonomyEnabled => _settings().AutonomyEnabled;

    private void LoadPlan()
    {
        var plan = _planStore.Load();
        Budget = plan.StartBudget;
        RiskPercent = (int)Math.Round(plan.RiskFraction * 100);
        TargetPercent = (int)Math.Round(plan.DailyTargetFraction * 100);
        FloorPercent = (int)Math.Round(plan.FloorFraction * 100);
        RecoverySteps = plan.MaxRecoverySteps;
        IntervalMinutes = plan.IntervalMinutes;
    }

    private GrowthPlan BuildPlan() => new(
        StartBudget: Math.Max(0.50m, Budget),
        RiskFraction: Math.Clamp(RiskPercent / 100.0, 0.01, 0.5),
        MaxRecoverySteps: Math.Clamp(RecoverySteps, 1, 5),
        DailyTargetFraction: Math.Clamp(TargetPercent / 100.0, 0.05, 10.0),
        FloorFraction: Math.Clamp(FloorPercent / 100.0, 0.10, 0.9),
        MinStake: 1.00m,
        IntervalMinutes: Math.Clamp(IntervalMinutes, 1, 60),
        CooldownMinutesAfterLoss: 1);

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

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var connection in _hub.Accounts)
        {
            Rows.Add(new GrowthRowViewModel(connection));
        }
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
}

/// <summary>One row in the growth grid; nests the account and its live runner.</summary>
public sealed partial class GrowthRowViewModel : ObservableObject
{
    [ObservableProperty]
    private GrowthRunner? runner;

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
