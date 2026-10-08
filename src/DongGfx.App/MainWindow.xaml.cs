using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;

namespace DongGfx.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private System.Windows.Threading.DispatcherTimer? _mapsTimer;

    private TrayIconService? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        // Shipped binaries identify themselves: the publish stamp
        // (-p:InformationalVersion="<tag>+<sha>") lands in the title; dev
        // builds stay unstamped.
        Title += VersionInfo.TitleSuffix;
        Loaded += OnLoaded;

        // MT5-style function-key shortcuts (preview: fire before focus
        // owners can swallow them).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F9)
            {
                OnMenuNewOrder(this, e);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F5)
            {
                OnMenuRefresh(this, e);
                e.Handled = true;
            }
        };
    }

    // ── Menu bar handlers (MT5 chrome; everything routes through the
    // existing view-model commands — no new order-path logic here) ──

    private void OnMenuNewOrder(object sender, RoutedEventArgs e)
    {
        SelectTab("Terminal");
    }

    private void OnMenuCloseAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _ = vm.TerminalVm.FxEmergencyFlattenAsync("File menu — emergency close all");
        }
    }

    private void OnMenuExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnMenuViewTab(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string name)
        {
            SelectTab(name);
        }
    }

    private void OnMenuRefresh(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _ = vm.TerminalVm.LoadSymbolsCommand.ExecuteAsync(null);
        }
    }
    private void OnMenuCheckUpdates(object sender, RoutedEventArgs e)
    {
        SelectTab("Settings");
        if (DataContext is MainViewModel vm)
        {
            _ = vm.UpdateVm.CheckForUpdateCommand.ExecuteAsync(null);
        }
    }

    // File→Login: switch the terminal's signed-in MT5 account through the
    // sidecar's POST /login (M2). The dialog holds the credentials; the
    // account+server prefill comes from the last successful switch (the
    // password is never persisted); on success the account bar refreshes
    // so the switch is visible at once.
    private void OnMenuLogin(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var dialog = new LoginDialog { Owner = this };
        var loginVm = new LoginViewModel(
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetRequiredService<Mt5BridgeClient>())
        {
            Account = vm.SettingsVm.Mt5LastLogin,
            Server = vm.SettingsVm.Mt5LastServer,
        };
        // Recent picker: last + previous successful switches (two slots).
        loginVm.SetRecentAccounts(
            vm.SettingsVm.Mt5LastLogin, vm.SettingsVm.Mt5LastServer,
            vm.SettingsVm.Mt5PrevLogin, vm.SettingsVm.Mt5PrevServer);
        loginVm.OnSignedIn = () =>
        {
            // Persist the switch through the two-slot Recent history (never
            // the password) so the next dialog opens prefilled; the
            // account-bar refresh rides behind it so the switch shows at
            // once.
            vm.SettingsVm.RecordMt5Login(loginVm.Account, loginVm.Server);
            _ = vm.SettingsVm.SaveSettingsQuietAsync();
            // Account-switch safety guards: the FX brain stops (its
            // positions/caps/authorization belong to the old account) and
            // the real-money unlock resets (a new account starts locked).
            // Best-effort and before the refresh — the switch succeeded
            // either way.
            vm.OnMt5AccountSwitched(loginVm.Account, loginVm.Server);
            return vm.TerminalVm.RefreshAccountBarCommand.ExecuteAsync(null);
        };
        loginVm.SignedIn += () => dialog.Close();
        dialog.DataContext = loginVm;
        dialog.ShowDialog();
    }

    private void SelectTab(string header)
    {
        foreach (var item in MainTabs.Items)
        {
            if (item is System.Windows.Controls.TabItem tab &&
                string.Equals(tab.Header as string, header, StringComparison.Ordinal))
            {
                MainTabs.SelectedItem = tab;
                return;
            }
        }
    }

    /// <summary>Navigates to the Journal tab with a category pre-selected —
    /// the dashboard's fault notice uses it so the rows behind a fault count
    /// are one click away. UI thread only.</summary>
    public void OpenJournal(string category)
    {
        SelectTab("Journal");
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.JournalVm.ShowCategory(category);

        // Land on the newest row of the category (the VM selected it) and bring
        // it into view once layout has caught up with the reload.
        var selected = vm.JournalVm.SelectedEntry;
        if (selected is not null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    JournalEntriesGrid.ScrollIntoView(selected);
                }
                catch
                {
                    // Scrolling is best-effort — never fault navigation.
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Right-clicking a journal row selects it first, so the context
    /// menu's "Copy details" acts on the row under the cursor rather than
    /// whatever happened to be selected before.</summary>
    private void OnJournalRowRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var row = System.Windows.Controls.ItemsControl.ContainerFromElement(
                (System.Windows.Controls.ItemsControl)sender,
                (System.Windows.DependencyObject)e.OriginalSource)
                as System.Windows.Controls.DataGridRow;
            if (row is not null)
            {
                JournalEntriesGrid.SelectedItem = row.Item;
            }
        }
        catch
        {
            // Selection-on-right-click is a convenience — never throw out of a
            // mouse handler.
        }
    }

    /// <summary>The TP1 hint can go stale while the app runs (an overdue
    /// plan-% review trips the breaker asynchronously). Recompute it
    /// whenever the tab changes so the Settings surface is never lying.
    /// Cheap: an uncached ledger read guarded by the switch itself.</summary>
    private void OnMainTabChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel m)
        {
            m.SettingsVm.RefreshTp1Hint();
            m.Dashboard.RefreshTp1PlanHold();
            m.Dashboard.RefreshTp1PlanReview();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Dashboard.Chart = Chart;
            vm.Dashboard.CandleChart = DashboardCandles;
            vm.TerminalVm.CandleChart = TerminalCandles;
            WireChartTrading(vm.TerminalVm, TerminalCandles);
            WireMaps(vm);
            vm.SettingsVm.RefreshTp1Hint();   // breaker state at first paint
            vm.Dashboard.RefreshTp1PlanHold();
            vm.Dashboard.RefreshTp1PlanReview();
        }

        // Initialize tray icon for headless operation.
        _trayIcon = new TrayIconService(this);
        _trayIcon.ExitRequested += () =>
        {
            if (DataContext is MainViewModel m)
                m.Shutdown();
            Application.Current.Shutdown();
        };
    }

    /// <summary>Instance wrapper: opens the chart trading menu through the
    /// dispatcher queue. See the static core for why the open must be
    /// deferred.</summary>
    private void WireChartTrading(
        ViewModels.TerminalViewModel terminal,
        Controls.CandleChartControl chart)
    {
        WireChartTradingCore(terminal, chart,
            menu => Dispatcher.BeginInvoke(() => menu.IsOpen = true));
    }

    /// <summary>Maps tab wiring: hand the view-model its data providers
    /// (bridge candles via the Terminal's window, archived ticks via the
    /// reader) and the canvas outlet; every Refresh repaints the canvas.
    /// Pure read-side — no order path anywhere.</summary>
    private void WireMaps(ViewModels.MainViewModel vm)
    {
        var maps = vm.MapsVm;
        var canvas = MapsCanvas;
        var settingsFactory = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetRequiredService<Func<Core.Models.AppSettings>>();
        var symbols = (settingsFactory().FxSymbols is { Length: > 0 } csv ? csv : "XAUUSDmicro")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        maps.BarsProvider = () =>
        {
            var candles = vm.TerminalVm.Candles;
            var bars = new Core.Fx.FxBar[candles.Count];
            var t = 0L;
            foreach (var c in candles)
            {
                bars[t] = new Core.Fx.FxBar(t, c.Open, c.High, c.Low, c.Close, 0);
                t++;
            }
            return bars;
        };
        maps.TicksProvider = () =>
        {
            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data", "ticks");
            return Services.TickArchiveReader.LoadToday(root, "mt5", symbols.Length > 0 ? symbols[0] : "XAUUSDmicro");
        };
        maps.QuotesProvider = () =>
        {
            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data", "ticks");
            return Services.TickArchiveReader.LoadQuotesToday(root, "mt5", symbols.Length > 0 ? symbols[0] : "XAUUSDmicro");
        };
        maps.ConfluenceProvider = () =>
        {
            // Cache reads only — network fetches happen in the async
            // preloader, off the UI thread (sync-over-async here deadlocks
            // the dispatcher — seen live 2026-09-27).
            var frames = new System.Collections.Generic.List<(string, System.Collections.Generic.IReadOnlyList<Core.Fx.FxBar>)>();
            foreach (var tf in new[] { "M1", "M5", "M15", "M30", "H1" })
            {
                if (maps.CachedCandles.TryGetValue(tf, out var bars) && bars.Count > 0)
                {
                    frames.Add((tf, bars));
                }
            }
            return frames;
        };
        maps.CorrelationProvider = () =>
        {
            // Cache reads only — preloader fills these off the UI thread.
            var series = new System.Collections.Generic.List<(string, System.Collections.Generic.IReadOnlyList<double>)>();
            foreach (var s in symbols)
            {
                if (maps.CachedCloses.TryGetValue(s, out var closes) && closes.Length >= 2)
                {
                    series.Add((s, closes));
                }
            }
            return series;
        };
        maps.AiBeliefsProvider = () =>
        {
            var scorecard = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Services.FxScorecardService>();
            return scorecard?.LastBeliefs ?? [];
        };

        maps.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModels.MapsViewModel.CurrentMap)
                or nameof(ViewModels.MapsViewModel.CurrentBuyShare))
            {
                canvas.Render(maps.CurrentMap, maps.CurrentBuyShare);
            }
            else if (args.PropertyName is nameof(ViewModels.MapsViewModel.SelectedColormap))
            {
                canvas.SetColormap(DongGfx.Core.Fx.FxCmap.TryParse(maps.SelectedColormap, out var kind) ? kind : null);
                var settingsVm = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                    .GetService<ViewModels.SettingsViewModel>();
                if (settingsVm is not null && settingsVm.MapsColormap != maps.SelectedColormap)
                {
                    settingsVm.MapsColormap = maps.SelectedColormap;
                    _ = settingsVm.SaveSettingsQuietAsync();
                }
            }
        };

        // Restore the persisted colormap choice (picker fires the same path).
        var savedCmap = settingsFactory().MapsColormap;
        if (!string.IsNullOrEmpty(savedCmap) && savedCmap != "Auto"
            && ViewModels.MapsViewModel.ColormapChoices.Contains(savedCmap))
        {
            maps.SelectedColormap = savedCmap;
        }
        canvas.Render(maps.CurrentMap);
        maps.RefreshCommand.Execute(null);

        // Auto-refresh: recompute the selected map every 15s while the
        // Maps tab is visible. Timer-driven only — the providers never
        // write anything, so this cannot influence the trading loop.
        _mapsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _mapsTimer.Tick += async (_, _) =>
        {
            if (!MapsTabItem.IsSelected)
            {
                return;
            }
            await PreloadMapsCandlesAsync(vm, maps).ConfigureAwait(true);
            await maps.RefreshAsync().ConfigureAwait(true);
        };
        _mapsTimer.Start();
        _ = PreloadMapsCandlesAsync(vm, maps).ContinueWith(
            _ => maps.Refresh(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Fetches the MTF frames (primary symbol) and per-symbol
    /// closes for the correlation map — every await is configured off the
    /// dispatcher, so nothing here can deadlock the UI thread. Failures
    /// leave caches stale and the maps render honest gaps.</summary>
    private static async Task PreloadMapsCandlesAsync(
        ViewModels.MainViewModel vm,
        ViewModels.MapsViewModel maps)
    {
        var primary = "XAUUSDmicro";
        var settingsFactory = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetRequiredService<Func<Core.Models.AppSettings>>();
        var symbols = (settingsFactory().FxSymbols is { Length: > 0 } csv ? csv : primary)
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (symbols.Length > 0)
        {
            primary = symbols[0];
        }

        foreach (var tf in new[] { "M1", "M5", "M15", "M30", "H1" })
        {
            try
            {
                var candles = await vm.TerminalVm.BridgeCandles(primary, tf, 60).ConfigureAwait(false);
                if (candles.Count > 0)
                {
                    var bars = new Core.Fx.FxBar[candles.Count];
                    var i = 0;
                    foreach (var c in candles)
                    {
                        bars[i] = new Core.Fx.FxBar(c.Time, c.Open, c.High, c.Low, c.Close, 0);
                        i++;
                    }
                    maps.CachedCandles[tf] = bars;
                }
            }
            catch
            {
                // bridge down / unsupported tf: leave the cache as-is
            }
        }

        foreach (var s in symbols)
        {
            try
            {
                var candles = await vm.TerminalVm.BridgeCandles(s, "M1", 60).ConfigureAwait(false);
                if (candles.Count >= 2)
                {
                    var closes = new double[candles.Count];
                    var i = 0;
                    foreach (var c in candles)
                    {
                        closes[i] = c.Close;
                        i++;
                    }
                    maps.CachedCloses[s] = closes;
                }
            }
            catch
            {
                // symbol offline: dropped, never faked
            }
        }
    }

    /// <summary>Chart trading + drawing menu wiring: right-click offers
    /// market buy/sell (the ticket's guards apply — the chart is a
    /// shortcut, never a bypass), a horizontal level at the clicked price,
    /// and drawing cleanup; a dragged SL/TP line maps onto the position
    /// modify (journaled, kill-switched) via the view model.
    ///
    /// The <paramref name="openMenu"/> callback owns WHEN the menu opens:
    /// the live window defers to the dispatcher queue because a
    /// synchronous IsOpen inside the right-button-up route is dismissed by
    /// that same input sequence (the context-menu service closes what it
    /// just opened) — the menu flashed and never showed. The regression
    /// test asserts the deferral; do not inline a synchronous open.</summary>
    internal static void WireChartTradingCore(
        ViewModels.TerminalViewModel terminal,
        Controls.CandleChartControl chart,
        Action<System.Windows.Controls.ContextMenu> openMenu)
    {
        chart.ChartContextMenuRequested += (_, price, _) =>
        {
            var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

            var buy = new System.Windows.Controls.MenuItem
            {
                Header = $"Buy {terminal.Mt5Lots:0.##} {terminal.SelectedSymbol?.Symbol ?? terminal.Mt5Symbol} at market",
            };
            buy.Click += (_, _) => _ = terminal.PlaceChartOrderCommand.ExecuteAsync("buy");
            menu.Items.Add(buy);

            var sell = new System.Windows.Controls.MenuItem
            {
                Header = $"Sell {terminal.Mt5Lots:0.##} {terminal.SelectedSymbol?.Symbol ?? terminal.Mt5Symbol} at market",
            };
            sell.Click += (_, _) => _ = terminal.PlaceChartOrderCommand.ExecuteAsync("sell");
            menu.Items.Add(sell);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var hline = new System.Windows.Controls.MenuItem
            {
                Header = $"Add horizontal line @ {price:0.#####}",
                ToolTip = "Right-drag also draws a trendline",
            };
            hline.Click += (_, _) => chart.AddDrawing(
                new Controls.CandleChartControl.ChartDrawing("hlevel", 0, price, 0, 0));
            menu.Items.Add(hline);

            var clear = new System.Windows.Controls.MenuItem { Header = "Clear drawings" };
            clear.Click += (_, _) => chart.ClearDrawings();
            menu.Items.Add(clear);

            // The openMenu callback owns WHEN the menu opens — the live
            // window defers to the dispatcher queue (see the method doc).
            openMenu(menu);
        };

        chart.PriceLineDragged += (line, newPrice) =>
        {
            if (line.Ticket is { } ticket)
            {
                _ = terminal.ChartLineDraggedCommand.ExecuteAsync(
                    $"{ticket}|{line.Kind}|{newPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
        };
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            $"{VersionInfo.FullTitle}\n" +
            $"Version: {VersionInfo.FullVersion}\n\n" +
            "Demo by default. Real trading requires the API-verified\n" +
            "real-money unlock at every trade path\n" +
            "(docs/real-money-safety-audit.md).\n\n" +
            "Educational software — never trade money you cannot afford to lose.",
            $"About {VersionInfo.ProductName}", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        // The real-money unlocks are session-scoped by design: arming them
        // never survives a restart, so a fresh process always starts locked.
        // The gate is the DI singleton shared by every manual surface.
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetRequiredService<ManualRealMoneyGate>().Reset();
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }
}