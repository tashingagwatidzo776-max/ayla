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

    public bool IsDemo { get; set; } = true;

    /// <summary>Market symbol, e.g. frxEURUSD.</summary>
    public string Symbol { get; set; } = DefaultSymbol;

    public string Currency { get; set; } = DefaultCurrency;

    /// <summary>Contract duration in minutes (used from milestone 2).</summary>
    public int DurationMinutes { get; set; } = 5;

    /// <summary>Stake per contract in account currency (used from milestone 2).</summary>
    public decimal Stake { get; set; } = 1.00m;

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
}