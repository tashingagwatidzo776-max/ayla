using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.App.ViewModels;

namespace Tf.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private TrayIconService? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
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