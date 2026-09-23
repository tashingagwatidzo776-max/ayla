using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
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
        Func<FxPortfolioHost?>? fxHostFactory = null)
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

        if (SelectedSymbol?.Symbol == row.Symbol)
        {
            QuoteText = quote.ToString("0.#####");
            RebuildLadder(bid > 0 ? bid : quote);
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

    // ── Price ladder (synthetic DOM — the bridge's /book is empty on Deriv-Demo) ──

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
            var deals = await _mt5.GetDealsAsync(days: 7).ConfigureAwait(true);
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
                }

                return;
            }

            var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
            var positions = await _mt5.GetPositionsAsync().ConfigureAwait(true);
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


