using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.Core;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.ViewModels;

public sealed partial class TradesViewModel : ObservableObject
{
    private readonly DerivClient _client;
    private readonly TradeStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly DashboardViewModel _dashboard;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool>? _isRealMoneyUnlocked;

    public ObservableCollection<Trade> Trades { get; } = new();

    public IReadOnlyList<Direction> Directions { get; } = new[] { Direction.Rise, Direction.Fall };

    [ObservableProperty]
    private string summaryText = "No trades yet";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private Direction selectedDirection = Direction.Rise;

    /// <summary>True when a demo trade can be placed right now.</summary>
    [ObservableProperty]
    private bool canTrade;

    /// <summary>True when the manual trade button is blocked by the
    /// real-money gate (a real/unverified account without the session
    /// unlock) — the button then advertises the lock instead of trading.</summary>
    public bool IsRealModeBlocked =>
        !_settings().IsDemo && _isRealMoneyUnlocked?.Invoke() != true;

    public TradesViewModel(DerivClient client, TradeStore store,
        Func<AppSettings> settings, DashboardViewModel dashboard,
        Func<bool>? isRealMoneyUnlocked = null)
    {
        _client = client;
        _store = store;
        _settings = settings;
        _dashboard = dashboard;
        // Session-unlock accessor for the manual trade gate; tests may omit
        // it (locked ⇒ the gate fails closed on real accounts).
        _isRealMoneyUnlocked = isRealMoneyUnlocked;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _client.StatusChanged += _ => RefreshCanTrade();
        _store.TradeAdded += OnTradeAdded;
        RefreshCanTrade();
    }

    partial void OnIsBusyChanged(bool value) => RefreshCanTrade();

    private void RefreshCanTrade() => CanTrade = !IsBusy && _client.IsConnected;

    /// <summary>Loads the persisted trade log into the UI.</summary>
    public void Load()
    {
        Trades.Clear();
        foreach (var trade in _store.Trades)
        {
            Trades.Add(trade);
        }

        RefreshSummary();
    }

    /// <summary>
    /// Manual end-to-end demo trade: proposal → buy → poll until settlement
    /// → append to the log. Guarded to demo mode (or an unlocked, API-verified
    /// real account via the shared real-money gate), a connected+authorized
    /// session, and the master kill switch.
    /// </summary>
    [RelayCommand]
    private async Task PlaceDemoTradeAsync()
    {
        var settings = _settings();

        // Manual stake ceiling: the typed Stake is otherwise unbounded, and
        // this is the one trade path that bypasses the risk engine's stake
        // checks (the brain and growth paths are bounded by MaxStake and the
        // session plan ladder). A mistyped stake must not reach a real
        // account — refuse loudly instead of silently clamping.
        if (settings.ManualMaxStake > 0 && settings.Stake > settings.ManualMaxStake)
        {
            StatusMessage = $"Stake {settings.Stake:0.##} {settings.Currency} exceeds the manual max " +
                $"of {settings.ManualMaxStake:0.##} — lower the stake in Settings (Stake field) to trade.";
            return;
        }

        // Real-money gate: a real account needs the session unlock. The
        // API-verified flag comes from the client's own authorize/balance —
        // unverified fails closed (never treated as real).
        var apiVerifiedVirtual = string.IsNullOrEmpty(_client.LoginId)
            ? (bool?)null : _client.Balance.IsVirtual;
        var decision = RealMoneyGate.Evaluate(
            settings.IsDemo, apiVerifiedVirtual, _isRealMoneyUnlocked?.Invoke() ?? false);
        if (decision is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            StatusMessage = RealMoneyGate.Explain(decision);
            return;
        }

        if (string.IsNullOrEmpty(settings.ApiToken))
        {
            StatusMessage = "No API token — add your Deriv demo token in Settings and press Connect.";
            return;
        }

        if (_dashboard.IsKillSwitchEngaged)
        {
            StatusMessage = "Kill switch is engaged — reset it on the Dashboard first.";
            return;
        }

        if (IsBusy || !_client.IsConnected)
        {
            StatusMessage = IsBusy ? "A trade is already in progress." : "Not connected — press Connect in Settings.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Requesting proposal…";
            var proposal = await _client.GetProposalAsync(
                settings.Symbol, SelectedDirection, settings.Stake, settings.Currency,
                settings.DurationMinutes);

            StatusMessage = $"Proposal {proposal.Id} @ {proposal.Spot:0.00000} — buying…";
            var buy = await _client.BuyAsync(proposal.Id, proposal.Spot);

            StatusMessage = $"Contract {buy.ContractId} open — waiting for settlement (≤ {settings.DurationMinutes} min)…";
            var final = await _client.WaitForSettlementAsync(
                buy.ContractId, TimeSpan.FromMinutes(settings.DurationMinutes + 3));

            var trade = new Trade(
                Guid.NewGuid(), settings.Symbol, SelectedDirection, settings.Stake, settings.Currency,
                final.EntrySpot, final.EntryTime, final.ContractId, final.Status, final.Profit,
                final.ExitSpot > 0 ? final.ExitSpot : null,
                final.ExitTime > 0 ? final.ExitTime : null,
                DateTimeOffset.UtcNow);

            _store.Add(trade with { Source = TradeSource.Manual });
            StatusMessage = $"Settled {final.Status} · P&L {final.Profit:+0.##;-0.##;0} {settings.Currency}";
        }
        catch (DerivApiException ex)
        {
            StatusMessage = $"Trade failed: [{ex.Code}] {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Trade failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Live-inserts settled trades (from any brain) into the grid.</summary>
    private void OnTradeAdded(Trade trade)
    {
        void Update()
        {
            Trades.Insert(0, trade);
            RefreshSummary();
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private void RefreshSummary()
    {
        var today = _store.SummaryFor(DateTimeOffset.Now);
        SummaryText = today.Count == 0
            ? "No trades yet"
            : $"Today: {today.Count} trade(s) · {today.Wins}W / {today.Losses}L · " +
              $"Net {today.NetProfit:+0.##;-0.##;0} {(_settings().Currency)} · {today.WinRate:P0} win rate";
    }
}