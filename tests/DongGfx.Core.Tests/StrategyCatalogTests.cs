using DongGfx.Core.Brain;
using DongGfx.Core.Models;
using DongGfx.Core.Optimization;

namespace DongGfx.Core.Tests;

/// <summary>
/// Tests for the headless strategy catalog (extracted from the Optimizer
/// view model): decision resolution, parameter ranges, parameterized
/// decisions, and the synthetic-data generator.
/// </summary>
[Trait("Category", "Unit")]
public class StrategyCatalogTests
{
    private static IReadOnlyList<Tick> RisingWindow(int count = 60)
    {
        var ticks = new List<Tick>();
        var price = 1.10;
        var at = DateTimeOffset.UtcNow.AddMinutes(-count);
        for (int i = 0; i < count; i++)
        {
            price += 0.0002;
            ticks.Add(new Tick("frxEURUSD", price, price + 0.00001, price - 0.00001,
                at.ToUnixTimeMilliseconds(), 5));
            at = at.AddSeconds(5);
        }
        return ticks;
    }

    [Fact]
    public void ResolveDecideFunc_KnownStrategies_ProduceValidDecisions()
    {
        foreach (var strategy in StrategyCatalog.Strategies)
        {
            var decide = StrategyCatalog.ResolveDecideFunc(strategy);
            var decision = decide(RisingWindow());

            Assert.False(string.IsNullOrEmpty(decision.Reasoning));
        }
    }

    [Fact]
    public void ResolveDecideFunc_UnknownStrategy_Holds()
    {
        var decision = StrategyCatalog.ResolveDecideFunc("Nope")(RisingWindow());

        Assert.Equal(BrainDirection.Hold, decision.Direction);
        Assert.Contains("Unknown", decision.Reasoning);
    }

    [Fact]
    public void ResolveDecideFunc_Growth_HoldsWithSessionNote()
    {
        var decision = StrategyCatalog.ResolveDecideFunc("Growth")(RisingWindow());

        Assert.Equal(BrainDirection.Hold, decision.Direction);
        Assert.Contains("session", decision.Reasoning);
    }

    [Fact]
    public void ResolveParameterRanges_KnownStrategies_HaveRanges()
    {
        Assert.Equal(3, StrategyCatalog.ResolveParameterRanges("TrendFollowing").Count);
        Assert.Equal(3, StrategyCatalog.ResolveParameterRanges("Breakout").Count);
        Assert.Equal(4, StrategyCatalog.ResolveParameterRanges("MeanReversion").Count);
    }

    [Fact]
    public void ResolveParameterRanges_Growth_IsEmpty()
    {
        Assert.Empty(StrategyCatalog.ResolveParameterRanges("Growth"));
        Assert.Empty(StrategyCatalog.ResolveParameterRanges("Nope"));
    }

    [Fact]
    public void ResolveParameterizedDecideFunc_UsesProvidedParameters()
    {
        var decide = StrategyCatalog.ResolveParameterizedDecideFunc("MeanReversion");
        var parameters = new Dictionary<string, double>
        {
            ["RsiPeriod"] = 14,
            ["OversoldRsi"] = 30,
            ["OverboughtRsi"] = 70,
            ["MinZScore"] = 1.5
        };

        // Must not throw and must yield a decision with reasoning.
        var decision = decide(RisingWindow(), parameters);
        Assert.False(string.IsNullOrEmpty(decision.Reasoning));
    }

    [Fact]
    public void GenerateSyntheticData_ProducesSeededDeterministicWalk()
    {
        var a = StrategyCatalog.GenerateSyntheticData(200);
        var b = StrategyCatalog.GenerateSyntheticData(200);

        Assert.Equal(200, a.Count);
        Assert.All(a, t => Assert.Equal("frxEURUSD", t.Symbol));
        Assert.All(a, t => Assert.InRange(t.Quote, 1.05, 1.15));

        // Fixed seed: identical sequences, including start price.
        Assert.Equal(a[0].Quote, b[0].Quote);
        Assert.Equal(a[57].Quote, b[57].Quote);
        Assert.Equal(a[199].Quote, b[199].Quote);
    }
}
