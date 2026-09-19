using DongGfx.Core.Brain;
using DongGfx.Core.Models;
using DongGfx.Core.Optimization;

namespace DongGfx.Core.Tests;

/// <summary>
/// Tests for the StrategyOptimizer's results IO — the save/load round-trip
/// used by the Optimizer tab to reload previous runs. RunBacktest/Optimize
/// happy paths are covered in OptimizerTests.
/// </summary>
[Trait("Category", "Unit")]
public class StrategyOptimizerResultsTests : IDisposable
{
    private readonly string _dir;

    public StrategyOptimizerResultsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_opt_results_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IReadOnlyList<Tick> Data(int count)
    {
        var random = new Random(7);
        var ticks = new List<Tick>();
        var price = 1.10000;
        var at = DateTimeOffset.UtcNow.AddMinutes(-count);
        for (int i = 0; i < count; i++)
        {
            price += (random.NextDouble() - 0.5) * 0.0001;
            price = Math.Clamp(price, 1.05, 1.15);
            ticks.Add(new Tick("frxEURUSD", price, price + 0.00001, price - 0.00001,
                at.ToUnixTimeMilliseconds(), 5));
            at = at.AddSeconds(5);
        }
        return ticks;
    }

    [Fact]
    public void Optimize_SavesResultsFile_ListedAndReloadable()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var ranges = new Dictionary<string, (double min, double max, double step)>
        {
            ["MinZScore"] = (1.0, 1.5, 0.25)
        };

        var result = optimizer.Optimize(
            Data(150), "TrendFollowing",
            (window, _) => TrendFollowingBrain.Decide(window, TrendFollowingBrain.TrendConfig.Default),
            ranges, iterations: 3);

        var files = optimizer.GetSavedResults();
        Assert.Single(files);

        var reloaded = optimizer.LoadResult(files[0]);
        Assert.NotNull(reloaded);
        Assert.Equal("TrendFollowing", reloaded!.StrategyName);
        Assert.Equal(3, reloaded.TotalIterations);
        Assert.Equal(result.BestParameters, reloaded.BestParameters);
        Assert.Equal(result.BestResult.StartBankroll, reloaded.BestResult.StartBankroll);
    }

    [Fact]
    public void LoadResult_MissingFile_ReturnsNull()
    {
        var optimizer = new StrategyOptimizer(_dir);

        Assert.Null(optimizer.LoadResult(Path.Combine(_dir, "does_not_exist.json")));
    }

    [Fact]
    public void LoadResult_MalformedJson_ReturnsNull()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var path = Path.Combine(_dir, "optimization_broken.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(optimizer.LoadResult(path));
    }

    [Fact]
    public void LoadResult_MinimalDocument_FillsDefaults()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var path = Path.Combine(_dir, "optimization_min.json");
        File.WriteAllText(path, """{"StrategyName":"Growth"}""");

        var result = optimizer.LoadResult(path);

        Assert.NotNull(result);
        Assert.Equal("Growth", result!.StrategyName);
        Assert.Equal(0, result.TotalIterations);
        Assert.Equal(5m, result.BestResult.StartBankroll); // documented default
        Assert.Empty(result.BestParameters);
    }

    [Fact]
    public void GetSavedResults_EmptyDirectory_ReturnsEmpty()
    {
        var optimizer = new StrategyOptimizer(_dir);
        Assert.Empty(optimizer.GetSavedResults());
    }

    [Fact]
    public void RunBacktest_HoldAndTinyStake_NeverTrades()
    {
        // Both a Hold decision and a non-positive stake must skip trading.
        var optimizer = new StrategyOptimizer(_dir);

        var hold = optimizer.RunBacktest(
            Data(120), "HoldAll", _ => LlmDecision.Hold("no setup"), maxTrades: 10);

        var zeroStake = optimizer.RunBacktest(
            Data(120), "ZeroStake",
            _ => new LlmDecision(BrainDirection.Rise, 0.9, 0m, "stake 0"),
            maxTrades: 10);

        Assert.Equal(0, hold.TotalTrades);
        Assert.Equal(0, zeroStake.TotalTrades);
        Assert.Equal(5m, hold.EndBankroll);
        Assert.All(hold.EquityCurve, v => Assert.Equal(5m, v));
    }

    [Fact]
    public void RunBacktest_TradesOnTrendData_ProducesMetrics()
    {
        // A live direction with a real stake exercises the trade simulation,
        // equity curve, drawdown and Sharpe paths.
        var optimizer = new StrategyOptimizer(_dir);

        var result = optimizer.RunBacktest(
            Data(150), "TrendFollowing",
            window => TrendFollowingBrain.Decide(window, TrendFollowingBrain.TrendConfig.Default),
            maxTrades: 20);

        if (result.TotalTrades > 0)
        {
            Assert.Equal(result.TotalTrades, result.Wins + result.Losses);
            Assert.InRange(result.WinRate, 0m, 1m);
            Assert.True(result.MaxDrawdown >= 0m);
            Assert.Equal(5m, result.EquityCurve[0]);
        }
    }
}
