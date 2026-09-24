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

    // ── Logging ────────────────────────────────────────────────────
    /// <summary>Minimum log level: 0=Debug, 1=Info, 2=Warn, 3=Error.</summary>
    public int LogLevel { get; set; } = 1;
}
