namespace Tf.Core.Models;

/// <summary>
/// User-editable application settings. Persisted (with the API token
/// DPAPI-encrypted) by the app's settings service under %APPDATA%\tf\.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultAppId = "1089";
    public const string DefaultSymbol = "frxEURUSD";
    public const string DefaultCurrency = "USD";
    public const string DefaultLlmBaseUrl = "http://localhost:11434/v1";
    public const string DefaultLlmModel = "gpt-4o-mini";

    /// <summary>Deriv API token. Kept in memory only; persisted encrypted.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Deriv application id (works without a token for market data).</summary>
    public string AppId { get; set; } = DefaultAppId;

    /// <summary>True when the primary client (Dashboard/Trades/Brain
    /// surfaces) connects as a new-platform PAT: its token lives in
    /// ApiToken encrypted as usual, but authentication goes through the
    /// OTP flow with <see cref="PrimaryDerivAppId"/> and
    /// <see cref="PrimaryDerivAccountId"/> instead of the classic
    /// authorize call. Set by the Accounts tab's "Set as primary" switch.</summary>
    public bool PrimaryNewPlatform { get; set; }

    /// <summary>The PAT app's registered Deriv application id for the
    /// primary client (the Deriv-App-ID header). Only read when
    /// <see cref="PrimaryNewPlatform"/> is set; empty falls back to
    /// <see cref="AppId"/>.</summary>
    public string PrimaryDerivAppId { get; set; } = "";

    /// <summary>The new-platform account id the primary client opens its
    /// OTP socket for (e.g. DOT92951338 for demo, ROT… for real). Only
    /// read when <see cref="PrimaryNewPlatform"/> is set.</summary>
    public string PrimaryDerivAccountId { get; set; } = "";

    public bool IsDemo { get; set; } = true;

    /// <summary>Market symbol, e.g. frxEURUSD.</summary>
    public string Symbol { get; set; } = DefaultSymbol;

    public string Currency { get; set; } = DefaultCurrency;

    /// <summary>Contract duration in minutes (used from milestone 2).</summary>
    public int DurationMinutes { get; set; } = 5;

    /// <summary>Stake per contract in account currency (used from milestone 2).</summary>
    public decimal Stake { get; set; } = 1.00m;

    /// <summary>Hard ceiling on the MANUAL trade surfaces (Trades tab stake,
    /// Brain tab cycles). The growth engines are already bounded by the risk
    /// engine's MaxStake and the session plan ladder; this closes the last
    /// unbounded path — a mistyped stake reaching a real account. Ships CAPPED
    /// (matching the risk engine's MaxStake) so a fresh install can never
    /// place an unbounded manual trade; blank/0 in the Settings tab still
    /// disables the extra limit, but that is now an explicit act (the
    /// go-live readiness panel flags it). The manual paths are always gated
    /// by the real-money gate and the kill switch regardless.</summary>
    public decimal ManualMaxStake { get; set; } = 10.00m;

    /// <summary>Master autonomy switch; when off the brain never places trades.</summary>
    public bool AutonomyEnabled { get; set; }

    /// <summary>Decision interval in minutes (used from milestone 3).</summary>
    public int DecisionIntervalMinutes { get; set; } = 5;

    // ── LLM (used from milestone 3) ────────────────────────────────
    public string LlmBaseUrl { get; set; } = DefaultLlmBaseUrl;
    public string LlmModel { get; set; } = DefaultLlmModel;
    public string LlmApiKey { get; set; } = "";

    // ── Risk guardrails (used from milestone 3) ────────────────────
    public decimal MaxStake { get; set; } = 10.00m;
    public int MaxConcurrentContracts { get; set; } = 1;
    public decimal DailyLossCap { get; set; } = 50.00m;
    public double MinConfidence { get; set; } = 0.60;
    public int CooldownMinutesAfterLoss { get; set; } = 15;

    // ── Market hours ─────────────────────────────────────────────
    /// <summary>Only trade during active forex sessions.</summary>
    public bool RespectMarketHours { get; set; }

    /// <summary>Only trade during high-liquidity overlap windows.</summary>
    public bool OverlapsOnly { get; set; }

    // ── Webhooks ───────────────────────────────────────────────
    /// <summary>Discord or Slack webhook URL for trade notifications.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>True = Discord format, false = Slack format.</summary>
    public bool IsDiscordWebhook { get; set; } = true;

    /// <summary>Post trade settlements to webhook.</summary>
    public bool WebhookOnTrade { get; set; } = true;

    /// <summary>Post growth milestones (target/floor) to webhook.</summary>
    public bool WebhookOnMilestone { get; set; } = true;

    /// <summary>Post circuit breaker events to webhook.</summary>
    public bool WebhookOnCircuitBreaker { get; set; } = true;

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

    // ── Logging ─────────────────────────────────────────────────
    /// <summary>Minimum log level: 0=Debug, 1=Info, 2=Warn, 3=Error.</summary>
    public int LogLevel { get; set; } = 1;
}