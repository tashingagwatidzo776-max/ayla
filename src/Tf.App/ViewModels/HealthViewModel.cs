using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Services;
using Tf.Core.Analytics;

namespace Tf.App.ViewModels;

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
        Accounts.Clear();
        ConnectedCount = 0;
        TotalAccounts = _hub.Accounts.Count;
        DegradedCount = 0;
        PausedCount = 0;
        RunningEngines = 0;

        foreach (var acct in _hub.Accounts)
        {
            if (acct.IsConnected) ConnectedCount++;
            if (acct.IsDegraded) DegradedCount++;
            if (acct.IsPaused) PausedCount++;

            var hasRunner = _hub.Runners.ContainsKey(acct.Config.Id);
            if (hasRunner) RunningEngines++;

            var tickCount = acct.Ticks.Count;
            var cachedCount = _tickCache.GetTickCount(acct.Config.Symbol);

            Accounts.Add(new AccountHealthRow
            {
                Name = acct.DisplayName,
                LoginId = acct.LoginIdText,
                Status = acct.StatusText,
                Balance = acct.BalanceText,
                IsConnected = acct.IsConnected,
                IsDegraded = acct.IsDegraded,
                IsPaused = acct.IsPaused,
                HasRunner = hasRunner,
                CircuitStatus = acct.CircuitStatus,
                TickBuffer = $"{tickCount}/400",
                TickBufferFill = tickCount / 400.0,
                CachedTicks = $"{cachedCount:N0}",
                Symbol = acct.Config.Symbol,
                Brain = acct.Config.BrainKey
            });
        }

        // Overall status
        if (DegradedCount > 0)
            OverallStatus = $"⚠ {DegradedCount} degraded";
        else if (ConnectedCount == TotalAccounts)
            OverallStatus = $"✓ All {TotalAccounts} accounts connected";
        else if (ConnectedCount > 0)
            OverallStatus = $"{ConnectedCount}/{TotalAccounts} connected";
        else
            OverallStatus = "No accounts connected";

        // Cache stats
        var symbols = _tickCache.GetSymbols();
        var totalTicks = symbols.Sum(s => _tickCache.GetTickCount(s));
        CacheStats = symbols.Count > 0
            ? $"{totalTicks:N0} ticks cached across {symbols.Count} symbol(s)"
            : "No tick data cached yet";
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
