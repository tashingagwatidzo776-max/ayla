using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.ViewModels;

/// <summary>One row in the terminal's Market Watch grid.</summary>
public sealed partial class TerminalSymbolRow : ObservableObject
{
    public TerminalSymbolRow(string symbol, string? displayName, bool? isOpen)
    {
        Symbol = symbol;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? symbol : displayName!;
        IsOpen = isOpen;
    }

    public string Symbol { get; }
    public string DisplayName { get; }
    public bool? IsOpen { get; }

    public string OpenText => IsOpen is null ? "?" : IsOpen.Value ? "OPEN" : "closed";

    [ObservableProperty]
    private double last;

    [ObservableProperty]
    private double bid;

    [ObservableProperty]
    private double ask;

    /// <summary>True when the last tick moved the price up (drives the row color).</summary>
    [ObservableProperty]
    private bool? lastUp;

    public void UpdateTick(Tick tick)
    {
        LastUp = Last == 0 ? null : tick.Quote > Last;
        Last = tick.Quote;
        Bid = tick.Bid > 0 ? tick.Bid : tick.Quote;
        Ask = tick.Ask > 0 ? tick.Ask : tick.Quote;
    }

    public override string ToString() => $"{Symbol} — {DisplayName}";
}

/// <summary>One open or settled contract in the terminal's positions grid.</summary>
public sealed partial class TerminalPositionRow : ObservableObject
{
    public TerminalPositionRow(string contractId, string symbol, Direction direction,
        decimal stake, double entrySpot, long entryTime)
    {
        ContractId = contractId;
        Symbol = symbol;
        Direction = direction;
        Stake = stake;
        EntrySpot = entrySpot;
        EntryTime = entryTime;
    }

    public string ContractId { get; }
    public string Symbol { get; }
    public Direction Direction { get; }
    public decimal Stake { get; }
    public double EntrySpot { get; }
    public long EntryTime { get; }

    public string DirectionText => Direction == Direction.Rise ? "RISE ▲" : "FALL ▼";

    [ObservableProperty]
    private double currentSpot;

    [ObservableProperty]
    private string status = "open";

    /// <summary>Realized profit once settled; null while open.</summary>
    [ObservableProperty]
    private decimal? profit;

    [ObservableProperty]
    private string exitSpotText = "—";

    /// <summary>Indicative in/out-of-the-money delta while the contract runs.</summary>
    public string DeltaText
    {
        get
        {
            if (Profit.HasValue || CurrentSpot <= 0 || EntrySpot <= 0)
            {
                return "";
            }

            var delta = Direction == Direction.Rise
                ? CurrentSpot - EntrySpot
                : EntrySpot - CurrentSpot;
            return $"{(delta >= 0 ? "+" : "")}{delta:0.#####}";
        }
    }

    public void Settle(ContractInfo final)
    {
        Status = final.Status.ToString().ToLowerInvariant();
        Profit = final.Profit;
        ExitSpotText = final.ExitSpot > 0 ? final.ExitSpot.ToString("0.#####") : "—";
        CurrentSpot = final.ExitSpot;
        OnPropertyChanged(nameof(DeltaText));
    }

    public void UpdateSpot(double spot)
    {
        CurrentSpot = spot;
        OnPropertyChanged(nameof(DeltaText));
    }
}

