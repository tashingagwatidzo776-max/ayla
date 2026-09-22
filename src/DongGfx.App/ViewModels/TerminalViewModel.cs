commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.App/ViewModels/TerminalViewModel.cs b/src/DongGfx.App/ViewModels/TerminalViewModel.cs
index 85cf98a..5c5d90a 100644
--- a/src/DongGfx.App/ViewModels/TerminalViewModel.cs
+++ b/src/DongGfx.App/ViewModels/TerminalViewModel.cs
@@ -189,10 +189,198 @@ public sealed partial class TerminalViewModel : ObservableObject
     private readonly DashboardViewModel _dashboard;
     private readonly PublicMarketDataClient _public;
     private readonly Mt5BridgeClient _mt5;
+    private readonly TickArchive _tickArchive;
+    private readonly Func<AccountsViewModel> _accounts;
+
+    // ── DON G FX forex brain (C): paper/live engine surfaced in the tab ──
+
+    private FxEngineHost? _fxHost;
+    private readonly Func<FxEngineHost?> _fxHostFactory;
+
+    [ObservableProperty]
+    private string fxBadge = "FX BRAIN: OFF";
+
+    [ObservableProperty]
+    private string fxStatusText = "engine not running";
+
+    public FxEngineHost? FxHost => _fxHost;
+
+    [RelayCommand]
+    private void ToggleFxBrain()
+    {
+        if (_fxHost is { } host && host.IsRunning)
+        {
+            host.Stop();
+            FxBadge = "FX BRAIN: OFF";
+            FxStatusText = "engine stopped";
+            return;
+        }
+
+        _fxHost?.Dispose();
+        _fxHost = _fxHostFactory?.Invoke();
+        if (_fxHost is null)
+        {
+            FxStatusText = "brain unavailable (no factory)";
+            return;
+        }
+
+        _fxHost.StatusChanged += s => OnUiThread(() => FxStatusText = s);
+        _fxHost.Start();
+        FxBadge = "FX BRAIN: PAPER";
+        FxStatusText = $"engine running on {_fxHost.Symbol} {_fxHost.Timeframe} (paper mode)";
+    }
+
+    [RelayCommand]
+    private void FxGoLive()
+    {
+        if (_fxHost is not { } host || !host.IsRunning)
+        {
+            FxStatusText = "start the brain first";
+            return;
+        }
+
+        if (!host.PaperSoakComplete)
+        {
+            FxStatusText = $"go-live refused — paper soak {host.PaperSignalsSeen}/{host.PaperSoakSignalsRequired} signals";
+            return;
+        }
+
+        host.GoLive();
+        FxBadge = "FX BRAIN: LIVE";
+    }
+
+    [RelayCommand]
+    private void FxGoPaper()
+    {
+        _fxHost?.GoPaper();
+        FxBadge = "FX BRAIN: PAPER";
+    }
+
+    // ── Terminal sign-in (A2): accounts + PAT/OAuth inside the Terminal ──
+
+    /// <summary>Every hub account as a read-only wrapper row, rebuilt when
+    /// the hub's account set changes. The hub stays the single owner of
+    /// connection state — the Terminal only projects it.</summary>
+    public ObservableCollection<TerminalAccountRow> TerminalAccounts { get; } = new();
+
+    /// <summary>MT5 bridge status line for the sign-in band (read-only here:
+    /// the sidecar is a machine-level process, started by Watchdog/autostart).</summary>
+    public string Mt5BridgeLine => IsMt5Connected
+        ? $"MT5 bridge: connected · {(Mt5AccountText ?? "waiting for poll")}"
+        : "MT5 bridge: down — auto-restart pending";
+
+    [ObservableProperty]
+    private string terminalTokenInput = "";
+
+    [ObservableProperty]
+    private string terminalSignInStatus = "";
+
+    [ObservableProperty]
+    private bool isTerminalSignInBusy;
+
+    /// <summary>Rebuild the account wrapper rows from the hub. The accounts
+    /// factory is optional at construction (tests, DI ordering) — an unwired
+    /// factory just leaves the sign-in band empty instead of throwing.
+    /// </summary>
+    private void RebuildTerminalAccounts()
+    {
+        OnUiThread(() =>
+        {
+            TerminalAccounts.Clear();
+            try
+            {
+                foreach (var a in _accounts().Hub.Accounts)
+                {
+                    TerminalAccounts.Add(new TerminalAccountRow(a));
+                }
+            }
+            catch (InvalidOperationException)
+            {
+                // no AccountsViewModel wired — sign-in stays unavailable
+            }
+            OnPropertyChanged(nameof(Mt5BridgeLine));
+        });
+    }
+
+    [RelayCommand]
+    private async Task TerminalImportTokenAsync()
+    {
+        if (IsTerminalSignInBusy)
+        {
+            return;
+        }
+
+        var token = TerminalTokenInput.Trim();
+        if (token.Length < 8)
+        {
+            TerminalSignInStatus = "Paste a Deriv token (at least 8 characters).";
+            return;
+        }
+
+        IsTerminalSignInBusy = true;
+        TerminalSignInStatus = "verifying via discovery…";
+        try
+        {
+            var added = await _accounts().ImportTokenFromTerminalAsync(token).ConfigureAwait(true);
+            TerminalTokenInput = "";
+            TerminalSignInStatus = added > 0
+                ? "signed in — account added and connecting."
+                : "that token's account is already signed in.";
+            RebuildTerminalAccounts();
+        }
+        catch (Exception ex)
+        {
+            TerminalSignInStatus = $"sign-in failed: {ex.Message}";
+        }
+        finally
+        {
+            IsTerminalSignInBusy = false;
+        }
+    }
+
+    [RelayCommand]
+    private async Task TerminalSignInOAuthAsync()
+    {
+        if (IsTerminalSignInBusy)
+        {
+            return;
+        }
+
+        IsTerminalSignInBusy = true;
+        TerminalSignInStatus = "browser opening — approve the Deriv consent screen…";
+        try
+        {
+            await _accounts().SignInFromTerminalAsync().ConfigureAwait(true);
+            TerminalSignInStatus = "OAuth sign-in complete — accounts imported.";
+            RebuildTerminalAccounts();
+        }
+        catch (Exception ex)
+        {
+            TerminalSignInStatus = $"OAuth sign-in failed: {ex.Message}";
+        }
+        finally
+        {
+            IsTerminalSignInBusy = false;
+        }
+    }
+
+    [RelayCommand]
+    private void TerminalConnectAccount(TerminalAccountRow? row) => row?.Connect();
+
+    [RelayCommand]
+    private async Task TerminalDisconnectAccountAsync(TerminalAccountRow? row)
+    {
+        if (row is not null)
+        {
+            await row.DisconnectAsync().ConfigureAwait(true);
+        }
+    }
     private readonly Action<bool>? _setAutonomyBound;
     private readonly Action<string>? _setSymbolBound;
     private readonly Dispatcher _dispatcher;
     private readonly DispatcherTimer _mt5PollTimer;
