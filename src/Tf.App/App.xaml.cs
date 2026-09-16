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

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First-run wizard: show setup dialog if no settings exist.
        var firstRunMarker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "tf", "data", "wizard_done.flag");
        if (!File.Exists(firstRunMarker))
        {
            var wizard = new FirstRunWizard();
            wizard.ShowDialog();

            if (wizard.Completed)
            {
                // Persist the wizard choices.
                var dir = Path.GetDirectoryName(firstRunMarker)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(firstRunMarker, wizard.ApiToken.Length > 0 ? "configured" : "skipped");

                // Apply wizard settings immediately.
                var wizardSettings = new AppSettings
                {
                    ApiToken = wizard.ApiToken,
                    Symbol = wizard.Symbol,
                    AutonomyEnabled = wizard.AutonomyEnabled,
                    RespectMarketHours = wizard.RespectMarketHours,
                    IsDemo = true
                };

                var settingsService = new SettingsService();
                settingsService.Save(wizardSettings);
            }
        }

        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton(_ => new TradeStore(SettingsService.DataDir));
        services.AddSingleton<DerivClient>();

        // Multi-account layer: vault → hub (connections + growth runners) → VMs.
        services.AddSingleton<AccountVault>();
        services.AddSingleton<GrowthPlanStore>();
        services.AddSingleton(_ => new TickHistoryCache(SettingsService.DataDir));
        services.AddSingleton(_ => new HeartbeatLog(SettingsService.DataDir));
        services.AddSingleton(_ => new AppLogger(SettingsService.DataDir));
        services.AddSingleton(_ => new ApiAuditLog(SettingsService.DataDir));
        services.AddSingleton<NotificationService>();
        services.AddSingleton<WebhookService>();
        services.AddSingleton(_ => new TradeJournal(Path.Combine(SettingsService.DataDir, "journal")));
        services.AddSingleton(_ => new PerformanceTracker(Path.Combine(SettingsService.DataDir, "analytics")));
        // Keeps the growth-bankroll CSV (the trend page's money axis) fresh on
        // every settled trade — no manual export_bankroll.py run needed. The
        // canonical copy lives in app data; docs/ is updated best-effort so a
        // checkout-run app stages the Pages input for commit.
        services.AddSingleton(sp => new BankrollCsvFile(
            sp.GetRequiredService<TradeStore>(),
            BankrollCsvFile.FindRepoDocsPath(AppContext.BaseDirectory),
            Path.Combine(SettingsService.DataDir, "growth-bankroll.csv")));
        // Auto-publishes the committed export to main (single-file, main-only,
        // best-effort) so the Pages deploy picks it up without a manual commit.
        // Failures retry on later ticks but are also raised as events — the
        // dashboard risk rail latches on them so a broken publish (and the
        // frozen money axis it causes) cannot hide behind silent retries.
        services.AddSingleton(sp => new BankrollCsvPublisher(
            sp.GetRequiredService<BankrollCsvFile>().DocsPath,
            msg => System.Diagnostics.Debug.WriteLine(msg)));
        services.AddSingleton(sp =>
            new MultiAccountHub(
                sp.GetRequiredService<AccountVault>(),
                sp.GetRequiredService<TradeStore>(),
                sp.GetRequiredService<TradeJournal>(),
                sp.GetRequiredService<PerformanceTracker>(),
                sp.GetRequiredService<TickHistoryCache>(),
                sp.GetRequiredService<HeartbeatLog>(),
                sp.GetRequiredService<NotificationService>(),
                sp.GetRequiredService<WebhookService>()));

        services.AddSingleton(sp =>
            new DashboardViewModel(
                sp.GetRequiredService<DerivClient>(),
                sp.GetRequiredService<MultiAccountHub>(),
                sp.GetRequiredService<NotificationService>(),
                sp.GetRequiredService<WebhookService>()));
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
        services.AddSingleton(sp =>
            new GrowthViewModel(
                sp.GetRequiredService<MultiAccountHub>(),
                sp.GetRequiredService<GrowthPlanStore>(),
                sp.GetRequiredService<Func<AppSettings>>(),
                sp.GetRequiredService<DashboardViewModel>(),
                sp.GetRequiredService<PerformanceTracker>()));
        services.AddSingleton(sp =>
            (Func<bool>)(() => sp.GetRequiredService<DashboardViewModel>().IsKillSwitchEngaged));
        services.AddSingleton(sp =>
            new JournalViewModel(
                sp.GetRequiredService<TradeJournal>(),
                sp.GetRequiredService<TradeStore>(),
                () => sp.GetRequiredService<DashboardViewModel>().IsKillSwitchEngaged));

        // New services: auto-update, performance tracking, strategy optimizer
        services.AddSingleton(_ => new AutoUpdater("1.0.0"));
        services.AddSingleton(_ => new StrategyOptimizer(Path.Combine(SettingsService.DataDir, "backtests")));
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton(sp => new PerformanceViewModel(
            sp.GetRequiredService<PerformanceTracker>(),
            sp.GetRequiredService<TradeStore>(),
            metrics: sp.GetRequiredService<MultiAccountHub>().Metrics));
        services.AddSingleton(sp =>
            new OptimizerViewModel(
                sp.GetRequiredService<StrategyOptimizer>(),
                sp.GetRequiredService<TickHistoryCache>()));
        services.AddSingleton<HealthViewModel>();
        services.AddSingleton<MainViewModel>();

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        // Eagerly start the growth-bankroll CSV auto-refresh (nothing else
        // depends on it): writes the initial export and hooks settled trades,
        // and starts the periodic publish of the committed export. Publish
        // failures/recoveries surface on the dashboard risk rail (toast +
        // webhook fire from the rail's change machinery).
        _ = provider.GetRequiredService<BankrollCsvFile>();
        var publisher = provider.GetRequiredService<BankrollCsvPublisher>();
        var dashboardVm = provider.GetRequiredService<DashboardViewModel>();
        publisher.PublishFailed += dashboardVm.OnBankrollPublishFailed;
        publisher.PublishRecovered += dashboardVm.OnBankrollPublishRecovered;
        publisher.Start();

        var window = new MainWindow
        {
            DataContext = provider.GetRequiredService<MainViewModel>()
        };
        window.Show();

        var mainVm = (MainViewModel)window.DataContext;
        _ = mainVm.InitializeAsync();
        provider.GetRequiredService<BrainViewModel>().StartAutonomy();

        // Wire the Growth tab's P&L chart once the window (and its controls) exist.
        provider.GetRequiredService<GrowthViewModel>().AttachPnlChart(window.PnlCurve);

        // Configure webhook from persisted settings.
        var settings = provider.GetRequiredService<SettingsService>().Load();
        if (!string.IsNullOrEmpty(settings.WebhookUrl))
        {
            var webhook = provider.GetRequiredService<WebhookService>();
            webhook.WebhookUrl = settings.WebhookUrl;
            webhook.IsDiscord = settings.IsDiscordWebhook;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Ioc.Default.GetService<MainViewModel>() is { } vm)
        {
            vm.Shutdown();
        }

        // Dispose all IDisposable services to release file handles, timers, etc.
        IDisposable?[] disposables = [
            Ioc.Default.GetService<AppLogger>(),
            Ioc.Default.GetService<TradeJournal>(),
            Ioc.Default.GetService<ApiAuditLog>(),
            Ioc.Default.GetService<NotificationService>(),
            Ioc.Default.GetService<WebhookService>(),
            Ioc.Default.GetService<AutoUpdater>(),
            Ioc.Default.GetService<BankrollCsvFile>(),
            Ioc.Default.GetService<BankrollCsvPublisher>()
        ];

        foreach (var d in disposables)
        {
            d?.Dispose();
        }

        base.OnExit(e);
    }
}