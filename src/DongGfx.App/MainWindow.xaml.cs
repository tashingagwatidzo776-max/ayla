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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Dashboard.Chart = Chart;
            vm.Dashboard.CandleChart = DashboardCandles;
            vm.TerminalVm.CandleChart = TerminalCandles;
            WireChartTrading(vm.TerminalVm, TerminalCandles);
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

    /// <summary>Chart trading + drawing menu wiring: right-click offers
    /// market buy/sell (the ticket's guards apply — the chart is a
    /// shortcut, never a bypass), a horizontal level at the clicked price,
    /// and drawing cleanup; a dragged SL/TP line maps onto the position
    /// modify (journaled, kill-switched) via the view model.</summary>
    private void WireChartTrading(ViewModels.TerminalViewModel terminal, Controls.CandleChartControl chart)
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

            // Defer the open: raising IsOpen synchronously inside the
            // right-button-up route gets the menu dismissed by that same
            // input sequence (the context-menu service sees the button-up
            // and closes what it just opened) — the menu flashes and
            // never shows. Queued to the dispatcher, it opens cleanly
            // after the click route completes.
            Dispatcher.BeginInvoke(() => menu.IsOpen = true);
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