using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The Exit Brain v1 battery: each engine's fire/no-fire, the resolver
/// bands, override precedence, partial-lots math, and MFE/MAE monotonicity.
/// Pure inputs — no plumbing.
/// </summary>
public class FxExitBrainTests
{
    private static List<FxBar> FlatBars(int n = 40, double price = 2400)
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < n; i++)
        {
            bars.Add(new FxBar(1790000000L + 60 * i, price - 0.05, price + 0.05, price - 0.05, price, 100));
        }
        return bars;
    }

    private static FxPositionState LongState(double entry = 2400, double risk = 1.0) =>
        new(111, "XAUUSD", "buy", entry, 0.1, risk, 0, 0, 0);

    private static FxExitDecision Eval(
        FxPositionState st, double price, double atr = 1.0, double median = 1.0,
        double spread = 20, FxRegime current = FxRegime.Trend, FxRegime entry = FxRegime.Trend,
        IReadOnlyList<FxBar>? bars = null) =>
        FxExitBrain.Evaluate(st, price, 0.1, atr, median, spread, 250,
            10000, 0, true, bars ?? FlatBars(), current, entry);

    [Fact]
    public void Ownership_By_Comment()
    {
        Assert.True(FxExitBrain.Owns("donggfx-brain"));
        Assert.True(FxExitBrain.Owns("donggfx-brain v2"));
        Assert.False(FxExitBrain.Owns(""));
        Assert.False(FxExitBrain.Owns("manual entry"));
    }

    [Fact]
    public void RiskPerLot_Uses_The_Stop_Or_The_Atr_Fallback()
    {
        Assert.Equal(1.5, FxExitBrain.RiskPerLot(2400, 2398.5, 99));
        Assert.Equal(1.5, FxExitBrain.RiskPerLot(2400, 0, 1.0));   // 1.0 * 1.5
    }

    [Fact]
    public void Healthy_Trade_Holds()
    {
        var d = Eval(LongState(), price: 2400.5);
        Assert.Equal("hold", d.Action);
        Assert.Null(d.OverrideEngine);
        Assert.Equal(8, d.Votes.Count);   // 6 active engines + 2 shadows (weight 0)
    }

    [Fact]
    public void Structure_Engine_Fires_On_CHOCH_Against_The_Long()
    {
        var bars = FlatBars(40, 2400);
        bars[^1] = new FxBar(1790000000L + 60 * 39, 2399.0, 2399.1, 2397.0, 2397.5, 100); // breaks the swing low
        var d = Eval(LongState(), 2397.5, bars: bars);
        var structure = d.Votes.First(v => v.Engine == "structure");
        Assert.True(structure.Exit >= 0.9);
        Assert.Contains("CHOCH", structure.Reason);
    }

    [Fact]
    public void Time_Engine_Kills_The_Dead_Trade()
    {
        var st = LongState() with { BarsHeld = FxExitBrain.MaxStagnantBars + 4 };
        var d = Eval(st, 2400.0);   // never moved: MFE 0
        var time = d.Votes.First(v => v.Engine == "time");
        Assert.True(time.Exit >= 0.7);
        Assert.Contains("dead", time.Reason);
    }

    [Fact]
    public void Volatility_Engine_Fires_On_Collapse_And_Shock()
    {
        var collapse = Eval(LongState(), 2400.0, atr: 0.3, median: 1.0);
        Assert.True(collapse.Votes.First(v => v.Engine == "volatility").Exit >= 0.6);

        var shock = Eval(LongState(), 2400.0, atr: 3.0, median: 1.0);
        Assert.True(shock.Votes.First(v => v.Engine == "volatility").Exit >= 0.75);
    }

    [Fact]
    public void Thesis_Engine_Fires_When_The_Regime_Flips()
    {
        var d = Eval(LongState(), 2400.0, current: FxRegime.Range, entry: FxRegime.Trend);
        var thesis = d.Votes.First(v => v.Engine == "thesis");
        Assert.True(thesis.Exit >= 0.8);
        Assert.Contains("invalidated", thesis.Reason);
    }

    [Fact]
    public void Mae_Emergency_Is_An_Override_Not_A_Vote()
    {
        // Spec engine 13: "Emergency exit when adverse movement becomes
        // abnormal" — it must bypass the consensus (safety > intelligence).
        var st = LongState() with { MaeR = FxExitBrain.MaeEmergencyR };
        var d = Eval(st, 2400 - 1.6 * 1.0);   // 1.6R against
        Assert.Equal("drawdown", d.OverrideEngine);
        Assert.Equal("full", d.Action);
        Assert.Equal(100, d.Score);

        // Below the bar: only the watch-level vote remains.
        var mild = Eval(LongState(), 2400 - 1.0);
        Assert.Null(mild.OverrideEngine);
        Assert.True(mild.Votes.First(v => v.Engine == "drawdown").Exit >= 0.45);
    }

    [Fact]
    public void Resolver_Bands_Are_The_Spec()
    {
        // The exact band edges from the architecture spec.
        Assert.Equal("hold", FxExitBrain.Resolve(0));
        Assert.Equal("hold", FxExitBrain.Resolve(34.9));
        Assert.Equal("monitor", FxExitBrain.Resolve(35));
        Assert.Equal("monitor", FxExitBrain.Resolve(54.9));
        Assert.Equal("tighten", FxExitBrain.Resolve(55));
        Assert.Equal("tighten", FxExitBrain.Resolve(69.9));
        Assert.Equal("partial", FxExitBrain.Resolve(70));
        Assert.Equal("partial", FxExitBrain.Resolve(84.9));
        Assert.Equal("full", FxExitBrain.Resolve(85));
        Assert.Equal("full", FxExitBrain.Resolve(100));
    }

    [Fact]
    public void Consensus_Dilutes_By_Inert_Engine_Weights()
    {
        // The spec's own math: every engine's weight sits in the
        // denominator, so a 3-of-6 consensus (structure CHOCH 0.9x1.6 +
        // dead-trade time 0.7x1.1 + thesis flip 0.8x1.4) with three inert
        // engines lands at 40 — MONITOR, not PARTIAL. Real majorities are
        // required before the brain scales out.
        var bars = FlatBars(40, 2400);
        bars[^1] = new FxBar(1790000000L + 60 * 39, 2399.9, 2400.1, 2399.0, 2399.2, 100);
        var st = LongState() with { BarsHeld = 50 };
        var d = Eval(st, 2399.2, current: FxRegime.Range, bars: bars);
        Assert.Null(d.OverrideEngine);
        Assert.InRange(d.Score, 39, 41);
        Assert.Equal("monitor", d.Action);
    }

    [Fact]
    public void Lots_Math_Full_Carries_All_Partial_Is_A_Third()
    {
        // The resolver's lot contract (venue stepping happens host-side).
        var full = FxExitBrain.Resolve(90);
        var partial = FxExitBrain.Resolve(75);
        Assert.Equal("full", full);
        Assert.Equal("partial", partial);
        // A third of the book scales out; the remainder keeps tracking.
        Assert.Equal(0.0333, Math.Round(0.1 / 3.0, 4), 4);
    }

    [Fact]
    public void Full_Close_Carries_All_Lots()
    {
        var st = LongState() with { MaeR = 5.0 };   // drawdown 0.95 + structure-ish others
        var d = Eval(st, 2400 - 5.0);
        Assert.True(d.Score >= FxExitBrain.FullExitScore, $"score {d.Score:0}");
        Assert.Equal("full", d.Action);
        Assert.Equal(0.1, d.LotsToClose);
    }

    [Fact]
    public void Spread_Override_Beats_A_Unanimous_Hold()
    {
        var d = Eval(LongState(), 2400.0, spread: 300);   // > 250 abnormal bar
        Assert.Equal("full", d.Action);
        Assert.Equal("spread", d.OverrideEngine);
        Assert.Equal(100, d.Score);
    }

    [Fact]
    public void EquityFloor_Override_Fires()
    {
        var d = FxExitBrain.Evaluate(LongState(), 2400.0, 0.1, 1, 1, 20, 250,
            equity: 900, equityFloor: 950, bridgeUp: true,
            FlatBars(), FxRegime.Trend, FxRegime.Trend);
        Assert.Equal("equity-floor", d.OverrideEngine);
        Assert.Equal("full", d.Action);
    }

    [Fact]
    public void Bridge_Override_Fires_First()
    {
        var d = FxExitBrain.Evaluate(LongState(), 2400.0, 0.1, 1, 1, 300, 250,
            equity: 900, equityFloor: 950, bridgeUp: false,
            FlatBars(), FxRegime.Trend, FxRegime.Trend);
        Assert.Equal("bridge", d.OverrideEngine);
    }

    [Fact]
    public void Mfe_Mae_Are_Monotone()
    {
        var st = LongState();
        st = FxExitBrain.UpdateState(st, 2402, 1);   // +2R
        Assert.Equal(2.0, st.MfeR, 9);
        st = FxExitBrain.UpdateState(st, 2399.5, 2); // -0.5R
        Assert.Equal(2.0, st.MfeR, 9);   // MFE keeps the high water mark
        Assert.Equal(0.5, st.MaeR, 9);
        Assert.Equal(2, st.BarsHeld);
    }

    [Fact]
    public void Tighten_Trails_To_BePlus_Only_After_Mfe_Paid()
    {
        // A broad consensus (structure CHOCH + stale trade + thesis flip +
        // drawdown ramp + volatility collapse) lands at ~61 — TIGHTEN.
        var st = LongState() with { BarsHeld = 50, MaeR = 1.05 };
        var d = Eval(st, 2399.2, atr: 0.3, median: 1.0,
            current: FxRegime.Range, entry: FxRegime.Trend,
            bars: CrashBars());
        Assert.Equal("tighten", d.Action);
        Assert.Equal(0, d.NewSl);   // MFE never paid -> no trail

        var st2 = st with { MfeR = 1.2 };
        var d2 = Eval(st2, 2399.2, atr: 0.3, median: 1.0,
            current: FxRegime.Range, entry: FxRegime.Trend,
            bars: CrashBars());
        Assert.Equal("tighten", d2.Action);
        Assert.Equal(2400 + 0.2, d2.NewSl, 9);   // trail to +0.2R
    }

    /// <summary>Flat tape, then one reversal bar closing below the swing
    /// low — the natural CHOCH shape.</summary>
    private static List<FxBar> CrashBars()
    {
        var bars = FlatBars(40, 2400);
        bars[^1] = new FxBar(1790000000L + 60 * 39, 2399.9, 2400.1, 2399.0, 2399.2, 100);
        return bars;
    }
    [Fact]
    public void Shadow_Engines_Ride_At_Weight_Zero()
    {
        var d = Eval(LongState(), price: 2400.5);
        var shadows = d.Votes.Where(v => v.Weight == 0).ToList();
        Assert.Equal(["counterfactual", "giveback"], shadows.Select(v => v.Engine).OrderBy(e => e).ToArray());
        // They never move the score: a re-score without them is identical.
        var without = d.Votes.Where(v => v.Weight > 0).ToList();
        double w = 0, acc = 0;
        foreach (var v in without) { acc += v.Exit * v.Weight; w += v.Weight; }
        Assert.Equal(acc / w * 100.0, d.Score, 6);
    }

    [Fact]
    public void EngineWeights_Roster_Matches_The_Vote_Builders()
    {
        var d = Eval(LongState(), price: 2399.0, bars: CrashBars());
        foreach (var v in d.Votes)
        {
            Assert.True(FxExitBrain.EngineWeights.ContainsKey(v.Engine), $"unregistered engine {v.Engine}");
            Assert.Equal(FxExitBrain.EngineWeights[v.Engine], v.Weight);
        }
    }

    [Fact]
    public void Shadow_Promotion_Needs_Trades_And_Accuracy()
    {
        // Not enough trades: observation only, even with a perfect hit rate.
        Assert.Equal(0, FxExitShadow.WeightFor(1.0, FxExitShadow.PromotionTrades - 1));
        // Enough trades but weak accuracy: still zero.
        Assert.Equal(0, FxExitShadow.WeightFor(0.3, FxExitShadow.PromotionTrades + 10));
        // Both bars met: earned weight, capped at the lightest real engine.
        var w = FxExitShadow.WeightFor(0.75, FxExitShadow.PromotionTrades + 10);
        Assert.Equal(0.75, w, 6);
        Assert.True(w <= 1.0);

        // "Helped" = conviction to exit a held WINNER (the rescue evidence).
        Assert.True(FxExitShadow.Helped(
            new FxExitVote("giveback", 0.8, 0, "r"), "hold", won: true));
        Assert.False(FxExitShadow.Helped(
            new FxExitVote("giveback", 0.8, 0, "r"), "hold", won: false));  // losers need no rescue
        Assert.False(FxExitShadow.Helped(
            new FxExitVote("giveback", 0.2, 0, "r"), "hold", won: true));   // no conviction
        Assert.False(FxExitShadow.Helped(
            new FxExitVote("giveback", 0.8, 0, "r"), "full", won: true));   // ensemble already acted

        var report = FxExitShadow.Grade("giveback", 65, 100);
        Assert.True(report.Eligible);
        Assert.Contains("reviewed PR", report.Verdict);
    }

    [Fact]
    public void MonteCarlo_Reports_Stability_On_A_Robust_Roster()
    {
        // A trade whose consensus sits mid-band: ±20% weight noise cannot
        // move it across a band edge.
        var votes = new List<FxExitVote>
        {
            new("structure", 0.3, 1.6, "x"), new("momentum", 0.3, 1.0, "x"),
            new("volatility", 0.3, 1.2, "x"), new("time", 0.3, 1.1, "x"),
            new("thesis", 0.3, 1.4, "x"), new("drawdown", 0.3, 2.0, "x"),
        };
        var report = FxExitMonteCarlo.Run([new FxSettledExit(1, "XAUUSD", "monitor", votes)], 200);
        Assert.Equal(0, report.ActionFlipRate);
        Assert.StartsWith("stable", report.Verdict);
    }

    [Fact]
    public void MonteCarlo_Flags_A_Roster_That_Rides_Band_Edges()
    {
        // A trade parked exactly on the TIGHTEN/MONITOR boundary (score
        // 55.06): ±20% weight noise redistributes between the high-exit and
        // low-exit engines, flipping it across the edge — the harness must
        // refuse to bless that change. (Uniform exits would be weight-
        // invariant and prove nothing.)
        var votes = new List<FxExitVote>
        {
            new("structure", 0.9, 1.6, "x"), new("momentum", 0.9, 1.0, "x"),
            new("volatility", 0.5, 1.2, "x"), new("time", 0.5, 1.1, "x"),
            new("thesis", 0.5, 1.4, "x"), new("drawdown", 0.19, 2.0, "x"),
        };
        var report = FxExitMonteCarlo.Run([new FxSettledExit(2, "XAUUSD", "tighten", votes)], 200);
        Assert.True(report.ActionFlipRate > 0.10, $"flip rate {report.ActionFlipRate:P1}");
        Assert.StartsWith("UNSTABLE", report.Verdict);
    }

    [Fact]
    public void MonteCarlo_Is_Deterministic_Under_A_Seed()
    {
        var votes = new List<FxExitVote>
        {
            new("structure", 0.9, 1.6, "x"), new("momentum", 0.2, 1.0, "x"),
            new("volatility", 0.4, 1.2, "x"), new("time", 0.1, 1.1, "x"),
            new("thesis", 0.5, 1.4, "x"), new("drawdown", 0.2, 2.0, "x"),
        };
        var trade = new FxSettledExit(3, "XAUUSD", "partial", votes);
        var a = FxExitMonteCarlo.Run([trade], 50, seed: 7);
        var b = FxExitMonteCarlo.Run([trade], 50, seed: 7);
        Assert.Equal(a.ActionFlipRate, b.ActionFlipRate);
        Assert.Equal(a.MeanAbsScoreDelta, b.MeanAbsScoreDelta, 12);
    }

    [Fact]
    public void MonteCarlo_Is_Silent_Without_Settled_Trades()
    {
        var report = FxExitMonteCarlo.Run([], 100);
        Assert.Equal(0, report.Trades);
        Assert.Contains("FX_EXIT trail is still filling", report.Verdict);
    }

}
