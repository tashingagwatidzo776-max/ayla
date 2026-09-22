using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The advanced brain families: microstructure (tick-rule imbalance, VPIN),
/// Kalman slope, stat-arb spread stats, triangular-arb pricing, execution
/// schedules, market-making quotes, the genetic optimizer, the walk-forward
/// OOS gate, the feature store, and the online logistic model. Everything
/// deterministic (seeded RNG) and millisecond-fast — none of this touches a
/// transport; live wiring is exercised in the App/host tests.
/// </summary>
[Trait("Category", "Unit")]
public class FxAdvancedTests
{
    private static List<(double Price, double Vol)> TickRun(int n, double start, double step, double vol)
    {
        var ticks = new List<(double, double)>(n);
        for (var i = 0; i < n; i++)
        {
            ticks.Add((start + step * i, vol));
        }

        return ticks;
    }

    // ── family 5: microstructure ────────────────────────────────────────

    [Fact]
    public void OrderFlowImbalance_PureUpticks_Is_PlusOne()
    {
        var ticks = TickRun(30, 1.10, 0.0001, 1.0);
        Assert.Equal(1.0, FxMicrostructure.OrderFlowImbalance(ticks), 6);
    }

    [Fact]
    public void OrderFlowImbalanced_PureDownticks_Is_MinusOne()
    {
        var ticks = TickRun(30, 1.10, -0.0001, 2.0);
        Assert.Equal(-1.0, FxMicrostructure.OrderFlowImbalance(ticks), 6);
    }

    [Fact]
    public void Microstructure_ShortOrFlatWindows_AreNeutral()
    {
        Assert.Equal(0, FxMicrostructure.OrderFlowImbalance(TickRun(5, 1.1, 0.0001, 1)), 6);
        var flat = TickRun(30, 1.10, 0, 1);       // every tick unchanged → tick rule abstains
        Assert.Equal(0, FxMicrostructure.OrderFlowImbalance(flat), 6);
    }

    [Fact]
    public void VpinLite_ToxicBuckets_Count()
    {
        // alternating violent up/down runs produce |imbalance| = 1 buckets
        var ticks = new List<(double, double)>();
        var p = 1.10;
        for (var i = 0; i < 200; i++)
        {
            p += (i % 10 < 5 ? 0.001 : -0.001);
            ticks.Add((p, 1.0));
        }

        var vpin = FxMicrostructure.VpinLite(ticks, bucketSize: 10);
        Assert.True(vpin > 0.5, $"vpin {vpin:0.00}");
    }

    [Fact]
    public void VpinLite_NeedsEnoughData()
    {
        Assert.Equal(0, FxMicrostructure.VpinLite(TickRun(15, 1.1, 0.0001, 1), bucketSize: 10), 6);
    }

    // ── family 8: Kalman ────────────────────────────────────────────────

    [Fact]
    public void Kalman_Tracks_Drift()
    {
        var k = new KalmanSlope();
        var drift = 0.01;
        var price = 1.1000;
        foreach (var _ in Enumerable.Range(0, 300))
        {
            price += drift;
            k.Update(price);
        }

        Assert.True(Math.Abs(k.Drift - drift) < drift * 0.5, $"drift {k.Drift:0.0000} vs {drift}");
        Assert.True(Math.Abs(k.Level - price) < 0.001, $"level {k.Level:0.0000} vs {price:0.0000}");
    }

    [Fact]
    public void Kalman_FlatSeries_Slope_GoesToZero()
    {
        var k = new KalmanSlope();
        foreach (var _ in Enumerable.Range(0, 200))
        {
            k.Update(1.10);
        }

        Assert.True(Math.Abs(k.Drift) < 1e-5, $"drift {k.Drift}");
    }

    // ── family 6: stat-arb ──────────────────────────────────────────────

    [Fact]
    public void StatArb_ZScore_Flags_ExtremeSpread()
    {
        var spread = Enumerable.Range(0, 60).Select(i => i < 55 ? 0.0 : 3.0).ToList();
        Assert.True(FxStatArb.ZScore(spread, 50) > 2, $"z {FxStatArb.ZScore(spread, 50):0.00}");
    }

    [Fact]
    public void StatArb_HalfLife_FastReversion_IsShort()
    {
        // AR(1) with phi=0.5 → half-life of 1 bar
        var rng = new Random(7);
        var spread = new List<double>();
        var x = 0.0;
        for (var i = 0; i < 400; i++)
        {
            x = 0.5 * x + rng.NextGaussian() * 0.1;
            spread.Add(x);
        }

        var hl = FxStatArb.HalfLife(spread);
        Assert.True(hl < 10, $"half-life {hl:0.0}");
    }