+    private readonly DispatcherTimer _mt5QuoteTimer;
+    private readonly DispatcherTimer _mt5WatchTimer;
     private readonly List<(long Second, double Open, double High, double Low, double Close)> _candles = new();
     private int _busy;
 
@@ -207,9 +395,12 @@ public sealed partial class TerminalViewModel : ObservableObject
         TradeJournal? journal = null,
         Mt5BridgeClient? mt5 = null,
         Action<bool>? setAutonomyBound = null,
-        Action<string>? setSymbolBound = null)
+        Action<string>? setSymbolBound = null,
+        TickArchive? tickArchive = null,
+        Func<AccountsViewModel>? accounts = null,
+        Func<FxEngineHost?>? fxHostFactory = null)
         : this(client, hub, store, settings, persist, isRealMoneyUnlocked, dashboard,
-               journal, mt5, setAutonomyBound, setSymbolBound, new PublicMarketDataClient())
+               journal, mt5, setAutonomyBound, setSymbolBound, new PublicMarketDataClient(), tickArchive, accounts, fxHostFactory)
     {
     }
 
@@ -225,8 +416,14 @@ public sealed partial class TerminalViewModel : ObservableObject
         Mt5BridgeClient? mt5,
         Action<bool>? setAutonomyBound,
         Action<string>? setSymbolBound,
