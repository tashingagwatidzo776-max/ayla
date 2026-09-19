using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Services;
using DongGfx.Core.Analytics;

namespace DongGfx.App.ViewModels;

/// <summary>
/// Consolidated system health view. Shows per-account connection health,
/// circuit breaker state, tick buffer levels, and growth engine status
/// in one place instead of scattering across multiple tabs.
/// </summary>
public partial class HealthViewModel : ObservableObject
{
    private readonly MultiAccountHub _hub;
    private readonly TickHistoryCache _tickCache;
    private readonly Dispatcher _dispatcher;
    private System.Threading.Timer? _refreshTimer;

    [ObservableProperty]
    private string overallStatus = "Unknown";

    [ObservableProperty]
    private int connectedCount;

    [ObservableProperty]
    private int totalAccounts;

    [ObservableProperty]
    private int degradedCount;

    [ObservableProperty]
    private int pausedCount;

    [ObservableProperty]
    private int runningEngines;

    [ObservableProperty]
    private string cacheStats = "";

    public ObservableCollection<AccountHealthRow> Accounts { get; } = new();

    public HealthViewModel(MultiAccountHub hub, TickHistoryCache tickCache)
    {
        _hub = hub;
        _tickCache = tickCache;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _hub.AccountsChanged += OnAccountsChanged;
        foreach (var acct in _hub.Accounts)
            acct.StateChanged += _ => OnUiThread(Refresh);

        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        OnUiThread(RefreshInternal);
    }

    public void StartAutoRefresh()
    {
        _refreshTimer = new System.Threading.Timer(_ => OnUiThread(RefreshInternal), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void StopAutoRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    private void RefreshInternal()
    {
        // The counting/formatting rules live in the headless
        // HealthSummaryBuilder; this only maps the result onto observable
        // properties and display rows.
        var inputs = new List<AccountHealthInput>();
        foreach (var acct in _hub.Accounts)
        {
            inputs.Add(new AccountHealthInput(
                acct.Config.Id,
                acct.DisplayName,
                acct.LoginIdText,
                acct.StatusText,
                acct.BalanceText,
                acct.IsConnected,
                acct.IsDegraded,
                acct.IsPaused,
                _hub.Runners.ContainsKey(acct.Config.Id),
                acct.CircuitStatus,
                acct.Ticks.Count,
                400,
                _tickCache.GetTickCount(acct.Config.Symbol),
                acct.Config.Symbol,
                acct.Config.BrainKey));
        }

        var symbols = _tickCache.GetSymbols();
        var totalTicks = symbols.Sum(s => (long)_tickCache.GetTickCount(s));
        var summary = HealthSummaryBuilder.Build(inputs, symbols.Count, totalTicks);

        OverallStatus = summary.OverallStatus;
        ConnectedCount = summary.ConnectedCount;
        TotalAccounts = summary.TotalAccounts;
        DegradedCount = summary.DegradedCount;
        PausedCount = summary.PausedCount;
        RunningEngines = summary.RunningEngines;
        CacheStats = summary.CacheStats;

        Accounts.Clear();
        foreach (var row in summary.Rows)
        {
            Accounts.Add(new AccountHealthRow
            {
                Name = row.Name,
                LoginId = row.LoginId,
                Status = row.Status,
                Balance = row.Balance,
                IsConnected = row.IsConnected,
                IsDegraded = row.IsDegraded,
                IsPaused = row.IsPaused,
                HasRunner = row.HasRunner,
                CircuitStatus = row.CircuitStatus,
                TickBuffer = row.TickBuffer,
                TickBufferFill = row.TickBufferFill,
                CachedTicks = row.CachedTicks,
                Symbol = row.Symbol,
                Brain = row.Brain
            });
        }
    }

    private void OnAccountsChanged()
    {
        OnUiThread(() =>
        {
            foreach (var acct in _hub.Accounts)
            {
                acct.StateChanged -= _ => { };
                acct.StateChanged += _ => OnUiThread(RefreshInternal);
            }
            RefreshInternal();
        });
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}

public class AccountHealthRow
{
    public string Name { get; set; } = "";
    public string LoginId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Balance { get; set; } = "";
    public bool IsConnected { get; set; }
    public bool IsDegraded { get; set; }
    public bool IsPaused { get; set; }
    public bool HasRunner { get; set; }
    public string CircuitStatus { get; set; } = "";
    public string TickBuffer { get; set; } = "";
    public double TickBufferFill { get; set; }
    public string CachedTicks { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Brain { get; set; } = "";
}
