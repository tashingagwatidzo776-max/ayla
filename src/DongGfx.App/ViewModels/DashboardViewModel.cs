using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Controls;
using DongGfx.Core.Models;

namespace DongGfx.App.ViewModels;

/// <summary>
/// The slimmed dashboard: the app-wide kill switch (which crosses venues —
/// engaging it stops the FX brain and flattens every open MT5 position),
/// the FX brain's status line, and one alert line per latched risk rail.
/// The binary-options panels (growth drill-down, combined growth P&amp;L,
/// Deriv connection/chart feed) went away with that integration; the risk
/// rails and their toast/webhook machinery are deliberately unchanged —
/// they are what stops a bad day.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly Infrastructure.NotificationService? _notifications;
    private readonly Infrastructure.WebhookService? _webhook;

    /// <summary>Top-line status (kill switch state / bridge reachability).</summary>
    [ObservableProperty]
    private string statusText = "Connected   ";

    /// <summary>One line describing the FX brain: mode (PAPER/LIVE/stopped),
    /// symbols and halt state. Kept current by the Terminal via
    /// <see cref="ReportFxStatus"/>.</summary>
    [ObservableProperty]
    private string fxStatusText = "FX brain idle";

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string symbolText = "—";

    [ObservableProperty]
    private string lastPriceText = "—";

    [ObservableProperty]
    private string tickCountText = "0 ticks";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isKillSwitchEngaged;

    /// <summary>True while the FX brain's loss stop / risk halt is latched
    /// (the FX equivalent of the old portfolio governor: the supervisor's
    /// latched daily-loss stop, equity floor or kill-switch cross-venue
    /// halt). Driven by <see cref="ReportFxStatus"/>.</summary>
    [ObservableProperty]
    private bool isGovernorLatched;

    /// <summary>True while the supervisor is close to its loss cap (80% of
    /// the daily-loss budget used) — the pre-trip warning equivalent.</summary>
    [ObservableProperty]
    private bool isGovernorWarned;

    /// <summary>Today's realised FX P/L (fed from the deal feed).</summary>
    [ObservableProperty]
    private string combinedGrowthPnlText = "$0.00";

    /// <summary>Per-risk-rail summary lines for the dashboard card.</summary>
    public ObservableCollection<string> RiskRailAlerts { get; } = new();

    /// <summary>The live chart view (attached by MainWindow; VM stays UI-agnostic).</summary>
    public TickChartControl? Chart { get; set; }

    /// <summary>MT5-style candle chart (attached by MainWindow; fed when
    /// ChartStyle is Candles — ticks aggregate into synthetic 5-second
    /// bars so zoom/pan work on live data).</summary>
    public Controls.CandleChartControl? CandleChart { get; set; }

    /// <summary>Dashboard chart style — MT5 shows candles by default.</summary>
    [ObservableProperty]
    private bool candlesPreferred;

    [RelayCommand]
    private void ToggleChartStyle()
    {
        CandlesPreferred = !CandlesPreferred;
        if (CandlesPreferred && CandleChart is not null)
        {
            // Seed the candle view from the ticks the line chart already
            // holds, so the switch isn't an empty chart.
            CandleChart.SetBars(System.Array.Empty<Core.Fx.FxBar>());
            PushCandles(Chart?.Ticks ?? new System.Collections.Generic.List<Tick>());
        }
    }

    /// <summary>Aggregates ticks into ~5s bars for the candle view
    /// (called on the UI thread after each feed update).</summary>
    private void PushCandles(IReadOnlyList<Tick> ticks)
    {
        if (!CandlesPreferred || CandleChart is null)
        {
            return;
        }

        foreach (var t in ticks)
        {
            var second = t.Epoch / 1000 / 5 * 5;
            if (CandleChart.Bars.Count > 0 && CandleChart.Bars[^1].Time == second)
            {
                var prev = CandleChart.Bars[^1];
                CandleChart.UpdateLast(prev with
                {
                    High = Math.Max(prev.High, t.Quote),
                    Low = Math.Min(prev.Low, t.Quote),
                    Close = t.Quote,
                });
            }
            else
            {
                CandleChart.UpsertBar(new Core.Fx.FxBar(second, t.Quote, t.Quote, t.Quote, t.Quote, 0));
            }
        }
    }

    public DashboardViewModel(
        Infrastructure.NotificationService? notifications = null,
        Infrastructure.WebhookService? webhook = null)
    {
        _notifications = notifications;
        _webhook = webhook;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    // ── shadow-engine promotion card ─────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _promotionTimer;
    private string? _promotionLedgerDir;

    /// <summary>One row of the promotion card (per shadow engine, rolled up
    /// across ALL symbols). Helps = saved a winner's give-back (giveback
    /// engine) or wanted out of a held winner (classic rule). Strings are
    /// pre-formatted so the XAML binds plain text.</summary>
    public sealed record PromotionRow(
        string Engine, string Settled, string Evidence, string HitRate, string Weight);

    /// <summary>Per-engine promotion progress: settled trades, evidence
    /// events, hit rate, and the weight that record has (not yet) earned.
    /// Promotion bar: 100 settled trades at a 60% hit rate, Monte-Carlo
    /// STABLE, shipped only by a reviewed PR.</summary>
    public ObservableCollection<PromotionRow> PromotionRows { get; } = new();

    /// <summary>One line about the engine that matters most — giveback.</summary>
    [ObservableProperty]
    private string promotionSummaryText = "no promotion evidence yet";

    // ── TP1 plan-% review hold (breaker state beyond Settings) ────────

    private Func<bool>? _tp1PlanHoldProvider;

    /// <summary>True while an overdue plan-% review is holding the TP1 rung
    /// — drives the always-visible dashboard banner, so an operator sees the
    /// pause without opening Settings.</summary>
    [ObservableProperty]
    private bool isTp1PlanHeld;

    /// <summary>Banner text for the hold (empty when not held).</summary>
    [ObservableProperty]
    private string tp1PlanHoldText = "";

    /// <summary>Points the banner at the breaker's hold state (a provider, so
    /// the read stays uncached-but-cheap on each refresh). Null clears it.
    /// Read-only over the watcher's ledger; never trades.</summary>
    public void ConfigureTp1PlanHold(Func<bool>? hold)
    {
        _tp1PlanHoldProvider = hold;
        RefreshTp1PlanHold();
    }

    /// <summary>Re-reads the breaker's hold and updates the banner. Polled on
    /// the promotion timer and on tab change, so it cannot go stale while the
    /// app runs.</summary>
    public void RefreshTp1PlanHold()
    {
        var held = _tp1PlanHoldProvider?.Invoke() ?? false;
        OnUiThread(() =>
        {
            IsTp1PlanHeld = held;
            Tp1PlanHoldText = held
                ? "⚠ TP1 rung HELD — an overdue plan-% review is open (Settings → Prototype)"
                : "";
        });
    }

    private Func<Infrastructure.Tp1PlanReviewLedger.Recommendation?>? _tp1Recommendation;

    /// <summary>The watcher's latest gate recommendation — the candidate plan
    /// %, direction, net R and graded count — read from the plan-review
    /// ledger, so the operator sees what the gate advised without a terminal.</summary>
    [ObservableProperty]
    private string tp1RecommendationText = "";

    /// <summary>True when there is a recommendation to show.</summary>
    [ObservableProperty]
    private bool hasTp1Recommendation;

    /// <summary>Label for the one-click arm button, naming the plan % it will
    /// arm (e.g. "Arm 15%") — so the act is explicit about its effect.</summary>
    [ObservableProperty]
    private string tp1RecommendationArmLabel = "Arm recommendation";

    /// <summary>The one-click arm (App layer): runs the confirm dialog, appends
    /// the <c>acted</c> review event and persists the plan-% override the
    /// engine honors. Returns true when the override was armed. Null
    /// (tests/headless) leaves the button inert.</summary>
    public Func<bool>? ArmTp1Plan { get; set; }

    /// <summary>Clears the armed plan-% override (App layer). Null leaves the
    /// button inert.</summary>
    public Func<bool>? RevertTp1Plan { get; set; }

    /// <summary>True while an operator-armed plan-% override is in force —
    /// the engine is using it in place of the allocation plan's TP1 leg.</summary>
    [ObservableProperty]
    private bool hasTp1PlanOverride;

    /// <summary>Banner text for the armed override (empty when none).</summary>
    [ObservableProperty]
    private string tp1PlanOverrideText = "";

    private Func<Infrastructure.Tp1PlanOverrideCheck.Verdict?>?
        _tp1PlanOverrideCheck;

    /// <summary>True while the watcher's verdict on the armed override is
    /// current enough to show — drives the banner's second line.</summary>
    [ObservableProperty]
    private bool hasTp1PlanOverrideCheck;

    /// <summary>The watcher's verdict line (empty when there is none to
    /// show: nothing armed, stale, or about a different plan %).</summary>
    [ObservableProperty]
    private string tp1PlanOverrideCheckText = "";

    /// <summary>Points the banner at the watcher's verdict reader. Null
    /// clears it. Read-only over a file the watcher writes; never trades.</summary>
    public void ConfigureTp1PlanOverrideCheck(
        Func<Infrastructure.Tp1PlanOverrideCheck.Verdict?>? check)
    {
        _tp1PlanOverrideCheck = check;
        RefreshTp1PlanOverride();
    }

    /// <summary>Re-reads the armed plan-% override (a process-wide engine
    /// static) and the watcher's verdict on it. Polled with the rest on the
    /// timer and on tab change. The verdict is fetched only while an
    /// override is armed, and the reader already refuses one computed for a
    /// different plan % — so a stale file can never label the banner.</summary>
    public void RefreshTp1PlanOverride()
    {
        var ov = Services.FxEngineHost.Tp1PlanPctOverride;
        var check = ov is null ? null : _tp1PlanOverrideCheck?.Invoke();
        // The blocker and its fix are decided together: no override armed,
        // or a reason with no in-app fix, means no button to press.
        var reason = check?.Reason ?? "";
        var label = ActionLabelFor(reason);
        var hint = ActionHintFor(reason);
        OnUiThread(() =>
        {
            HasTp1PlanOverride = ov is not null;
            Tp1PlanOverrideText = ov is { } p
                ? $"TP1 plan % armed at {p:0.##}% — the engine uses it in place of the allocation plan"
                : "";
            HasTp1PlanOverrideCheck = check is not null;
            Tp1PlanOverrideCheckText = check?.Summary ?? "";
            Tp1PlanActionReason = reason;
            Tp1PlanActionLabel = label;
            HasTp1PlanAction = label.Length > 0;
            Tp1PlanActionHint = hint;
            HasTp1PlanActionHint = hint.Length > 0;
        });
    }

    /// <summary>The dashboard's one-click arm: applies the gate's recommended
    /// plan % (confirm-first, through the App layer), then refreshes so the
    /// armed state and the review's new status show immediately.</summary>
    [RelayCommand]
    private void ArmTp1Recommendation()
    {
        _ = ArmTp1Plan?.Invoke() ?? false;
        RefreshTp1PlanReview();
        RefreshTp1PlanOverride();
    }

    /// <summary>Releases the armed plan-% override (back to the brain's own
    /// allocation plan) and refreshes.</summary>
    [RelayCommand]
    private void RevertTp1PlanOverride()
    {
        _ = RevertTp1Plan?.Invoke() ?? false;
        RefreshTp1PlanReview();
        RefreshTp1PlanOverride();
    }

    // ── the reason-matched action (fix the blocker, don't just report it) ─

    /// <summary>Starts the brain's engine loop when it is stopped (App
    /// layer, guarded so it can never STOP a running brain). Null leaves
    /// the button inert.</summary>
    public Func<bool>? StartTp1Brain { get; set; }

    /// <summary>Arms TP1 partial execution — confirm-gated in the setter,
    /// so declining leaves it off. Null leaves the button inert.</summary>
    public Func<bool>? ArmTp1Partials { get; set; }

    /// <summary>Marks the overdue plan-% review acted, releasing the
    /// breaker's hold on the rung. Null leaves the button inert.</summary>
    public Func<bool>? ActTp1OverdueReview { get; set; }

    /// <summary>The verdict's reason, kept raw so the command can dispatch
    /// on it (the banner text is the rendered form of the same field).</summary>
    [ObservableProperty]
    private string tp1PlanActionReason = "";

    /// <summary>Label for the button that fixes the blocker — empty when
    /// this reason has no in-app fix (or no override is armed).</summary>
    [ObservableProperty]
    private string tp1PlanActionLabel = "";

    /// <summary>True while there is a reason-matched action to offer.</summary>
    [ObservableProperty]
    private bool hasTp1PlanAction;

    /// <summary>What to do about each blocker. Reasons the banner can name
    /// but the app cannot fix (the trailing gate, a rung simply not yet
    /// eligible) map to no button — offering a dead control is worse than
    /// offering none.</summary>
    private static string ActionLabelFor(string reason) => reason switch
    {
        "engine-idle" or "app-idle" => "Start brain",
        "partials-off" => "Arm TP1 partials",
        "hold-blocked" => "Act on review",
        _ => "",
    };

    /// <summary>What stands where the button would be — for the reasons
    /// the banner can name but the app cannot fix. A blank where a button
    /// should be reads as a broken UI; this says the absence is
    /// deliberate, and why. Empty for every fixable reason (the button
    /// speaks for itself) and for reasons this build cannot interpret.</summary>
    [ObservableProperty]
    private string tp1PlanActionHint = "";

    /// <summary>True while an explanation stands in for the button.</summary>
    [ObservableProperty]
    private bool hasTp1PlanActionHint;

    private static string ActionHintFor(string reason) => reason switch
    {
        "gate-blocked" => "nothing to press — the trailing gate has said no; the floor owns this rung",
        "no-eligible-rung" => "nothing to press — nothing is unmet; the setup simply has not come",
        _ => "",
    };

    /// <summary>Does what the banner's reason says it needs, then refreshes
    /// so the verdict (and therefore the button) reflects the attempt.</summary>
    [RelayCommand]
    private void TakeTp1PlanAction()
    {
        var reason = Tp1PlanActionReason;
        var taken = reason switch
        {
            "engine-idle" or "app-idle" => StartTp1Brain?.Invoke(),
            "partials-off" => ArmTp1Partials?.Invoke(),
            "hold-blocked" => ActTp1OverdueReview?.Invoke(),
            _ => null,
        };
        ConfirmTp1PlanAction(reason, taken);
        RefreshTp1PlanReview();
        RefreshTp1PlanOverride();
    }

    /// <summary>The operator pressed the fix — say whether it took. A
    /// silent click is indistinguishable from a dead button, so every
    /// dispatched attempt reports its outcome (done or refused); a reason
    /// that dispatched nothing stays quiet. Sent directly through the
    /// notification service — deliberately past its rate limiter, which
    /// exists to space out background events, not to swallow the
    /// confirmation of the user's own click.</summary>
    private void ConfirmTp1PlanAction(string reason, bool? taken)
    {
        var (body, severity) = reason switch
        {
            "engine-idle" or "app-idle" => taken == true
                ? ("Brain started — the engine loop is running; the next rung can carry the override",
                   "success")
                : ("Brain did not start — it may already be running; check the FX BRAIN badge",
                   "warning"),
            "partials-off" => taken == true
                ? ("TP1 partial execution armed — rungs can now take the override", "success")
                : ("Arming declined — TP1 partial execution stays OFF (fail-closed)", "warning"),
            "hold-blocked" => taken == true
                ? ("Oldest overdue review marked acted — the rung is released", "success")
                : ("No open review was acted — the hold stays until one is", "warning"),
            _ => ("", ""),
        };
        if (body.Length > 0 && _notifications is { Enabled: true })
        {
            _notifications.SendToast("🧩 TP1 override action", body, severity);
        }
    }

    /// <summary>Points the recommendation line at the ledger reader. Null
    /// clears it. Read-only over the watcher's ledger; never trades.</summary>
    public void ConfigureTp1PlanReview(
        Func<Infrastructure.Tp1PlanReviewLedger.Recommendation?>? latest)
    {
        _tp1Recommendation = latest;
        RefreshTp1PlanReview();
    }

    /// <summary>Re-reads the watcher's latest recommendation. Polled on the
    /// promotion timer and on tab change, like the hold banner.</summary>
    public void RefreshTp1PlanReview()
    {
        var rec = _tp1Recommendation?.Invoke();
        OnUiThread(() =>
        {
            HasTp1Recommendation = rec is not null;
            Tp1RecommendationText = rec is null
                ? ""
                : $"TP1 plan-% recommendation · {rec.Summary}";
            Tp1RecommendationArmLabel = rec?.CandidatePct is { } c
                ? $"Arm {c:0.##}%"
                : "Arm recommendation";
        });

        RefreshTp1PlanHistory();
        RefreshTp1PlanOverride();
        RefreshFxSoakNote();
        RefreshFxSoakTimeline();
        RefreshFaultNotice();
    }

    // ── the paper-soak line (progress AND where it came from) ─────────

    private Func<string?>? _fxSoakNote;
    private Func<IReadOnlyList<SoakSymbolRow>>? _fxSoakRows;

    /// <summary>Points the dashboard's paper-soak line at the live brain
    /// (null clears it). Read-only: the line reports the bar's progress and
    /// where it came from; it never starts or stops anything.</summary>
    public void ConfigureFxSoakNote(Func<string?>? note, Func<IReadOnlyList<SoakSymbolRow>>? rows = null)
    {
        _fxSoakNote = note;
        _fxSoakRows = rows;
        RefreshFxSoakNote();
    }

    /// <summary>Re-reads the paper-soak line AND the per-symbol rows. Polled
    /// with the rest, and after a brain start/stop.</summary>
    public void RefreshFxSoakNote()
    {
        var text = _fxSoakNote?.Invoke() ?? "";
        var rows = _fxSoakRows?.Invoke()
            ?? (IReadOnlyList<SoakSymbolRow>)Array.Empty<SoakSymbolRow>();
        OnUiThread(() =>
        {
            FxSoakNote = text;
            HasFxSoakNote = text.Length > 0;
            FxSoakSymbols.Clear();
            foreach (var row in rows)
            {
                FxSoakSymbols.Add(row);
            }

            HasFxSoakSymbols = FxSoakSymbols.Count > 0;
        });
    }

    /// <summary>One symbol's line in the per-symbol soak readout.</summary>
    public sealed record SoakSymbolRow(string Symbol, int Seen, int Required, bool Complete)
    {
        /// <summary>Display text: "SYMBOL seen/required" with a check when
        /// the symbol has met its bar — readable without opening the ledger.</summary>
        public string Display => $"{Symbol} {Seen}/{Required}{(Complete ? " ✓" : "")}";
    }

    /// <summary>Per-symbol paper-soak progress (seen/required for every
    /// symbol), so the whole book's bar is visible at a glance instead of
    /// only the aggregate + one laggard.</summary>
    public ObservableCollection<SoakSymbolRow> FxSoakSymbols { get; } = new();

    /// <summary>True when there are per-symbol rows to show.</summary>
    [ObservableProperty]
    private bool hasFxSoakSymbols;

    /// <summary>The per-symbol rows read from the live portfolio (empty when
    /// the brain is not running). Read-only; never starts or stops anything.</summary>
    public static IReadOnlyList<SoakSymbolRow> SoakRowsFor(Services.FxPortfolioHost? host)
    {
        if (host is null || !host.IsRunning)
        {
            return Array.Empty<SoakSymbolRow>();
        }

        return host.Hosts
            .OrderBy(h => h.PaperSignalsSeen)
            .ThenBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(h => new SoakSymbolRow(
                h.Symbol, h.PaperSignalsSeen, h.PaperSoakSignalsRequired, h.PaperSoakComplete))
            .ToList();
    }

    /// <summary>The line's wording, read from the live portfolio — a static
    /// so the sentences stay pinned by tests, like the reason→button
    /// mapping. Null when the brain is not running (nothing to report).
    /// <para>The account bar's soak pill is the FIRST element the
    /// right-docked bar clips when that row is full (measured: four symbols
    /// shrink it to its tail), so progress and provenance get a line of their
    /// own here, where nothing can squeeze them out.</para></summary>
    public static string? SoakNoteFor(Services.FxPortfolioHost? host)
    {
        if (host is null || !host.IsRunning)
        {
            return null;
        }

        var laggard = host.SoakLaggards.FirstOrDefault();
        var progress = $"paper soak {host.PaperSignalsSeen}/{host.PaperSoakSignalsRequired}"
            + (laggard is null
                ? ""
                : $" · laggard {laggard.Symbol} {laggard.PaperSignalsSeen}/{laggard.PaperSoakSignalsRequired}");
        if (host.PaperSoakInvalidatedBuild is { } dropped)
        {
            return $"{progress} · restarted — build changed since {dropped}";
        }

        if (host.PaperSoakInvalidatedAccount is { } droppedAccount)
        {
            return $"{progress} · restarted — account changed since {droppedAccount}";
        }

        return host.PaperSoakRestored > 0
            ? $"{progress} · {host.PaperSoakRestored} carried over from the last session"
            : progress;
    }

    /// <summary>The paper-soak line: per-symbol progress plus — the point of
    /// the line — whether the bar was carried over from an earlier session or
    /// restarted on a build change.</summary>
    [ObservableProperty]
    private string fxSoakNote = "";

    /// <summary>True while there is a soak line to show (the brain runs).</summary>
    [ObservableProperty]
    private bool hasFxSoakNote;

    // ── the paper-soak timeline (the audit trail behind the line) ─────

    /// <summary>One row of the soak timeline: when it happened, the sentence
    /// the engine journaled, and whether it restarted the bar (a build or
    /// account change) rather than carried it over.</summary>
    public sealed record SoakTimelineRowView(string When, string Text, bool IsRestart);

    /// <summary>The restore/restart history parsed from the journal, newest
    /// first — so the soak line's provenance is auditable, not just stated.</summary>
    public ObservableCollection<SoakTimelineRowView> FxSoakTimeline { get; } = new();

    /// <summary>True when there is any soak history to show.</summary>
    [ObservableProperty]
    private bool hasFxSoakTimeline;

    private Func<IReadOnlyList<Infrastructure.SoakTimeline.Row>>? _fxSoakTimeline;

    /// <summary>Points the soak timeline at the journal reader (newest
    /// first). Null clears it. Read-only; never trades.</summary>
    public void ConfigureFxSoakTimeline(
        Func<IReadOnlyList<Infrastructure.SoakTimeline.Row>>? recent)
    {
        _fxSoakTimeline = recent;
        RefreshFxSoakTimeline();
    }

    /// <summary>Re-reads the soak timeline. Polled with the rest.</summary>
    public void RefreshFxSoakTimeline()
    {
        var rows = _fxSoakTimeline?.Invoke()
            ?? (IReadOnlyList<Infrastructure.SoakTimeline.Row>)
                Array.Empty<Infrastructure.SoakTimeline.Row>();
        OnUiThread(() =>
        {
            FxSoakTimeline.Clear();
            foreach (var row in rows)
            {
                FxSoakTimeline.Add(new SoakTimelineRowView(
                    row.At.ToLocalTime().ToString("MM-dd HH:mm"), row.Text, row.Restart));
            }
            HasFxSoakTimeline = FxSoakTimeline.Count > 0;
        });
    }

    // ── contained-fault notice (and safe mode) ───────────────────────

    /// <summary>What to show about contained crash-guard faults: the sentence
    /// and whether the engine loop was held back (safe mode).</summary>
    public sealed record FaultNotice(string Text, bool SafeMode);

    private Func<FaultNotice?>? _faultNotice;

    /// <summary>Points the notice at the journal reader. Null clears it.
    /// Read-only; never trades.</summary>
    public void ConfigureFaultNotice(Func<FaultNotice?>? notice)
    {
        _faultNotice = notice;
        RefreshFaultNotice();
    }

    /// <summary>Re-reads the fault notice. Polled with the rest, and after a
    /// resume so the notice clears the moment it is acted on.</summary>
    public void RefreshFaultNotice()
    {
        FaultNotice? notice = null;
        try
        {
            notice = _faultNotice?.Invoke();
        }
        catch
        {
            // A fault-notice provider must never be the next fault.
        }

        OnUiThread(() =>
        {
            FaultNoticeText = notice?.Text ?? "";
            HasFaultNotice = FaultNoticeText.Length > 0;
            IsSafeMode = notice?.SafeMode == true;
        });
    }

    /// <summary>The contained-fault line. Empty when nothing recent — silence
    /// is the healthy state.</summary>
    [ObservableProperty]
    private string faultNoticeText = "";

    /// <summary>True while there is a fault notice to show.</summary>
    [ObservableProperty]
    private bool hasFaultNotice;

    /// <summary>True when the engine loop was held back after a fault.</summary>
    [ObservableProperty]
    private bool isSafeMode;

    /// <summary>One-line outcome of the last resume attempt.</summary>
    [ObservableProperty]
    private string faultStatus = "";

    /// <summary>Set by the app shell: explicitly starts the engine loop after
    /// a safe-mode start. True when the brain is running afterwards.</summary>
    public Func<bool>? ResumeAfterFault { get; set; }

    /// <summary>Operator's explicit "resume anyway" after a safe-mode start —
    /// the loop only ever comes back by a human act.</summary>
    [RelayCommand]
    private void ResumeFromSafeMode()
    {
        var started = false;
        try
        {
            started = ResumeAfterFault?.Invoke() ?? false;
        }
        catch
        {
            started = false;
        }

        FaultStatus = started
            ? "engine loop started"
            : "engine loop did not start — see the Terminal tab";
        RefreshFaultNotice();
    }

    /// <summary>Set by the app shell: opens the Journal tab filtered to the
    /// fault category, so the rows behind the count are one click away.</summary>
    public Action? OpenFaultJournal { get; set; }

    /// <summary>Opens the journal at the contained-fault rows.</summary>
    [RelayCommand]
    private void ViewFaults()
    {
        try
        {
            OpenFaultJournal?.Invoke();
        }
        catch
        {
            // Navigation is best-effort; never let it fault the dashboard.
        }
    }

    /// <summary>The notice's wording, read from a fault summary — a static so
    /// the sentences stay pinned by tests, like SoakNoteFor.</summary>
    public static string? FaultNoticeFor(Infrastructure.CrashFaults.Summary? summary, bool safeMode)
    {
        if (summary is null || summary.Count == 0)
        {
            return null;
        }

        var when = summary.LatestAt is { } at
            ? at.ToLocalTime().ToString("MM-dd HH:mm")
            : "unknown time";
        return safeMode
            ? $"SAFE MODE — the engine loop was NOT auto-started after {summary.Count} "
              + $"contained fault(s); latest {when} · {summary.Latest}"
            : $"{summary.Count} contained fault(s) in the last 24h; latest {when} · {summary.Latest}";
    }

    // ── recommendation history (has acting been helping?) ─────────────

    private Func<IReadOnlyList<Infrastructure.Tp1PlanReviewLedger.HistoryRow>>?
        _tp1HistoryProvider;

    /// <summary>One row of the history list: the recommendation's evidence
    /// and how the review ended.</summary>
    public sealed record HistoryRowView(string Summary, string Outcome, bool IsOpen);

    /// <summary>The last few gate recommendations, newest first — so the
    /// operator sees the whole advice-to-action trail in one place.</summary>
    public ObservableCollection<HistoryRowView> Tp1ReviewHistory { get; } = new();

    /// <summary>True when there is any history to show.</summary>
    [ObservableProperty]
    private bool hasTp1ReviewHistory;

    /// <summary>Points the history list at the ledger reader (newest first).
    /// Null clears it. Read-only; never trades.</summary>
    public void ConfigureTp1PlanHistory(
        Func<IReadOnlyList<Infrastructure.Tp1PlanReviewLedger.HistoryRow>>? recent)
    {
        _tp1HistoryProvider = recent;
        RefreshTp1PlanHistory();
    }

    /// <summary>Re-reads the recommendation history.</summary>
    public void RefreshTp1PlanHistory()
    {
        var rows = _tp1HistoryProvider?.Invoke() ?? [];
        OnUiThread(() =>
        {
            Tp1ReviewHistory.Clear();
            foreach (var r in rows)
            {
                Tp1ReviewHistory.Add(new HistoryRowView(
                    r.Summary, r.Outcome, r.Status == "open"));
            }

            HasTp1ReviewHistory = Tp1ReviewHistory.Count > 0;
        });
    }

    /// <summary>Points the card at the shadow-ledger directory, refreshes
    /// once, and starts a light refresh timer. Idempotent. Null directory
    /// (bare tests, missing data dir) just clears to the empty state.
    /// Journal-only by construction: reads fx-shadow-*.jsonl, never writes,
    /// never trades.</summary>
    public void ConfigurePromotionLedger(string? directory) =>
        OnUiThread(() =>
        {
            _promotionLedgerDir = directory;
            RefreshPromotion();
            if (_promotionTimer is null && directory is not null)
            {
                _promotionTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(60),
                };
                _promotionTimer.Tick += (_, _) =>
                {
                    RefreshPromotion();
                    RefreshTp1PlanHold();
                    RefreshTp1PlanReview();
                    RefreshTp1PlanOverride();
                };
                _promotionTimer.Start();
            }
        });

    /// <summary>Re-reads the shadow ledgers (every symbol) into the card.</summary>
    public void RefreshPromotion()
    {
        var rows = Core.Fx.FxShadowLedger.SummarizeDir(_promotionLedgerDir);
        var ordered = rows
            .OrderByDescending(r => r.SuggestedWeight > 0)
            .ThenByDescending(r => r.HitRate)
            .ThenByDescending(r => r.Trades)
            .ToList();
        OnUiThread(() =>
        {
            PromotionRows.Clear();
            foreach (var r in ordered)
            {
                PromotionRows.Add(new PromotionRow(
                    r.Engine,
                    $"{r.Trades} settled",
                    $"{r.Helped} helped",
                    $"hit {r.HitRate:P0}",
                    r.SuggestedWeight > 0
                        ? $"EARNED weight {r.SuggestedWeight:0.##}"
                        : "weight 0 (observing)"));
            }

            var giveback = rows.FirstOrDefault(r => r.Engine == "giveback");
            PromotionSummaryText = giveback is { } g
                ? $"giveback engine: {g.Trades} settled trade(s), {g.Helped} save(s), " +
                  $"hit {g.HitRate:P0} — " +
                  (g.SuggestedWeight > 0
                    ? $"EARNED weight {g.SuggestedWeight:0.##}"
                    : $"weight 0 until {Core.Fx.FxExitShadow.PromotionTrades} trades @ {Core.Fx.FxExitShadow.PromotionHitRate:P0}")
                : rows.Count > 0
                    ? $"{rows.Count} shadow engine(s) observed, no giveback rows yet"
                    : "no promotion evidence yet";
        });
    }

    /// <summary>Refreshes the risk-rail card after any state change (also
    /// fires the toast/webhook for rails that newly engaged).</summary>
    private void RefreshRiskRails()
    {
        var alerts = BuildRiskRailAlerts();
        if (alerts.SequenceEqual(RiskRailAlerts))
        {
            return;
        }

        var previous = RiskRailAlerts.ToList();

        RiskRailAlerts.Clear();
        foreach (var alert in alerts)
        {
            RiskRailAlerts.Add(alert);
        }

        NotifyOnRiskRailChange(previous, alerts);
    }

    /// <summary>Builds one alert line per currently latched risk rail.</summary>
    private List<string> BuildRiskRailAlerts()
    {
        var alerts = new List<string>();

        if (IsGovernorLatched)
        {
            alerts.Add("FX loss stop latched — re-arm on the Terminal tab");
        }
        else if (IsGovernorWarned)
        {
            alerts.Add("FX drawdown warning — 80% of the daily-loss cap used");
        }

        if (IsKillSwitchEngaged)
        {
            alerts.Add("Global kill switch engaged");
        }

        return alerts;
    }

    /// <summary>Fires the toast/webhook alerts for rails that newly engaged
    /// (and one all-clear when the last rail releases).</summary>
    private void NotifyOnRiskRailChange(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var engaged = current.Where(a => !previous.Contains(a)).ToList();
        if (engaged.Count > 0)
        {
            var change = string.Join("; ", engaged);
            var summary = current.Count == 0 ? "no rails latched" : string.Join("; ", current);
            _notifications?.NotifyRiskRailEngaged(change, summary);
            _webhook?.PostRiskRail("⚠ Risk rail engaged", $"{change} — {summary}");
        }
        else if (previous.Count > 0 && current.Count == 0)
        {
            _webhook?.PostRiskRail("✅ Risk rails clear", "all latched risk rails have been released");
        }
    }

    /// <summary>Terminal pushes the FX brain's state here: status line,
    /// realised P/L and the supervisor's halt/warning flags. Any thread.</summary>
    public void ReportFxStatus(
        string status, bool halted, bool warned, decimal? dayPnl = null) =>
        OnUiThread(() =>
        {
            FxStatusText = status;
            IsGovernorLatched = halted;
            IsGovernorWarned = warned && !halted;
            if (dayPnl.HasValue)
            {
                CombinedGrowthPnlText =
                    $"{(dayPnl.Value >= 0 ? "+" : "−")}${Math.Abs(dayPnl.Value):0.00}";
            }

            RefreshRiskRails();
        });

    public void SetSymbol(string symbol) => OnUiThread(() => SymbolText = symbol);

    public void SetBalance(string balance) => OnUiThread(() => BalanceText = balance);

    public void AddHistory(IReadOnlyList<Tick> ticks)
    {
        OnUiThread(() =>
        {
            Chart?.Clear();
            if (ticks.Count > 0)
            {
                Chart?.AddTicks(ticks);
                PushCandles(ticks);
                LastPriceText = ticks[^1].Quote.ToString("0.00000");
                TickCountText = $"{ticks.Count} ticks";
            }
        });
    }

    [RelayCommand]
    private void ToggleKillSwitch()
    {
        IsKillSwitchEngaged = !IsKillSwitchEngaged;
        if (IsKillSwitchEngaged)
        {
            StatusText = "KILL SWITCH ENGAGED — engines stopped, positions flattening";

            // Cross-venue: stop the FX brain and flatten every open MT5
            // position so the kill switch covers the MT5 leg too.
            // (Ioc-guarded: tests construct this VM without the container.)
            try
            {
                _ = Ioc.Default.GetRequiredService<TerminalViewModel>().FxEmergencyFlattenAsync("kill switch");
            }
            catch (InvalidOperationException)
            {
                // no TerminalViewModel registered (tests) — the latch above
                // still refuses every subsequent order path.
            }
        }
        else
        {
            StatusText = "Engines re-armed — press the Terminal's brain switch to resume";
        }

        RefreshRiskRails();
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
