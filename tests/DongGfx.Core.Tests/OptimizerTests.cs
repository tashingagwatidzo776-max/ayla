using DongGfx.Core.Brain;
using DongGfx.Core.Models;
using DongGfx.Core.Optimization;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class OptimizerTests : IDisposable
{
    private readonly string _dir;

    public OptimizerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_opt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static IReadOnlyList<Tick> GenerateSyntheticData(int count)
    {
        var random = new Random(42);
        var ticks = new List<Tick>();
        var price = 1.10000;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-count);

        for (int i = 0; i < count; i++)
        {
            price += (random.NextDouble() - 0.5) * 0.0001;
            price = Math.Max(1.05, Math.Min(1.15, price));

            ticks.Add(new Tick("frxEURUSD", price, price + 0.00001, price - 0.00001,
                timestamp.ToUnixTimeMilliseconds(), 5));
            timestamp = timestamp.AddSeconds(5);
        }

        return ticks;
    }

    [Fact]
    public void RunBacktest_ReturnsValidResult()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var data = GenerateSyntheticData(200);

        var result = optimizer.RunBacktest(
            data,
            "TrendFollowing",
            window => TrendFollowingBrain.Decide(window, TrendFollowingBrain.TrendConfig.Default),
            startBankroll: 5m,
            riskFraction: 0.20m,
            maxTrades: 50);

        Assert.True(result.TotalTrades >= 0);
        Assert.Equal(5m, result.StartBankroll);
        Assert.NotNull(result.EquityCurve);
        Assert.True(result.EquityCurve.Count > 0);
    }

    [Fact]
    public void RunBacktest_WithMeanReversion()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var data = GenerateSyntheticData(200);

        var result = optimizer.RunBacktest(
            data,
            "MeanReversion",
            window => MeanReversionBrain.Decide(window, MeanReversionBrain.MeanReversionConfig.Default));

        Assert.True(result.TotalTrades >= 0);
        Assert.Equal("MeanReversion", result.StrategyName);
    }

    [Fact]
    public void Optimize_FindsBestParameters()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var data = GenerateSyntheticData(300);

        var ranges = new Dictionary<string, (double min, double max, double step)>
        {
            ["RsiPeriod"] = (10, 20, 2),
            ["OversoldRsi"] = (25, 35, 2),
            ["OverboughtRsi"] = (65, 75, 2),
            ["MinZScore"] = (1.0, 2.0, 0.25)
        };

        var result = optimizer.Optimize(
            data,
            "MeanReversion",
            (window, parameters) => MeanReversionBrain.Decide(window,
                new MeanReversionBrain.MeanReversionConfig(
                    RsiPeriod: (int)parameters["RsiPeriod"],
                    OversoldRsi: parameters["OversoldRsi"],
                    OverboughtRsi: parameters["OverboughtRsi"],
                    MinZScore: parameters["MinZScore"])),
            ranges,
            iterations: 10);

        Assert.True(result.TotalIterations == 10);
        Assert.True(result.BestParameters.Count > 0);
        Assert.NotNull(result.BestResult);
        Assert.True(result.AllResults.Count > 0);
    }

    [Fact]
    public void GenerateSyntheticData_CreatesCorrectCount()
    {
        var data = GenerateSyntheticData(100);
        Assert.Equal(100, data.Count);
        Assert.All(data, t => Assert.Equal("frxEURUSD", t.Symbol));
    }
}