-        PublicMarketDataClient publicClient)
+        PublicMarketDataClient publicClient,
+        TickArchive? tickArchive = null,
+        Func<AccountsViewModel>? accounts = null,
+        Func<FxEngineHost?>? fxHostFactory = null)
     {
+        // FIRST: the UI-thread helper is used from the constructor itself
+        // (hub subscription below) — it must never see an unset dispatcher.
+        _dispatcher = Dispatcher.CurrentDispatcher;
         _client = client;
         _hub = hub;
         _store = store;
@@ -238,9 +435,14 @@ public sealed partial class TerminalViewModel : ObservableObject
         _dashboard = dashboard;
         _public = publicClient;
         _mt5 = mt5 ?? new Mt5BridgeClient();
+        _tickArchive = tickArchive ?? new TickArchive();
+        _accounts = accounts ?? (() => throw new InvalidOperationException(
+            "Terminal sign-in requires the AccountsViewModel (DI wiring)"));
+        _hub.AccountsChanged += RebuildTerminalAccounts;
+        RebuildTerminalAccounts();
+        _fxHostFactory = fxHostFactory;
         _setAutonomyBound = setAutonomyBound;
         _setSymbolBound = setSymbolBound;
-        _dispatcher = Dispatcher.CurrentDispatcher;
 
         _public.TickReceived += OnPublicTick;
         _store.TradeAdded += OnTradeAdded;
@@ -253,9 +455,19 @@ public sealed partial class TerminalViewModel : ObservableObject
             }
         };
 
+        // Low-CPU model: one 3 s health/positions poll (was the only timer),
+        // a 1 s selected-symbol quote refresh (event-like, single small HTTP
+        // GET), and a 30 s full-watchlist refresh. UI updates ride the
+        // OnUiThread path (pump-free); no fixed 3 s polling of everything.
         _mt5PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
         _mt5PollTimer.Tick += async (_, _) => await PollMt5Async().ConfigureAwait(true);
 
+        _mt5QuoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
+        _mt5QuoteTimer.Tick += async (_, _) => await RefreshMt5QuotesAsync().ConfigureAwait(true);
+
+        _mt5WatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
+        _mt5WatchTimer.Tick += async (_, _) => await RefreshMt5WatchAsync().ConfigureAwait(true);
+
         // Seed the ladder so the panel is never empty on first paint.
         RebuildLadder(0);
         RefreshHistory();
