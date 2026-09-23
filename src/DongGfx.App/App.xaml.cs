using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Core.Update;

namespace DongGfx.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // MT5/forex-only composition: no Deriv client, no trade store, no
        // multi-account hub, no growth engines — the binary-options
        // integration (and its first-run API-token wizard) was removed.
        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();

        // Core singletons.
        services.AddSingleton(_ => new TradeJournal(Path.Combine(SettingsService.DataDir, "journal")));
        services.AddSingleton(_ => new PerformanceTracker(Path.Combine(SettingsService.DataDir, "analytics")));
        services.AddSingleton(_ => new AppLogger(SettingsService.DataDir));
        services.AddSingleton<NotificationService>();
        services.AddSingleton<WebhookService>();
        services.AddSingleton<ManualRealMoneyGate>();
        services.AddSingleton<UnlockStalenessMonitor>();

        // MT5 bridge + FX brain plumbing.
        services.AddSingleton<Mt5BridgeClient>();
        services.AddSingleton<TickArchive>();
        services.AddSingleton<MetricsCollector>();
        services.AddSingleton(sp => new FxTradeFeed(
            sp.GetRequiredService<Mt5BridgeClient>(),
            sp.GetRequiredService<PerformanceTracker>(),
            sp.GetRequiredService<TradeJournal>()));
        services.AddSingleton(sp => new FxScorecardService(
            sp.GetRequiredService<Mt5BridgeClient>(),
            sp.GetRequiredService<TradeJournal>(),
            sp.GetRequiredService<Func<AppSettings>>()));

        // View models.
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp =>
            (Func<AppSettings>)(() => sp.GetRequiredService<SettingsViewModel>().BuildSettings()));
        services.AddSingleton(sp => new JournalViewModel(
            sp.GetRequiredService<TradeJournal>(),
            () => sp.GetRequiredService<DashboardViewModel>().IsKillSwitchEngaged));
        services.AddSingleton(_ => new AutoUpdater(
            (VersionInfo.FullVersion).Split('+')[0]));   // strip the +sha stamp
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton(sp => new PerformanceViewModel(
            sp.GetRequiredService<PerformanceTracker>(),
            trades: () => sp.GetRequiredService<FxTradeFeed>().Trades,
            metrics: sp.GetRequiredService<MetricsCollector>()));
        services.AddSingleton(sp => new MetricsDigestService(
            sp.GetRequiredService<MetricsCollector>(),
            sp.GetRequiredService<WebhookService>()));
        services.AddSingleton(sp => new TerminalViewModel(
            () => sp.GetRequiredService<SettingsViewModel>().BuildSettings(),
            persist: () => _ = sp.GetRequiredService<SettingsViewModel>().SaveSettingsQuietAsync(),
            isRealMoneyUnlocked: () => sp.GetRequiredService<ManualRealMoneyGate>().IsUnlocked,
            dashboard: sp.GetRequiredService<DashboardViewModel>(),
            journal: sp.GetRequiredService<TradeJournal>(),
            mt5: sp.GetRequiredService<Mt5BridgeClient>(),
            setAutonomyBound: v => sp.GetRequiredService<SettingsViewModel>().AutonomyEnabled = v,
            setSymbolBound: s => sp.GetRequiredService<SettingsViewModel>().FxSymbol = s,
            tickArchive: sp.GetRequiredService<TickArchive>(),
            fxHostFactory: () =>
            {
                var s = sp.GetRequiredService<Func<AppSettings>>()();
                var symbols = FxScorecardService.ParseSymbols(s.FxSymbols, s.FxSymbol);
                return new FxPortfolioHost(
                    sp.GetRequiredService<Mt5BridgeClient>(),
                    sp.GetRequiredService<TradeJournal>(),
                    symbols,
                    () => sp.GetRequiredService<DashboardViewModel>().IsKillSwitchEngaged,
                    () => sp.GetRequiredService<Func<AppSettings>>()().Mt5MaxLots,
                    () => sp.GetRequiredService<ManualRealMoneyGate>().IsUnlocked,
                    governorTripped: () => sp.GetRequiredService<DashboardViewModel>().IsGovernorLatched,
                    dailyLossCap: () => sp.GetRequiredService<Func<AppSettings>>()().Mt5DailyLossCap,
                    equityFloor: () => sp.GetRequiredService<Func<AppSettings>>()().Mt5EquityFloor,
                    portfolioMaxLots: () => sp.GetRequiredService<Func<AppSettings>>()().FxPortfolioMaxLots,
                    webhook: sp.GetRequiredService<WebhookService>(),
                    newsCalendarPath: () => Path.Combine(SettingsService.DataDir, "news-calendar.json"),
                    newsWindow: () => TimeSpan.FromMinutes(
                        sp.GetRequiredService<Func<AppSettings>>()().NewsBlackoutMinutes));
            }));
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<DashboardViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<JournalViewModel>(),
            sp.GetRequiredService<UpdateViewModel>(),
            sp.GetRequiredService<PerformanceViewModel>(),
            sp.GetRequiredService<TerminalViewModel>(),
            sp.GetRequiredService<TradeJournal>()));

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var settings = provider.GetRequiredService<SettingsService>().Load();

        // Configure webhook from persisted settings.
        if (!string.IsNullOrEmpty(settings.WebhookUrl))
        {
            var webhook = provider.GetRequiredService<WebhookService>();
            webhook.WebhookUrl = settings.WebhookUrl;
            webhook.IsDiscord = settings.IsDiscordWebhook;
        }

        // Cycle-telemetry digest: periodically posts the live latency/error
        // digest to the same webhook trade settlements use, so monitoring
        // sees session health without anyone exporting manually. Gated by
        // the settings toggle so it can be silenced without rebuilding.
        var digest = provider.GetRequiredService<MetricsDigestService>();
        digest.Disabled = !settings.MetricsDigestEnabled;
        digest.Interval = TimeSpan.FromHours(Math.Max(1, settings.MetricsDigestIntervalHours));
        // Safety-audit leg: post the real-money rail coverage table whenever
        // it changes so monitoring sees rail changes after each release.
        digest.SafetyAuditPath = SafetyAuditDigest.FindAuditPath(AppContext.BaseDirectory);
        // Unlock arm-state leg: every digest carries the current session
        // unlock state, so monitoring sees real trading re-enabled after a
        // restart (and its absence the rest of the time).
        var manualGate = provider.GetRequiredService<ManualRealMoneyGate>();
        digest.UnlockStateProvider = () =>
            manualGate.IsUnlocked ? "real-money session unlock: ARMED" : null;
        // FX-brain leg: mode, symbols, soak progress, halt state, and the
        // latest alpha-scorecard verdict — monitoring sees the forex brain's
        // health (and family degradation) without opening the app.
        digest.FxStateProvider = () =>
        {
            var terminal = provider.GetRequiredService<TerminalViewModel>();
            var scorecard = provider.GetRequiredService<FxScorecardService>();
            var parts = new List<string>();
            if (terminal.FxHost is { } portfolio)
            {
                var mode = portfolio.IsLiveEngine ? "LIVE" : portfolio.IsRunning ? "PAPER" : "stopped";
                var halt = portfolio.Supervisor.IsHalted
                    ? $"halt:{portfolio.Supervisor.HaltReason}"
                    : "clear";
                parts.Add($"FX brain {mode} on {string.Join("+", portfolio.Symbols)} " +
                          $"soak {portfolio.PaperSignalsSeen}/{portfolio.PaperSignalsSeen} {halt}");
            }

            if (scorecard.LastSummary is { } sc)
            {
                parts.Add(sc);
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        };

        // Scorecard service (nightly 03:00 walk-forward verdicts per family).
        provider.GetRequiredService<FxScorecardService>();   // start the timer

        // Deal feed: turns settled MT5 deals into Performance/Journal rows.
        provider.GetRequiredService<FxTradeFeed>().Start();

        // Startup update check: silent probe ~45 s after launch; a newer
        // release surfaces as a toast + the Update tab's normal flow.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            try
            {
                var updater = provider.GetRequiredService<AutoUpdater>();
                var update = await updater.CheckForUpdateAsync(
                    AutoUpdater.GitHubReleasesUrl).ConfigureAwait(false);
                if (update is not null)
                {
                    provider.GetRequiredService<NotificationService>()
                        .NotifyUpdateAvailable(update.Version);
                }
            }
            catch
            {
                // update checks are best-effort — never touch startup
            }
        });

        // Publish the resolved MT5 terminal path + sidecar port: the
        // watchdog and sidecar then target the SAME terminal exe the app
        // uses (data/mt5-bridge.json).
        var settingsFactory = provider.GetRequiredService<Func<AppSettings>>();
        Mt5TerminalLocator.WriteConfig(
            Mt5TerminalLocator.Find(settingsFactory().Mt5TerminalPath));
        digest.Start();

        // Unlock-staleness alert: an armed session unlock past the
        // configured threshold journals REAL_MONEY_UNLOCK_STALE + toast +
        // webhook (the rail the removed hub used to own).
        provider.GetRequiredService<UnlockStalenessMonitor>().Start();

        var window = new MainWindow
        {
            DataContext = provider.GetRequiredService<MainViewModel>()
        };
        window.Show();

        var mainVm = (MainViewModel)window.DataContext;
        _ = mainVm.InitializeAsync();

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
        // TickArchive closes late on purpose: it flushes buffered ticks, so
        // every feed that writes it must be gone before this runs.
        IDisposable?[] disposables = [
            Ioc.Default.GetService<AppLogger>(),
            Ioc.Default.GetService<TradeJournal>(),
            Ioc.Default.GetService<NotificationService>(),
            Ioc.Default.GetService<WebhookService>(),
            Ioc.Default.GetService<AutoUpdater>(),
            Ioc.Default.GetService<FxScorecardService>(),
            Ioc.Default.GetService<Mt5BridgeClient>(),
            Ioc.Default.GetService<FxTradeFeed>(),
            Ioc.Default.GetService<UnlockStalenessMonitor>(),
            Ioc.Default.GetService<MetricsDigestService>(),
            Ioc.Default.GetService<TickArchive>()
        ];

        foreach (var d in disposables)
        {
            d?.Dispose();
        }

        base.OnExit(e);
    }
}
