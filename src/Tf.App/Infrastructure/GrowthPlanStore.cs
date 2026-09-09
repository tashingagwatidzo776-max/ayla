using System.IO;
using System.Text.Json;
using Tf.Core.Brain;

namespace Tf.App.Infrastructure;

/// <summary>
/// Persists the Growth brain plan (risk %, daily target, floor, recovery
/// ladder, cadence) as plain JSON under %APPDATA%\tf\data\growth-plan.json —
/// no secrets here, so no encryption needed.
/// </summary>
public sealed class GrowthPlanStore
{
    private static readonly string PlanPath =
        Path.Combine(SettingsService.DataDir, "growth-plan.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public GrowthPlan Load()
    {
        try
        {
            if (!File.Exists(PlanPath))
            {
                return GrowthPlan.Default;
            }

            var json = File.ReadAllText(PlanPath);
            var dto = JsonSerializer.Deserialize<GrowthPlanDto>(json, JsonOptions);
            return dto?.ToPlan() ?? GrowthPlan.Default;
        }
        catch
        {
            return GrowthPlan.Default;
        }
    }

    public void Save(GrowthPlan plan)
    {
        Directory.CreateDirectory(SettingsService.DataDir);
        var json = JsonSerializer.Serialize(GrowthPlanDto.From(plan), JsonOptions);
        File.WriteAllText(PlanPath, json);
    }

    /// <summary>Settable mirror of <see cref="GrowthPlan"/> for JSON round-trips.</summary>
    private sealed class GrowthPlanDto
    {
        public decimal StartBudget { get; set; } = 5.00m;
        public double RiskFraction { get; set; } = 0.20;
        public int MaxRecoverySteps { get; set; } = 3;
        public double DailyTargetFraction { get; set; } = 1.00;
        public double FloorFraction { get; set; } = 0.40;
        public decimal MinStake { get; set; } = 1.00m;
        public int IntervalMinutes { get; set; } = 1;
        public int CooldownMinutesAfterLoss { get; set; } = 1;

        public GrowthPlan ToPlan() => new(
            StartBudget, RiskFraction, MaxRecoverySteps, DailyTargetFraction,
            FloorFraction, MinStake, IntervalMinutes, CooldownMinutesAfterLoss);

        public static GrowthPlanDto From(GrowthPlan plan) => new()
        {
            StartBudget = plan.StartBudget,
            RiskFraction = plan.RiskFraction,
            MaxRecoverySteps = plan.MaxRecoverySteps,
            DailyTargetFraction = plan.DailyTargetFraction,
            FloorFraction = plan.FloorFraction,
            MinStake = plan.MinStake,
            IntervalMinutes = plan.IntervalMinutes,
            CooldownMinutesAfterLoss = plan.CooldownMinutesAfterLoss
        };
    }
}
