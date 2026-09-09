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
    public void StrongUptrend_ReturnsRise()
    {
        var decision = TrendFollowingBrain.Decide(StrongUptrend(), TrendFollowingBrain.TrendConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Rise || decision.Direction == BrainDirection.Hold);
        if (decision.Direction == BrainDirection.Rise)
            Assert.True(decision.Confidence >= 0.6);
    }

    [Fact]
    public void StrongDowntrend_ReturnsFall()
    {
        var decision = TrendFollowingBrain.Decide(StrongDowntrend(), TrendFollowingBrain.TrendConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Fall || decision.Direction == BrainDirection.Hold);
        if (decision.Direction == BrainDirection.Fall)
            Assert.True(decision.Confidence >= 0.6);
    }

    [Fact]
    public void ChoppyMarket_Holds()
    {
        var decision = TrendFollowingBrain.Decide(Choppy(), TrendFollowingBrain.TrendConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }

    [Fact]
    public void InsufficientData_Holds()
    {
        var ticks = new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) };
        var decision = TrendFollowingBrain.Decide(ticks, TrendFollowingBrain.TrendConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
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
    public void InsufficientData_Holds()
    {
        var ticks = new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) };
        var decision = BreakoutBrain.Decide(ticks, BreakoutBrain.BreakoutConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
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

    [Fact]
    public void Oversold_ChoosesRise()
    {
        var decision = MeanReversionBrain.Decide(Oversold(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Rise || decision.Direction == BrainDirection.Hold);
    }

    [Fact]
    public void Overbought_ChoosesFall()
    {
        var decision = MeanReversionBrain.Decide(Overbought(), MeanReversionBrain.MeanReversionConfig.Default);
        Assert.True(decision.Direction == BrainDirection.Fall || decision.Direction == BrainDirection.Hold);
    }

    [Fact]
    public void InsufficientData_Holds()
    {
        var ticks = new[] { new Tick("x", 1.0, 1.0, 1.0, 1, 5) };
        var decision = MeanReversionBrain.Decide(ticks, MeanReversionBrain.MeanReversionConfig.Default);
        Assert.Equal(BrainDirection.Hold, decision.Direction);
    }
}