@@ -281,6 +493,8 @@ public sealed partial class TerminalViewModel : ObservableObject
     public void StartTerminal()
     {
         _mt5PollTimer.Start();
+        _mt5QuoteTimer.Start();
+        _mt5WatchTimer.Start();
         _ = PollMt5Async();
         RefreshAccountBar();
         RefreshHistory();
@@ -364,6 +578,8 @@ public sealed partial class TerminalViewModel : ObservableObject
 
     private void OnPublicTick(Tick tick)
     {
+        _tickArchive.Add("deriv", tick.Symbol, tick.Bid, tick.Ask, tick.Epoch);
+
         var row = Symbols.FirstOrDefault(s => s.Symbol == tick.Symbol)
                   ?? (SelectedSymbol?.Symbol == tick.Symbol ? SelectedSymbol : null);
         if (row is not null)
@@ -390,14 +606,37 @@ public sealed partial class TerminalViewModel : ObservableObject
         MarketWatchStatus = "loading catalog…";
         try
         {
-            var symbols = await _public.GetActiveSymbolsAsync();
-            Symbols.Clear();
-            foreach (var s in symbols)
+            // MT5-native first: the bridge's catalog with live quotes is the
+            // Market Watch source (XAUUSD, EURUSD, …). Deriv's catalog is
+            // only the fallback while the bridge is down.
+            var mt5Symbols = await _mt5.GetSymbolsAsync().ConfigureAwait(true);
+            if (mt5Symbols.Count > 0)
+            {
+                Symbols.Clear();
+                foreach (var s in mt5Symbols)
+                {
+                    var row = new TerminalSymbolRow(s.Symbol, s.Description, s.TradeMode == 4 ? (bool?)true : null);
+                    if (s.Bid is double bid && s.Ask is double ask)
+                    {
+                        ApplyQuote(row, bid, ask, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
+                    }
+                    Symbols.Add(row);
+                }
+
+                MarketWatchStatus = $"{Symbols.Count} symbols · MT5 bridge";
+            }
+            else
             {
-                Symbols.Add(new TerminalSymbolRow(s.Symbol, s.DisplayName, s.ExchangeIsOpen));
+                var symbols = await _public.GetActiveSymbolsAsync();
+                Symbols.Clear();
+                foreach (var s in symbols)
+                {
+                    Symbols.Add(new TerminalSymbolRow(s.Symbol, s.DisplayName, s.ExchangeIsOpen));
+                }
+
+                MarketWatchStatus = $"{Symbols.Count} symbols · Deriv catalog (bridge down)";
             }
 
-            MarketWatchStatus = $"{Symbols.Count} symbols · {Symbols.Count(s => s.IsOpen == true)} open";
             var preferred = _settings().Symbol;
             SelectedSymbol = Symbols.FirstOrDefault(s => s.Symbol == preferred)
                              ?? Symbols.FirstOrDefault();
@@ -413,6 +652,107 @@ public sealed partial class TerminalViewModel : ObservableObject
         }
     }
 
+    /// <summary>Shared quote application for MT5 and Deriv ticks: row state
+    /// + selected-symbol quote text + ladder + P/L spot updates in one place.</summary>
+    private void ApplyQuote(TerminalSymbolRow row, double bid, double ask, long epochMs)
+    {
+        _tickArchive.Add("mt5", row.Symbol, bid, ask, epochMs);
+
+        var quote = bid > 0 ? bid : ask;
+        row.UpdateTick(new Tick(row.Symbol, quote, ask, bid, epochMs, 0));
+
+        if (SelectedSymbol?.Symbol == row.Symbol)
+        {
+            QuoteText = quote.ToString("0.#####");
+            RebuildLadder(bid > 0 ? bid : quote);
+        }
+    }
+
+    /// <summary>Event-driven MT5 quotes: refresh only the selected symbol
+    /// every 1 s (not a 3 s poll of account+positions+quotes together) and
+    /// the whole watchlist every 30 s. No-op while the bridge is down.</summary>
+    private async Task RefreshMt5QuotesAsync()
+    {
+        if (IsMt5Busy || !IsMt5Connected)
+        {
+            return;
+        }
+
+        var symbol = SelectedSymbol?.Symbol;
+        if (string.IsNullOrEmpty(symbol))
+        {
+            return;
+        }
+
+        try
+        {
+            var tick = await _mt5.GetTickAsync(symbol).ConfigureAwait(true);
+            if (tick is { } t)
+            {
+                var row = Symbols.FirstOrDefault(s => s.Symbol == symbol)
+                          ?? (SelectedSymbol?.Symbol == symbol ? SelectedSymbol : null);
+                if (row is not null)
+                {
+                    ApplyQuote(row, t.Bid, t.Ask, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
+                }
+
+                foreach (var p in Positions.Where(p => p.Symbol == symbol && !p.Profit.HasValue))
+                {
+                    p.UpdateSpot(t.Bid);
+                }
+            }
+        }
+        catch
+        {
+            // bridge hiccup — the poll's health check will flag real outages
+        }
+    }
+
+    /// <summary>30 s watchlist refresh: pull the bridge catalog and update
+    /// the rows in place (no Clear/Add churn — the grid does not re-virtualize).
+    /// New symbols get appended; vanished ones stay but lose quotes.</summary>
+    private async Task RefreshMt5WatchAsync()
+    {
+        if (IsMt5Busy || !IsMt5Connected)
+        {
+            return;
+        }
+
+        try
+        {
+            var mt5Symbols = await _mt5.GetSymbolsAsync().ConfigureAwait(true);
+            if (mt5Symbols.Count == 0)
+            {
+                return;
+            }
+
+            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
+            foreach (var s in mt5Symbols)
+            {
+                var row = Symbols.FirstOrDefault(x => x.Symbol == s.Symbol);
+                if (row is null)
+                {
+                    row = new TerminalSymbolRow(s.Symbol, s.Description, s.TradeMode == 4 ? (bool?)true : null);
+                    OnUiThread(() => Symbols.Add(row));
+                }
+
+                if (s.Bid is double bid && s.Ask is double ask)
+                {
+                    ApplyQuote(row, bid, ask, now);
+                }
+            }
+
+            if (SelectedSymbol is null)
+            {
+                SelectedSymbol = Symbols.FirstOrDefault();
+            }
+        }
+        catch
+        {
+            // best-effort — MarketWatchStatus already reflects bridge state
+        }
+    }
+
     // ── Candle chart (M1, built from the live tick feed) ───────────
 
     public sealed record CandleDto(string TimeText, double Open, double High, double Low, double Close,
@@ -1108,3 +1448,51 @@ internal static class JsonSerializerOps
     public static string ToJson(object value) =>
         System.Text.Json.JsonSerializer.Serialize(value);
 }
+
+/// <summary>One account in the Terminal's sign-in band: a read-only wrapper
+/// over the hub's AccountConnection so the Terminal can list, connect,
+/// disconnect, and monitor every account without owning connection state.</summary>
+public sealed partial class TerminalAccountRow : ObservableObject
+{
+    private readonly AccountConnection _connection;
+    private readonly System.Windows.Threading.Dispatcher _dispatcher;
+
+    public TerminalAccountRow(AccountConnection connection)
+    {
+        _connection = connection;
+        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
+        _connection.StateChanged += _ =>
+        {
+            void Refresh()
+            {
+                OnPropertyChanged(nameof(IsConnected));
+                OnPropertyChanged(nameof(StatusText));
+                OnPropertyChanged(nameof(BalanceText));
+                OnPropertyChanged(nameof(VerifiedText));
+            }
+
+            if (_dispatcher.CheckAccess())
+            {
+                Refresh();
+            }
+            else
+            {
+                _dispatcher.BeginInvoke(Refresh);
+            }
+        };
+    }
+
+    public AccountConnection Connection => _connection;
+    public string Label => _connection.DisplayName;
+    public string Kind => _connection.Config.IsDemo ? "DEMO" : "REAL";
+    public bool IsDemo => _connection.Config.IsDemo;
+    public bool IsMt5 => false;
+
+    public bool IsConnected => _connection.IsConnected;
+    public string StatusText => _connection.StatusText;
+    public string BalanceText => string.IsNullOrEmpty(_connection.BalanceText) ? "—" : _connection.BalanceText;
+    public string VerifiedText => _connection.VerifiedText;
+
+    public void Connect() => _ = _connection.ConnectAsync();
+    public Task DisconnectAsync() => _connection.DisconnectAsync();
+}