/// <summary>
/// DON G FX's own MT5-style trading terminal: Market Watch (live quotes for
/// every tradable Deriv symbol), an order ticket (Rise/Fall at any offered
/// duration with bid/ask snapshot), a positions grid with live in/out-of-the-
/// money deltas, and the account bar. Orders run through the same verified
/// pipeline as every other surface — real-money gate, ManualMaxStake, kill
/// switch — and settle into the shared TradeStore tagged "Terminal". The
/// growth brain is switched from here too: the ON/OFF button writes the same
/// AutonomyEnabled setting the engine snapshots, so one switch governs both.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject
{
    private readonly Func<DerivClient> _client;
    private readonly MultiAccountHub _hub;
    private readonly TradeStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly Action _persist;
    private readonly Func<bool> _isRealMoneyUnlocked;
    private readonly DashboardViewModel _dashboard;
    private readonly PublicMarketDataClient _public;

    public TerminalViewModel(
        Func<DerivClient> client,
        MultiAccountHub hub,
        TradeStore store,
        Func<AppSettings> settings,
        Action persist,
        Func<bool> isRealMoneyUnlocked,
        DashboardViewModel dashboard)
        : this(client, hub, store, settings, persist, isRealMoneyUnlocked, dashboard,
               new PublicMarketDataClient())
    {
    }

    public TerminalViewModel(
        Func<DerivClient> client,
        MultiAccountHub hub,
        TradeStore store,
        Func<AppSettings> settings,
        Action persist,
        Func<bool> isRealMoneyUnlocked,
        DashboardViewModel dashboard,
        PublicMarketDataClient publicClient)
    {
        _client = client;
        _hub = hub;
        _store = store;
        _settings = settings;
        _persist = persist;
        _isRealMoneyUnlocked = isRealMoneyUnlocked;
        _dashboard = dashboard;
        _public = publicClient;

        _public.TickReceived += OnPublicTick;
        Positions.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasPositions));
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                OnPropertyChanged(nameof(OpenPositionsText));
            }
        };
    }

    // ── Account bar ────────────────────────────────────────────────

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string accountText = "not connected";

    [ObservableProperty]
    private string connectionText = "disconnected";

    // ── Market watch ───────────────────────────────────────────────

    public ObservableCollection<TerminalSymbolRow> Symbols { get; } = new();

    [ObservableProperty]
    private TerminalSymbolRow? selectedSymbol;

    [ObservableProperty]
    private bool isMarketWatchLoading;

    [ObservableProperty]
    private string marketWatchStatus = "press ⟳ to load the tradable catalog";

    [ObservableProperty]
    private string quoteText = "—";

    partial void OnSelectedSymbolChanged(TerminalSymbolRow? value)
    {
        if (value is null)
        {
            return;
        }

        QuoteText = value.Last > 0 ? value.Last.ToString("0.#####") : "—";
        SubscribeSelected();
    }

    private async void SubscribeSelected()
    {
        var symbol = SelectedSymbol?.Symbol;
        if (string.IsNullOrEmpty(symbol))
        {
            return;
        }

        try
        {
            await _public.SubscribeTicksAsync(symbol);
        }
        catch
        {
            // Quotes are best-effort; the order ticket still works.
        }
    }

    private void OnPublicTick(Tick tick)
    {
        var row = Symbols.FirstOrDefault(s => s.Symbol == tick.Symbol)
                  ?? (SelectedSymbol?.Symbol == tick.Symbol ? SelectedSymbol : null);
        if (row is not null)
        {
            row.UpdateTick(tick);
        }

        if (SelectedSymbol?.Symbol == tick.Symbol)
        {
            QuoteText = tick.Quote.ToString("0.#####");
        }

        foreach (var p in Positions.Where(p => p.Symbol == tick.Symbol && !p.Profit.HasValue))
        {
            p.UpdateSpot(tick.Quote);
        }
    }

    [RelayCommand]
    private async Task LoadSymbolsAsync()
    {
        IsMarketWatchLoading = true;
        MarketWatchStatus = "loading catalog…";
        try
        {
            var symbols = await _public.GetActiveSymbolsAsync();
            Symbols.Clear();
            foreach (var s in symbols)
            {
                Symbols.Add(new TerminalSymbolRow(s.Symbol, s.DisplayName, s.ExchangeIsOpen));
            }

            MarketWatchStatus = $"{Symbols.Count} symbols · {Symbols.Count(s => s.IsOpen == true)} open";
            var preferred = _settings().Symbol;
            SelectedSymbol = Symbols.FirstOrDefault(s => s.Symbol == preferred)
                             ?? Symbols.FirstOrDefault();
        }
        catch (Exception ex)
        {
            MarketWatchStatus = $"catalog failed: {ex.Message}";
        }
        finally
        {
            IsMarketWatchLoading = false;
        }
    }

    // ── Order ticket ───────────────────────────────────────────────

    /// <summary>Rise = buy the up contract; Fall = the down contract.</summary>
    [ObservableProperty]
    private Direction selectedDirection = Direction.Rise;

    partial void OnSelectedDirectionChanged(Direction value)
    {
        OnPropertyChanged(nameof(IsRise));
        OnPropertyChanged(nameof(IsFall));
    }

    /// <summary>RadioButton bindings for the direction toggle.</summary>
    public bool IsRise
    {
        get => SelectedDirection == Direction.Rise;
        set { if (value) { SelectedDirection = Direction.Rise; } }
    }

    public bool IsFall
    {
        get => SelectedDirection == Direction.Fall;
        set { if (value) { SelectedDirection = Direction.Fall; } }
    }

    [ObservableProperty]
    private decimal stake;

    [ObservableProperty]
    private int durationMinutes;

    [ObservableProperty]
    private string ticketStatus = "";

    [ObservableProperty]
    private bool isTicketBusy;

    public string ContractTypeInfo =>
        "Deriv binary options: Rise (wins if exit > entry) or Fall (wins if exit < entry) " +
        "at any offered duration. Payout is fixed at entry; the live delta shows " +
        "in/out-of-the-money while the contract runs.";

    [RelayCommand]
    private async Task PlaceOrderAsync()
    {
        var settings = _settings();

        // Sync the ticket's symbol into the shared settings so the brain,
        // growth engines and terminal trade the same instrument.
        if (!string.IsNullOrWhiteSpace(SelectedSymbol?.Symbol)
            && !string.Equals(settings.Symbol, SelectedSymbol.Symbol, StringComparison.Ordinal))
        {
            settings.Symbol = SelectedSymbol.Symbol;
            _persist();
        }

        var stakeUsed = Stake > 0 ? Stake : settings.Stake;
        var duration = DurationMinutes > 0 ? DurationMinutes : settings.DurationMinutes;

        // ── The same rails as every other trade path ──
        if (_dashboard.IsKillSwitchEngaged)
        {
            TicketStatus = "kill switch is engaged — reset it on the Dashboard first";
            return;
        }

        if (settings.ManualMaxStake > 0 && stakeUsed > settings.ManualMaxStake)
        {
            TicketStatus = $"stake {stakeUsed:0.##} {settings.Currency} exceeds the manual max " +
                $"of {settings.ManualMaxStake:0.##} — lower it and retry";
            return;
        }

        var connection = _hub.Accounts.FirstOrDefault(a => a.IsConnected);
        var client = connection is not null ? connection.Client : _client();

        // ── Real-money gate, evaluated on the SAME state as every other
        // surface: the connected account's config flag, the API's own
        // verification, and the hub's per-account session unlock. The
        // fallback (primary client, no hub row) uses the primary gate.
        RealMoneyDecision gate;
        if (connection is not null)
        {
            gate = RealMoneyGate.Evaluate(
                connection.Config.IsDemo,
                connection.ApiVerifiedVirtual,
                _hub.IsRealMoneyUnlocked(connection.Config.Id));
        }
        else
        {
            var apiVerifiedVirtual = string.IsNullOrEmpty(client.LoginId)
                ? (bool?)null : client.Balance.IsVirtual;
            gate = RealMoneyGate.Evaluate(
                settings.IsDemo, apiVerifiedVirtual, _isRealMoneyUnlocked?.Invoke() ?? false);
        }

        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            TicketStatus = RealMoneyGate.Explain(gate);
            return;
        }

        if (!client.IsConnected)
        {
            TicketStatus = "not connected — connect an account first (Accounts & Growth tab)";
            return;
        }

        IsTicketBusy = true;
        try
        {
            TicketStatus = "requesting proposal…";
            var proposal = await client.GetProposalAsync(
                settings.Symbol, SelectedDirection, stakeUsed, settings.Currency, duration);

            TicketStatus = $"buying {SelectedDirection} @ {proposal.Spot:0.#####} (payout {proposal.Payout:0.##} {settings.Currency})…";
            var buy = await client.BuyAsync(proposal.Id, proposal.Spot);

            var row = new TerminalPositionRow(
                buy.ContractId, settings.Symbol, SelectedDirection, stakeUsed,
                proposal.Spot, 0);
            row.CurrentSpot = proposal.Spot;
            Positions.Insert(0, row);

            TicketStatus = $"contract {buy.ContractId} open — settling (≤ {duration} min)…";

            // Settle in the background so the terminal stays responsive.
            _ = Task.Run(async () =>
            {
                try
                {
                    var final = await client.WaitForSettlementAsync(
                        buy.ContractId, TimeSpan.FromMinutes(duration + 3));
                    row.Settle(final);
                    _store.Add(new Trade(
                        Guid.NewGuid(), settings.Symbol, SelectedDirection, stakeUsed, settings.Currency,
                        final.EntrySpot, final.EntryTime, final.ContractId, final.Status, final.Profit,
                        final.ExitSpot > 0 ? final.ExitSpot : null,
                        final.ExitTime > 0 ? final.ExitTime : null,
                        DateTimeOffset.UtcNow) with
                    {
                        Source = TradeSource.Manual,
                        AccountId = connection?.Config.Id,
                        AccountName = connection?.DisplayName,
                    });
                }
                catch
                {
                    row.Status = "settlement error";
                }
            });

            TicketStatus = $"contract {buy.ContractId} open @ {buy.BuyPrice:0.##}";
        }
        catch (DerivApiException ex)
        {
            TicketStatus = $"order failed: [{ex.Code}] {ex.Message}";
        }
        catch (Exception ex)
        {
            TicketStatus = $"order failed: {ex.Message}";
        }
        finally
        {
            IsTicketBusy = false;
        }
    }

    // ── Positions ──────────────────────────────────────────────────

    public ObservableCollection<TerminalPositionRow> Positions { get; } = new();

    public bool HasPositions => Positions.Count > 0;

    public string OpenPositionsText
    {
        get
        {
            var open = Positions.Count(p => !p.Profit.HasValue);
            return $"{open} open · {Positions.Count} total";
        }
    }

    // ── The brain switch (autonomy ON/OFF) ─────────────────────────

    /// <summary>True when the shared AutonomyEnabled setting is on: the brain
    /// places its allowed trades. The terminal's ON/OFF button writes this —
    /// one switch governs the brain on every surface.</summary>
    public bool BrainIsOn => _settings().AutonomyEnabled;

    public string BrainStateText => BrainIsOn
        ? "AUTONOMY ON — the brain places its allowed trades"
        : "AUTONOMY OFF — cycles decide + risk-check but never place";

    [RelayCommand]
    private void TurnBrainOn()
    {
        var settings = _settings();
        settings.AutonomyEnabled = true;
        _persist();
        OnPropertyChanged(nameof(BrainIsOn));
        OnPropertyChanged(nameof(BrainStateText));
        TicketStatus = "autonomy ON — the brain will place its allowed trades";
    }

    [RelayCommand]
    private void TurnBrainOff()
    {
        var settings = _settings();
        settings.AutonomyEnabled = false;
        _persist();
        OnPropertyChanged(nameof(BrainIsOn));
        OnPropertyChanged(nameof(BrainStateText));
        TicketStatus = "autonomy OFF — decisions continue, nothing is placed";
    }

    /// <summary>Emergency flatten: engage the global kill switch (stops all
    /// feeds and engines). Reset lives on the Dashboard, deliberate friction.</summary>
    [RelayCommand]
    private void EmergencyStop()
    {
        if (_dashboard.ToggleKillSwitchCommand.CanExecute(null))
        {
            _dashboard.ToggleKillSwitchCommand.Execute(null);
        }

        TicketStatus = "KILL SWITCH ENGAGED — everything stopped; reset on the Dashboard";
    }

    /// <summary>Refreshes the account bar and recent history. Called when the
    /// view loads and on balance events.</summary>
    [RelayCommand]
    public void RefreshAccountBar()
    {
        var connection = _hub.Accounts.FirstOrDefault(a => a.IsConnected);
        var client = connection is not null ? connection.Client : _client();
        if (connection is not null)
        {
            AccountText = connection.DisplayName;
            ConnectionText = connection.StatusText;
            BalanceText = string.IsNullOrEmpty(connection.BalanceText)
                ? "—" : connection.BalanceText;
        }
        else if (client.IsConnected)
        {
            AccountText = string.IsNullOrEmpty(client.LoginId) ? "primary" : client.LoginId;
            ConnectionText = "connected";
            BalanceText = client.Balance.Balance > 0
                ? $"{client.Balance.Balance:0.##} {client.Balance.Currency}" : "—";
        }
        else
        {
            AccountText = "not connected";
            ConnectionText = "disconnected";
            BalanceText = "—";
        }
    }
}
