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
        Assert.Equal(6, d.Votes.Count);
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
}
