using System.Text;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Brain;

namespace DongGfx.App.Tests;

/// <summary>
/// Tests for the headless Growth plan JSON store: round-trips, malformed
/// input fallbacks, and plan sanitization. The on-disk %APPDATA% store is a
/// thin file wrapper over this logic and is deliberately not tested here.
/// </summary>
[Trait("Category", "Unit")]
public class GrowthPlanJsonStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var plan = new GrowthPlan(
            StartBudget: 12.50m,
            RiskFraction: 0.30,
            MaxRecoverySteps: 4,
            DailyTargetFraction: 1.5,
            FloorFraction: 0.60,
            MinStake: 1.00m,
            IntervalMinutes: 7,
            CooldownMinutesAfterLoss: 2,
            FailureBackoffSeconds: 11.0,
            MaxAutoRestarts: 5,
            RestartBaseDelaySeconds: 8.0,
            RestartBackoffFactor: 2.5,
            PortfolioDailyDrawdownCap: 150m);

        var reloaded = GrowthPlanJsonStore.Load(GrowthPlanJsonStore.Save(plan));

        Assert.Equal(plan, reloaded);
    }

    [Fact]
    public void Load_GarbageBytes_FallsBackToDefault()
    {
        Assert.Equal(GrowthPlan.Default, GrowthPlanJsonStore.Load(Encoding.UTF8.GetBytes("{ not json")));
        Assert.Equal(GrowthPlan.Default, GrowthPlanJsonStore.Load(Array.Empty<byte>()));
    }

    [Fact]
    public void Load_MinimalDocument_FillsDtoDefaults()
    {
        var plan = GrowthPlanJsonStore.Load(Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(5.00m, plan.StartBudget);
        Assert.Equal(0.20, plan.RiskFraction);
        Assert.Equal(3, plan.MaxRecoverySteps);
        Assert.Null(plan.PortfolioDailyDrawdownCap);
    }

    [Fact]
    public void Sanitize_ClampsEveryKnobIntoSafeRange()
    {
        var wild = new GrowthPlan(
            StartBudget: 0.01m,
            RiskFraction: 5.0,
            MaxRecoverySteps: 99,
            DailyTargetFraction: 100.0,
            FloorFraction: 0.01,
            IntervalMinutes: 0,
            FailureBackoffSeconds: 0.0,
            MaxAutoRestarts: 999,
            RestartBaseDelaySeconds: 0.0,
            RestartBackoffFactor: 100.0);

        var safe = GrowthPlanJsonStore.Sanitize(wild);

        Assert.Equal(0.50m, safe.StartBudget);
        Assert.Equal(0.5, safe.RiskFraction);
        Assert.Equal(5, safe.MaxRecoverySteps);
        Assert.Equal(10.0, safe.DailyTargetFraction);
        Assert.Equal(0.10, safe.FloorFraction);
        Assert.Equal(1, safe.IntervalMinutes);
        Assert.Equal(1.0, safe.FailureBackoffSeconds);
        Assert.Equal(10, safe.MaxAutoRestarts);
        Assert.Equal(0.5, safe.RestartBaseDelaySeconds);
        Assert.Equal(10.0, safe.RestartBackoffFactor);
    }

    [Fact]
    public void Sanitize_ParsesDrawdownCapText()
    {
        var withCap = GrowthPlanJsonStore.Sanitize(GrowthPlan.Default, "$1,000");
        var withoutCap = GrowthPlanJsonStore.Sanitize(GrowthPlan.Default, "   ");

        Assert.Equal(1000m, withCap.PortfolioDailyDrawdownCap);
        Assert.Null(withoutCap.PortfolioDailyDrawdownCap);
    }
}
