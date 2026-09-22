using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>A candidate parameter vector for an alpha family.</summary>
public sealed record FxGenome(double[] Genes)
{
    public FxGenome Mutated(double rate, double scale, Random rng) =>
        new(Genes.Select(g => rng.NextDouble() < rate
            ? g + Gaussian(rng) * scale * Math.Max(1e-9, Math.Abs(g))
            : g).ToArray());

    private static double Gaussian(Random rng)
    {
        // Box–Muller
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>
/// Family 10 — genetic optimizer over a fitness function (a backtest
/// objective). Deterministic given the same seed; elitist, tournament
/// selection, uniform crossover, gaussian mutation. The optimizer is generic
/// over the genome — the WALK-FORWARD harness (below) decides whether any
/// optimized parameter set may ever trade.
/// </summary>
public static class FxGenetic
{
    public static FxGenome Optimize(
        int geneCount, Func<FxGenome, double> fitness, Random rng,
        int population = 40, int generations = 30, double mutationRate = 0.25, double mutationScale = 0.15)
    {
        if (geneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(geneCount));
        }

        var pop = new List<FxGenome>(population);
        for (var i = 0; i < population; i++)
        {
            pop.Add(new FxGenome(Enumerable.Range(0, geneCount).Select(_ => rng.NextDouble()).ToArray()));
        }

        var scores = pop.Select(fitness).ToList();

        for (var g = 0; g < generations; g++)
        {
            var ranked = pop.Zip(scores, (genome, s) => (genome, s)).OrderByDescending(x => x.s).ToList();

            var next = new List<FxGenome> { ranked[0].genome };                    // elitism
            while (next.Count < population)
            {
                var a = Tournament(ranked, rng);
                var b = Tournament(ranked, rng);
                var child = new FxGenome(a.Genes.Zip(b.Genes, (x, y) => rng.NextDouble() < 0.5 ? x : y).ToArray());
                next.Add(child.Mutated(mutationRate, mutationScale, rng));
            }

            pop = next;
            scores = pop.Select(fitness).ToList();
        }

        var bestIdx = 0;
        for (var i = 1; i < pop.Count; i++)
        {
            if (scores[i] > scores[bestIdx])
            {
                bestIdx = i;
            }
        }

        return pop[bestIdx];
    }

    private static FxGenome Tournament(List<(FxGenome Genome, double Score)> ranked, Random rng)
    {
        var a = ranked[rng.Next(ranked.Count)];
        var b = ranked[rng.Next(ranked.Count)];
        return a.Score >= b.Score ? a.Genome : b.Genome;
    }
}

/// <summary>One walk-forward fold: optimize in-sample, evaluate out-of-sample.</summary>
public sealed record FxFold(int Fold, double InSamplePnl, double OutOfSamplePnl);

/// <summary>
/// The non-negotiable gate: a strategy factory is optimized on each
/// in-sample window and evaluated ONCE on the following out-of-sample
/// window. Approval requires the OOS PnL fraction (and no catastrophic
/// single fold) — this is what keeps the genetic/ML families honest.
/// </summary>
public static class FxWalkForward
{
    /// <summary>Run anchored walk-forward folds over a bar series.</summary>
    /// <param name="fitnessOf">objective over an IS window (e.g. backtest PnL with candidate params)</param>
    /// <param name="evaluateOn">final OOS evaluation with the chosen params</param>
    public static IReadOnlyList<FxFold> Run(
        IReadOnlyList<FxBar> bars, int folds, double isFraction, Random rng,
        Func<IReadOnlyList<FxBar>, FxGenome, double> fitnessOf,
        Func<IReadOnlyList<FxBar>, FxGenome, double> evaluateOn,
        int geneCount = 2, int population = 16, int generations = 10)
    {
        if (folds <= 0 || isFraction <= 0.2 || isFraction >= 0.95)
        {
            throw new ArgumentOutOfRangeException(nameof(isFraction));
        }

        var results = new List<FxFold>(folds);
        var window = (int)(bars.Count * (1 - isFraction) / Math.Max(1, folds - (folds > 1 ? 1 : 0)));
        var isSize = (int)(bars.Count * isFraction);

        for (var f = 0; f < folds; f++)
        {
            var isStart = f * window;
            var isEnd = Math.Min(bars.Count, isStart + isSize);
            var oosEnd = Math.Min(bars.Count, isEnd + window);
            if (isEnd - isStart < 40 || oosEnd - isEnd < 10)
            {
                break;
            }

            var isBars = bars.Skip(isStart).Take(isEnd - isStart).ToList();
            var oosBars = bars.Skip(isEnd).Take(oosEnd - isEnd).ToList();

            var best = FxGenetic.Optimize(geneCount, genome => fitnessOf(isBars, genome), rng,
                population: population, generations: generations);

            results.Add(new FxFold(f, fitnessOf(isBars, best), evaluateOn(oosBars, best)));
        }

        return results;
    }

    /// <summary>The approval rule: majority of OOS folds profitable AND no
    /// fold worse than the loss cap. In-sample results are irrelevant here.</summary>
    public static bool Approved(IReadOnlyList<FxFold> folds, double minPositiveFraction = 0.6, double lossCap = 0)
    {
        if (folds.Count == 0)
        {
            return false;
        }

        var positive = folds.Count(x => x.OutOfSamplePnl > 0);
        return positive >= Math.Ceiling(folds.Count * minPositiveFraction)
               && folds.All(x => x.OutOfSamplePnl >= lossCap);
    }
}
