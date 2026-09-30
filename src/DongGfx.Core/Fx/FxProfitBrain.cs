using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>One candidate destination price ("where is price likely to
/// travel"), scored 0-100. Score is quality (liquidity + structure +
/// momentum alignment + volatility fit − distance penalty), NOT
/// probability — the probability engine reads it separately.</summary>
public sealed record FxProfitTarget(
    string Kind,     // swing-high | swing-low | equal-highs | equal-lows | session-high | session-low | prev-day-high | prev-day-low | atr-projection
    double Price,
    double R,        // distance from ENTRY in R units (signed: >0 = profit side)
    double DistanceInAtr,
    double Score);

/// <summary>Advisory output for one open position — the machine-readable
/// PROFIT BRAIN REPORT. Nothing here executes; the Exit Brain remains the
/// sole executor until a shadow engine earns weight through the promotion
/// gates (100 trades @ 60% + a STABLE Monte-Carlo verdict).</summary>
public sealed record FxProfitReport(
    double CurrentR,
    double PeakR,
    double GivebackR,
    double GivebackPct,
    string GivebackClass,        // NORMAL | WARNING | DETERIORATING | CRITICAL
    string ProfitState,          // the PROFIT_* state machine
    double FloorR,               // protected-profit floor (0 = none active)
    bool FloorBreached,          // CurrentR < FloorR (peak >= 1R)
    string RecommendedAction,    // HOLD | HOLD_AND_EXTEND | MOVE_TO_PROTECTED | TIGHTEN_PROTECTION | EXIT_REQUEST
    double ContinuationProbability,
    double ReversalProbability,
    string MomentumClass,        // strong | stable | weakening
    string RegimeClass,
    FxProfitTarget? Tp1,
    FxProfitTarget? Tp2,
    FxProfitTarget? Tp3,
    double Tp1Probability,
    double Tp2Probability,
    double Tp3Probability,
    (double Tp1, double Tp2, double Tp3, double Runner) Allocation,
    string TrailingMode,
    double ProfitScore,
    FxExitVote TargetTpVote);     // the target-TP shadow engine's vote (Weight 0)

/// <summary>Configurable weights/thresholds (spec §22/§35: nothing
/// load-bearing is hardcoded — tune only through the evidence gates).</summary>
public sealed record FxProfitBrainOptions(
    double StructureWeight = 0.20,
    double LiquidityWeight = 0.20,
    double MomentumWeight = 0.15,
    double VolatilityWeight = 0.10,
    double RegimeWeight = 0.10,
    double ContinuationWeight = 0.15,
    double ReversalWeight = 0.10,
    (double Peak, double Floor)[] FloorSchedule = null!)
{
    public static readonly FxProfitBrainOptions Default = new()
    {
        FloorSchedule = new[]
        {
            (1.0, 0.3), (2.0, 1.0), (3.0, 2.0), (4.0, 3.0), (5.0, 4.0),
        },
    };
}

/// <summary>
/// The HWARANG Profit Brain — deterministic-first profit extraction.
///
/// Answers "what is the highest expected-value way to extract profit while
/// controlling giveback?" for one open position: candidate targets from
/// liquidity/structure, rule-based probabilities, an EV ranking, the
/// giveback classifier, the never-down profit floor, the PROFIT_* state
/// machine, and an allocation plan for partials.
///
/// LAW: advisory only. It journals and (via the host) casts weight-0
/// shadow votes; it never sends orders, never moves stops, and never
/// overrides the Exit Brain or risk overrides (spec §26). Pure: snapshots
/// in, report out; no I/O, no clock, no threads.
/// </summary>
public static class FxProfitBrain
{
    // Giveback classes (percent of peak given back).
    public const double GivebackNormalPct = 30.0;
    public const double GivebackWarningPct = 50.0;
    public const double GivebackDeterioratingPct = 70.0;

    // Reversal confluence: evidence points, each ≥0.15 — never one candle.
    public const double ReversalHighConfidence = 0.55;

    // The floor never exceeds this fraction of peak profit — a floor AT the
    // peak would close on the first tick of normal retracement.
    public const double FloorCapOfPeak = 0.9;

    // Pre-tighten engages at this reversal probability (below the 0.55
    // high-confidence bar) — moderate evidence still buys protection.
    public const double PreTightenReversal = 0.40;

    // Target scoring caps.
    public const int MaxTargetCandidates = 6;

    public static FxProfitReport Evaluate(
        FxPositionState st,
        double price,
        IReadOnlyList<FxBar> bars,
        double atr,
        double atrMedian,
        FxRegime regime,
        double prevFloorR = 0,
        FxProfitBrainOptions? options = null)
    {
        options ??= FxProfitBrainOptions.Default;
        var risk = Math.Max(st.RiskPerLot, 1e-9);
        var isBuy = st.Side == "buy";
        var currentR = isBuy ? (price - st.EntryPrice) / risk : (st.EntryPrice - price) / risk;
        var peakR = Math.Max(st.MfeR, currentR);

        var momentum = MomentumClass(bars, atr, isBuy);
        var reversalP = ReversalProbability(st, price, bars, atr, atrMedian, regime, peakR, currentR);
        var continuationP = 1.0 - reversalP;

        var givebackR = Math.Max(0, peakR - currentR);
        var givebackPct = peakR > 0.1 ? givebackR / peakR * 100.0 : 0;
        var givebackClass = ClassifyGiveback(givebackPct, reversalP);

        var floorR = ProfitFloor(peakR, currentR, reversalP, prevFloorR, options);
        var floorBreached = floorR > 0 && peakR >= 1.0 && currentR < floorR;

        var targets = TargetLadder(st, price, bars, atr, momentum, regime);
        var (tp1, tp2, tp3) = PickLadder(targets, st, atr);
        var p1 = tp1 is null ? 0 : TargetProbability(tp1, momentum, regime);
        var p2 = tp2 is null ? 0 : TargetProbability(tp2, momentum, regime);
        var p3 = tp3 is null ? 0 : TargetProbability(tp3, momentum, regime);

        var state = ProfitStateFor(peakR, currentR, floorR, floorBreached, givebackClass);
        var action = RecommendedAction(state, momentum, continuationP);
        var allocation = AllocationPlan(continuationP, reversalP);
        var trailing = TrailingMode(regime);

        var score = ProfitScore(tp1 ?? tp2 ?? tp3, continuationP, givebackPct, floorR, momentum, options);

        var vote = TargetTpVote(st, tp2, currentR, peakR, givebackPct);

        return new FxProfitReport(
            currentR, peakR, givebackR, givebackPct, givebackClass,
            state, floorR, floorBreached, action,
            continuationP, reversalP, momentum, regime.ToString(),
            tp1, tp2, tp3, p1, p2, p3,
            allocation, trailing, score, vote);
    }

    // ── targets ──────────────────────────────────────────────────────────

    /// <summary>Candidate destinations on the profit side, scored and
    /// ranked. Deterministic: fractal swings over the window, equal
    /// highs/lows (two swings within 0.15 ATR), session extremes, previous
    /// UTC day's extremes, and ATR projections as the floor of the ladder.</summary>
    public static IReadOnlyList<FxProfitTarget> TargetLadder(
        FxPositionState st, double price, IReadOnlyList<FxBar> bars,
        double atr, string momentum, FxRegime regime)
    {
        var risk = Math.Max(st.RiskPerLot, 1e-9);
        var isBuy = st.Side == "buy";
        var outp = new List<FxProfitTarget>();
        if (bars.Count < 10 || !double.IsFinite(atr) || atr <= 0)
        {
            return outp;
        }

        var window = bars.Skip(Math.Max(0, bars.Count - 120)).ToList();
        var last = window[^1];

        void Add(string kind, double targetPrice)
        {
            if (!double.IsFinite(targetPrice) || targetPrice <= 0) return;
            var ahead = isBuy ? targetPrice > price : targetPrice < price;
            if (!ahead) return;   // only destinations still in front of us
            var r = (isBuy ? targetPrice - st.EntryPrice : st.EntryPrice - targetPrice) / risk;
            if (r <= 0.15) return;
            outp.Add(new FxProfitTarget(kind, targetPrice, r, Math.Abs(targetPrice - price) / atr, 0));
        }

        // Fractal swings over the window (k = 2 bars each side).
        var swingHighs = new List<double>();
        var swingLows = new List<double>();
        for (var i = 2; i < window.Count - 2; i++)
        {
            if (window[i].High >= window[i - 1].High && window[i].High >= window[i - 2].High
                && window[i].High >= window[i + 1].High && window[i].High >= window[i + 2].High)
            {
                swingHighs.Add(window[i].High);
            }
            if (window[i].Low <= window[i - 1].Low && window[i].Low <= window[i - 2].Low
                && window[i].Low <= window[i + 1].Low && window[i].Low <= window[i + 2].Low)
            {
                swingLows.Add(window[i].Low);
            }
        }

        foreach (var h in swingHighs.TakeLast(4)) Add("swing-high", h);
        foreach (var l in swingLows.TakeLast(4)) Add("swing-low", l);

        // Equal highs/lows: two+ swings within 0.15 ATR — a liquidity pool.
        var tol = 0.15 * atr;
        foreach (var h in swingHighs.OrderByDescending(x => x).ToList())
        {
            if (swingHighs.Count(x => Math.Abs(x - h) <= tol) >= 2) { Add("equal-highs", h); break; }
        }
        foreach (var l in swingLows.OrderBy(x => x).ToList())
        {
            if (swingLows.Count(x => Math.Abs(x - l) <= tol) >= 2) { Add("equal-lows", l); break; }
        }

        // Session extremes (the 120-bar window ≈ the recent session on M1).
        Add("session-high", window.Max(b => b.High));
        Add("session-low", window.Min(b => b.Low));

        // Previous UTC day's extremes from the window's timestamps.
        DateTimeOffset.FromUnixTimeSeconds(window[^1].Time)
            .UtcHandles(out var prevDayStart, out var prevDayEnd);
        var prevDay = window.Where(b =>
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(b.Time);
            return t >= prevDayStart && t < prevDayEnd;
        }).ToList();
        if (prevDay.Count >= 30)
        {
            Add("prev-day-high", prevDay.Max(b => b.High));
            Add("prev-day-low", prevDay.Min(b => b.Low));
        }

        // ATR projections keep the ladder non-empty in clean trends with
        // no visible structure ahead: 1R beyond the recent range per 2 ATR.
        Add("atr-projection", isBuy ? price + 2 * atr : price - 2 * atr);
        Add("atr-projection", isBuy ? price + 4 * atr : price - 4 * atr);

        // Records are immutable: rebuild with scores attached.
        var scored = new List<FxProfitTarget>(outp.Count);
        foreach (var t in outp)
        {
            scored.Add(t with { Score = ScoreTarget(t, swingHighs, swingLows, momentum, regime, tol) });
        }

        return scored
            .GroupBy(t => Math.Round(t.Price, 6))   // same price = same destination
            .Select(g => g.OrderByDescending(t => t.Score).First())
            .OrderByDescending(t => t.Score)
            .Take(MaxTargetCandidates)
            .ToList();
    }

    private static double ScoreTarget(
        FxProfitTarget t, List<double> swingHighs, List<double> swingLows,
        string momentum, FxRegime regime, double tol)
    {
        double s = 20;   // base: it is a real destination
        // Liquidity weight.
        s += t.Kind switch
        {
            "equal-highs" or "equal-lows" => 30,   // stop pools: strongest draw
            "prev-day-high" or "prev-day-low" => 22,
            "session-high" or "session-low" => 18,
            "swing-high" or "swing-low" => 12,
            _ => 4,                                 // atr-projection: no structure
        };
        // Confluence: another swing near the same price.
        var near = swingHighs.Concat(swingLows).Count(x => Math.Abs(x - t.Price) <= tol);
        if (near >= 2) s += 10;
        // Momentum alignment.
        s += momentum switch { "strong" => 12, "stable" => 4, _ => -12 };
        // Regime fit: ranges reject far targets; trends accept them.
        if (regime == FxRegime.Range && t.R > 2.0) s -= 15;
        if (regime == FxRegime.Trend && t.R > 2.0) s += 6;
        // Distance penalty: beyond ~4 ATR the path risk dominates.
        s -= Math.Max(0, (t.DistanceInAtr - 4.0)) * 6.0;
        return Math.Clamp(s, 0, 100);
    }

    /// <summary>TP1 = best nearby high-probability target, TP2 = best EV
    /// major target beyond TP1, TP3 = best extension beyond TP2.</summary>
    private static (FxProfitTarget?, FxProfitTarget?, FxProfitTarget?) PickLadder(
        IReadOnlyList<FxProfitTarget> targets, FxPositionState st, double atr)
    {
        if (targets.Count == 0) return (null, null, null);
        var ordered = targets.OrderBy(t => t.R).ToList();
        var tp1 = ordered.First();
        var tp2 = ordered.Where(t => t.R > tp1.R * 1.25 + 1e-9)
            .OrderByDescending(t => t.Score).FirstOrDefault();
        var tp3 = ordered.Where(t => tp2 is null || t.R > tp2.R * 1.2 + 1e-9)
            .OrderByDescending(t => t.Score).FirstOrDefault();
        return (tp1, tp2, tp3);
    }

    // ── probability + EV ─────────────────────────────────────────────────

    /// <summary>Rule-based, calibrated-bounds probability of reaching a
    /// target before reversing: decays with ATR-distance, adjusted by
    /// momentum class and regime. Always inside [0.05, 0.95].</summary>
    public static double TargetProbability(FxProfitTarget t, string momentum, FxRegime regime)
    {
        var p = 0.92 - 0.13 * t.DistanceInAtr;
        p += momentum switch { "strong" => 0.06, "weakening" => -0.10, _ => 0 };
        p += regime switch
        {
            FxRegime.Trend => 0.05,
            FxRegime.Range => -0.10,
            FxRegime.HighVol => -0.05,
            _ => 0,
        };
        return Math.Clamp(p, 0.05, 0.95);
    }

    /// <summary>Expected value of a target: probability × R with a giveback
    /// haircut for the path length (longer paths bleed more giveback).</summary>
    public static double TargetEv(FxProfitTarget t, double probability) =>
        probability * t.R * Math.Max(0.5, 1.0 - 0.05 * t.DistanceInAtr);

    // ── momentum ─────────────────────────────────────────────────────────

    /// <summary>strong | stable | weakening, from the regression slope of
    /// the last closes measured in ATRs, split by whether the recent slope
    /// is decelerating versus the prior window. In the trade's direction.</summary>
    public static string MomentumClass(IReadOnlyList<FxBar> bars, double atr, bool isBuy)
    {
        if (bars.Count < 16 || !double.IsFinite(atr) || atr <= 0) return "stable";
        var closes = bars.Skip(bars.Count - 15).Select(b => b.Close).ToList();
        var slope = Slope(closes) / atr;                      // ATRs per bar
        var slopePrev = Slope(closes.Take(10).ToList()) / atr;
        var dir = isBuy ? 1 : -1;
        var recent = slope * dir;
        var prior = slopePrev * dir;
        // Acceleration needs a real margin, not regression noise: two
        // mathematically equal slopes differ by float ε, and without the
        // tolerance a perfectly steady tape classifies as "weakening" —
        // which then feeds the reversal detector's heaviest evidence.
        if (recent > 0.05)
        {
            return recent >= prior - 0.01 ? "strong" : "weakening";
        }
        if (recent < -0.03 || (recent < 0.02 && prior > 0.08)) return "weakening";
        return "stable";
    }

    private static double Slope(IReadOnlyList<double> ys)
    {
        var n = ys.Count;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            sx += i; sy += ys[i]; sxy += i * ys[i]; sxx += i * i;
        }
        var denom = n * sxx - sx * sx;
        return denom <= 0 ? 0 : (n * sxy - sx * sy) / denom;
    }

    // ── reversal regime detector (confluence-gated, spec §11) ────────────

    /// <summary>Reversal probability from weighted evidence: momentum
    /// weakening, a failed new extreme (wick beyond the prior swing then a
    /// close back inside), opposite displacement, range regime on an aged
    /// trade, and heavy giveback. Base rate 0.08; each item adds weight;
    /// any single item can never exceed ~0.3 — confluence is the point.</summary>
    public static double ReversalProbability(
        FxPositionState st, double price, IReadOnlyList<FxBar> bars,
        double atr, double atrMedian, FxRegime regime, double peakR, double currentR)
    {
        var isBuy = st.Side == "buy";
        double evidence = 0;

        var momentum = MomentumClass(bars, atr, isBuy);
        if (momentum == "weakening") evidence += 0.25;

        if (bars.Count >= 6)
        {
            var last = bars[^1];
            var prev = bars[^2];
            var failedHigh = isBuy && last.High > prev.High && last.Close < prev.Close && last.Close < last.High - 0.5 * (last.High - last.Low);
            var failedLow = !isBuy && last.Low < prev.Low && last.Close > prev.Close && last.Close > last.Low + 0.5 * (last.High - last.Low);
            if (failedHigh || failedLow) evidence += 0.20;

            var body = Math.Abs(last.Close - last.Open);
            var against = isBuy ? last.Close < last.Open : last.Close > last.Open;
            if (against && body > 1.2 * atr) evidence += 0.20;   // opposite displacement
        }

        if (regime == FxRegime.Range && st.BarsHeld > 20) evidence += 0.15;

        if (peakR > 0.5 && (peakR - currentR) / peakR > 0.4) evidence += 0.20;

        return Math.Clamp(0.08 + evidence, 0.05, 0.90);
    }

    // ── giveback + profit floor ──────────────────────────────────────────

    public static string ClassifyGiveback(double givebackPct, double reversalP)
    {
        var cls = givebackPct switch
        {
            < GivebackNormalPct => "NORMAL",
            < GivebackWarningPct => "WARNING",
            < GivebackDeterioratingPct => "DETERIORATING",
            _ => "CRITICAL",
        };
        // High reversal probability escalates one class — context, not just
        // the raw percentage (spec §15).
        return reversalP > ReversalHighConfidence ? Escalate(cls) : cls;
    }

    private static string Escalate(string cls) => cls switch
    {
        "NORMAL" => "WARNING",
        "WARNING" => "DETERIORATING",
        _ => "CRITICAL",
    };

    /// <summary>The protected-profit floor: schedule-based on peak R,
    /// tightened (never loosened) while reversal probability is high, and
    /// NEVER below the floor already established for this trade.</summary>
    public static double ProfitFloor(
        double peakR, double currentR, double reversalP,
        double prevFloorR, FxProfitBrainOptions options)
    {
        double floor = 0;
        foreach (var (peak, f) in options.FloorSchedule ?? Array.Empty<(double, double)>())
        {
            if (peakR >= peak) floor = f;
        }
        if (floor <= 0) return Math.Max(0, prevFloorR);        // Adaptive tighten: strong reversal evidence protects nearly all of
        // the current profit (but never above the cap of the peak).
        if (reversalP > ReversalHighConfidence && currentR > floor)
        {
            floor = Math.Max(floor, currentR - 0.1);
        }
        // Pre-tighten: MODERATE reversal (0.40–0.55) no longer leaves the
        // schedule floor untouched — giveback plus fading continuation
        // tightens toward the cap without demanding full reversal confluence
        // (the all-or-nothing gap: heavy giveback with only moderate
        // reversal evidence kept yesterday's schedule floor all day).
        else if (reversalP >= PreTightenReversal && currentR > floor * 1.25)
        {
            floor = Math.Max(floor, currentR * 0.75);   // capped below, with every other floor
        }
        floor = Math.Min(floor, peakR * FloorCapOfPeak);

        // The law: established floors never move down.
        return Math.Max(floor, Math.Max(0, prevFloorR));
    }

    // ── state machine (spec §21) ─────────────────────────────────────────

    public static string ProfitStateFor(
        double peakR, double currentR, double floorR, bool floorBreached, string givebackClass)
    {
        if (floorBreached) return "PROFIT_EXIT_READY";
        if (peakR >= 1.0 && currentR <= 0.1) return "PROFIT_DETERIORATING";   // earned, then given back
        if (givebackClass == "CRITICAL") return "PROFIT_CRITICAL";
        if (givebackClass == "DETERIORATING") return "PROFIT_DETERIORATING";
        if (givebackClass == "WARNING") return "PROFIT_GIVEBACK_WARNING";
        if (floorR > 0) return "PROFIT_PROTECTED";
        if (peakR >= 2.0) return "PROFIT_EXTENDED";
        if (currentR > 0.5) return "PROFIT_POSITIVE";   // half a risk-unit earned
        return "PROFIT_FORMING";
    }

    private static string RecommendedAction(string state, string momentum, double continuationP) =>
        state switch
        {
            "PROFIT_EXIT_READY" => "EXIT_REQUEST",
            "PROFIT_CRITICAL" or "PROFIT_DETERIORATING" => "TIGHTEN_PROTECTION",
            "PROFIT_GIVEBACK_WARNING" => "MOVE_TO_PROTECTED",
            "PROFIT_PROTECTED" or "PROFIT_EXTENDED" when momentum == "strong" && continuationP > 0.7 => "HOLD_AND_EXTEND",
            _ => "HOLD",
        };

    // ── allocation + runner (reported, never executed) ───────────────────

    /// <summary>Partial-allocation plan per regime of confidence; always
    /// sums to 100 (spec §18).</summary>
    public static (double Tp1, double Tp2, double Tp3, double Runner) AllocationPlan(
        double continuationP, double reversalP) =>
        (continuationP, reversalP) switch
        {
            var (c, _) when c > 0.80 => (15, 20, 25, 40),      // strong continuation: feed the runner
            var (_, r) when r > ReversalHighConfidence => (40, 35, 15, 10),   // protect: front-load exits
            _ => (25, 30, 20, 25),
        };

    public static string TrailingMode(FxRegime regime) => regime switch
    {
        FxRegime.Trend => "STRUCTURE_TRAIL",
        FxRegime.HighVol => "ATR_TRAIL",
        _ => "HYBRID_STRUCTURE_ATR",
    };

    // ── fusion + shadow vote ─────────────────────────────────────────────

    private static double ProfitScore(
        FxProfitTarget? best, double continuationP, double givebackPct,
        double floorR, string momentum, FxProfitBrainOptions o)
    {
        var targetQ = (best?.Score ?? 30) / 100.0;
        var health = 1.0 - Math.Min(givebackPct, 100) / 100.0;
        var protection = floorR > 0 ? 1.0 : 0.4;
        var mom = momentum switch { "strong" => 1.0, "stable" => 0.6, _ => 0.25 };
        var raw = targetQ * (o.LiquidityWeight + o.StructureWeight)
                  + continuationP * (o.ContinuationWeight + o.RegimeWeight)
                  + health * (o.VolatilityWeight + o.ReversalWeight)
                  + protection * 0.15
                  + mom * (o.MomentumWeight * 0.667);
        return Math.Round(Math.Clamp(raw * 100, 0, 100), 1);
    }

    /// <summary>The target-TP shadow engine's final vote: exit conviction
    /// from what the trade EARNED versus what it gave back — deliberately
    /// independent of the current ladder (a target already reached and
    /// passed is behind price and leaves the ahead-only ladder, but its
    /// lesson — "extract at the target" — lives in the peak/giveback).
    /// Weight is ALWAYS 0 — evidence, never execution.</summary>
    private static FxExitVote TargetTpVote(
        FxPositionState st, FxProfitTarget? tp2,
        double currentR, double peakR, double givebackPct)
    {
        var conv = 0.10;
        var reason = "no meaningful profit yet";
        if (currentR >= 1.0)
        {
            conv = 0.45;
            reason = "in profit — first target zone";
        }
        if (tp2 is { } t2 && peakR >= t2.R)
        {
            conv = 0.70;
            reason = $"peak reached the major target ({t2.R:0.0}R) — extracting beats giving back";
        }
        if (peakR >= 1.0 && givebackPct >= 40)
        {
            conv = 0.80;
            reason = $"{givebackPct:0}% of peak given back — the target said take it";
        }
        return new FxExitVote("target-tp", Math.Clamp(conv, 0, 0.9), 0, reason);
    }
}

/// <summary>Internal helper: split a timestamp into the previous UTC day's
/// boundaries (00:00 of the previous day → 00:00 of today).</summary>
internal static class FxProfitDay
{
    public static DateTimeOffset UtcHandles(this DateTimeOffset t,
        out DateTimeOffset prevStart, out DateTimeOffset prevEnd)
    {
        var todayStart = new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero);
        prevEnd = todayStart;
        prevStart = todayStart.AddDays(-1);
        return t;
    }
}
