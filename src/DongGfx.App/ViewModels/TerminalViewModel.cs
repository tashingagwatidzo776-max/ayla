using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Core.Update;

namespace DongGfx.App.ViewModels;    /// <summary>One row in the terminal's Market Watch grid.</summary>
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

/// <summary>One row in the MT5-style Trade tab: an open MT5 position,
/// normalized to a ticket-like grid with live P/L.</summary>
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
/// MT5 bridge card for CFD/forex orders routed to the running MetaTrader 5
/// terminal, and the forex brain's paper/live controls. Every MT5 order runs
/// the same rails: kill switch, real-money gate, lot caps. The brain's
/// autonomy switch lives here too: one switch governs every surface.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject
{
    private const int LadderLevels = 6;
    private const int CandleSeconds = 60; // M1 candles built from the tick feed

    private readonly TradeJournal _journal;
    private readonly Func<AppSettings> _settings;
    private readonly Action _persist;
    private readonly Func<bool> _isRealMoneyUnlocked;
    private readonly DashboardViewModel _dashboard;
    private readonly Mt5BridgeClient _mt5;
    private readonly TickArchive _tickArchive;

    // ── DON G FX forex brain (C): paper/live engine surfaced in the tab ──

    private FxPortfolioHost? _fxHost;
    private readonly Func<FxPortfolioHost?>? _fxHostFactory;

    [ObservableProperty]
    private string fxBadge = "FX BRAIN: OFF";

    [ObservableProperty]
    private string fxStatusText = "engine not running";

    public FxPortfolioHost? FxHost => _fxHost;

    [RelayCommand]
    private void ToggleFxBrain()
    {
        if (_fxHost is { } host && host.IsRunning)
        {
            host.Stop();
            FxBadge = "FX BRAIN: OFF";
            FxStatusText = "engine stopped";
            return;
        }

        _fxHost?.Dispose();
        _fxHost = _fxHostFactory?.Invoke();
        if (_fxHost is null)
        {
            FxStatusText = "brain unavailable (no factory)";
            return;
        }

        _fxHost.StatusChanged += s => OnUiThread(() => FxStatusText = s);
        _fxHost.Start();
        FxBadge = "FX BRAIN: PAPER";
        FxStatusText = $"engine running on {string.Join(", ", _fxHost.Symbols)} (paper mode)";
    }

    [RelayCommand]
    private void FxGoLive()
    {
        if (_fxHost is not { } host || !host.IsRunning)
        {
            FxStatusText = "start the brain first";
            return;
        }

        if (!host.PaperSoakComplete)
        {
            FxStatusText = $"go-live refused — paper soak {host.PaperSignalsSeen}/{host.PaperSoakSignalsRequired} signals";
            return;
        }

        host.GoLive();
        FxBadge = "FX BRAIN: LIVE";
    }

    /// <summary>Stops and disposes the FX portfolio host (app shutdown
    /// path): no engine cycle may fire while the app tears down the bridge
    /// client the engines order through.</summary>
    public void ShutdownFxBrain()
    {
        try
        {
            _fxHost?.Stop();
            _fxHost?.Dispose();
        }
        catch
        {
            // Teardown must never throw — the rest of shutdown proceeds.
        }

        _fxHost = null;
        FxBadge = "FX BRAIN: OFF";
    }

    [RelayCommand]
    private void FxGoPaper()
    {
        _fxHost?.GoPaper();
        FxBadge = "FX BRAIN: PAPER";
    }

    /// <summary>Operator re-arm after a supervisor loss stop.</summary>
    [RelayCommand]
    private void FxReArm()
    {
        if (_fxHost is not { } host)
        {
            FxStatusText = "start the brain first";
            return;
        }

        host.ReArmLossStop();
        FxStatusText = "loss stop re-armed — baseline re-anchored to live balance";
    }

    /// <summary>Kill-switch leg for the FX brain: stop it, force paper, and
    /// flatten every open MT5 position. Called by the dashboard's kill
    /// switch and by a governor trip.</summary>
    public async Task FxEmergencyFlattenAsync(string reason)
    {
        if (_fxHost is { } host)
        {
            host.Stop();
            host.GoPaper();
        }

        var closed = 0;
        try
        {
            foreach (var p in await _mt5.GetPositionsAsync().ConfigureAwait(true))
            {
                var r = await _mt5.ClosePositionAsync(p.Ticket).ConfigureAwait(true);
                if (r.Ok)
                {
                    closed++;
                }
            }
        }
        catch
        {
            // bridge down — nothing to flatten; journal below still records the stop
        }

        _journal?.Log(System.Guid.Empty, "FX_RISK",
            $"EMERGENCY FLATTEN ({reason}): brain stopped → paper, {closed} MT5 position(s) closed", "{}");
        StatusChangedInternal($"EMERGENCY FLATTEN ({reason}): brain stopped, {closed} position(s) closed");
    }

    private void StatusChangedInternal(string message) => OnUiThread(() => FxStatusText = message);

    // ── Terminal sign-in (A2): MT5 bridge status in the sign-in band ──

    /// <summary>MT5 bridge status line for the sign-in band (read-only here:
    /// the sidecar is a machine-level process, started by Watchdog/autostart).</summary>
    public string Mt5BridgeLine => IsMt5Connected
        ? $"MT5 bridge: connected · {(Mt5AccountText ?? "waiting for poll")}"
        : "MT5 bridge: down — auto-restart pending";

    private readonly Action<bool>? _setAutonomyBound;
    private readonly Action<string>? _setSymbolBound;
    private readonly PriceAlertEngine _alerts;   // Market Watch → Create Alert
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _mt5PollTimer;
    private readonly DispatcherTimer _mt5QuoteTimer;
    private readonly DispatcherTimer _mt5WatchTimer;
    private readonly List<(long Second, double Open, double High, double Low, double Close)> _candles = new();
    private int _busy;

    public TerminalViewModel(
        Func<AppSettings> settings,
        Action persist,
        Func<bool> isRealMoneyUnlocked,
        DashboardViewModel dashboard,
        TradeJournal? journal = null,
        Mt5BridgeClient? mt5 = null,
        Action<bool>? setAutonomyBound = null,
        Action<string>? setSymbolBound = null,
        TickArchive? tickArchive = null,
        Func<FxPortfolioHost?>? fxHostFactory = null,
        PriceAlertEngine? alerts = null)
    {
        // FIRST: the UI-thread helper is used from the constructor itself —
        // it must never see an unset dispatcher.
        _dispatcher = Dispatcher.CurrentDispatcher;
        _journal = journal ?? new TradeJournal(
            Path.Combine(Path.GetTempPath(), "dg-terminal-fallback-journal"));
        _settings = settings;
        _persist = persist;
        _isRealMoneyUnlocked = isRealMoneyUnlocked;
        _dashboard = dashboard;
        _mt5 = mt5 ?? new Mt5BridgeClient();
        _tickArchive = tickArchive ?? new TickArchive();
        _fxHostFactory = fxHostFactory;
        _setAutonomyBound = setAutonomyBound;
        _setSymbolBound = setSymbolBound;
        // Market Watch right-click → Create Alert arms PriceAlertEngine
        // alerts; a test-injected engine is used as-is (no toast plumbing).
        _alerts = alerts ?? new PriceAlertEngine(new NotificationService());

        // The Toolbox's Journal rows follow the FX engine, the supervisor and
        // the deal feed live (the journal is the app's single append path).
        _journal.EntryAdded += _ => OnUiThread(RefreshJournalRows);

        // Low-CPU model: one 3 s health/positions poll (was the only timer),
        // a 1 s selected-symbol quote refresh (event-like, single small HTTP
        // GET), and a 30 s full-watchlist refresh. UI updates ride the
        // OnUiThread path (pump-free); no fixed 3 s polling of everything.
        _mt5PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mt5PollTimer.Tick += async (_, _) => await PollMt5Async().ConfigureAwait(true);

        _mt5QuoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mt5QuoteTimer.Tick += async (_, _) => await RefreshMt5QuotesAsync().ConfigureAwait(true);

        _mt5WatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _mt5WatchTimer.Tick += async (_, _) => await RefreshMt5WatchAsync().ConfigureAwait(true);

        // Seed the ladder so the panel is never empty on first paint.
        RebuildLadder(0);
        _ = RefreshHistoryAsync();
    }

    private void OnUiThread(Action action)
    {
        // Begin-only (never Invoke/InvokeAsync): the app runs a message pump,
        // tests and Release CI do not, and an awaited InvokeAsync deadlocks
        // without one. BeginInvoke keeps queue order and works everywhere.
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>Starts the MT5 poll loop (called when the view loads).</summary>
    [RelayCommand]
    public void StartTerminal()
    {
        _mt5PollTimer.Start();
        _mt5QuoteTimer.Start();
        _mt5WatchTimer.Start();
        _ = PollMt5Async();
        _ = RefreshAccountBarAsync();
        _ = RefreshHistoryAsync();
        _ = RefreshBuildBadgeAsync();
    }

    // ── Build-freshness badge ──────────────────────────────────────

    private static DateTimeOffset? _lastBadgeProbe;

    /// <summary>Test seam: resets the badge throttle between tests.</summary>
    internal static void ResetBadgeThrottleForTests() => _lastBadgeProbe = null;

    /// <summary>The running build vs the latest published release, shown on
    /// the account bar so a stale binary is visible in-app instead of only
    /// in monitoring. Throttled to one probe per 5 minutes.</summary>
    [ObservableProperty]
    private string buildBadge = $"build {VersionInfo.Stamp}";

    /// <summary>Test seam: the release probe. Default is the real GitHub
    /// releases check; tests inject a canned value or a throwing probe.</summary>
    public Func<Task<string?>>? LatestReleaseProbe { get; set; }

    /// <summary>One badge refresh: current stamp vs the latest release tag.
    /// Never throws — a failed probe leaves the current stamp showing.</summary>
    public async Task RefreshBuildBadgeAsync()
    {
        // Throttle: StartTerminal fires on every view load; the probe is a
        // network call. One live probe per 5 minutes is plenty.
        if (_lastBadgeProbe is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastBadgeProbe = DateTimeOffset.UtcNow;
        try
        {
            LatestReleaseProbe ??= async () =>
            {
                var updater = new AutoUpdater(VersionInfo.FullVersion.Split('+')[0]);
                var update = await updater.CheckForUpdateAsync(AutoUpdater.GitHubReleasesUrl)
                    .ConfigureAwait(true);
                return update?.Version;
            };

            var latest = await LatestReleaseProbe().ConfigureAwait(true);
            if (string.IsNullOrEmpty(latest))
            {
                return;   // probe found nothing — keep the plain stamp
            }

            // Normalized compare: release tags are "v0.7.0" while the stamp
            // may be "0.7.0" (dev) or "0.7.0+sha" (published) — compare the
            // bare version on both sides or every published build would
            // claim an update exists.
            var currentBare = VersionInfo.Stamp.Split('+')[0].TrimStart('v', 'V');
            var latestBare = latest.Split('+')[0].TrimStart('v', 'V');

            BuildBadge = latestBare.Equals(currentBare, StringComparison.OrdinalIgnoreCase)
                ? $"build {VersionInfo.Stamp} · up to date"
                : $"build {VersionInfo.Stamp} · update available: {latest}";
        }
        catch
        {
            // No update verdict is better than a wrong one — keep the stamp.
        }
    }

    /// <summary>Test seam: one explicit MT5 poll without the timer.</summary>
    internal Task PollMt5ForTestsAsync() => PollMt5Async();

    /// <summary>Rebuilds the chart's tradable levels for the selected
    /// symbol: one entry line per position (open price) and per pending
    /// order (its price), plus each SL/TP that is actually set. Advisory
    /// entry lines carry no ticket; SL/TP carry the position's ticket and
    /// are the draggable ones. No chart attached → no-op.</summary>
    internal void RebuildChartPriceLines(
        IReadOnlyList<Mt5Position> positions, IReadOnlyList<Mt5PendingOrder> orders)
    {
        if (CandleChart is null)
        {
            return;
        }

        var symbol = SelectedSymbol?.Symbol;
        var lines = new List<Controls.CandleChartControl.ChartPriceLine>();
        foreach (var p in positions.Where(p => p.Symbol == symbol))
        {
            lines.Add(new Controls.CandleChartControl.ChartPriceLine(null, "entry", p.PriceOpen));
            if (p.Sl > 0)
            {
                lines.Add(new Controls.CandleChartControl.ChartPriceLine(p.Ticket, "sl", p.Sl));
            }

            if (p.Tp > 0)
            {
                lines.Add(new Controls.CandleChartControl.ChartPriceLine(p.Ticket, "tp", p.Tp));
            }
        }

        foreach (var o in orders.Where(o => o.Symbol == symbol))
        {
            lines.Add(new Controls.CandleChartControl.ChartPriceLine(null, "entry", o.Price));
            if (o.Sl > 0)
            {
                lines.Add(new Controls.CandleChartControl.ChartPriceLine(o.Ticket, "sl", o.Sl));
            }

            if (o.Tp > 0)
            {
                lines.Add(new Controls.CandleChartControl.ChartPriceLine(o.Ticket, "tp", o.Tp));
            }
        }

        CandleChart.SetPriceLines(lines);
    }

    /// <summary>Test seam: rebuild the history/journal rows on demand.</summary>
    internal Task RefreshHistoryForTests() => RefreshHistoryAsync();

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

    // ── Market Watch context menu (right-click a symbol) ───────────

    /// <summary>Right-click → New Order: pre-selects the symbol on the
    /// order ticket. No order is sent from the menu itself — the ticket's
    /// guards (kill switch, lots cap, real-money gate) stay the only path.</summary>
    [RelayCommand]
    private Task SymbolNewOrderAsync(TerminalSymbolRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        Mt5Symbol = row.Symbol;
        Mt5OrderStatus = $"order ticket ready: {row.Symbol} — choose action/type/lots and send";
        return Task.CompletedTask;
    }

    /// <summary>Right-click → Alert: arms one alert above and one below
    /// the symbol's last price (spread-offset). The engine collapses
    /// duplicates, so repeat clicks never stack. A row with no quote yet
    /// is refused with a hint instead of arming a nonsense trigger.</summary>
    [RelayCommand]
    private Task SymbolCreateAlertAsync(TerminalSymbolRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        var price = row.Last > 0 ? row.Last : (row.Bid > 0 ? row.Bid : 0);
        if (price <= 0)
        {
            MarketWatchStatus = $"no quote yet for {row.Symbol} — an alert needs a price";
            return Task.CompletedTask;
        }

        var offset = Math.Max(row.Spread, price * 0.0005);
        _alerts.Add(row.Symbol, "above", price + offset);
        _alerts.Add(row.Symbol, "below", price - offset);
        MarketWatchStatus = $"alerts armed: {row.Symbol} above {price + offset:0.#####} / below {price - offset:0.#####}";
        _journal.Log(Guid.Empty, "MT5_ALERT", $"{row.Symbol} ±{offset:0.#####} around {price:0.#####}");
        return Task.CompletedTask;
    }

    /// <summary>Right-click → Chart: selects the row — candles, ladder
    /// and the brain-symbol sync all follow from OnSelectedSymbolChanged.</summary>
    [RelayCommand]
    private Task SymbolOpenChartAsync(TerminalSymbolRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        SelectedSymbol = Symbols.FirstOrDefault(s => s.Symbol == row.Symbol) ?? row;
        return Task.CompletedTask;
    }

    partial void OnSelectedSymbolChanged(TerminalSymbolRow? value)
    {
        if (value is null)
        {
            return;
        }

        QuoteText = value.Last > 0 ? value.Last.ToString("0.#####") : "—";
        RebuildLadder(value.Bid > 0 ? value.Bid : value.Last);
        _ = LoadCandlesFromBridgeAsync(value.Symbol);

        // Sync the selected symbol into the shared settings so the brain
        // trades the same instrument the terminal shows. The settings
        // editor's bound field is the source of truth on save — writing
        // only the shared snapshot gets clobbered by the next save.
        var settings = _settings();
        if (!string.IsNullOrWhiteSpace(value.Symbol)
            && !string.Equals(settings.FxSymbol, value.Symbol, StringComparison.Ordinal))
        {
            settings.FxSymbol = value.Symbol;
            _setSymbolBound?.Invoke(value.Symbol);
            _persist();
        }
    }

    private void OnPublicTick(Tick tick)
    {
        _tickArchive.Add("mt5", tick.Symbol, tick.Bid, tick.Ask, tick.Epoch);

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
        }
    }

    [RelayCommand]
    private async Task LoadSymbolsAsync()
    {
        IsMarketWatchLoading = true;
        MarketWatchStatus = "loading catalog…";
        try
        {
            // MT5-native only: the bridge's catalog with live quotes is
            // the Market Watch source (XAUUSD, EURUSD, …); while the bridge
            // is down the watch stays empty and the hint below is shown.
            var mt5Symbols = await _mt5.GetSymbolsAsync().ConfigureAwait(true);
            if (mt5Symbols.Count > 0)
            {
                Symbols.Clear();
                foreach (var s in mt5Symbols)
                {
                    var row = new TerminalSymbolRow(s.Symbol, s.Description, s.TradeMode == 4 ? (bool?)true : null);
                    if (s.Bid is double bid && s.Ask is double ask)
                    {
                        ApplyQuote(row, bid, ask, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                    Symbols.Add(row);
                }

                MarketWatchStatus = $"{Symbols.Count} symbols · MT5 bridge";
            }
            else
            {
                MarketWatchStatus = "bridge down — run:  python bridge/mt5_sidecar.py";
                return;
            }

            var preferred = _settings().FxSymbols.Split(',')[0].Trim();
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

    /// <summary>Shared quote application for MT5 ticks: row state +
    /// selected-symbol quote text + ladder + candle aggregation in one place.</summary>
    private void ApplyQuote(TerminalSymbolRow row, double bid, double ask, long epochMs)
    {
        _tickArchive.Add("mt5", row.Symbol, bid, ask, epochMs);

        var quote = bid > 0 ? bid : ask;
        row.UpdateTick(new Tick(row.Symbol, quote, ask, bid, epochMs, 0));

        // Session high/low roll (MT5's Market Watch columns): seeded from
        // the symbol catalog's daily stats when present, extended by ticks.
        if (quote > 0)
        {
            if (row.DayHigh == 0 || quote > row.DayHigh) { row.DayHigh = quote; }
            if (row.DayLow == 0 || quote < row.DayLow) { row.DayLow = quote; }
        }

        if (SelectedSymbol?.Symbol == row.Symbol)
        {
            QuoteText = quote.ToString("0.#####");
            _ = RebuildLadderFromBookAsync(row.Symbol, bid > 0 ? bid : quote);
            AggregateTick(new Tick(row.Symbol, quote, ask, bid, epochMs, 0));
        }
    }

    /// <summary>Event-driven MT5 quotes: refresh only the selected symbol
    /// every 1 s (not a 3 s poll of account+positions+quotes together) and
    /// the whole watchlist every 30 s. No-op while the bridge is down.</summary>
    private async Task RefreshMt5QuotesAsync()
    {
        if (IsMt5Busy || !IsMt5Connected)
        {
            return;
        }

        var symbol = SelectedSymbol?.Symbol;
        if (string.IsNullOrEmpty(symbol))
        {
            return;
        }

        try
        {
            var tick = await _mt5.GetTickAsync(symbol).ConfigureAwait(true);
            if (tick is { } t)
            {
                var row = Symbols.FirstOrDefault(s => s.Symbol == symbol)
                          ?? (SelectedSymbol?.Symbol == symbol ? SelectedSymbol : null);
                if (row is not null)
                {
                    ApplyQuote(row, t.Bid, t.Ask, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
            }
        }
        catch
        {
            // bridge hiccup — the poll's health check will flag real outages
        }
    }

    /// <summary>30 s watchlist refresh: pull the bridge catalog and update
    /// the rows in place (no Clear/Add churn — the grid does not re-virtualize).
    /// New symbols get appended; vanished ones stay but lose quotes.</summary>
    private async Task RefreshMt5WatchAsync()
    {
        if (IsMt5Busy || !IsMt5Connected)
        {
            return;
        }

        try
        {
            var mt5Symbols = await _mt5.GetSymbolsAsync().ConfigureAwait(true);
            if (mt5Symbols.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var s in mt5Symbols)
            {
                var row = Symbols.FirstOrDefault(x => x.Symbol == s.Symbol);
                if (row is null)
                {
                    row = new TerminalSymbolRow(s.Symbol, s.Description, s.TradeMode == 4 ? (bool?)true : null);
                    OnUiThread(() => Symbols.Add(row));
                }

                if (s.Bid is double bid && s.Ask is double ask)
                {
                    ApplyQuote(row, bid, ask, now);
                }
            }

            if (SelectedSymbol is null)
            {
                SelectedSymbol = Symbols.FirstOrDefault();
            }
        }
        catch
        {
            // best-effort — MarketWatchStatus already reflects bridge state
        }
    }

    // ── Candle chart (M1, built from the live tick feed) ───────────

    public sealed record CandleDto(string TimeText, double Open, double High, double Low, double Close,
        double BarHeight, bool Up);

    public ObservableCollection<CandleDto> Candles { get; } = new();

    /// <summary>Out-of-band outlet for the MT5-style candle chart (assigned
    /// by MainWindow's code-behind, like Dashboard.Chart). RenderCandles
    /// feeds it the full bar window.</summary>
    public Controls.CandleChartControl? CandleChart { get; set; }

    [ObservableProperty]
    private string ohlcText = "select a symbol";

    /// <summary>One-line indicator read-out for the chart header
    /// (EMA value + RSI state), refreshed with the candles.</summary>
    [ObservableProperty]
    private string indicatorText = "";

    [ObservableProperty]
    private string candleSourceText = "M1 candles · live tick feed";

    /// <summary>The chart's timeframe (MT5's M1…MN1 selector). The bridge
    /// serves all 21 MT5 timeframes; changing it refetches the candles.</summary>
    [ObservableProperty]
    private string selectedTimeframe = "M1";

    public System.Collections.Generic.IReadOnlyList<string> Timeframes { get; } =
        new[] { "M1", "M5", "M15", "M30", "H1", "H4", "D1", "W1", "MN1" };

    partial void OnSelectedTimeframeChanged(string value)
    {
        if (SelectedSymbol is { } row && IsMt5Connected)
        {
            _ = LoadCandlesFromBridgeAsync(row.Symbol);
        }
    }

    /// <summary>The EMA(20) overlay over the current candle window, aligned
    /// with Candles (null warm-up renders as gaps). Consumed by the chart
    /// header and any external overlay surface.</summary>
    public System.Collections.Generic.IReadOnlyList<IndicatorPoint> EmaOverlay
    {
        get
        {
            var closes = Candles.Select(c => c.Close).ToArray();
            return closes.Length == 0 ? Array.Empty<IndicatorPoint>() : FxIndicators.Ema(closes, 20);
        }
    }

    /// <summary>RSI(14) over the current window, for the status strip.</summary>
    public double? RsiLast
    {
        get
        {
            var closes = Candles.Select(c => c.Close).ToArray();
            if (closes.Length < 15)
            {
                return null;
            }

            return FxIndicators.Rsi(closes, 14)[^1].Value;
        }
    }

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
        var fromBridge = await _mt5.GetCandlesAsync(symbol, SelectedTimeframe, 90).ConfigureAwait(true);
        if (fromBridge.Count > 0)
        {
            _candles.Clear();
            foreach (var c in fromBridge)
            {
                _candles.Add((c.Time, c.Open, c.High, c.Low, c.Close));
            }

            CandleSourceText = $"{SelectedTimeframe} candles · MT5 bridge";
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

        // MT5-style candle chart gets the full window (wicks + axes there).
        CandleChart?.SetBars(_candles.Select(c =>
            new Core.Fx.FxBar(c.Second, c.Open, c.High, c.Low, c.Close, 0)));

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

        // Keep the indicator overlays and header read-out in lock-step with
        // the candles (live ticks re-render through this same path).
        OnPropertyChanged(nameof(EmaOverlay));
        OnPropertyChanged(nameof(RsiLast));
        var closes = Candles.Select(c => c.Close).ToArray();
        if (closes.Length >= 20 && FxIndicators.Ema(closes, 20)[^1].Value is { } emaValue)
        {
            var rsi = closes.Length >= 15 ? FxIndicators.Rsi(closes, 14)[^1].Value : null;
            IndicatorText = $"EMA(20) {emaValue:0.#####}"
                + (rsi is { } r ? $" · RSI(14) {r:0.0}" : "");
        }
        else
        {
            IndicatorText = "";
        }
    }

    // ── Price ladder (synthetic DOM — the bridge's /book is empty on Deriv-Demo) ──

    public ObservableCollection<TerminalLadderRow> Ladder { get; } = new();

    [ObservableProperty]
    private string domSourceText = "DOM: synthetic (bid/ask)";

    /// <summary>Real DOM when the broker streams it (Deriv does not — the
    /// probe showed 0 levels), synthetic ladder otherwise. Called on the
    /// selected symbol's quote refresh.</summary>
    private async Task RebuildLadderFromBookAsync(string symbol, double syntheticCenter)
    {
        try
        {
            var book = await _mt5.GetBookAsync(symbol).ConfigureAwait(true);
            if (book.Count > 0)
            {
                OnUiThread(() =>
                {
                    DomSourceText = $"DOM: real ({book.Count} levels)";
                    Ladder.Clear();
                    foreach (var level in book.OrderByDescending(b => b.Price))
                    {
                        Ladder.Add(new TerminalLadderRow(level.Price, level.Side) { Size = (int)Math.Min(level.Volume, 1_000_000) });
                    }
                });
                return;
            }
        }
        catch
        {
            // book fetch failed — synthetic fallback below
        }

        DomSourceText = "DOM: synthetic (bid/ask)";
        RebuildLadder(syntheticCenter);
    }

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

    // ── Order ticket status (shared by the brain switch + MT5 card) ───

    [ObservableProperty]
    private string ticketStatus = "";

    [ObservableProperty]
    private bool isTicketBusy;

    // ── Positions (MT5) ───────────────────────────────────────

    // ── Toolbox: Trade / Exposure / Account History / Journal ──────

    public ObservableCollection<TerminalTradeRow> TradeRows { get; } = new();

    public ObservableCollection<TerminalExposureRow> ExposureRows { get; } = new();

    public ObservableCollection<TerminalHistoryRow> HistoryRows { get; } = new();

    public ObservableCollection<JournalEntryViewModel> JournalRows { get; } = new();

    /// <summary>Open (pending) orders — MT5's Trade tab keeps them beside
    /// positions; rebuilt on every MT5 poll.</summary>
    public ObservableCollection<Mt5PendingOrder> OrderRows { get; } = new();

    [ObservableProperty]
    private string orderStatus = "";

    /// <summary>Account-history range (ISO dates for the bridge's
    /// /deals?from=&to=). Empty = the trailing-7-days default.</summary>
    [ObservableProperty]
    private string historyFrom = "";

    [ObservableProperty]
    private string historyTo = "";

    private void RebuildTradeRows()
    {
        TradeRows.Clear();
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

    /// <summary>Rebuilds the Toolbox rows: deal history from the bridge
    /// (settled FX trades) plus the journal's latest entries.</summary>
    private async Task RefreshHistoryAsync()
    {
        try
        {
            var deals = string.IsNullOrWhiteSpace(HistoryFrom) && string.IsNullOrWhiteSpace(HistoryTo)
                ? await _mt5.GetDealsAsync(days: 7).ConfigureAwait(true)
                : await _mt5.GetDealsAsync(
                    string.IsNullOrWhiteSpace(HistoryFrom) ? "2000-01-01" : HistoryFrom.Trim(),
                    string.IsNullOrWhiteSpace(HistoryTo) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : HistoryTo.Trim())
                    .ConfigureAwait(true);
            HistoryRows.Clear();
            foreach (var d in deals
                         .Where(d => d.Profit + d.Commission + d.Swap != 0)
                         .OrderByDescending(d => d.Time)
                         .Take(50))
            {
                HistoryRows.Add(new TerminalHistoryRow(
                    DateTimeOffset.FromUnixTimeSeconds(d.Time).LocalDateTime.ToString("MM-dd HH:mm"),
                    d.Symbol,
                    d.Side.ToUpperInvariant(),
                    d.Volume,
                    d.Profit + d.Commission + d.Swap >= 0 ? "win" : "loss",
                    d.Profit + d.Commission + d.Swap,
                    "FX"));
            }
        }
        catch
        {
            // bridge down — the account-history rows stay as they were
        }

        RefreshJournalRows();
    }

    private void RefreshJournalRows()
    {
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
                    OrderRows.Clear();
                }

                return;
            }

            var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
            var positions = await _mt5.GetPositionsAsync().ConfigureAwait(true);
            var orders = await _mt5.GetOrdersAsync().ConfigureAwait(true);
            OnUiThread(() =>
            {
                IsMt5Connected = true;
                Mt5StatusText = "MT5 bridge: connected";
                if (account is not null)
                {
                    Mt5AccountText = $"{account.Login} @ {account.Server} · {account.Equity:0.##} {account.Currency}";
                }

                _mt5Positions = positions.ToList();
                RebuildTradeRows();
                OrderRows.Clear();
                foreach (var o in orders)
                {
                    OrderRows.Add(o);
                }

                RebuildChartPriceLines(positions, orders);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>The one order path: every guard (kill switch, lot cap,
    /// lots sanity, pending-type prices, bridge, real-money gate) and the
    /// send+journal. Both the order ticket and the chart context menu go
    /// through here — a chart order is a market order at bid/ask with the
    /// same Mt5Lots sizing and the full gate. Returns the status line.</summary>
    private async Task<string> ExecuteMt5OrderAsync(
        string symbol, string action, double lots,
        string type = "market", double? price = null,
        double? sl = null, double? tp = null)
    {
        var settings = _settings();

        if (_dashboard.IsKillSwitchEngaged)
        {
            return "kill switch is engaged — reset it on the Dashboard first";
        }

        // The MT5 lot cap fail-closes at 0: MT5 order placement must be
        // deliberately enabled by the operator.
        if (settings.Mt5MaxLots <= 0)
        {
            return "MT5 orders are disabled (Mt5MaxLots = 0) — enable it on the Settings tab";
        }

        if (lots <= 0 || (decimal)lots > settings.Mt5MaxLots)
        {
            return $"lots {lots:0.##} outside the allowed 0 < lots ≤ {settings.Mt5MaxLots:0.##}";
        }

        // The bridge's account decides demo/real; a real MT5 account obeys
        // the same session unlock as every other real-money path.
        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (account is null)
        {
            return "bridge unavailable — run:  python bridge/mt5_sidecar.py";
        }

        // Demo/real: the bridge's account_info().trade_mode is the venue's
        // own verdict (0=demo, 2=real); the server-name heuristic is a
        // demo-only fallback for older sidecars (see Mt5Account). From one
        // verdict, config and API verification agree — demo passes through,
        // real demands the session unlock.
        var verifiedVirtual = account.GateVerifiedVirtual;
        var unlocked = _isRealMoneyUnlocked?.Invoke() ?? false;
        var gate = RealMoneyGate.Evaluate(configIsDemo: verifiedVirtual is true,
                                          apiVerifiedVirtual: verifiedVirtual,
                                          unlockArmed: unlocked);
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            return RealMoneyGate.Explain(gate);
        }

        IsMt5Busy = true;
        try
        {
            var result = await _mt5.PlaceOrderAsync(
                symbol, action, type, lots, price, null, sl, tp).ConfigureAwait(true);

            var status = result.Ok
                ? $"{action} {type} filled: ticket {result.Order ?? result.Deal} @ {result.Price:0.#####}"
                : $"refused: {result.RetcodeName} ({result.Retcode})";

            _journal.Log(Guid.Empty, "MT5_ORDER",
                $"{action} {type} {lots} {symbol} → {result.RetcodeName}",
                JsonSerializerOps.ToJson(new
                {
                    result.Retcode,
                    result.Order,
                    result.Deal,
                    result.Price,
                    Server = account.Server,
                }));

            _ = PollMt5Async();
            return status;
        }
        catch (Mt5BridgeException ex)
        {
            return $"order refused: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"order failed: {ex.Message}";
        }
        finally
        {
            IsMt5Busy = false;
        }
    }

    [RelayCommand]
    private async Task PlaceMt5OrderAsync()
    {
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

        // The ticket's own pending-type validation rides ahead of the
        // shared executor; everything else (guards + send) is one path.
        Mt5OrderStatus = await ExecuteMt5OrderAsync(
            Mt5Symbol, Mt5Action, Mt5Lots, Mt5Type, Mt5Price, Mt5Sl, Mt5Tp)
            .ConfigureAwait(true);
    }

    /// <summary>Chart order: buy/sell at market on the chart's symbol at
    /// the ticket's size — every guard of the shared executor applies, so
    /// the chart is a shortcut to the ticket, never a bypass.</summary>
    [RelayCommand]
    private async Task PlaceChartOrderAsync(string? side)
    {
        if (side is not ("buy" or "sell"))
        {
            return;
        }

        var symbol = SelectedSymbol?.Symbol ?? Mt5Symbol;
        Mt5OrderStatus = $"chart order sending: {side} {Mt5Lots:0.##} {symbol}…";
        Mt5OrderStatus = await ExecuteMt5OrderAsync(symbol, side, Mt5Lots)
            .ConfigureAwait(true);
    }

    /// <summary>A chart SL/TP line was dragged: modify only that leg of
    /// the position. Journaled and re-polled like the ticket's modify.</summary>
    [RelayCommand]
    private async Task ChartLineDraggedAsync(string? spec)
    {
        // spec: "<ticket>|<kind>|<price>" (event adapter from code-behind).
        var parts = (spec ?? "").Split('|');
        if (parts.Length != 3
            || !long.TryParse(parts[0], out var ticket)
            || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var price)
            || parts[1] is not ("sl" or "tp"))
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
            var result = parts[1] == "sl"
                ? await _mt5.ModifyPositionAsync(ticket, sl: price).ConfigureAwait(true)
                : await _mt5.ModifyPositionAsync(ticket, tp: price).ConfigureAwait(true);
            Mt5OrderStatus = result.Ok
                ? $"position {ticket} {parts[1]} dragged to {price:0.#####}"
                : $"drag modify refused: {result.RetcodeName}";
            _journal.Log(Guid.Empty, "MT5_ORDER", $"drag modify {ticket} {parts[1]}={price:0.#####} → {result.RetcodeName}");
            _ = PollMt5Async();
        }
        catch (Exception ex)
        {
            Mt5OrderStatus = $"drag modify failed: {ex.Message}";
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

    /// <summary>Close part of a position (MT5 partial close): the lots are
    /// validated by the bridge against the symbol's volume geometry; the
    /// kill switch guards it like every close.</summary>
    [RelayCommand]
    private async Task PartialCloseMt5PositionAsync(long? ticket)
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

        if (!double.TryParse(PartialCloseLots, System.Globalization.CultureInfo.InvariantCulture, out var lots) || lots <= 0)
        {
            Mt5OrderStatus = "enter the lots to close (e.g. 0.1)";
            return;
        }

        try
        {
            var result = await _mt5.ClosePositionAsync(ticket.Value, lots).ConfigureAwait(true);
            Mt5OrderStatus = result.Ok
                ? $"position {ticket} partially closed ({lots:0.##} lots)"
                : $"partial close refused: {result.RetcodeName}";
            _journal.Log(Guid.Empty, "MT5_ORDER", $"partial close {ticket} {lots:0.##} → {result.RetcodeName}");
            _ = PollMt5Async();
        }
        catch (Exception ex)
        {
            Mt5OrderStatus = $"partial close failed: {ex.Message}";
        }
    }

    /// <summary>The lots box feeding the partial-close button.</summary>
    [ObservableProperty]
    private string partialCloseLots = "";

    /// <summary>Change SL/TP on an open position. Pass only the legs being
    /// changed; the bridge enforces the broker's stops level.</summary>
    [RelayCommand]
    private async Task ModifyMt5PositionAsync(long? ticket)
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

        double? sl = double.TryParse(ModifySl, System.Globalization.CultureInfo.InvariantCulture, out var slv) ? slv : null;
        double? tp = double.TryParse(ModifyTp, System.Globalization.CultureInfo.InvariantCulture, out var tpv) ? tpv : null;
        if (sl is null && tp is null)
        {
            Mt5OrderStatus = "enter an SL and/or TP to apply";
            return;
        }

        try
        {
            var result = await _mt5.ModifyPositionAsync(ticket.Value, sl, tp).ConfigureAwait(true);
            Mt5OrderStatus = result.Ok
                ? $"position {ticket} SL/TP updated"
                : $"modify refused: {result.RetcodeName}";
            _journal.Log(Guid.Empty, "MT5_ORDER", $"modify {ticket} sl={sl} tp={tp} → {result.RetcodeName}");
            _ = PollMt5Async();
        }
        catch (Exception ex)
        {
            Mt5OrderStatus = $"modify failed: {ex.Message}";
        }
    }

    /// <summary>SL/TP edit boxes feeding the modify button (empty = keep).</summary>
    [ObservableProperty]
    private string modifySl = "";

    [ObservableProperty]
    private string modifyTp = "";

    /// <summary>Cancel (delete) a pending order.</summary>
    [RelayCommand]
    private async Task CancelMt5OrderAsync(long? ticket)
    {
        if (ticket is null)
        {
            return;
        }

        try
        {
            var result = await _mt5.CancelOrderAsync(ticket.Value).ConfigureAwait(true);
            OrderStatus = result.Ok
                ? $"pending order {ticket} cancelled"
                : $"cancel refused: {result.RetcodeName}";
            _journal.Log(Guid.Empty, "MT5_ORDER", $"cancel {ticket} → {result.RetcodeName}");
            _ = PollMt5Async();
        }
        catch (Exception ex)
        {
            OrderStatus = $"cancel failed: {ex.Message}";
        }
    }

    /// <summary>Re-pulls the history rows honoring the from/to range boxes.</summary>
    [RelayCommand]
    private async Task ApplyHistoryRangeAsync()
    {
        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    /// <summary>MT5-style account-history export: the visible rows as CSV
    /// into the Downloads folder. Returns the path via HistoryExportStatus.</summary>
    [RelayCommand]
    private void ExportHistoryCsv()
    {
        if (HistoryRows.Count == 0)
        {
            HistoryExportStatus = "nothing to export";
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"donggfx-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("time,symbol,side,volume,outcome,profit,source");
            foreach (var r in HistoryRows)
            {
                sb.AppendLine(string.Join(",",
                    r.Time.Replace(",", " "),
                    r.Symbol,
                    r.Side,
                    r.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Outcome.Replace(",", " "),
                    r.Profit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Source.Replace(",", " ")));
            }

            File.WriteAllText(path, sb.ToString());
            HistoryExportStatus = $"exported {HistoryRows.Count} rows → {path}";
        }
        catch (Exception ex)
        {
            HistoryExportStatus = $"export failed: {ex.Message}";
        }
    }

    [ObservableProperty]
    private string historyExportStatus = "";

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

    /// <summary>Refreshes the account bar from the MT5 bridge (login, server,
    /// balance). Called on view load and balance events.</summary>
    [RelayCommand]
    public async Task RefreshAccountBarAsync()
    {
        try
        {
            var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
            if (account is null)
            {
                AccountText = "not connected";
                ConnectionText = "bridge offline";
                BalanceText = "—";
                return;
            }

            AccountText = account.Login.ToString();
            ConnectionText = account.Server;
            BalanceText = $"{account.Balance:0.##} {account.Currency}";
        }
        catch
        {
            AccountText = "not connected";
            ConnectionText = "bridge offline";
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


