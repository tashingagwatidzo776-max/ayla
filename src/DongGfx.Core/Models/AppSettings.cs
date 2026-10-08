namespace DongGfx.Core.Models;

/// <summary>
/// User-editable application settings, persisted by the app's settings
/// service under %APPDATA%\tf\. MT5/forex only: the Deriv binary-options
/// surface (API token, app id, contract market symbol, stake/duration, LLM
/// brain, risk-engine caps, growth profile) was removed along with the
/// binary integration.
/// </summary>
public sealed class AppSettings
{
    /// <summary>True when the configured trading account is a demo one.
    /// Demo passes the real-money gate untouched; real demands the session
    /// unlock on every trade path.</summary>
    public bool IsDemo { get; set; } = true;

    /// <summary>UI theme: "Dark" (modern, default) or "Classic" (MT5-gray).
    /// Swapped at runtime by the ThemeManager; unknown values fall back to
    /// Dark at load.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Master autonomy switch; when off the FX brain never places
    /// trades (the Terminal tab's brain switch writes this).</summary>
    public bool AutonomyEnabled { get; set; }

    /// <summary>Whether the FX brain's engine loop was RUNNING at the last
    /// persist — the toggle's state, not an autonomy grant. The loop is
    /// ALWAYS-ON by operator policy: every launch auto-starts it regardless
    /// of this flag, except when the crash safe-mode hold is active (a fault
    /// loop is never auto-started). This flag is retained so the last state
    /// is still recorded. Autonomy stays the safety master: a running loop
    /// with autonomy off journals decisions and places nothing.</summary>
    public bool FxBrainRunning { get; set; }

    /// <summary>TP1 partial prototype (default OFF): when armed, the Profit
    /// Brain's TP1 rung is executed once per ticket through the Exit
    /// Brain's own close path — the allocation plan is graded live instead
    /// of only journaled. Every execution journals as TP1-EXEC under
    /// FX_PROFIT; the Exit Brain's overrides and vote still outrank it.</summary>
    public bool FxExecuteTp1Partials { get; set; }

    /// <summary>Operator-armed TP1 plan-% override (null = the allocation
    /// plan's own TP1 leg). Set by the dashboard's one-click Arm action when
    /// the plan-% gate recommends a different percentage: the engine then
    /// uses THIS figure in place of the brain's own TP1 leg for eligibility,
    /// lot sizing and journaling — the path from advice to an effective plan.
    /// Persisted, because arming the recommendation is a standing operator
    /// decision that must survive a restart; cleared by the Revert action.
    /// Clamped to [0, 100] on load so a hand-edited file can never size past
    /// the position.</summary>
    public double? Tp1PlanPctOverride { get; set; }

    /// <summary>Minimum entry confidence for dispatch (default 0 = off,
    /// the pre-gate behavior). A winning signal whose raw confidence falls
    /// below this is journaled as FX_SIGNAL and counted toward the paper
    /// soak but never dispatched — the host refuses it with a
    /// "confidence gate" FX_ORDER row. Clamped to [0, 1] on load.</summary>
    public double FxMinEntryConfidence { get; set; }

    // ── MT5 / FX brain safety ──────────────────────────────────────
    /// <summary>Maximum volume (lots) for a single MT5 order placed through
    /// the bridge. 0 disables MT5 order placement entirely (fail-closed).</summary>
    public decimal Mt5MaxLots { get; set; } = 1.00m;

    /// <summary>Fx brain safety: stop trading (and flatten) when the MT5
    /// account's realized+floating P/L is this far below the session start
    /// balance. 0 disables the stop — not recommended for live.</summary>
    public decimal Mt5DailyLossCap { get; set; } = 25m;

    /// <summary>Fx brain safety: go back to paper when total MT5 account
    /// equity falls below this absolute floor (0 = disabled).</summary>
    public decimal Mt5EquityFloor { get; set; } = 0m;

    /// <summary>The MT5 symbol the forex brain trades (bridge catalog name).</summary>
    public string FxSymbol { get; set; } = "XAUUSD";

    /// <summary>CSV of MT5 symbols the FX brain runs (one engine each).
    /// Falls back to FxSymbol when empty; unknown symbols degrade to a
    /// skipped cycle, never an error.</summary>
    public string FxSymbols { get; set; } = "XAUUSDmicro,EURUSD,GBPUSD,USDJPY";

    /// <summary>Total open lots across ALL FX-brain symbols (0 = disabled).
    /// The portfolio veto refuses any order that would exceed it.</summary>
    public decimal FxPortfolioMaxLots { get; set; } = 0.10m;

