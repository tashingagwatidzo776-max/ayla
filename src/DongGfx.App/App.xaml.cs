using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Analytics;
using DongGfx.Core.Brain;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Core.Optimization;
using DongGfx.Core.Update;
using DongGfx.Deriv;

namespace DongGfx.App;

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

            // Both outcomes persist the completion flag: a save marks
            // "configured", a skip marks "skipped". The wizard must not
            // re-appear on every launch just because setup was deferred —
            // the Settings tab is the place to finish it.
            if (wizard.Completed || wizard.Skipped)
            {
                var dir = Path.GetDirectoryName(firstRunMarker)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(firstRunMarker, wizard.Completed ? "configured" : "skipped");
            }

            if (wizard.Completed)
            {
                // Merge the wizard choices into whatever settings already
                // exist (overwriting only the five wizard fields) instead of
                // replacing them — a pre-existing webhook, manual stake cap
                // or staleness alert survives first-run completion. The
                // token floor was already enforced by the wizard's Save.
                var choices = new FirstRunChoices(
                    wizard.ApiToken, wizard.Symbol, wizard.BrainKey,
                    wizard.Budget, wizard.AutonomyEnabled, wizard.RespectMarketHours);
                var settingsService = new SettingsService();
                settingsService.Save(
                    FirstRunWizardLogic.ApplyChoices(settingsService.Load(), choices));
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
        // One process-wide manual-surface gate: Trades/Brain evaluate it, the
        // Growth tab's unlock panel arms it, shutdown resets it.
        services.AddSingleton<ManualRealMoneyGate>();
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
                sp.GetRequiredService<WebhookService>(),
                manualGate: sp.GetRequiredService<ManualRealMoneyGate>()));

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
        // The manual trading surfaces share one session unlock (typed
        // confirmation phrase) and evaluate the same real-money gate as the
        // growth engines — the primary client has no per-account hub gate.
        services.AddSingleton(sp =>
            new TradesViewModel(
                sp.GetRequiredService<DerivClient>(),
                sp.GetRequiredService<TradeStore>(),
                sp.GetRequiredService<Func<AppSettings>>(),
                sp.GetRequiredService<DashboardViewModel>(),
                isRealMoneyUnlocked: () => sp.GetRequiredService<ManualRealMoneyGate>().IsUnlocked));
        services.AddSingleton(sp =>
            new BrainViewModel(
                sp.GetRequiredService<DerivClient>(),
                sp.GetRequiredService<TradeStore>(),
                sp.GetRequiredService<DashboardViewModel>(),
                sp.GetRequiredService<Func<AppSettings>>(),
                sp.GetRequiredService<Func<IReadOnlyList<Tick>>>(),
                sp.GetRequiredService<Func<RiskContext>>(),
                sp.GetRequiredService<Func<IReadOnlyList<string>>>(),
                realMoneyDecision: () => sp.GetRequiredService<ManualRealMoneyGate>().Evaluate(
                    sp.GetRequiredService<Func<AppSettings>>()().IsDemo,
                    sp.GetRequiredService<DerivClient>().LoginId is null
                        ? null : sp.GetRequiredService<DerivClient>().Balance.IsVirtual)));
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton(sp =>
            new GrowthViewModel(
                sp.GetRequiredService<MultiAccountHub>(),
                sp.GetRequiredService<GrowthPlanStore>(),
                sp.GetRequiredService<Func<AppSettings>>(),
                sp.GetRequiredService<DashboardViewModel>(),
                sp.GetRequiredService<PerformanceTracker>(),
                manualGate: sp.GetRequiredService<ManualRealMoneyGate>()));
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
        services.AddSingleton(sp => new MetricsDigestService(
            sp.GetRequiredService<MultiAccountHub>().Metrics,
            sp.GetRequiredService<WebhookService>()));
        services.AddSingleton(sp =>
            new OptimizerViewModel(
                sp.GetRequiredService<StrategyOptimizer>(),
                sp.GetRequiredService<TickHistoryCache>()));
        services.AddSingleton<HealthViewModel>();
        services.AddSingleton(sp =>
            new TerminalViewModel(
                () => sp.GetRequiredService<DerivClient>(),
                sp.GetRequiredService<MultiAccountHub>(),
                sp.GetRequiredService<TradeStore>(),
                sp.GetRequiredService<Func<AppSettings>>(),
                persist: () => _ = sp.GetRequiredService<SettingsViewModel>().SaveSettingsQuietAsync(),
                isRealMoneyUnlocked: () => sp.GetRequiredService<ManualRealMoneyGate>().IsUnlocked,
                dashboard: sp.GetRequiredService<DashboardViewModel>(),
                setAutonomyBound: v => sp.GetRequiredService<SettingsViewModel>().AutonomyEnabled = v,
                setSymbolBound: s => sp.GetRequiredService<SettingsViewModel>().Symbol = s));
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<DerivClient>(),
            sp.GetRequiredService<TradeStore>(),
            sp.GetRequiredService<DashboardViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<TradesViewModel>(),
            sp.GetRequiredService<BrainViewModel>(),
            sp.GetRequiredService<AccountsViewModel>(),
            sp.GetRequiredService<GrowthViewModel>(),
            sp.GetRequiredService<JournalViewModel>(),
            sp.GetRequiredService<UpdateViewModel>(),
            sp.GetRequiredService<PerformanceViewModel>(),
            sp.GetRequiredService<OptimizerViewModel>(),
            sp.GetRequiredService<HealthViewModel>(),
            sp.GetRequiredService<TerminalViewModel>(),
            sp.GetRequiredService<TradeJournal>()));

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

        // Cycle-telemetry digest: periodically posts the live latency/error
        // digest to the same webhook trade settlements use, so monitoring
        // sees session health without anyone exporting manually. The
        // optional GitHub dispatch leg (machine token + repo) lets CI add a
        // scheduled companion run over the committed artifacts. Gated by the
        // settings toggle so it can be silenced without rebuilding.
        var digest = provider.GetRequiredService<MetricsDigestService>();
        digest.Disabled = !settings.MetricsDigestEnabled;
        digest.Interval = TimeSpan.FromHours(Math.Max(1, settings.MetricsDigestIntervalHours));
        // Safety-audit leg: post the real-money rail coverage table whenever
        // it changes so monitoring sees rail changes after each release.
        digest.SafetyAuditPath = SafetyAuditDigest.FindAuditPath(AppContext.BaseDirectory);
        // Unlock arm-state leg: every digest carries the current session
        // unlock state, so monitoring sees real trading re-enabled after a
        // restart (and its absence the rest of the time).
        var hub = provider.GetRequiredService<MultiAccountHub>();
        digest.UnlockStateProvider = hub.DescribeUnlockState;
        // The unlock-staleness alert reads its hours from the settings
        // editor LIVE — a save re-arms the watches without an app restart
        // (0 disables the alert). Default 4h when never configured.
        var settingsFactory = provider.GetRequiredService<Func<AppSettings>>();
        hub.SetThresholdSource(() => settingsFactory().ArmStalenessHours);
        digest.Start();

        // Live telemetry panel on the Performance tab (cycles/latency/errors
        // between exports). The timer is created here so it never runs in
        // unit tests, which construct the view model directly.
        provider.GetRequiredService<PerformanceViewModel>().StartTelemetryRefresh();
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
            Ioc.Default.GetService<BankrollCsvPublisher>(),
            Ioc.Default.GetService<MetricsDigestService>()
        ];

        foreach (var d in disposables)
        {
            d?.Dispose();
        }

        base.OnExit(e);
    }
}