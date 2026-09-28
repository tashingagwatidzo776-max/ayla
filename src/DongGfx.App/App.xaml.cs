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

        var provider = ConfigureServices(new ServiceCollection()).BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var settings = provider.GetRequiredService<SettingsService>().Load();
        ThemeManager.Apply(settings);   // MT5-classic or modern dark, persisted
        ConfigureFromSettings(provider, settings);
        StartBackgroundServices(provider);

        var window = new MainWindow
        {
            DataContext = provider.GetRequiredService<MainViewModel>()
        };
        window.Show();

        // Build-freshness auto-check at startup: staleness is visible on the
        // Terminal account bar the moment the window renders, without waiting
        // for the Terminal view's own throttled probe. Never throws.
        _ = provider.GetRequiredService<TerminalViewModel>().RefreshBuildBadgeAsync();

        var mainVm = (MainViewModel)window.DataContext;
        _ = mainVm.InitializeAsync();

        // Live telemetry panel on the Performance tab (cycles/latency/errors
        // between exports). The timer is created here so it never runs in
        // unit tests, which construct the view model directly.
        provider.GetRequiredService<PerformanceViewModel>().StartTelemetryRefresh();
    }

    /// <summary>The full DI composition. Static and side-effect-free so the
    /// startup wiring can be built and resolved in tests: every service the
    /// app resolves at runtime must come out of this graph.</summary>
    internal static IServiceCollection ConfigureServices(IServiceCollection services)
    {
        // MT5/forex-only composition: no Deriv client, no trade store, no
        // multi-account hub, no growth engines — the binary-options
        // integration (and its first-run API-token wizard) was removed.
        services.AddSingleton<SettingsService>();

        // Core singletons.
        services.AddSingleton(_ => new TradeJournal(Path.Combine(SettingsService.DataDir, "journal")));
        services.AddSingleton(_ => new PerformanceTracker(Path.Combine(SettingsService.DataDir, "analytics")));
        services.AddSingleton(_ => new AppLogger(SettingsService.DataDir));
        services.AddSingleton<NotificationService>();
        services.AddSingleton<PriceAlertEngine>();
        services.AddSingleton<WebhookService>();
        services.AddSingleton<ManualRealMoneyGate>();
        services.AddSingleton<UnlockStalenessMonitor>();

        // MT5 bridge + FX brain plumbing.
        services.AddSingleton<Mt5BridgeClient>();
        services.AddSingleton<TickArchive>();
        services.AddSingleton<MetricsCollector>();
        services.AddSingleton(sp =>
        {
            var feed = new FxTradeFeed(
                sp.GetRequiredService<Mt5BridgeClient>(),
                sp.GetRequiredService<PerformanceTracker>(),
                sp.GetRequiredService<TradeJournal>());
            // Milestones (first settled FX trade) ride the same webhook as
            // trade settlements; PostFxMilestone no-ops while no URL is set.
            var webhook = sp.GetRequiredService<WebhookService>();
            feed.MilestoneNotifier = (title, body, progress) =>
                webhook.PostFxMilestone(title, body, progress);
            // The persisted WebhookOnMilestone toggle gates milestone posts
            // (read live: the settings editor applies without a restart).
            feed.MilestonesEnabled = () =>
                sp.GetRequiredService<Func<AppSettings>>()().WebhookOnMilestone;
            return feed;
        });
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

        // AI agent 1 (docs/ai-agent-program.md): the journal analyst. It
        // reads the journal, optionally asks a local LLM (Ollama/Qwen3 by
        // default) to narrate the session, and posts to the webhook.
        // Read-only by construction — it talks to the webhook, never to the
        // order path; failures degrade to the template narrative.
        services.AddSingleton(sp => new JournalAnalystService(
            sp.GetRequiredService<TradeJournal>(),
            sp.GetRequiredService<WebhookService>()));

        // AI agent 2: the risk narrator. Subscribes to the journal's
        // EntryAdded stream and explains FX supervisor halts on the webhook
        // within seconds. Pure observer — it holds no reference to the
        // supervisor or any order path and can never re-arm a halt.
        services.AddSingleton(sp => new RiskNarratorService(
            sp.GetRequiredService<TradeJournal>(),
            sp.GetRequiredService<WebhookService>()));

        // Genetic lab (agent-6 support): nightly walk-forward replays over
        // the journal's own decision bars. Journal-only by construction —
        // no bridge, no order path, no promotion. Approval is evidence for
        // a human, never an action.
        services.AddSingleton(sp => new FxLabService(
            sp.GetRequiredService<TradeJournal>(),
            log: msg => System.Diagnostics.Debug.WriteLine(msg)));

        // Weekly FX_LAB digest: rolls the genetic lab's journal entries into
        // one webhook summary + an append to the soak evidence doc. The
        // append lands as an uncommitted working-copy edit — committing
        // evidence stays a deliberate human act (soak rules).
        services.AddSingleton(sp => new FxLabWeeklyDigest(
            sp.GetRequiredService<TradeJournal>(),
            sp.GetRequiredService<WebhookService>())
        {
            SoakDocPath = FindSoakDocPath(AppContext.BaseDirectory),
        });

        // Maps tab: one selector-driven canvas over the market surfaces.
        // Pure read-side over the bridge bars + tick archive; never trades.
        services.AddSingleton(_ => new MapsViewModel());
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
            alerts: sp.GetRequiredService<PriceAlertEngine>(),
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
            sp.GetRequiredService<MapsViewModel>(),
            sp.GetRequiredService<TradeJournal>(),
            sp.GetRequiredService<ManualRealMoneyGate>()));

        return services;
    }

    /// <summary>Wires persisted settings onto the resolved services (webhook
    /// endpoint, digest cadence and state providers). Called once after Load.
    /// Internal so tests can run the real configuration pass against the real
    /// graph without starting anything.</summary>
    internal static void ConfigureFromSettings(IServiceProvider provider, AppSettings settings)
    {
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
                // Laggard-first per-symbol detail, matching the badge: the
                // digest is where monitoring sees the laggard without the app.
                var laggards = string.Join(", ", portfolio.SoakLaggards
                    .Select(h => $"{h.Symbol} {h.PaperSignalsSeen}/{h.PaperSoakSignalsRequired}"));
                parts.Add($"FX brain {mode} on {string.Join("+", portfolio.Symbols)} " +
                          $"soak {portfolio.PaperSignalsSeen}/{portfolio.PaperSoakSignalsRequired} {halt}" +
                          (laggards.Length > 0 ? $"; waiting on: {laggards}" : ""));
            }

            if (scorecard.LastSummary is { } sc)
            {
                parts.Add(sc);
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        };
    }

    /// <summary>Starts the fire-and-forget services after configuration.</summary>
    /// <summary>Walks up from the app directory to the enclosing checkout
    /// (docs/ + .git/ markers, same protocol as SafetyAuditDigest) and
    /// returns the soak evidence doc path for the weekly FX lab digest.
    /// Null when running outside a checkout — the append leg no-ops and
    /// only the webhook posts.</summary>
    private static string? FindSoakDocPath(string startDirectory)
    {
        for (var dir = Path.GetFullPath(startDirectory); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var soak = Path.Combine(dir, "docs", "soak");
            if (Directory.Exists(soak) && Directory.Exists(Path.Combine(dir, ".git")))
            {
                return Path.Combine(soak, "FX-LAB-WEEKLY.md");
            }
        }

        return null;
    }

    private static void StartBackgroundServices(IServiceProvider provider)
    {
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
        provider.GetRequiredService<MetricsDigestService>().Start();

        // AI journal analyst: toggles read live from the settings factory so
        // the Settings checkboxes apply without a restart (same pattern as
        // the milestone gate). Start() no-ops while Disabled.
        var analyst = provider.GetRequiredService<JournalAnalystService>();
        analyst.AnalystEnabledToggle = () => settingsFactory().AnalystEnabled;
        analyst.MemoryEnabledToggle = () => settingsFactory().AnalystMemoryEnabled;
        analyst.Start();

        // The risk narrator is event-driven (no Start); wire its live toggle.
        provider.GetRequiredService<RiskNarratorService>().NarratorEnabledToggle =
            () => settingsFactory().RiskNarratorEnabled;

        // Nightly genetic lab: same live-toggle pattern; Start() no-ops
        // while the toggle reports off.
        var lab = provider.GetRequiredService<FxLabService>();
        lab.Disabled = !settingsFactory().FxLabEnabled;
        lab.EnabledToggle = () => settingsFactory().FxLabEnabled;
        lab.Start();

        // Unlock-staleness alert: an armed session unlock past the
        // configured threshold journals REAL_MONEY_UNLOCK_STALE + toast +
        // webhook (the rail the removed hub used to own).
        provider.GetRequiredService<UnlockStalenessMonitor>().Start();

        // Weekly FX lab digest (webhook + soak doc append; silence with no
        // lab runs is correct — the lab journals FX_LAB only when it runs).
        provider.GetRequiredService<FxLabWeeklyDigest>().Start();
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
            Ioc.Default.GetService<JournalAnalystService>(),
            Ioc.Default.GetService<RiskNarratorService>(),
            Ioc.Default.GetService<FxLabService>(),
            Ioc.Default.GetService<FxLabWeeklyDigest>(),
            Ioc.Default.GetService<TickArchive>()
        ];

        foreach (var d in disposables)
        {
            d?.Dispose();
        }

        base.OnExit(e);
    }
}
