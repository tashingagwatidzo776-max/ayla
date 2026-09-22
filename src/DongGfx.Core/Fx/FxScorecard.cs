using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>One family's verdict from a scorecard run.</summary>
public sealed record FxScorecardEntry(
    string Family,
    int IsTrades,
    double IsPnl,
    int OosTrades,
    double OosPnl,
    bool Approved,
    string Note);

/// <summary>
/// The nightly alpha scorecard: every production family is backtested over
/// an in-sample window and then evaluated ONCE on the following
/// out-of-sample window with the same parameters. Approval requires OOS
/// profitability with a minimum trade count — the walk-forward guardrail made
/// visible and journaled (`FX_SCORECARD`) so monitoring can watch families
/// degrade before they ever receive real orders.
/// </summary>
public static class FxScorecard
{
    public const int MinOosTrades = 3;

    /// <summary>Families under score, default parameters (no fitting — the
    /// point is out-of-sample honesty, not in-sample beauty).</summary>
    public static IReadOnlyList<IFxAlpha> DefaultFamilies() => new IFxAlpha[]
    {
        new FxMomentum.EmaCross(),
        new FxMomentum.DonchianBreakout(),
        new FxMomentum.Roc(),
        new FxMeanReversion.ZScore(),
        new FxMeanReversion.BollingerReversion(),
        new FxMeanReversion.VwapReversion(),
    };

    /// <summary>Score every family over bars: IS = first isFraction of bars,
    /// OOS = the remainder (chronological split — no peeking).</summary>
    public static IReadOnlyList<FxScorecardEntry> Run(
        IReadOnlyList<FxBar> bars, double isFraction = 0.7,
        IReadOnlyList<IFxAlpha>? families = null, int warmup = 60)
    {
        if (bars.Count < warmup + 40)
        {
            return Array.Empty<FxScorecardEntry>();
        }

        var isEnd = Math.Clamp((int)(bars.Count * isFraction), warmup + 10, bars.Count - 20);
        var results = new List<FxScorecardEntry>();

        foreach (var alpha in families ?? DefaultFamilies())
        {
            // A fresh detector per window keeps spread/session state from
            // leaking across the split (the OOS window must be unseen).
            var isPnl = Backtest(bars, 0, isEnd, alpha, out var isTrades);
            var oosPnl = Backtest(bars, isEnd, bars.Count, alpha, out var oosTrades);

            var approved = oosPnl > 0 && oosTrades >= MinOosTrades;
            var note = oosTrades < MinOosTrades
                ? $"insufficient OOS trades ({oosTrades})"
                : approved ? "OOS positive" : "OOS negative";

            results.Add(new FxScorecardEntry(alpha.Name, isTrades, isPnl, oosTrades, oosPnl, approved, note));
        }

        return results;
    }

    /// <summary>Fractional PnL of one family over [start, end): at each bar
    /// the alpha sees the rolling window ending there (warmup bars before
    /// `start` are available for indicator state) and the position settles on
    /// the NEXT bar's close. No slippage model here — the verdict is
    /// comparative across families on identical conditions.</summary>
    private static double Backtest(
        IReadOnlyList<FxBar> bars, int start, int end, IFxAlpha alpha, out int trades)
    {
        var detector = new FxRegimeDetector();
        var window = Math.Min(120, Math.Max(30, (end - start) / 4));
        var pnl = 0.0;
        trades = 0;

        for (var i = Math.Max(window, start); i < end - 1; i++)
        {
            var slice = bars.Skip(i - window + 1).Take(window + 1).ToList();
            if (slice.Count < 31)
            {
                continue;
            }

            // spread unknown historically — feed a neutral value so the
            // spread veto behaves as it would on a normal session
            var verdict = detector.Evaluate(slice, 10,
                DateTimeOffset.FromUnixTimeSeconds(slice[^1].Time));
            if (verdict.Regime is FxRegime.StandDown or FxRegime.LowLiquidity)
            {
                continue;
            }

            var signal = alpha.Evaluate(slice, verdict);
            if (signal is null)
            {
                continue;
            }

            var entry = slice[^1].Close;
            var exit = bars[i + 1].Close;
            if (entry <= 0)
            {
                continue;
            }

            var direction = signal.Direction == FxDirection.Buy ? 1 : -1;
            pnl += ((exit - entry) / entry) * direction;
            trades++;
        }

        return pnl;
    }
}
