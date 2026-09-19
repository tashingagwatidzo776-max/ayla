using DongGfx.Core.Brain;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class GrowthSessionEngineTests
{
    private static GrowthPlan Plan() => new(); // $5 budget, 20% risk, ×2 ladder, floor $2, target $10

    [Fact]
    public void Starts_WithBudgetBankroll_AndCanTrade()
    {
        var engine = new GrowthSessionEngine(Plan(), 5m);

        Assert.Equal(5m, engine.Bankroll);
        Assert.True(engine.CanTrade);
        Assert.Equal(0, engine.LossStreak);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9.99)]
    public void SuggestStake_IsRiskFractionOfBankroll(decimal bankroll)
    {
        var engine = new GrowthSessionEngine(Plan(), bankroll);
        var stake = engine.SuggestStake();

        Assert.True(stake > 0m);
        Assert.True(stake <= bankroll * 0.5m, $"stake {stake} must never exceed half the bankroll");
        Assert.True(stake >= 0.80m * bankroll * (decimal)Plan().RiskFraction,
            "no rounding should shrink the stake by more than 20%");
    }

    [Fact]
    public void BankrollBelowFloorAtStart_IsImmediatelyBlocked()
    {
        var engine = new GrowthSessionEngine(Plan(), 1m);
        Assert.True(engine.FloorHit);
        Assert.False(engine.CanTrade);
        Assert.Equal(0m, engine.SuggestStake());
    }

    [Fact]
    public void LossStreak_DoublesNextStake_UpToHalfCap()
    {
        var engine = new GrowthSessionEngine(Plan(), 9m);
        var first = engine.SuggestStake();            // 1.80 (20% of 9)
        engine.ApplySettlement(won: false, -first);   // bankroll 7.2, streak 1
        var second = engine.SuggestStake();           // 2.88 (2 × 20% of 7.2)
        Assert.True(second > first * 1.5m, $"expected roughly doubled stake, got {second} vs {first}");

        engine.ApplySettlement(won: false, -second);  // bankroll 4.32, streak 2
        var third = engine.SuggestStake();            // capped at 2.16 (half of 4.32)
        Assert.True(third <= 2.2m + 0.01m);

        engine.ApplySettlement(won: false, -third);   // streak 3 == MaxRecoverySteps
        Assert.True(engine.RecoveryExhausted);
        Assert.False(engine.CanTrade);
        Assert.Equal(0m, engine.SuggestStake());
    }

    [Fact]
    public void Win_ResetsLossStreak()
    {
        var engine = new GrowthSessionEngine(Plan(), 5m);
        engine.ApplySettlement(won: false, -1m);
        engine.ApplySettlement(won: false, -1m);
        Assert.Equal(2, engine.LossStreak);

        engine.ApplySettlement(won: true, +2.4m);
        Assert.Equal(0, engine.LossStreak);
        Assert.True(engine.CanTrade);
    }

    [Fact]
    public void FloorHit_StopsTrading()
    {
        var engine = new GrowthSessionEngine(Plan(), 5m);
        engine.ApplySettlement(won: false, -3.5m); // bankroll 1.5 ≤ floor 2

        Assert.True(engine.FloorHit);
        Assert.False(engine.CanTrade);
        Assert.Contains("floor", engine.BlockReason);
    }

    [Fact]
    public void TargetHit_StopsTrading_AfterWins()
    {
        var engine = new GrowthSessionEngine(Plan(), 5m);
        engine.ApplySettlement(won: true, +5.1m); // bankroll 10.1 ≥ target 10

        Assert.True(engine.TargetHit);
        Assert.False(engine.CanTrade);
        Assert.Contains("target", engine.BlockReason);
    }

    [Fact]
    public void ApplySettlement_MovesBankrollByNetProfit()
    {
        var engine = new GrowthSessionEngine(Plan(), 5m);
        engine.ApplySettlement(won: true, +0.9m);
        Assert.Equal(5.9m, engine.Bankroll);
    }
}

[Trait("Category", "Unit")]
public class GrowthBrainTests
{
    private static IReadOnlyList<Tick> DowntrendWindow(int count = 40)
    {
        // Strictly falling prices → RSI well under 30 (oversold).
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.20000 - i * 0.0001, 1.20000 - i * 0.0001,
                1.20000 - i * 0.0001, 1700000000L + i, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> UptrendWindow(int count = 40)
    {
        // Strictly rising prices → RSI above 70 (overbought).
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + i * 0.0001, 1.10000 + i * 0.0001,
                1.10000 + i * 0.0001, 1700000000L + i, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> FlatWindow(int count = 40)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + (i % 3) * 0.000001, 1.10000 + (i % 3) * 0.000001,
                1.10000 + (i % 3) * 0.000001, 1700000000L + i, 5))
            .ToArray();
    }

    [Fact]
    public void OversoldDowntrend_ChoosesRise_WithPositiveStake()
    {
        var session = new GrowthSessionEngine(new GrowthPlan(), 5m);
        var decision = GrowthBrain.Decide(DowntrendWindow(), session);

        Assert.Equal(BrainDirection.Rise, decision.Direction);
        Assert.True(decision.Confidence >= 0.6);
        Assert.True(decision.Stake > 0m);
    }

    [Fact]
    public void OverboughtUptrend_ChoosesFall_WithPositiveStake()
    {
        var session = new GrowthSessionEngine(new GrowthPlan(), 5m);
        var decision = GrowthBrain.Decide(UptrendWindow(), session);

        Assert.Equal(BrainDirection.Fall, decision.Direction);
        Assert.True(decision.Confidence >= 0.6);
    }

    [Fact]
    public void FlatMarket_Holds()
    {
        var session = new GrowthSessionEngine(new GrowthPlan(), 5m);
        var decision = GrowthBrain.Decide(FlatWindow(), session);

        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void TooLittleData_Holds()
    {
        var session = new GrowthSessionEngine(new GrowthPlan(), 5m);
        var decision = GrowthBrain.Decide(new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) }, session);

        Assert.Equal(BrainDirection.Hold, decision.Direction);
        Assert.Contains("insufficient", decision.Reasoning);
    }

    [Fact]
    public void FloorHit_AlwaysHolds()
    {
        var session = new GrowthSessionEngine(new GrowthPlan(), 5m);
        session.ApplySettlement(won: false, -3.5m); // bankroll below floor

        var decision = GrowthBrain.Decide(DowntrendWindow(), session);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
        Assert.Contains("floor", decision.Reasoning);
    }

    [Fact]
    public void StakeScalesUp_AfterConsecutiveLosses()
    {
        var session = new GrowthSessionEngine(new GrowthPlan(), 5m);
        var first = GrowthBrain.Decide(DowntrendWindow(), session).Stake;

        session.ApplySettlement(won: false, -first);
        var second = GrowthBrain.Decide(DowntrendWindow(), session).Stake;

        Assert.True(second > first, $"ladder should raise stake: {second} vs {first}");
    }
}