    [Fact]
    public void StatArb_TrendingSpread_IsNotMeanReverting()
    {
        var spread = Enumerable.Range(0, 100).Select(i => i * 0.01).ToList();
        Assert.True(double.IsPositiveInfinity(FxStatArb.HalfLife(spread)));
    }

    // ── family 14: triangular arb ───────────────────────────────────────

    [Fact]
    public void TriArb_ConsistentQuotes_HaveNoEdge()
    {
        // A/B=1.1, B/C=1.2 → synthetic A/C=1.32; direct A/C=1.32 → zero raw edge,
        // so after costs the round trip must be a LOSS (never a fake opportunity)
        Assert.True(FxTriangularArb.RoundTripEdge(1.1, 1.2, 1.32, costPerLeg: 0.0005) < 0);
        Assert.Equal(0, FxTriangularArb.RoundTripEdge(1.1, 1.2, 1.32, costPerLeg: 0), 9);
    }

    [Fact]
    public void TriArb_MispricedCross_ClearsCosts()
    {
        // synthetic A/C = 1.1*1.25 = 1.375 vs direct 1.30 → +5.77% edge minus costs
        var edge = FxTriangularArb.RoundTripEdge(1.1, 1.25, 1.30, costPerLeg: 0.0005);
        Assert.True(edge > 0.03, $"edge {edge:0.0000}");
    }

    [Fact]
    public void TriArb_BadInputs_AreZero()
    {
        Assert.Equal(0, FxTriangularArb.RoundTripEdge(0, 1, 1, 0.0001), 9);
        Assert.Equal(0, FxTriangularArb.RoundTripEdge(-1, 1, 1, 0.0001), 9);
    }

    // ── family 12: execution ────────────────────────────────────────────

    [Fact]
    public void Twap_Splits_Evenly()
    {
        var slices = FxExecution.Twap(1.0, 4, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(30));
        Assert.Equal(4, slices.Count);
        Assert.All(slices, s => Assert.Equal(0.25, s.Lots, 9));
        Assert.True(slices[1].NotBefore > slices[0].NotBefore);
    }

    [Fact]
    public void Vwap_Follows_Volume_Shape()
    {
        var slices = FxExecution.Vwap(1.0, new[] { 1.0, 3.0 }, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(10));
        Assert.Equal(2, slices.Count);
        Assert.Equal(0.25, slices[0].Lots, 9);
        Assert.Equal(0.75, slices[1].Lots, 9);
    }

    [Fact]
    public void Iceberg_EmitsClips_And_CoversTotal()
    {
        var clips = FxExecution.Iceberg(0.9, 0.25);
        Assert.Equal(0.9, clips.Sum(c => c.Lots), 9);
        Assert.All(clips.Take(clips.Count - 1), c => Assert.Equal(0.25, c.Lots, 9));
    }