    /// <summary>Minutes before AND after a high-impact calendar event that
    /// the FX brain refuses new orders.</summary>
    public int NewsBlackoutMinutes { get; set; } = 15;

    /// <summary>Pinned MT5 terminal64.exe (empty = auto-discover the known
    /// install locations). Watchdog, sidecar and startup profile all honor it.</summary>
    public string Mt5TerminalPath { get; set; } = "";

    // ── Last MT5 login (dialog prefill) ─────────────────────────────
    /// <summary>The numeric account id last signed in through the login
    /// dialog. Prefills the dialog so switching between demo and real is
    /// two clicks. Deliberately NOT a password field: the password is
    /// never persisted anywhere.</summary>
    public string Mt5LastLogin { get; set; } = "";

    /// <summary>The server last signed in through the login dialog
    /// (prefills the dialog's server field).</summary>
    public string Mt5LastServer { get; set; } = "";

    /// <summary>The account before the last one (second slot of the
    /// dialog's Recent picker — handy when flipping demo ↔ real).
    /// Passwords are never persisted, in either slot.</summary>
    public string Mt5PrevLogin { get; set; } = "";

    /// <summary>The server belonging to <see cref="Mt5PrevLogin"/>.</summary>
    public string Mt5PrevServer { get; set; } = "";

    // ── Webhooks ───────────────────────────────────────────────────
    /// <summary>Discord or Slack webhook URL for trade notifications.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>True = Discord format, false = Slack format.</summary>
    public bool IsDiscordWebhook { get; set; } = true;

    /// <summary>Post trade settlements to webhook.</summary>
    public bool WebhookOnTrade { get; set; } = true;

    /// <summary>Post milestones (target/floor) to webhook.</summary>
    public bool WebhookOnMilestone { get; set; } = true;

    /// <summary>Post circuit breaker events to webhook.</summary>
    public bool WebhookOnCircuitBreaker { get; set; } = true;

    // ── Monitoring ─────────────────────────────────────────────────
    /// <summary>Periodically post the live cycle-telemetry (latency/error)
    /// digest to the webhook. Off silences the MetricsDigestService timer
    /// entirely (trade settlements are unaffected).</summary>
    public bool MetricsDigestEnabled { get; set; } = true;

    /// <summary>Hours between cycle-telemetry digest posts (clamped 1–168
    /// by the settings editor; default 6).</summary>
    public int MetricsDigestIntervalHours { get; set; } = 6;

    /// <summary>Toast + webhook when a real-money session unlock has been
    /// armed this many hours (clamped 0–72 by the settings editor; default
    /// 4). 0 disables the staleness alert entirely — the arm stays silent
    /// but every other rail still applies.</summary>
    public int ArmStalenessHours { get; set; } = 4;

    /// <summary>Periodic AI journal-analyst narrative to the webhook
    /// (docs/ai-agent-program.md agent 1). Read-only agent: it narrates the
    /// journal, it never trades. Off silences the JournalAnalystService
    /// timer entirely (no LLM calls, no posts).</summary>
    public bool AnalystEnabled { get; set; } = true;

    /// <summary>Prediction-memory loop for the analyst: each cycle records
    /// what it expects next and the next cycle mechanically grades it
    /// against the journal (the guarded type-7 "learning" — context, never
    /// weights). State lives under %LOCALAPPDATA%\tf\ai\.</summary>
    public bool AnalystMemoryEnabled { get; set; } = true;

    /// <summary>AI risk narrator (docs/ai-agent-program.md agent 2): when
    /// the FX supervisor halts, an LLM explains what happened on the webhook
    /// within seconds. Pure journal subscriber — it can never re-arm, clear
    /// or influence a halt.</summary>
    public bool RiskNarratorEnabled { get; set; } = true;

    /// <summary>Nightly genetic lab (docs/ai-agent-program.md agent-6
    /// support): walk-forward replays of the journal's own decision bars,
    /// approval-gated and journal-only. It narrates; it never trades and
    /// never promotes — a human port is still a reviewed PR.</summary>
    public bool FxLabEnabled { get; set; } = true;

    /// <summary>Maps tab colormap: "Auto" (per-map defaults) or an explicit
    /// FxCmapKind name (Turbo, Viridis, Inferno, Plasma, Magma, CoolHot).</summary>
    public string MapsColormap { get; set; } = "Auto";

    // ── Logging ────────────────────────────────────────────────────
    /// <summary>Minimum log level: 0=Debug, 1=Info, 2=Warn, 3=Error.</summary>
    public int LogLevel { get; set; } = 1;
}
