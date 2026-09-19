using System.IO;
using System.Text.Json;
using DongGfx.Core.Brain;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Headless persistence for the Growth plan: JSON (de)serialization plus
/// plan validation/normalization. Extracted from <see cref="GrowthPlanStore"/>
/// (which owns the %APPDATA% path) so the logic is testable without touching
/// user data. Round-trips through a settable DTO, mirroring the on-disk
/// format exactly.
/// </summary>
public static class GrowthPlanJsonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Loads a plan from JSON bytes; falls back to defaults on any parse failure or empty input.</summary>
    public static GrowthPlan Load(byte[] json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<GrowthPlanDto>(json, JsonOptions);
            return dto?.ToPlan() ?? GrowthPlan.Default;
        }
        catch
        {
            return GrowthPlan.Default;
        }
    }

    /// <summary>Serializes a plan to JSON bytes in the on-disk format.</summary>
    public static byte[] Save(GrowthPlan plan) =>
        JsonSerializer.SerializeToUtf8Bytes(GrowthPlanDto.From(plan), JsonOptions);

    /// <summary>
    /// Normalizes a plan the way the UI's Save does: clamps every knob into
    /// its safe range (risk 1–50%, recovery 1–5, target 5–1000%, floor
    /// 10–90%, interval 1–60 min, backoff 1–120 s, restarts 0–10, base
    /// delay 0.5–600 s, factor 1–10) and parses the drawdown cap text.
    /// </summary>
    public static GrowthPlan Sanitize(GrowthPlan plan, string? drawdownCapText = null) => plan with
    {
        StartBudget = Math.Max(0.50m, plan.StartBudget),
        RiskFraction = Math.Clamp(plan.RiskFraction, 0.01, 0.5),
        MaxRecoverySteps = Math.Clamp(plan.MaxRecoverySteps, 1, 5),
        DailyTargetFraction = Math.Clamp(plan.DailyTargetFraction, 0.05, 10.0),
        FloorFraction = Math.Clamp(plan.FloorFraction, 0.10, 0.9),
        IntervalMinutes = Math.Clamp(plan.IntervalMinutes, 1, 60),
        FailureBackoffSeconds = Math.Clamp(plan.FailureBackoffSeconds, 1, 120),
        MaxAutoRestarts = Math.Clamp(plan.MaxAutoRestarts, 0, 10),
        RestartBaseDelaySeconds = Math.Clamp(plan.RestartBaseDelaySeconds, 0.5, 600.0),
        RestartBackoffFactor = Math.Clamp(plan.RestartBackoffFactor, 1.0, 10.0),
        PortfolioDailyDrawdownCap = PlanTextParser.TryCreateDrawdownCap(drawdownCapText)
    };

    /// <summary>Settable mirror of <see cref="GrowthPlan"/> for JSON round-trips.</summary>
    public sealed class GrowthPlanDto
    {
        public decimal StartBudget { get; set; } = 5.00m;
        public double RiskFraction { get; set; } = 0.20;
        public int MaxRecoverySteps { get; set; } = 3;
        public double DailyTargetFraction { get; set; } = 1.00;
        public double FloorFraction { get; set; } = 0.40;
        public decimal MinStake { get; set; } = 1.00m;
        public int IntervalMinutes { get; set; } = 1;
        public int CooldownMinutesAfterLoss { get; set; } = 1;
        public double FailureBackoffSeconds { get; set; } = 5.0;
        public int MaxAutoRestarts { get; set; } = 3;
        public double RestartBaseDelaySeconds { get; set; } = 5.0;
        public double RestartBackoffFactor { get; set; } = 3.0;
        public decimal? PortfolioDailyDrawdownCap { get; set; }

        public GrowthPlan ToPlan() => new(
            StartBudget, RiskFraction, MaxRecoverySteps, DailyTargetFraction,
            FloorFraction, MinStake, IntervalMinutes, CooldownMinutesAfterLoss,
            FailureBackoffSeconds, MaxAutoRestarts, RestartBaseDelaySeconds,
            RestartBackoffFactor, PortfolioDailyDrawdownCap);

        public static GrowthPlanDto From(GrowthPlan plan) => new()
        {
            StartBudget = plan.StartBudget,
            RiskFraction = plan.RiskFraction,
            MaxRecoverySteps = plan.MaxRecoverySteps,
            DailyTargetFraction = plan.DailyTargetFraction,
            FloorFraction = plan.FloorFraction,
            MinStake = plan.MinStake,
            IntervalMinutes = plan.IntervalMinutes,
            CooldownMinutesAfterLoss = plan.CooldownMinutesAfterLoss,
            FailureBackoffSeconds = plan.FailureBackoffSeconds,
            MaxAutoRestarts = plan.MaxAutoRestarts,
            RestartBaseDelaySeconds = plan.RestartBaseDelaySeconds,
            RestartBackoffFactor = plan.RestartBackoffFactor,
            PortfolioDailyDrawdownCap = plan.PortfolioDailyDrawdownCap
        };
    }
}
