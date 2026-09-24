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