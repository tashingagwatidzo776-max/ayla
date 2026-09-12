using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.Core.Optimization;

/// <summary>
/// Headless mapping between optimizer strategy names and their decision
/// functions / parameter ranges, plus the synthetic-data fallback generator.
/// Extracted from <see cref="Tf.App.ViewModels.OptimizerViewModel"/> so the
/// rules are unit-testable without WPF; production behavior is unchanged.
/// </summary>
public static class StrategyCatalog
{
    public static readonly IReadOnlyList<string> Strategies = new[]
    {
        "TrendFollowing", "Breakout", "MeanReversion", "Growth"
    };

    /// <summary>Decision function for a strategy with default parameters.</summary>
    public static Func<IReadOnlyList<Tick>, LlmDecision> ResolveDecideFunc(string strategy) => strategy switch
    {
        "TrendFollowing" => window => TrendFollowingBrain.Decide(window, TrendFollowingBrain.TrendConfig.Default),
        "Breakout" => window => BreakoutBrain.Decide(window, BreakoutBrain.BreakoutConfig.Default),
        "MeanReversion" => window => MeanReversionBrain.Decide(window, MeanReversionBrain.MeanReversionConfig.Default),
        "Growth" => window => LlmDecision.Hold("Growth brain requires session engine"),
        _ => window => LlmDecision.Hold("Unknown strategy")
    };

    /// <summary>Parameter search ranges per strategy; empty for strategies that cannot be optimized.</summary>
    public static Dictionary<string, (double min, double max, double step)> ResolveParameterRanges(string strategy) => strategy switch
    {
        "TrendFollowing" => new Dictionary<string, (double min, double max, double step)>
        {
            ["FastEma"] = (5, 15, 1),
            ["SlowEma"] = (15, 30, 1),
            ["MinAdx"] = (20, 35, 5)
        },
        "Breakout" => new Dictionary<string, (double min, double max, double step)>
        {
            ["BollingerPeriod"] = (15, 25, 2),
            ["BollingerStdDev"] = (1.5, 2.5, 0.25),
            ["BreakoutThreshold"] = (0.3, 0.8, 0.1)
        },
        "MeanReversion" => new Dictionary<string, (double min, double max, double step)>
        {
            ["RsiPeriod"] = (10, 20, 2),
            ["OversoldRsi"] = (25, 35, 2),
            ["OverboughtRsi"] = (65, 75, 2),
            ["MinZScore"] = (1.0, 2.0, 0.25)
        },
        _ => new Dictionary<string, (double min, double max, double step)>()
    };

    /// <summary>Parameterized decision function used during optimization.</summary>
    public static Func<IReadOnlyList<Tick>, Dictionary<string, double>, LlmDecision> ResolveParameterizedDecideFunc(string strategy) =>
        (window, parameters) => strategy switch
        {
            "TrendFollowing" => TrendFollowingBrain.Decide(window,
                new TrendFollowingBrain.TrendConfig(
                    FastEma: (int)parameters["FastEma"],
                    SlowEma: (int)parameters["SlowEma"],
                    MinAdx: parameters["MinAdx"])),
            "Breakout" => BreakoutBrain.Decide(window,
                new BreakoutBrain.BreakoutConfig(
                    BollingerPeriod: (int)parameters["BollingerPeriod"],
                    BollingerStdDev: parameters["BollingerStdDev"],
                    BreakoutThreshold: parameters["BreakoutThreshold"])),
            "MeanReversion" => MeanReversionBrain.Decide(window,
                new MeanReversionBrain.MeanReversionConfig(
                    RsiPeriod: (int)parameters["RsiPeriod"],
                    OversoldRsi: parameters["OversoldRsi"],
                    OverboughtRsi: parameters["OverboughtRsi"],
                    MinZScore: parameters["MinZScore"])),
            _ => LlmDecision.Hold("Unknown")
        };

    /// <summary>
    /// Synthetic fallback data when the tick cache has too few ticks: a
    /// seeded random walk with mean reversion around $1.10 (identical output
    /// to the previous VM-private implementation).
    /// </summary>
    public static IReadOnlyList<Tick> GenerateSyntheticData(int count)
    {
        var random = new Random(42);
        var ticks = new List<Tick>();
        var price = 1.10000;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-count);

        for (int i = 0; i < count; i++)
        {
            price += (random.NextDouble() - 0.5) * 0.0001;
            price = Math.Max(1.05, Math.Min(1.15, price));

            ticks.Add(new Tick(
                "frxEURUSD",
                price,
                price + random.NextDouble() * 0.00001,
                price - random.NextDouble() * 0.00001,
                timestamp.ToUnixTimeMilliseconds(),
                5));

            timestamp = timestamp.AddSeconds(5);
        }

        return ticks;
    }
}
