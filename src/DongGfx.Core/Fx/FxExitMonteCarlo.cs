using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>One settled trade's final exit evaluation, replayable: the
/// ensemble's resolved action and every engine's raw exit pressure at that
/// moment (straight from the FX_EXIT journal's Votes array).</summary>
public sealed record FxSettledExit(
    long Ticket,
    string Symbol,
    string ResolvedAction,
    IReadOnlyList<FxExitVote> Votes);

/// <summary>The stability verdict over one Monte-Carlo batch.</summary>
public sealed record FxExitMonteCarloReport(
    int Trades,
    int Iterations,
    double Sigma,
    double ActionFlipRate,
    double MeanAbsScoreDelta,
    string Verdict);

/// <summary>
/// The pre-ship gate for any weight change ("more engines ≠ more
/// robustness"). Every engine weight is perturbed by multiplicative lognormal
/// noise (σ = 20% — "vary weights ±20%"), the settled trades' final votes
/// are re-scored under the perturbed roster, and the harness reports how
/// often the resolved ACTION would have differed. A brain whose exits flip
/// under ±20% weight noise is riding noise, not evidence — such a change
/// must not ship. Deterministic given the seed; journal-only by
/// construction (the caller feeds it FxSettledExit records decoded from
/// FX_EXIT entries; it never touches the bridge or the order path).
/// </summary>
public static class FxExitMonteCarlo
{
    /// <summary>Perturbation strength: multiplicative lognormal σ on every
    /// engine weight (the spec's ±20%).</summary>
    public const double DefaultSigma = 0.2;

    /// <summary>Max tolerated fraction of (trade, trial) replays whose
    /// resolved action differs from the unperturbed one.</summary>
    public const double StabilityFlipRate = 0.10;

    public static FxExitMonteCarloReport Run(
        IReadOnlyList<FxSettledExit> trades,
        int iterations,
        double sigma = DefaultSigma,
        int seed = 20260928)
    {
        if (trades.Count == 0 || iterations <= 0)
        {
            return new FxExitMonteCarloReport(trades.Count, Math.Max(0, iterations), sigma, 0, 0,
                trades.Count == 0 ? "no settled trades to replay yet — the FX_EXIT trail is still filling" : "no iterations");
        }

        var rng = new Random(seed);
        var flips = 0;
        var replays = 0;
        var absDelta = 0.0;

        for (var it = 0; it < iterations; it++)
        {
            // One perturbed roster per trial: every engine's weight gets
            // independent lognormal noise (weight * exp(σ·z)); shadow
            // engines (weight 0) stay at 0 — they are not on the field yet.
            var perturbed = new Dictionary<string, double>();
            foreach (var (engine, w) in FxExitBrain.EngineWeights)
            {
                perturbed[engine] = w <= 0 ? 0 : w * Math.Exp(sigma * Gaussian(rng));
            }

            foreach (var trade in trades)
            {
                var (baseScore, baseAction) = Score(trade, FxExitBrain.EngineWeights);
                var (pertScore, pertAction) = Score(trade, perturbed);
                absDelta += Math.Abs(pertScore - baseScore);
                replays++;
                if (!string.Equals(pertAction, baseAction, StringComparison.Ordinal))
                {
                    flips++;
                }
            }
        }

        var flipRate = replays > 0 ? (double)flips / replays : 0;
        var meanDelta = replays > 0 ? absDelta / replays : 0;
        var verdict = flipRate <= StabilityFlipRate
            ? $"stable: {flipRate:P1} action flips under ±{(sigma * 100):0.#}% weight noise — safe to consider shipping"
            : $"UNSTABLE: {flipRate:P1} action flips under ±{(sigma * 100):0.#}% weight noise (gate {StabilityFlipRate:P0}) — the exits ride noise; do not ship";
        return new FxExitMonteCarloReport(trades.Count, iterations, sigma, flipRate, meanDelta, verdict);
    }

    /// <summary>Re-score one settled trade's votes under a weight roster and
    /// resolve the action — the identical math the live brain runs.</summary>
    private static (double Score, string Action) Score(FxSettledExit trade, IReadOnlyDictionary<string, double> weights)
    {
        double weighted = 0, weightSum = 0;
        foreach (var v in trade.Votes)
        {
            var w = weights.GetValueOrDefault(v.Engine, v.Weight);
            weighted += double.Clamp(v.Exit, 0, 1) * w;
            weightSum += w;
        }
        var score = weightSum > 0 ? weighted / weightSum * 100.0 : 0;
        return (score, FxExitBrain.Resolve(score));
    }

    /// <summary>Standard normal via Box-Muller (deterministic under the seed).</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();   // (0, 1]
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
