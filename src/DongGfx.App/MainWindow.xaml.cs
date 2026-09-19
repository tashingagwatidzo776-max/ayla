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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Dashboard.Chart = Chart;
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