    [Fact]
    public void Execution_Garbage_IsEmpty()
    {
        Assert.Empty(FxExecution.Twap(0, 4, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)));
        Assert.Empty(FxExecution.Vwap(1, Array.Empty<double>(), DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)));
        Assert.Empty(FxExecution.Iceberg(1, 0));
    }

    // ── family 13: market making ────────────────────────────────────────

    [Fact]
    public void MarketMaking_InventorySkews_Quotes()
    {
        var flat = FxMarketMaking.Quote(1.1000, halfSpread: 0.0002, gamma: 0.0001, inventory: 0, maxInventory: 5);
        var longSkew = FxMarketMaking.Quote(1.1000, halfSpread: 0.0002, gamma: 0.0001, inventory: 3, maxInventory: 5);
        Assert.NotNull(flat);
        Assert.NotNull(longSkew);
        // long inventory → reservation (and both quotes) lower than flat
        Assert.True(longSkew!.Value.Bid < flat!.Value.Bid);
        Assert.True(longSkew.Value.Ask < flat.Value.Ask);
    }

    [Fact]
    public void MarketMaking_AtCap_QuotesOnlyReducingSide()
    {
        var q = FxMarketMaking.Quote(1.1000, 0.0002, 0.0001, inventory: 5, maxInventory: 5);
        Assert.NotNull(q);
        Assert.True(double.IsNaN(q!.Value.Ask));       // cannot add longs
        var shortSide = FxMarketMaking.Quote(1.1000, 0.0002, 0.0001, inventory: -5, maxInventory: 5);
        Assert.True(double.IsNaN(shortSide!.Value.Bid));
    }

    // ── family 10: genetic + walk-forward ───────────────────────────────

    [Fact]
    public void Genetic_Finds_AcceptableGenome_OnSimpleObjective()
    {
        // objective peaks at g=[0.3, 0.7]
        static double Fitness(FxGenome g) => -(Math.Pow(g.Genes[0] - 0.3, 2) + Math.Pow(g.Genes[1] - 0.7, 2));
        var best = FxGenetic.Optimize(2, Fitness, new Random(42), population: 30, generations: 25);
        Assert.True(Fitness(best) > -0.01, $"fitness {Fitness(best):0.0000}");
    }

    [Fact]
    public void WalkForward_Gate_Approves_Genuine_Edge_And_Rejects_Overfit()
    {
        // a "strategy" whose OOS performance mirrors its IS performance → approved
        var honest = new[] { new FxFold(0, 100, 80), new FxFold(1, 120, 90), new FxFold(2, 90, 70) };
        Assert.True(FxWalkForward.Approved(honest));

        // an overfit one: great in-sample, loses money OOS → rejected
        var overfit = new[] { new FxFold(0, 500, -50), new FxFold(1, 480, -60), new FxFold(2, 510, -40) };
        Assert.False(FxWalkForward.Approved(overfit));

        // mixed with one catastrophic fold → rejected regardless of majority
        var catastrophic = new[] { new FxFold(0, 100, 80), new FxFold(1, 120, 90), new FxFold(2, 90, -500) };
        Assert.False(FxWalkForward.Approved(catastrophic));
    }

    [Fact]
    public void WalkForward_Runs_Folds_OverBars()
    {
        var bars = Enumerable.Range(0, 600)
            .Select(i => new FxBar(1_700_000_000L + i * 60, 1.1 + 0.001 * Math.Sin(i / 7.0),
                                   1.1 + 0.001 * Math.Sin(i / 7.0) + 0.0005,
                                   1.1 + 0.001 * Math.Sin(i / 7.0) - 0.0005,
                                   1.1 + 0.001 * Math.Sin((i + 1) / 7.0), 100))
            .ToList();

        var folds = FxWalkForward.Run(bars, folds: 3, isFraction: 0.5, rng: new Random(5),
            fitnessOf: (window, g) => g.Genes[0] - Math.Abs(g.Genes[1] - 0.5),
            evaluateOn: (window, g) => g.Genes[0] > 0.5 ? 0.01 : -0.01,
            geneCount: 2, population: 10, generations: 5);

        Assert.True(folds.Count >= 2, $"folds {folds.Count}");
        Assert.All(folds, f => Assert.NotEqual(0, f.InSamplePnl));
    }

    // ── family 9: feature store + online model ──────────────────────────

    [Fact]
    public void FeatureStore_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dg-fx-fs-{Guid.NewGuid():N}.csv");
        try
        {
            FxFeatureStore.Append(path, 1, new[] { 0.5, -1.25 });
            FxFeatureStore.Append(path, 0, new[] { -0.5, 1.25 });
            File.AppendAllText(path, "not,a,valid,row\n\n");
            FxFeatureStore.Append(path, 1, new[] { 0.6, -1.35 });

            var rows = FxFeatureStore.Read(path);
            Assert.Equal(3, rows.Count);
            Assert.Equal(1, rows[0].Label);
            Assert.Equal(-1.25, rows[0].Features[1], 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OnlineLogistic_Learns_Separable_Data()
    {
        var rng = new Random(11);
        var model = new OnlineLogistic(2, learningRate: 0.1);
        foreach (var _ in Enumerable.Range(0, 4))
        {
            foreach (var _i in Enumerable.Range(0, 200))
            {
                var label = rng.Next(2);
                var x0 = label == 1 ? 1.0 : -1.0;
                var feats = new[] { x0 + rng.NextGaussian() * 0.3, rng.NextGaussian() * 0.3 };
                model.Observe(feats, label);
            }
        }

        Assert.True(model.PredictProb(new[] { 1.0, 0.0 }) > 0.8);
        Assert.True(model.PredictProb(new[] { -1.0, 0.0 }) < 0.2);
    }
}

internal static class RngExtensions
{
    public static double NextGaussian(this Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
