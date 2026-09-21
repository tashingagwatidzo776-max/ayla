using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core;
using DongGfx.Core.Logging;
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

    [ObservableProperty]
    private double spread;

    [ObservableProperty]
    private double dayHigh;

    [ObservableProperty]
    private double dayLow;

    /// <summary>True when the last tick moved the price up (drives the row color).</summary>
    [ObservableProperty]
    private bool? lastUp;

    public void UpdateTick(Tick tick)
    {
        LastUp = Last == 0 ? null : tick.Quote > Last;
        Last = tick.Quote;
        Bid = tick.Bid > 0 ? tick.Bid : tick.Quote;
        Ask = tick.Ask > 0 ? tick.Ask : tick.Quote;
        Spread = Math.Round(Ask - Bid, 5);
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

/// <summary>One row in the MT5-style Trade tab: an open binary contract or an
/// MT5 position, normalized to a ticket-like grid with live P/L.</summary>
public sealed record TerminalTradeRow(
    string Ticket, string Symbol, string Side, double Volume,
    double Entry, double Current, double Profit, string Kind, long? Mt5Ticket = null)
{
    /// <summary>The ✕ close button only makes sense for MT5 positions.</summary>
    public System.Windows.Visibility Mt5CloseVisibility =>
        Mt5Ticket.HasValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

/// <summary>One settled trade in the Account History tab.</summary>
public sealed record TerminalHistoryRow(
    string Time, string Symbol, string Side, double Volume,
    string Outcome, double Profit, string Source);

/// <summary>One net-exposure row (Toolbox → Exposure).</summary>
public sealed record TerminalExposureRow(string Symbol, double NetVolume, double OpenProfit);

/// <summary>One price-ladder level (synthetic DOM around the live quote).</summary>
public sealed partial class TerminalLadderRow : ObservableObject
{
    public TerminalLadderRow(double price, string side)
    {
        Price = price;
        Side = side;
    }

    public double Price { get; }
    public string Side { get; }

    [ObservableProperty]
    private double size;
}

/// <summary>
/// The DON G FX Terminal — an MT5-style trading workspace: Market Watch over
/// every tradable symbol, a live candle chart, a four-tab Toolbox (Trade /
/// Exposure / Account History / Journal), a synthetic price ladder, the
/// binary order ticket, and the MT5 bridge card for CFD/forex orders routed
/// to the running MetaTrader 5 terminal. Every order — binary or MT5 — runs
/// the same rails: kill switch, real-money gate, stake/lot caps. The brain's
/// autonomy switch lives here too: one switch governs every surface.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject
{
    private const int LadderLevels = 6;
    private const int CandleSeconds = 60; // M1 candles built from the tick feed

    private readonly Func<DerivClient> _client;
    private readonly MultiAccountHub _hub;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly Func<AppSettings> _settings;
    private readonly Action _persist;
    private readonly Func<bool> _isRealMoneyUnlocked;
    private readonly DashboardViewModel _dashboard;
    private readonly PublicMarketDataClient _public;
    private readonly Mt5BridgeClient _mt5;
    private readonly Action<bool>? _setAutonomyBound;
    private readonly Action<string>? _setSymbolBound;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _mt5PollTimer;
    private readonly List<(long Second, double Open, double High, double Low, double Close)> _candles = new();
    private int _busy;

    public TerminalViewModel(
        Func<DerivClient> client,
        MultiAccountHub hub,
        TradeStore store,
        Func<AppSettings> settings,
        Action persist,
        Func<bool> isRealMoneyUnlocked,
        DashboardViewModel dashboard,
        TradeJournal? journal = null,
        Mt5BridgeClient? mt5 = null,
        Action<bool>? setAutonomyBound = null,
        Action<string>? setSymbolBound = null)
        : this(client, hub, store, settings, persist, isRealMoneyUnlocked, dashboard,
               journal, mt5, setAutonomyBound, setSymbolBound, new PublicMarketDataClient())
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
        TradeJournal? journal,
        Mt5BridgeClient? mt5,
        Action<bool>? setAutonomyBound,
        Action<string>? setSymbolBound,
        PublicMarketDataClient publicClient)
    {
        _client = client;
        _hub = hub;
        _store = store;
        _journal = journal ?? new TradeJournal(
            Path.Combine(Path.GetTempPath(), "dg-terminal-fallback-journal"));
        _settings = settings;
        _persist = persist;
        _isRealMoneyUnlocked = isRealMoneyUnlocked;
        _dashboard = dashboard;
        _public = publicClient;
        _mt5 = mt5 ?? new Mt5BridgeClient();
        _setAutonomyBound = setAutonomyBound;
        _setSymbolBound = setSymbolBound;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _public.TickReceived += OnPublicTick;
        _store.TradeAdded += OnTradeAdded;
        Positions.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasPositions));
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                OnPropertyChanged(nameof(OpenPositionsText));
            }
        };

        _mt5PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mt5PollTimer.Tick += async (_, _) => await PollMt5Async().ConfigureAwait(true);

        // Seed the ladder so the panel is never empty on first paint.
        RebuildLadder(0);
        RefreshHistory();
    }

    /// <summary>Starts the MT5 poll loop (called when the view loads).</summary>
    [RelayCommand]
    public void StartTerminal()
    {
        _mt5PollTimer.Start();
        _ = PollMt5Async();
        RefreshAccountBar();
        RefreshHistory();
    }

    /// <summary>Test seam: one explicit MT5 poll without the timer.</summary>
    internal Task PollMt5ForTestsAsync() => PollMt5Async();

    /// <summary>Test seam: rebuild the history/journal rows on demand.</summary>
    internal void RefreshHistoryForTests() => RefreshHistory();

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

    [ObservableProperty]
    private string symbolFilter = "";

    /// <summary>Market Watch rows matching the filter (MT5-style type-to-filter).</summary>
    public IEnumerable<TerminalSymbolRow> FilteredSymbols =>
        string.IsNullOrWhiteSpace(SymbolFilter)
            ? Symbols
            : Symbols.Where(s => s.Symbol.Contains(SymbolFilter, StringComparison.OrdinalIgnoreCase)
                                 || s.DisplayName.Contains(SymbolFilter, StringComparison.OrdinalIgnoreCase));

    partial void OnSymbolFilterChanged(string value) => OnPropertyChanged(nameof(FilteredSymbols));

    partial void OnSelectedSymbolChanged(TerminalSymbolRow? value)
    {
        if (value is null)
        {
            return;
        }

        QuoteText = value.Last > 0 ? value.Last.ToString("0.#####") : "—";
        _ = _public.SubscribeTicksAsync(value.Symbol);
        RebuildLadder(value.Bid > 0 ? value.Bid : value.Last);
        _ = LoadCandlesFromBridgeAsync(value.Symbol);
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
            RebuildLadder(tick.Bid > 0 ? tick.Bid : tick.Quote);
            AggregateTick(tick);
            foreach (var p in Positions.Where(p => p.Symbol == tick.Symbol && !p.Profit.HasValue))
            {
                p.UpdateSpot(tick.Quote);
            }
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
            OnPropertyChanged(nameof(FilteredSymbols));
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

    // ── Candle chart (M1, built from the live tick feed) ───────────

    public sealed record CandleDto(string TimeText, double Open, double High, double Low, double Close,
        double BarHeight, bool Up);

    public ObservableCollection<CandleDto> Candles { get; } = new();

    [ObservableProperty]
    private string ohlcText = "select a symbol";

    [ObservableProperty]
    private string candleSourceText = "M1 candles · live tick feed";

    private void AggregateTick(Tick tick)
    {
        var second = tick.Epoch / 1000;
        var price = tick.Quote;
        if (price <= 0)
        {
            return;
        }

        if (_candles.Count > 0 && _candles[^1].Second == second)
        {
            var c = _candles[^1];
            _candles[^1] = (c.Second, c.Open, Math.Max(c.High, price), Math.Min(c.Low, price), price);
        }
        else
        {
            _candles.Add((second, price, price, price, price));
            if (_candles.Count > 180)
            {
                _candles.RemoveAt(0);
            }
        }

        RenderCandles();
    }

    private async Task LoadCandlesFromBridgeAsync(string symbol)
    {
        var fromBridge = await _mt5.GetCandlesAsync(symbol, "M1", 90).ConfigureAwait(true);
        if (fromBridge.Count > 0)
        {
            _candles.Clear();
            foreach (var c in fromBridge)
            {
                _candles.Add((c.Time, c.Open, c.High, c.Low, c.Close));
            }

            CandleSourceText = "M1 candles · MT5 bridge";
            RenderCandles();
        }
    }

    private void RenderCandles()
    {
        if (_candles.Count == 0)
        {
            return;
        }

        var last = _candles[^1];
        OhlcText = $"O {last.Open:0.#####}  H {last.High:0.#####}  L {last.Low:0.#####}  C {last.Close:0.#####}";
        var window = _candles.TakeLast(60).ToList();
        var hi = window.Max(c => c.High);
        var lo = window.Min(c => c.Low);
        var span = Math.Max(hi - lo, 1e-9);
        Candles.Clear();
        foreach (var c in window)
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(c.Second).LocalDateTime.ToString("HH:mm");
            // Bar height proportional to the candle's range within the window.
            var bar = Math.Max(6.0, (c.High - c.Low) / span * 110.0);
            Candles.Add(new CandleDto(t, c.Open, c.High, c.Low, c.Close,
                bar, c.Close >= c.Open));
        }
    }

    // ── Price ladder (synthetic DOM: Deriv streams no depth) ───────

    public ObservableCollection<TerminalLadderRow> Ladder { get; } = new();

    [ObservableProperty]
    private string domSourceText = "DOM: synthetic (bid/ask)";

    private void RebuildLadder(double center)
    {
        if (center <= 0)
        {
            return;
        }

        var digits = center >= 100 ? 2 : 5;
        var step = Math.Pow(10, -digits);
        Ladder.Clear();
        for (var i = LadderLevels; i >= 1; i--)
        {
            Ladder.Add(new TerminalLadderRow(Math.Round(center + i * step, digits), "ask")
            {
                Size = LadderLevels - i + 1,
            });
        }

        Ladder.Add(new TerminalLadderRow(Math.Round(center, digits), "mid") { Size = LadderLevels });
        for (var i = 1; i <= LadderLevels; i++)
        {
            Ladder.Add(new TerminalLadderRow(Math.Round(center - i * step, digits), "bid")
            {
                Size = LadderLevels - i + 1,
            });
        }
    }

    // ── Binary order ticket ────────────────────────────────────────

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
            // Keep the settings UI's bound field in sync: it is the source of
            // truth on save — writing only the shared instance gets clobbered
            // by the next settings save.
            _setSymbolBound?.Invoke(SelectedSymbol.Symbol);
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
            // The store write comes FIRST (it must never depend on the UI
            // dispatcher being pumped); the grid updates are posted after.
            _ = Task.Run(async () =>
            {
                try
                {
                    var final = await client.WaitForSettlementAsync(
                        buy.ContractId, TimeSpan.FromMinutes(duration + 3));
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
                    _ = _dispatcher.InvokeAsync(() =>
                    {
                        row.Settle(final);
                        RefreshHistory();
                    });
                }
                catch
                {
                    _ = _dispatcher.InvokeAsync(() => row.Status = "settlement error");
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

    // ── Positions (binary contracts) ───────────────────────────────

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

    // ── Toolbox: Trade / Exposure / Account History / Journal ──────

    public ObservableCollection<TerminalTradeRow> TradeRows { get; } = new();

    public ObservableCollection<TerminalExposureRow> ExposureRows { get; } = new();

    public ObservableCollection<TerminalHistoryRow> HistoryRows { get; } = new();

    public ObservableCollection<JournalEntryViewModel> JournalRows { get; } = new();

    private void RebuildTradeRows()
    {
        TradeRows.Clear();
        foreach (var p in Positions.Where(p => !p.Profit.HasValue))
        {
            TradeRows.Add(new TerminalTradeRow(
                p.ContractId, p.Symbol, p.DirectionText, 1,
                p.EntrySpot, p.CurrentSpot, (double)(p.Profit ?? 0), "binary"));
        }

        foreach (var p in _mt5Positions)
        {
            TradeRows.Add(new TerminalTradeRow(
                $"MT5-{p.Ticket}", p.Symbol, p.Side.ToUpperInvariant() + (p.Side == "buy" ? " ▲" : " ▼"),
                p.Volume, p.PriceOpen, p.PriceCurrent, p.Profit, "mt5", p.Ticket));
        }

        RebuildExposure();
    }

    private void RebuildExposure()
    {
        ExposureRows.Clear();
        foreach (var g in TradeRows.GroupBy(r => r.Symbol))
        {
            var net = g.Sum(r => (r.Side.StartsWith("BUY", StringComparison.Ordinal) ? 1 : -1) * r.Volume);
            ExposureRows.Add(new TerminalExposureRow(g.Key, net, g.Sum(r => r.Profit)));
        }
    }

    private void RefreshHistory()
    {
        HistoryRows.Clear();
        foreach (var t in _store.Trades.OrderByDescending(t => t.SettledAt).Take(50))
        {
            HistoryRows.Add(new TerminalHistoryRow(
                t.SettledAt.LocalDateTime.ToString("MM-dd HH:mm"),
                t.Symbol,
                t.Direction.ToString().ToUpperInvariant(),
                1,
                t.Outcome.ToString().ToLowerInvariant(),
                (double)t.Profit,
                t.Source ?? "—"));
        }

        JournalRows.Clear();
        foreach (var e in _journal.GetRecent(count: 40))
        {
            JournalRows.Add(new JournalEntryViewModel
            {
                Timestamp = e.Timestamp.LocalDateTime.ToString("MM-dd HH:mm"),
                Category = e.Category,
                Details = e.Details,
            });
        }
    }

    private void OnTradeAdded(Trade trade)
    {
        _ = _dispatcher.InvokeAsync(RefreshHistory);
    }

    // ── MT5 bridge card ────────────────────────────────────────────

    private List<Mt5Position> _mt5Positions = new();

    [ObservableProperty]
    private string mt5StatusText = "bridge down — run:  python bridge/mt5_sidecar.py";

    [ObservableProperty]
    private bool isMt5Connected;

    [ObservableProperty]
    private string mt5AccountText = "—";

    [ObservableProperty]
    private string mt5Symbol = "XAUUSDmicro";

    [ObservableProperty]
    private string mt5Action = "buy";

    [ObservableProperty]
    private string mt5Type = "market";

    [ObservableProperty]
    private double mt5Lots = 0.1;

    [ObservableProperty]
    private double? mt5Price;

    [ObservableProperty]
    private double? mt5StopPrice;

    [ObservableProperty]
    private double? mt5Sl;

    [ObservableProperty]
    private double? mt5Tp;

    [ObservableProperty]
    private string mt5OrderStatus = "";

    [ObservableProperty]
    private bool isMt5Busy;

    public IReadOnlyList<string> Mt5Actions { get; } = new[] { "buy", "sell" };

    public IReadOnlyList<string> Mt5Types { get; } = new[] { "market", "limit", "stop", "stoplimit" };

    public bool Mt5NeedsPrice => Mt5Type is "limit" or "stop";

    public bool Mt5NeedsStopPrice => Mt5Type == "stoplimit";

    public System.Windows.Visibility Mt5PriceVisibility =>
        Mt5NeedsPrice ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility Mt5StopPriceVisibility =>
        Mt5NeedsStopPrice ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    partial void OnMt5TypeChanged(string value)
    {
        OnPropertyChanged(nameof(Mt5NeedsPrice));
        OnPropertyChanged(nameof(Mt5NeedsStopPrice));
    }

    private async Task PollMt5Async()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var health = await _mt5.HealthAsync().ConfigureAwait(true);
            if (health is null || !health.Value.Ok)
            {
                if (IsMt5Connected)
                {
                    IsMt5Connected = false;
                    Mt5StatusText = "bridge down — run:  python bridge/mt5_sidecar.py";
                    _mt5Positions.Clear();
                    RebuildTradeRows();
                }

                return;
            }

            var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
            var positions = await _mt5.GetPositionsAsync().ConfigureAwait(true);
            await _dispatcher.InvokeAsync(() =>
            {
                IsMt5Connected = true;
                Mt5StatusText = "MT5 bridge: connected";
                if (account is not null)
                {
                    Mt5AccountText = $"{account.Login} @ {account.Server} · {account.Equity:0.##} {account.Currency}";
                }

                _mt5Positions = positions.ToList();
                RebuildTradeRows();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    [RelayCommand]
    private async Task PlaceMt5OrderAsync()
    {
        var settings = _settings();

        if (_dashboard.IsKillSwitchEngaged)
        {
            Mt5OrderStatus = "kill switch is engaged — reset it on the Dashboard first";
            return;
        }

        // The MT5 lot cap fail-closes at 0: MT5 order placement must be
        // deliberately enabled by the operator.
        if (settings.Mt5MaxLots <= 0)
        {
            Mt5OrderStatus = "MT5 orders are disabled (Mt5MaxLots = 0) — enable it on the Settings tab";
            return;
        }

        if (Mt5Lots <= 0 || (decimal)Mt5Lots > settings.Mt5MaxLots)
        {
            Mt5OrderStatus = $"lots {Mt5Lots:0.##} outside the allowed 0 < lots ≤ {settings.Mt5MaxLots:0.##}";
            return;
        }

        if (Mt5NeedsPrice && Mt5Price is null)
        {
            Mt5OrderStatus = $"{Mt5Type} orders need a price";
            return;
        }

        if (Mt5NeedsStopPrice && Mt5StopPrice is null)
        {
            Mt5OrderStatus = "stop-limit orders need a stop trigger price";
            return;
        }

        // The bridge's account decides demo/real; a real MT5 account obeys
        // the same session unlock as every other real-money path.
        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (account is null)
        {
            Mt5OrderStatus = "bridge unavailable — run:  python bridge/mt5_sidecar.py";
            return;
        }

        var isDemo = account.Server.Contains("demo", StringComparison.OrdinalIgnoreCase)
                     || account.Login > 500000000; // Deriv demo logins are ≥ 5xxxxxxxx
        var unlocked = (_hub.Accounts.Any(a => !a.Config.IsDemo && _hub.IsRealMoneyUnlocked(a.Config.Id))
                        || (_isRealMoneyUnlocked?.Invoke() ?? false)) ;
        var gate = RealMoneyGate.Evaluate(isDemo, isDemo ? true : (bool?)null, unlocked);
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            Mt5OrderStatus = RealMoneyGate.Explain(gate);
            return;
        }

        IsMt5Busy = true;
        try
        {
            var result = await _mt5.PlaceOrderAsync(
                Mt5Symbol, Mt5Action, Mt5Type, Mt5Lots,
                Mt5Price, Mt5StopPrice, Mt5Sl, Mt5Tp).ConfigureAwait(true);

            Mt5OrderStatus = result.Ok
                ? $"{Mt5Action} {Mt5Type} filled: ticket {result.Order ?? result.Deal} @ {result.Price:0.#####}"
                : $"refused: {result.RetcodeName} ({result.Retcode})";

            _journal.Log(Guid.Empty, "MT5_ORDER",
                $"{Mt5Action} {Mt5Type} {Mt5Lots} {Mt5Symbol} → {result.RetcodeName}",
                JsonSerializerOps.ToJson(new
                {
                    result.Retcode,
                    result.Order,
                    result.Deal,
                    result.Price,
                    Server = account.Server,
                }));

            _ = PollMt5Async();
        }
        catch (Mt5BridgeException ex)
        {
            Mt5OrderStatus = $"order refused: {ex.Message}";
        }
        catch (Exception ex)
        {
            Mt5OrderStatus = $"order failed: {ex.Message}";
        }
        finally
        {
            IsMt5Busy = false;
        }
    }

    [RelayCommand]
    private async Task CloseMt5PositionAsync(long? ticket)
    {
        if (ticket is null)
        {
            return;
        }

        if (_dashboard.IsKillSwitchEngaged)
        {
            Mt5OrderStatus = "kill switch is engaged — reset it on the Dashboard first";
            return;
        }

        try
        {
            var result = await _mt5.ClosePositionAsync(ticket.Value).ConfigureAwait(true);
            Mt5OrderStatus = result.Ok
                ? $"position {ticket} closed"
                : $"close refused: {result.RetcodeName}";
            _journal.Log(Guid.Empty, "MT5_ORDER", $"close {ticket} → {result.RetcodeName}");
            _ = PollMt5Async();
        }
        catch (Exception ex)
        {
            Mt5OrderStatus = $"close failed: {ex.Message}";
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
        // Bridge to the settings UI's bound field (the save source of truth)
        // so the flip survives the persist and every later settings save.
        _setAutonomyBound?.Invoke(true);
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
        _setAutonomyBound?.Invoke(false);
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

    /// <summary>Refreshes the account bar. Called on view load and balance events.</summary>
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

/// <summary>Tiny JSON helper for journal detail payloads.</summary>
internal static class JsonSerializerOps
{
    public static string ToJson(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
