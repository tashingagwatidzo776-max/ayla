using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Analytics;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Core.Update;

namespace DongGfx.App;

public partial class App : System.Windows.Application
{
    /// <summary>Held for the process lifetime while this instance owns the
    /// session. The 5-minute "app autostart" watchdog relaunches the exe on
    /// a fixed schedule even when the app is healthy; without a guard the
    /// twin opens the journal beside the running instance and both race the
    /// same file (the IOException the crash guard contained twice today).
    /// The mutex makes the twin exit BEFORE any service wiring, so it never
    /// touches the journal at all.</summary>
    private Mutex? _singleInstanceMutex;

    /// <summary>True once this process passed the single-instance guard and
    /// wired its services — the early-exit twin runs none of the teardown.</summary>
    private bool _wired;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\DongGfx.App.SingleInstance",
            out var createdNew);
        if (!createdNew)
        {
            // Another instance already owns the session — exit before DI,
            // crash guards, or the journal exist in this process. Silent by
            // construction: journaling here is exactly the race we prevent.
            Shutdown();
            return;
        }

        var provider = ConfigureServices(new ServiceCollection()).BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);
        _wired = true;

        // Crash guards first: wire them before anything can fault, so a stray
        // exception anywhere (UI thread, a background thread, an unobserved
        // Task) is recorded and, where the runtime allows, contained — the app
        // must degrade, never vanish mid-session with the brain in an unknown
        // state.
        WireCrashGuards(Dispatcher, provider.GetRequiredService<AppLogger>(),
            provider.GetRequiredService<TradeJournal>());

        var settings = provider.GetRequiredService<SettingsService>().Load();
        ThemeManager.Apply(settings);   // MT5-classic or modern dark, persisted
        ConfigureFromSettings(provider, settings);
        StartBackgroundServices(provider);

        var window = new MainWindow
        {
            DataContext = provider.GetRequiredService<MainViewModel>()
        };
        window.Show();

        // The dashboard's fault notice links to the rows behind it: one click
        // opens the Journal tab filtered to APP_FAULT.
        provider.GetRequiredService<DashboardViewModel>().OpenFaultJournal =
            () => window.OpenJournal(Infrastructure.CrashFaults.Category);

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

        // Persistent brain memory (the type-7 guarded learning surface): the
        // training simulator's measured per-family record, loaded once per
        // session. Engines read it only as a bounded confidence tilt.
        services.AddSingleton(_ => FxBrainMemory.Load(SettingsService.DataDir));

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

        // Weekly FX_EXIT digest: the exit brain's evidence — override vs
        // consensus, exit reasons, MAE at exit, round-trips — plus the
        // shadow engines' promotion-ledger rollup. Journal-only, never trades.
        services.AddSingleton(sp => new FxExitWeeklyDigest(
            sp.GetRequiredService<TradeJournal>(),
            sp.GetRequiredService<WebhookService>())
        {
            SoakDocPath = FindSoakDocPath(AppContext.BaseDirectory),
            LedgerPath = Path.Combine(SettingsService.DataDir, "fx-shadow"),
            Tp1VerdictsPath = Path.Combine(
                SettingsService.DataDir, "watcher", "tp1-graded-verdicts.jsonl"),
            PlanReviewsPath = Path.Combine(
                SettingsService.DataDir, "watcher", "tp1-plan-reviews.jsonl"),
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
            setBrainRunningBound: v => sp.GetRequiredService<SettingsViewModel>().FxBrainRunning = v,
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
                        sp.GetRequiredService<Func<AppSettings>>()().NewsBlackoutMinutes),
                    shadowLedgerDir: Path.Combine(SettingsService.DataDir, "fx-shadow"),
                    // The paper-soak bar (10 signals per symbol, all-or-nothing)
                    // accrues across restarts; the build stamp scopes it, so a
                    // new commit starts the evidence over instead of inheriting
                    // another engine's. The MT5 login scopes it too: the bar is
                    // the evidence base for the go-live gate on the account
                    // about to trade, so an account switch restarts it.
                    soakLedger: new PaperSoakLedger(
                        PaperSoakLedger.PathFor(SettingsService.DataDir),
                        VersionInfo.Stamp,                    // The LIVE login the bridge last reported, not the
                    // login stored when settings were last saved: a switch
                    // made in the MT5 terminal's own GUI (bypassing the
                    // app's login dialog) still restarts the bar on the
                    // next brain start.
                        () => sp.GetRequiredService<Mt5BridgeClient>().LastLogin),
                    // Persistent brain memory: the training simulator's
                    // measured per-family record, loaded once per session and
                    // fed to every engine as a bounded confidence tilt. It
                    // re-ranks an alpha's already-computed confidence; it can
                    // never place, size, or schedule anything.
                    memory: sp.GetRequiredService<FxBrainMemory>());
            },
            // The real-money session unlock panel's arm action: latch the
            // shared gate AND write the audit row the journal formats as
            // REAL_MONEY_UNLOCK_ARMED, so arming real trading is as visible
            // as refusing it. The gate is process-lifetime and reset on
            // account switch / app close.
            armRealMoneyUnlock: () =>
            {
                var gate = sp.GetRequiredService<ManualRealMoneyGate>();
                gate.Arm();
                try
                {
                    sp.GetRequiredService<TradeJournal>().LogRealMoneyUnlockArmed(
                        Array.Empty<(Guid, string, bool)>(),
                        manualSurfaces: true,
                        armedBy: "unlock panel (Terminal)");
                }
                catch
                {
                    // The latch is the safety-relevant act; a journal hiccup
                    // must not leave the operator believing it did not arm.
                }
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

    /// <summary>Global crash guards. Wired once at startup; also callable in
    /// tests. Every handler records the fault (logger + journal) and contains
    /// it where the runtime permits: a UI-thread fault is marked handled so
    /// the window keeps running, and an unobserved Task fault is observed so
    /// it cannot escalate. Best-effort by construction — the guard must never
    /// throw while handling a throw.</summary>
    internal static void WireCrashGuards(
        System.Windows.Threading.Dispatcher dispatcher,
        AppLogger logger,
        TradeJournal? journal)
    {
        dispatcher.UnhandledException += (_, args) =>
        {
            ReportFatal(logger, journal, "UI", args.Exception);
            args.Handled = true;   // contain: keep the window alive
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(logger, journal, "process", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFatal(logger, journal, "task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Record a contained fault. Internal (not private) so tests can
    /// exercise the reporting path directly. Never throws — not with a null
    /// journal, not with a null exception, and not if the logger itself
    /// faults while logging a fault.</summary>
    internal static void ReportFatal(
        AppLogger logger, TradeJournal? journal, string surface, Exception? ex)
    {
        var summary = ex is null ? "unknown fault" : $"{ex.GetType().Name}: {ex.Message}";
        try
        {
            logger.Error(
                $"unhandled exception on the {surface} surface — contained (crash guard): {summary}",
                ex, "crash-guard");
        }
        catch
        {
            // Logging must never be the thing that brings the app down.
        }

        try
        {
            journal?.Log(Guid.Empty, "APP_FAULT",
                $"unhandled exception on the {surface} surface — contained (crash guard)",
                summary);
        }
        catch
        {
            // Same: a journal hiccup must not escalate a contained fault.
        }
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

        // Shadow-engine promotion card: the dashboard reads the per-symbol
        // shadow ledgers (fx-shadow-*.jsonl) so the giveback engine's road
        // to weight — settled trades, saves, hit rate — is visible in-app.
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigurePromotionLedger(Path.Combine(SettingsService.DataDir, "fx-shadow"));
        // TP1 plan-% review hold: surface the breaker beyond Settings with an
        // always-visible dashboard banner (read-only over the watcher's
        // ledger; refreshed on the promotion timer and on tab change).
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigureTp1PlanHold(() => Infrastructure.Tp1PlanBreaker.IsHeld(
                SettingsService.DataDir, System.DateTimeOffset.UtcNow));
        // ...and the watcher's latest recommendation itself: the candidate
        // plan % and its evidence, read from the same ledger (read-only).
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigureTp1PlanReview(() => Infrastructure.Tp1PlanReviewLedger.ReadLatest(
                Infrastructure.Tp1PlanReviewLedger.PathFor(SettingsService.DataDir),
                System.DateTimeOffset.UtcNow));
        // ...and the history behind it: the last few recommendations and how
        // each ended, so whether acting has been helping is visible in-app.
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigureTp1PlanHistory(() => Infrastructure.Tp1PlanReviewLedger.Recent(
                Infrastructure.Tp1PlanReviewLedger.PathFor(SettingsService.DataDir),
                System.DateTimeOffset.UtcNow));

        // TP1 partial prototype: the allocation plan's first rung is graded
        // live when this is armed (default OFF — an explicit opt-in).
        Services.FxEngineHost.ExecuteTp1Partials = settings.FxExecuteTp1Partials;
        // Entry-quality gate: clamp the persisted floor to [0,1] and arm it
        // on every launch/save. 0 = off (the historical behavior); a positive
        // value journals one startup row so the operator sees the floor.
        Services.FxEngineHost.MinEntryConfidence =
            Math.Clamp(settings.FxMinEntryConfidence, 0, 1);
        if (Services.FxEngineHost.MinEntryConfidence > 0)
        {
            provider.GetRequiredService<TradeJournal>().Log(Guid.Empty, "FX_MODE",
                $"entry confidence gate active at {Services.FxEngineHost.MinEntryConfidence:0.00} — "
                + "lower-confidence signals are journaled but not dispatched",
                "{}");
        }
        // The operator-armed plan-% override (dashboard one-click arm): clamp
        // to [0,100] and hand it to the engine, so a standing recommendation
        // armed in a previous session stays effective across restarts.
        Services.FxEngineHost.Tp1PlanPctOverride = settings.Tp1PlanPctOverride is { } pct
            ? Math.Clamp(pct, 0.0, 100.0)
            : null;
        // ...and the watcher's verdict on it: has that override actually
        // reached a rung? One JSON object the watcher rewrites every pass,
        // read back beside the Arm button so "waiting for a rung" /
        // "NEVER FIRED" shows where the operator clicked — and the pager's
        // answer and the banner's can never disagree. Wired after the
        // static above so the first refresh already sees the armed value.
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigureTp1PlanOverrideCheck(() => Infrastructure.Tp1PlanOverrideCheck.Read(
                Infrastructure.Tp1PlanOverrideCheck.PathFor(SettingsService.DataDir),
                Services.FxEngineHost.Tp1PlanPctOverride,
                System.DateTimeOffset.UtcNow));
        // Paper-soak progress AND its provenance on the dashboard: the account
        // bar's pill is the first thing that row clips when it is full, and a
        // bar resumed from an earlier session is different evidence from one
        // earned since this brain started.
        var soakTerminal = provider.GetRequiredService<TerminalViewModel>();
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigureFxSoakNote(
                () => DashboardViewModel.SoakNoteFor(soakTerminal.FxHost),
                () => DashboardViewModel.SoakRowsFor(soakTerminal.FxHost));
        // Refresh the line the instant the loop starts or stops — including
        // the ~10s startup auto-restore — instead of waiting for the next
        // 60s promotion tick, so a resumed bar is visible when it comes back.
        soakTerminal.FxBrainRunnerChanged += () =>
            provider.GetRequiredService<DashboardViewModel>().RefreshFxSoakNote();
        // ...and the timeline behind that line: every restore/restart the
        // engine journaled (build change, account switch), so the bar's
        // provenance is auditable in-app rather than only stated.
        provider.GetRequiredService<DashboardViewModel>()
            .ConfigureFxSoakTimeline(() => Infrastructure.SoakTimeline.FromJournal(
                provider.GetRequiredService<TradeJournal>().GetRecent(null, 400)));
        soakTerminal.FxBrainRunnerChanged += () =>
            provider.GetRequiredService<DashboardViewModel>().RefreshFxSoakTimeline();
        // The overdue plan-% review breaker: while a recommendation sits
        // open past its threshold, hold the TP1 rung (read-only over the
        // watcher's ledger, briefly cached). Wired live so a review the
        // operator acts on releases the rung without a restart.
        Services.FxEngineHost.Tp1PlanReviewHold = () =>
            Infrastructure.Tp1PlanBreaker.IsHeldCached(
                SettingsService.DataDir, System.DateTimeOffset.UtcNow);
        if (settings.FxExecuteTp1Partials)
        {
            // A restart re-arms silently otherwise — arming an execution
            // path is an event worth a journal row on EVERY activation,
            // not just the UI confirmation.
            provider.GetRequiredService<TradeJournal>().Log(Guid.Empty, "FX_MODE",
                "TP1 partial execution active at startup (armed in settings)", "{}");
        }

        // TP1 arming: the operator's confirmation gate (fail-closed when no
        // dialog host exists, e.g. headless/tests), plus a journal record —
        // arming an execution path is an event worth an audit trail.
        var settingsVm = provider.GetRequiredService<SettingsViewModel>();
        var journal = provider.GetRequiredService<TradeJournal>();
        settingsVm.ConfirmTp1Arm = summary =>
        {
            var result = System.Windows.MessageBox.Show(
                summary + "\n\nArm TP1 partial execution?",
                "Arm TP1 partials (prototype)",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (result == System.Windows.MessageBoxResult.Yes)
            {
                journal.Log(Guid.Empty, "FX_MODE",
                    "TP1 partial execution ARMED by operator (settings toggle confirmed)", "{}");
                return true;
            }
            return false;
        };

        // Saving settings for a REAL MONEY account: the same operator
        // confirmation gate, injected for the same reason (the view model
        // stays dialog-free and a headless/test host fails closed).
        settingsVm.ConfirmRealMoneySave = summary =>
            System.Windows.MessageBox.Show(
                summary + "\n\nDo you want to continue?",
                "⚠ Real Money Warning",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No)
            == System.Windows.MessageBoxResult.Yes;

        // One-click arm on the dashboard recommendation: the advice-to-action
        // step. Confirm-first (fail-closed when declined), then append the SAME
        // `acted` ledger event the CLI runbook writes AND persist the plan-%
        // override the engine honors — so the gate's advice becomes the plan.
        var dashboard = provider.GetRequiredService<DashboardViewModel>();
        var settingsService = provider.GetRequiredService<SettingsService>();
        dashboard.ArmTp1Plan = () =>
        {
            var path = Infrastructure.Tp1PlanReviewLedger.PathFor(SettingsService.DataDir);
            var rec = Infrastructure.Tp1PlanReviewLedger.ReadLatest(
                path, System.DateTimeOffset.UtcNow);
            if (rec?.CandidatePct is not { } pct)
            {
                return false;
            }

            pct = Math.Clamp(pct, 0.0, 100.0);
            var result = System.Windows.MessageBox.Show(
                $"Arm the gate's recommended TP1 plan %.\n\n{rec.Summary}\n\n" +
                $"The engine will use {pct:0.##}% for the TP1 rung (in place of the " +
                $"allocation plan's own leg) from the next cycle. This changes live " +
                $"demo order sizing.\n\nArm this plan?",
                "Arm recommended TP1 plan %",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return false;
            }

            var current = settingsService.Load();
            current.Tp1PlanPctOverride = pct;
            settingsService.Save(current);
            Services.FxEngineHost.Tp1PlanPctOverride = pct;
            Infrastructure.Tp1PlanReviewLedger.MarkActed(
                path, rec.Id, $"armed plan-% override {pct:0.##} (dashboard)");
            Infrastructure.Tp1PlanBreaker.Invalidate();
            journal.Log(Guid.Empty, "FX_MODE",
                $"TP1 plan-% override ARMED at {pct:0.##}% by operator (dashboard, review {rec.Id})",
                System.Text.Json.JsonSerializer.Serialize(new { PlanPct = pct, ReviewId = rec.Id }));
            return true;
        };
        dashboard.RevertTp1Plan = () =>
        {
            var current = settingsService.Load();
            if (current.Tp1PlanPctOverride is null
                && Services.FxEngineHost.Tp1PlanPctOverride is null)
            {
                return false;
            }

            current.Tp1PlanPctOverride = null;
            settingsService.Save(current);
            Services.FxEngineHost.Tp1PlanPctOverride = null;
            journal.Log(Guid.Empty, "FX_MODE",
                "TP1 plan-% override REVERTED by operator (dashboard) — back to the allocation plan",
                "{}");
            return true;
        };

        // The banner does not just report WHY the override has not reached
        // a rung — it offers the one thing that unblocks it. Each hook is
        // fail-closed on its own terms; the reason that maps to no fix (the
        // trailing gate, a rung not yet eligible) gets no button at all.
        dashboard.StartTp1Brain = () =>
        {
            // Resolved lazily: the banner action fires long after startup,
            // and pulling TerminalViewModel's whole graph up early would
            // race the shell's own wiring.
            var terminal = provider.GetRequiredService<TerminalViewModel>();
            // Only ever START: the toggle would stop a running brain, and
            // the banner's whole claim is that it is not running. A brain
            // already up means the verdict was stale — do nothing.
            if (terminal.FxHost?.IsRunning == true)
            {
                return false;
            }

            terminal.ToggleFxBrainCommand.Execute(null);
            return terminal.FxHost?.IsRunning == true;
        };
        dashboard.ArmTp1Partials = () =>
        {
            // The setter is the confirmation gate: declining reverts it to
            // OFF (fail-closed), so reading it back is the honest answer.
            settingsVm.FxExecuteTp1Partials = true;
            return settingsVm.FxExecuteTp1Partials;
        };
        dashboard.ActTp1OverdueReview = () =>
        {
            // Same act the Settings list offers: refill the open list, then
            // mark the oldest overdue one acted, which drops the breaker
            // cache and releases the rung next cycle.
            settingsVm.RefreshTp1Hint();
            var open = settingsVm.OpenTp1Reviews.FirstOrDefault();
            if (open is null)
            {
                return false;
            }

            settingsVm.ActTp1ReviewCommand.Execute(open);
            return true;
        };

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
        // Contained-fault notice + safe mode: a recent crash-guard row means
        // the last session faulted. Surface it on the dashboard and HOLD THE
        // ENGINE LOOP BACK — an auto-restoring brain that keeps faulting is a
        // crash loop, so the loop only comes back by an explicit human act.
        // The HOLD is gated by SafeModeNeeded: an operator release
        // acknowledges the faults that preceded it, so a restart after a
        // release auto-starts the brain; a fault newer than the release
        // re-arms the hold-back.
        var faultJournal = provider.GetRequiredService<TradeJournal>();
        var recentFaults = Infrastructure.CrashFaults.FromJournal(
            faultJournal.GetRecent(null, 400), DateTimeOffset.UtcNow);
        var safeMode = Infrastructure.CrashFaults.SafeModeNeeded(
            faultJournal.GetRecent(null, 400), DateTimeOffset.UtcNow);
        var dashboardVm = provider.GetRequiredService<DashboardViewModel>();
        dashboardVm.ConfigureFaultNotice(() => new DashboardViewModel.FaultNotice(
            DashboardViewModel.FaultNoticeFor(
                Infrastructure.CrashFaults.FromJournal(
                    faultJournal.GetRecent(null, 400), DateTimeOffset.UtcNow),
                safeMode) ?? "",
            safeMode));
        dashboardVm.ResumeAfterFault = () =>
        {
            var terminal = provider.GetRequiredService<TerminalViewModel>();
            if (terminal.FxHost?.IsRunning == true)
            {
                safeMode = false;
                return true;
            }

            terminal.ToggleFxBrainCommand.Execute(null);
            var running = terminal.FxHost?.IsRunning == true;
            if (running)
            {
                safeMode = false;
                // A DIFFERENT category from APP_FAULT: announcing the release
                // must not itself count as a fault.
                faultJournal.Log(Guid.Empty, Infrastructure.CrashFaults.SafeModeCategory,
                    "safe mode released by operator — engine loop started after contained fault(s)",
                    "{}");
            }

            return running;
        };
        if (safeMode)
        {
            faultJournal.Log(Guid.Empty, Infrastructure.CrashFaults.SafeModeCategory,
                $"safe mode: engine loop held back after {recentFaults.Count} contained fault(s) in the last 24h",
                "{}");
        }

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

        // Weekly FX exit digest: the exit brain's settlement evidence.
        provider.GetRequiredService<FxExitWeeklyDigest>().Start();

        // The engine loop is ALWAYS-ON by operator policy: every launch
        // auto-starts the brain loop (paper mode; go-live stays a human act),
        // unless the crash safe-mode hold above is active — a fault loop is
        // never auto-started. Delayed so the terminal view finishes
        // constructing, and marshaled to the UI thread — the toggle touches
        // the VM's observable properties (StartBackgroundServices runs on it).
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            try
            {
                if (safeMode)
                {
                    // Held back by the fault guard: never auto-restore into a
                    // fault loop. The dashboard's resume button is the way back.
                    return;
                }

                await ui.InvokeAsync(() =>
                    provider.GetRequiredService<TerminalViewModel>()
                        .AutoStartBrainLoop()).Task.ConfigureAwait(false);
            }
            catch
            {
                // auto-restore is a convenience — never a startup risk
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_wired && Ioc.Default.GetService<MainViewModel>() is { } vm)
        {
            vm.Shutdown();
        }

        // Dispose all IDisposable services to release file handles, timers, etc.
        // TickArchive closes late on purpose: it flushes buffered ticks, so
        // every feed that writes it must be gone before this runs. Skipped on
        // the single-instance early-exit path: that process wired no services
        // and must not touch the container.
        if (_wired)
        {
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
                Ioc.Default.GetService<FxExitWeeklyDigest>(),
                Ioc.Default.GetService<TickArchive>()
            ];

            foreach (var d in disposables)
            {
                d?.Dispose();
            }
        }

        if (_wired)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
                // The mutex is a session guard; failing to release it at exit
                // costs nothing (the handle closes with the process).
            }
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
