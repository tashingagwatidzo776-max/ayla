using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.App.ViewModels;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;
using Tf.Core.Optimization;
using Tf.Core.Update;
using Tf.Deriv;

namespace Tf.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton(_ => new TradeStore(SettingsService.DataDir));
        services.AddSingleton<DerivClient>();

        // Multi-account layer: vault → hub (connections + growth runners) → VMs.
        services.AddSingleton<AccountVault>();
        services.AddSingleton<GrowthPlanStore>();
        services.AddSingleton(_ => new TradeJournal(Path.Combine(SettingsService.DataDir, "journal")));
        services.AddSingleton<MultiAccountHub>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp =>
            (Func<AppSettings>)(() => sp.GetRequiredService<SettingsViewModel>().BuildSettings()));
        services.AddSingleton(sp =>
            (Func<IReadOnlyList<Tick>>)(() =>
                sp.GetRequiredService<DashboardViewModel>().Chart?.Ticks?.ToArray() ?? Array.Empty<Tick>()));
        services.AddSingleton(sp =>
            (Func<RiskContext>)(() => sp.GetRequiredService<BrainViewModel>().BuildRiskContext()));
        services.AddSingleton(sp =>
            (Func<IReadOnlyList<string>>)(() =>
                MarketContextBuilder.LessonsFrom(sp.GetRequiredService<TradeStore>().Trades, 5)));
        services.AddSingleton<TradesViewModel>();
        services.AddSingleton<BrainViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<GrowthViewModel>();
        services.AddSingleton(sp =>
            (Func<bool>)(() => sp.GetRequiredService<DashboardViewModel>().IsKillSwitchEngaged));
        services.AddSingleton<JournalViewModel>();

        // New services: auto-update, performance tracking, strategy optimizer
        services.AddSingleton(_ => new AutoUpdater("1.0.0"));
        services.AddSingleton(_ => new PerformanceTracker(Path.Combine(SettingsService.DataDir, "analytics")));
        services.AddSingleton(_ => new StrategyOptimizer(Path.Combine(SettingsService.DataDir, "backtests")));
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<OptimizerViewModel>();
        services.AddSingleton<MainViewModel>();

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var window = new MainWindow
        {
            DataContext = provider.GetRequiredService<MainViewModel>()
        };
        window.Show();

        var mainVm = (MainViewModel)window.DataContext;
        _ = mainVm.InitializeAsync();
        provider.GetRequiredService<BrainViewModel>().StartAutonomy();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Ioc.Default.GetService<MainViewModel>() is { } vm)
        {
            vm.Shutdown();
        }

        base.OnExit(e);
    }
}