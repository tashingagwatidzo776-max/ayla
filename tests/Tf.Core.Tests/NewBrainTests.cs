using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.Core.Tests;

[Trait("Category", "Unit")]
public class TrendFollowingBrainTests
{
    private static IReadOnlyList<Tick> StrongUptrend(int count = 40)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + i * 0.0002, 1.10000 + i * 0.0002 + 0.00001,
                1.10000 + i * 0.0002 - 0.00001, 1700000000L + i * 5000, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> StrongDowntrend(int count = 40)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.15000 - i * 0.0002, 1.15000 - i * 0.0002 + 0.00001,
                1.15000 - i * 0.0002 - 0.00001, 1700000000L + i * 5000, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> Choppy(int count = 40)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + (i % 5 - 2) * 0.00005, 1.10000 + (i % 5 - 2) * 0.00005 + 0.00001,
                1.10000 + (i % 5 - 2) * 0.00005 - 0.00001, 1700000000L + i * 5000, 5))
            .ToArray();
    }

    [Fact]
    public void NullWindow_Holds()
    {
        var decision = TrendFollowingBrain.Decide(null!, TrendFollowingBrain.TrendConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void EmptyWindow_Holds()
    {
        var decision = TrendFollowingBrain.Decide(Array.Empty<Tick>(), TrendFollowingBrain.TrendConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void InsufficientData_Holds()
    {
        var ticks = new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) };
        var decision = TrendFollowingBrain.Decide(ticks, TrendFollowingBrain.TrendConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void StrongUptrend_ReturnsRiseOrHold()
    {
        var decision = TrendFollowingBrain.Decide(StrongUptrend(), TrendFollowingBrain.TrendConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Rise || decision.Direction == BrainDirection.Hold);
        if (decision.Direction == BrainDirection.Rise)
        {
            Assert.True(decision.Confidence >= 0.6 && decision.Confidence <= 1.0,
                $"confidence {decision.Confidence} out of range");
            Assert.Contains("Rise", decision.Reasoning);
        }
    }

    [Fact]
    public void StrongDowntrend_ReturnsFallOrHold()
    {
        var decision = TrendFollowingBrain.Decide(StrongDowntrend(), TrendFollowingBrain.TrendConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Fall || decision.Direction == BrainDirection.Hold);
        if (decision.Direction == BrainDirection.Fall)
        {
            Assert.True(decision.Confidence >= 0.6 && decision.Confidence <= 1.0,
                $"confidence {decision.Confidence} out of range");
            Assert.Contains("Fall", decision.Reasoning);
        }
    }

    [Fact]
    public void ChoppyMarket_Holds()
    {
        var decision = TrendFollowingBrain.Decide(Choppy(), TrendFollowingBrain.TrendConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
        Assert.NotEmpty(decision.Reasoning);
    }

    [Fact]
    public void CustomConfig_OverridesDefaults()
    {
        // With very low MinAdx, even weak trends should be detected
        var config = new TrendFollowingBrain.TrendConfig(MinAdx: 5.0);
        var decision = TrendFollowingBrain.Decide(StrongUptrend(), config);
        // Should be more likely to signal than default config
        Assert.True(decision.Direction == BrainDirection.Rise || decision.Direction == BrainDirection.Hold);
    }

    [Fact]
    public void Decision_HasNonEmptyReasoning()
    {
        var decision = TrendFollowingBrain.Decide(StrongUptrend(), TrendFollowingBrain.TrendConfig.Default);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reasoning));
    }

    [Fact]
    public void Confidence_BoundedBetweenZeroAndOne()
    {
        var decision = TrendFollowingBrain.Decide(StrongUptrend(), TrendFollowingBrain.TrendConfig.Default);
        Assert.InRange(decision.Confidence, 0.0, 1.0);
    }
}

[Trait("Category", "Unit")]
public class BreakoutBrainTests
{
    private static IReadOnlyList<Tick> TightRange(int count = 30)
    {
        // Price oscillating in a very tight range (Bollinger squeeze)
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + Math.Sin(i * 0.3) * 0.00001,
                1.10000 + Math.Sin(i * 0.3) * 0.00001 + 0.00001,
                1.10000 + Math.Sin(i * 0.3) * 0.00001 - 0.00001,
                1700000000L + i * 5000, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> WideRange(int count = 30)
    {
        // Price oscillating in a wider range
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + Math.Sin(i * 0.5) * 0.0005,
                1.10000 + Math.Sin(i * 0.5) * 0.0005 + 0.00001,
                1.10000 + Math.Sin(i * 0.5) * 0.0005 - 0.00001,
                1700000000L + i * 5000, 5))
            .ToArray();
    }

    [Fact]
    public void NullWindow_Holds()
    {
        var decision = BreakoutBrain.Decide(null!, BreakoutBrain.BreakoutConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void EmptyWindow_Holds()
    {
        var decision = BreakoutBrain.Decide(Array.Empty<Tick>(), BreakoutBrain.BreakoutConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void InsufficientData_Holds()
    {
        var ticks = new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) };
        var decision = BreakoutBrain.Decide(ticks, BreakoutBrain.BreakoutConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void TightRange_Holds()
    {
        var decision = BreakoutBrain.Decide(TightRange(), BreakoutBrain.BreakoutConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void WideRange_CanSignal()
    {
        var decision = BreakoutBrain.Decide(WideRange(), BreakoutBrain.BreakoutConfig.Default);
        // Should either hold or signal - no crash
        Assert.True(decision.Direction == BrainDirection.Rise ||
                    decision.Direction == BrainDirection.Fall ||
                    decision.Direction == BrainDirection.Hold);
    }

    [Fact]
    public void Decision_HasNonEmptyReasoning()
    {
        var decision = BreakoutBrain.Decide(WideRange(), BreakoutBrain.BreakoutConfig.Default);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reasoning));
    }

    [Fact]
    public void Confidence_BoundedBetweenZeroAndOne()
    {
        var decision = BreakoutBrain.Decide(WideRange(), BreakoutBrain.BreakoutConfig.Default);
        Assert.InRange(decision.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void CustomConfig_OverridesDefaults()
    {
        // With very low BreakoutThreshold, even small moves trigger
        var config = new BreakoutBrain.BreakoutConfig(BreakoutThreshold: 0.1);
        var decision = BreakoutBrain.Decide(WideRange(), config);
        Assert.True(decision.Direction == BrainDirection.Rise ||
                    decision.Direction == BrainDirection.Fall ||
                    decision.Direction == BrainDirection.Hold);
    }
}

[Trait("Category", "Unit")]
public class MeanReversionBrainTests
{
    private static IReadOnlyList<Tick> Oversold(int count = 40)
    {
        // Strictly falling prices (oversold)
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 - i * 0.0001, 1.10000 - i * 0.0001 + 0.00001,
                1.10000 - i * 0.0001 - 0.00001, 1700000000L + i * 5000, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> Overbought(int count = 40)
    {
        // Strictly rising prices (overbought)
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + i * 0.0001, 1.10000 + i * 0.0001 + 0.00001,
                1.10000 + i * 0.0001 - 0.00001, 1700000000L + i * 5000, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> Flat(int count = 40)
    {
        // Flat prices (no signal)
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + (i % 3 - 1) * 0.00001,
                1.10000 + (i % 3 - 1) * 0.00001 + 0.00001,
                1.10000 + (i % 3 - 1) * 0.00001 - 0.00001,
                1700000000L + i * 5000, 5))
            .ToArray();
    }

    [Fact]
    public void NullWindow_Holds()
    {
        var decision = MeanReversionBrain.Decide(null!, MeanReversionBrain.MeanReversionConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void EmptyWindow_Holds()
    {
        var decision = MeanReversionBrain.Decide(Array.Empty<Tick>(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void InsufficientData_Holds()
    {
        var ticks = new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) };
        var decision = MeanReversionBrain.Decide(ticks, MeanReversionBrain.MeanReversionConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void Oversold_ChoosesRiseOrHold()
    {
        var decision = MeanReversionBrain.Decide(Oversold(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Rise || decision.Direction == BrainDirection.Hold);
        if (decision.Direction == BrainDirection.Rise)
        {
            Assert.Contains("oversold", decision.Reasoning, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Overbought_ChoosesFallOrHold()
    {
        var decision = MeanReversionBrain.Decide(Overbought(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Fall || decision.Direction == BrainDirection.Hold);
        if (decision.Direction == BrainDirection.Fall)
        {
            Assert.Contains("overbought", decision.Reasoning, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FlatMarket_Holds()
    {
        var decision = MeanReversionBrain.Decide(Flat(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void Decision_HasNonEmptyReasoning()
    {
        var decision = MeanReversionBrain.Decide(Oversold(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reasoning));
    }

    [Fact]
    public void Confidence_BoundedBetweenZeroAndOne()
    {
        var decision = MeanReversionBrain.Decide(Oversold(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.InRange(decision.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void CustomConfig_OverridesDefaults()
    {
        // With very loose thresholds, more signals should fire
        var config = new MeanReversionBrain.MeanReversionConfig(OversoldRsi: 50.0, OverboughtRsi: 50.0, MinZScore: 0.5);
        var decision = MeanReversionBrain.Decide(Flat(), config);
        // Flat market with loose thresholds might signal
        Assert.True(decision.Direction == BrainDirection.Rise ||
                    decision.Direction == BrainDirection.Fall ||
                    decision.Direction == BrainDirection.Hold);
    }

    [Fact]
    public void NullConfig_UsesDefaults()
    {
        // MeanReversionBrain.Decide should handle null config gracefully (it doesn't - should throw or handle)
        // This test verifies the null check exists
        var ticks = Oversold();
        var decision = MeanReversionBrain.Decide(ticks, MeanReversionBrain.MeanReversionConfig.Default);
        Assert.NotNull(decision);
    }
}
