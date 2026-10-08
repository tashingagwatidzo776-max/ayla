using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The HWARANG Profit Brain's load-bearing laws: the profit floor never
/// moves down, EV — not probability — picks the preferred target,
/// probabilities stay calibrated, giveback classification escalates with
/// reversal evidence, the state machine protects before it exits, and the
/// target-TP shadow vote rides at weight 0. All fixtures are synthetic
/// micro-liquidity bars (the same builder trick that exposed the dead-tape
/// regime floor — ±0.6 wicks on a price of 1.08 read as a huge relative
/// ATR, so builders must scale wicks to the price under test).
/// </summary>
[Trait("Category", "Unit")]
public class FxProfitBrainTests
{
    /// <summary>M1 bars around a price with sub-ATR wicks: open-to-close
    /// drift controls the momentum slope deterministically.</summary>
    private static List<FxBar> Bars(int count, double price, double drift, double wick)
    {
        var bars = new List<FxBar>(count);
        for (var i = 0; i < count; i++)
        {
            var c = price + drift * i;
            bars.Add(new FxBar(
                1_790_000_000 + 60L * i,
                c - drift, c + wick, c - wick, c, 100));
        }
        return bars;
    }

    private static FxPositionState State(string side, double entry, double riskPerLot, double mfeR = 0, double maeR = 0, int barsHeld = 5) =>
        new(111, "XAUUSDmicro", side, entry, 0.1, riskPerLot, mfeR, maeR, barsHeld);

    // ── targets ──────────────────────────────────────────────────────────

    [Fact]
    public void TargetLadder_Finds_Swing_And_Session_Destinations_In_Front_Of_Price()
    {
        // A long at 100.0 with swings at 101/102 ahead.
        var bars = Bars(60, 100.0, drift: 0.02, wick: 0.15);
        bars.Add(new FxBar(1_790_003_600, 101.3, 102.0, 101.0, 101.5, 100));   // swing high 102
        var st = State("buy", 100.0, riskPerLot: 1.0);
        var ladder = FxProfitBrain.TargetLadder(st, 100.5, bars, atr: 0.5, "stable", FxRegime.Trend);

        Assert.NotEmpty(ladder);
        Assert.All(ladder, t => Assert.True(t.R > 0, "profit-side destinations only"));
        Assert.All(ladder, t => Assert.InRange(t.Score, 0, 100));
        // Destinations ahead of 100.5 must exist and be ordered by score.
        Assert.True(ladder[0].Score >= ladder[^1].Score);
    }

    [Fact]
    public void TargetLadder_Never_Suggests_Destinations_Behind_The_Trade()
    {
        var bars = Bars(60, 100.0, drift: -0.01, wick: 0.1);   // drifting down
        var st = State("buy", 100.0, riskPerLot: 1.0);
        var ladder = FxProfitBrain.TargetLadder(st, 100.0, bars, atr: 0.4, "weakening", FxRegime.Range);

        // Everything ahead is still > entry; nothing may sit at/below entry.
        Assert.All(ladder, t => Assert.True(t.Price > 100.0));
    }

    // ── probability + EV ─────────────────────────────────────────────────

    [Fact]
    public void TargetProbability_Decays_With_Distance_And_Stays_Calibrated()
    {
        var near = new FxProfitTarget("swing-high", 101, 1.0, 1.0, 60);
        var far = new FxProfitTarget("atr-projection", 105, 5.0, 5.0, 40);

        var pNear = FxProfitBrain.TargetProbability(near, "stable", FxRegime.Trend);
        var pFar = FxProfitBrain.TargetProbability(far, "stable", FxRegime.Trend);
        Assert.True(pNear > pFar, "closer targets are more probable");
        Assert.InRange(pNear, 0.05, 0.95);
        Assert.InRange(pFar, 0.05, 0.95);
        // Momentum and regime adjust, never break calibration.
        var strong = FxProfitBrain.TargetProbability(near, "strong", FxRegime.Trend);
        Assert.True(strong >= pNear);
        var rangeP = FxProfitBrain.TargetProbability(near, "stable", FxRegime.Range);
        Assert.True(rangeP <= pNear);
    }

    [Fact]
    public void Ev_Ranking_Can_Prefer_A_Farther_Target_Like_The_Spec_Example()
    {
        // Spec §13: 84% × 1.7R vs 63% × 2.8R vs 37% × 4.3R → TP2 wins on EV.
        var tp1 = new FxProfitTarget("swing-high", 0, 1.7, 1.7, 80);
        var tp2 = new FxProfitTarget("prev-day-high", 0, 2.8, 2.8, 70);
        var tp3 = new FxProfitTarget("equal-highs", 0, 4.3, 4.3, 60);
        var p1 = FxProfitBrain.TargetProbability(tp1, "stable", FxRegime.Trend);
        var p2 = FxProfitBrain.TargetProbability(tp2, "stable", FxRegime.Trend);
        var p3 = FxProfitBrain.TargetProbability(tp3, "stable", FxRegime.Trend);

        var ev1 = FxProfitBrain.TargetEv(tp1, p1);
        var ev2 = FxProfitBrain.TargetEv(tp2, p2);
        var ev3 = FxProfitBrain.TargetEv(tp3, p3);

        // Distances 1.7/2.8/4.3 ATR make the probability decay real; the
        // middle target must beat BOTH neighbors — the spec's exact
        // "don't pick targets by probability alone" lesson.
        Assert.True(ev2 > ev3, $"EV of the middle target beats the far one: {ev2:0.000} vs {ev3:0.000}");
        Assert.True(ev2 > ev1, $"EV of the middle target beats the near one: {ev2:0.000} vs {ev1:0.000}");
    }

    // ── giveback ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, "NORMAL")]
    [InlineData(29.0, "NORMAL")]
    [InlineData(45.0, "WARNING")]
    [InlineData(65.0, "DETERIORATING")]
    [InlineData(85.0, "CRITICAL")]
    public void Giveback_Classification_Follows_The_Bands(double pct, string expected)
    {
        Assert.Equal(expected, FxProfitBrain.ClassifyGiveback(pct, reversalP: 0.1));
    }

    [Fact]
    public void High_Reversal_Probability_Escalates_The_Giveback_Class()
    {
        Assert.Equal("WARNING", FxProfitBrain.ClassifyGiveback(10, reversalP: 0.8));
        Assert.Equal("CRITICAL", FxProfitBrain.ClassifyGiveback(75, reversalP: 0.8));
    }

    // ── the profit floor: the core HWARANG law ───────────────────────────

    [Theory]
    [InlineData(1.0, 0.3)]
    [InlineData(2.0, 1.0)]
    [InlineData(3.5, 2.0)]
    [InlineData(4.0, 3.0)]
    [InlineData(5.7, 4.0)]
    public void Floor_Schedule_Protects_More_As_Peak_Grows(double peak, double expectedFloor)
    {
        var floor = FxProfitBrain.ProfitFloor(peak, currentR: peak, reversalP: 0.1, prevFloorR: 0,
            FxProfitBrainOptions.Default);
        Assert.Equal(expectedFloor, floor, 6);
    }

    [Fact]
    public void Floor_Never_Moves_Down_The_Core_Law()
    {
        // Established at peak 3R (floor 2R); the trade retraces to peak 2.2R
        // of *history* — wait, peak is monotonic in reality; the real test:
        // a new evaluation with the SAME peak must not lower the floor even
        // as reversal evidence collapses.
        var schedule = FxProfitBrainOptions.Default;
        var first = FxProfitBrain.ProfitFloor(3.0, 2.4, reversalP: 0.6, prevFloorR: 0, schedule);
        // Schedule says 2.0; strong reversal evidence tightens to 2.3.
        Assert.Equal(2.3, first, 6);
        var later = FxProfitBrain.ProfitFloor(3.0, 2.2, reversalP: 0.1, prevFloorR: first, schedule);
        Assert.True(later >= first - 1e-9, "established floors never move down");
        Assert.Equal(2.3, later, 6);
    }

    [Fact]
    public void Floor_Never_Exceeds_A_Cap_Of_The_Peak()
    {
        // Adaptive tighten must not set a floor at the peak itself — the
        // first tick of normal retracement would trigger exit-ready.
        var floor = FxProfitBrain.ProfitFloor(4.0, currentR: 3.95, reversalP: 0.9,
            prevFloorR: 3.0, FxProfitBrainOptions.Default);
        Assert.True(floor <= 4.0 * FxProfitBrain.FloorCapOfPeak + 1e-9);
    }

    [Fact]
    public void Floor_Is_Zero_Below_The_Activation_Threshold()
    {
        Assert.Equal(0, FxProfitBrain.ProfitFloor(0.8, 0.8, 0.1, 0, FxProfitBrainOptions.Default));
    }

    [Fact]
    public void Floor_Adaptive_Tighten_Reachable_Below_The_First_Schedule_Rung()
    {
        // The 2026-10-08 giveback gap: peaks under the first rung (1.0R)
        // used to early-return with floor 0 no matter how strong the
        // reversal confluence. High-confidence evidence (> 0.55) must now
        // open a floor at current - 0.1, still capped by the peak.
        var floor = FxProfitBrain.ProfitFloor(0.8, currentR: 0.7, reversalP: 0.60,
            prevFloorR: 0, FxProfitBrainOptions.Default);
        Assert.Equal(0.6, floor, 6);
        Assert.True(floor <= 0.8 * FxProfitBrain.FloorCapOfPeak + 1e-9,
            "the peak cap still binds below the rung");
    }

    [Fact]
    public void Floor_Below_The_First_Rung_Moderate_Reversal_Stays_Zero()
    {
        // Pre-tighten stays schedule-only: below the rung, moderate
        // (0.40-0.55) evidence must not lock a noise floor on a sliver of
        // profit — its `currentR > floor * 1.25` guard degenerates at 0.
        Assert.Equal(0, FxProfitBrain.ProfitFloor(0.8, currentR: 0.7, reversalP: 0.45,
            prevFloorR: 0, FxProfitBrainOptions.Default));
        // High confidence with a profit sliver smaller than 0.1R cannot
        // produce a floor either (current - 0.1 <= 0).
        Assert.Equal(0, FxProfitBrain.ProfitFloor(0.8, currentR: 0.05, reversalP: 0.60,
            prevFloorR: 0, FxProfitBrainOptions.Default));
    }

    [Fact]
    public void Floor_Below_The_First_Rung_Never_Floors_A_Losing_Position_And_Keeps_The_Ratchet()
    {
        // No floor may be minted out of a losing position, ever.
        Assert.Equal(0, FxProfitBrain.ProfitFloor(0.8, currentR: -0.5, reversalP: 0.9,
            prevFloorR: 0, FxProfitBrainOptions.Default));
        // An established 0.7R floor survives a later sub-rung evaluation
        // whose tighten would compute less (0.5R): never-down still holds.
        var kept = FxProfitBrain.ProfitFloor(0.8, currentR: 0.6, reversalP: 0.6,
            prevFloorR: 0.7, FxProfitBrainOptions.Default);
        Assert.Equal(0.7, kept, 6);
    }

    [Fact]
    public void Floor_PreTightens_On_Moderate_Reversal_While_Profit_Is_Near_Peak()
    {
        // Peak 8R, schedule floor 4R; reversal 0.45 (moderate, below the
        // 0.55 high-confidence bar) with profit still near the peak: the
        // schedule floor must no longer stand untouched — the pre-tighten
        // lifts it toward the profit (75% of current, capped at the peak).
        var floor = FxProfitBrain.ProfitFloor(8.0, currentR: 7.6, reversalP: 0.45,
            prevFloorR: 0, FxProfitBrainOptions.Default);
        Assert.Equal(7.6 * 0.75, floor, 6);   // 5.7R, above the 4.0R schedule

        // Still capped at 0.9x the peak.
        Assert.True(floor <= 8.0 * FxProfitBrain.FloorCapOfPeak + 1e-9);
    }

    [Fact]
    public void Floor_PreTighten_Leaves_Healthy_Trades_And_Low_Profit_Alone()
    {
        // Low reversal evidence: no pre-tighten — the schedule floor stands.
        var calm = FxProfitBrain.ProfitFloor(8.0, currentR: 7.6, reversalP: 0.2,
            prevFloorR: 0, FxProfitBrainOptions.Default);
        Assert.Equal(4.0, calm, 6);

        // Moderate reversal but profit NOT near peak (in the giveback
        // itself): pre-tighten does not lock in the drawdown.
        var deep = FxProfitBrain.ProfitFloor(8.0, currentR: 3.0, reversalP: 0.45,
            prevFloorR: 0, FxProfitBrainOptions.Default);
        Assert.Equal(4.0, deep, 6);
    }

    [Fact]
    public void Floor_PreTighten_Never_Lowers_An_Established_Floor()
    {
        // An established 5.7R floor survives a later calm evaluation.
        var established = FxProfitBrain.ProfitFloor(8.0, 7.6, 0.45, 0, FxProfitBrainOptions.Default);
        var later = FxProfitBrain.ProfitFloor(8.0, 7.2, 0.15, established, FxProfitBrainOptions.Default);
        Assert.True(later >= established - 1e-9);
    }

    // ── reversal detector: confluence, never one candle ──────────────────

    [Fact]
    public void Reversal_Probability_Is_Confluence_Gated()
    {
        var st = State("buy", 100.0, 1.0, barsHeld: 30);
        var calm = Bars(40, 100.0, drift: 0.02, wick: 0.1);
        var pCalm = FxProfitBrain.ReversalProbability(st, 100.8, calm, 0.4, 0.4, FxRegime.Trend, peakR: 2.0, currentR: 1.9);

        // One bearish candle alone must NOT produce a high reversal.
        var oneCandle = Bars(40, 100.0, drift: 0.02, wick: 0.1);
        oneCandle[^1] = oneCandle[^1] with { Open = 100.9, Close = 100.5, High = 101.1, Low = 100.4 };
        var pOne = FxProfitBrain.ReversalProbability(st, 100.6, oneCandle, 0.4, 0.4, FxRegime.Trend, peakR: 2.0, currentR: 1.6);

        Assert.True(pCalm < 0.3, $"calm baseline must stay low, was {pCalm:0.00}");
        Assert.True(pOne < FxProfitBrain.ReversalHighConfidence,
            $"a single candle can never trigger the reversal regime, was {pOne:0.00}");

        // Full confluence: weakening drift + failed high + displacement + range regime + heavy giveback.
        var heavy = Bars(40, 100.0, drift: -0.005, wick: 0.1);
        heavy[^1] = heavy[^1] with { Open = 100.6, Close = 99.9, High = 100.9, Low = 99.85 };
        var pFull = FxProfitBrain.ReversalProbability(st, 100.0, heavy, 0.4, 0.4, FxRegime.Range, peakR: 2.5, currentR: 1.0);
        Assert.True(pFull > pOne, $"confluence must outrank a single candle: {pFull:0.00} vs {pOne:0.00}");
        Assert.InRange(pFull, 0.05, 0.90);
    }

    // ── state machine ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.4, 0.3, 0.0, "PROFIT_FORMING")]
    [InlineData(2.2, 1.4, 0.0, "PROFIT_EXTENDED")]
    [InlineData(2.2, 1.8, 1.0, "PROFIT_PROTECTED")]
    [InlineData(3.0, 2.2, 2.0, "PROFIT_PROTECTED")]      // above floor: giveback 27% is NORMAL
    [InlineData(4.0, 1.0, 3.0, "PROFIT_EXIT_READY")]     // 1.0 < floor 3.0 → breached
    public void State_Machine_Transitions_Match_The_Spec(double peak, double current, double floor, string expected)
    {
        // floorBreached is derived in Evaluate; for direct state tests we
        // pass it explicitly per the expectations above.
        var breached = floor > 0 && peak >= 1.0 && current < floor;
        var state = FxProfitBrain.ProfitStateFor(peak, current, floor, breached, "NORMAL");
        if (expected == "PROFIT_EXIT_READY" && !breached)
        {
            // Not breached → the giveback path decides; 26.7% is NORMAL.
            Assert.Equal("PROFIT_EXTENDED", state);
        }
        else
        {
            Assert.Equal(expected, state);
        }
    }

    [Fact]
    public void Full_Giveback_Of_A_Peak_Is_Deteriorating_Even_Without_A_Floor()
    {
        // Peak +3R, now +0.05R: the trade earned protection and lost it all.
        var state = FxProfitBrain.ProfitStateFor(3.0, 0.05, floorR: 0, floorBreached: false, "NORMAL");
        Assert.Equal("PROFIT_DETERIORATING", state);
    }

    // ── allocation ───────────────────────────────────────────────────────

    [Fact]
    public void Allocation_Always_Sums_To_One_Hundred()
    {
        foreach (var (c, r) in new[] { (0.9, 0.05), (0.5, 0.3), (0.3, 0.7) })
        {
            var (a1, a2, a3, run) = FxProfitBrain.AllocationPlan(c, r);
            Assert.Equal(100, a1 + a2 + a3 + run);
        }
        // Strong continuation feeds the runner; reversal risk front-loads.
        var bull = FxProfitBrain.AllocationPlan(0.85, 0.1);
        var bear = FxProfitBrain.AllocationPlan(0.4, 0.7);
        Assert.True(bull.Runner > bear.Runner);
        Assert.True(bear.Tp1 > bull.Tp1);
    }

    // ── the shadow vote ──────────────────────────────────────────────────

    [Fact]
    public void Target_Tp_Vote_Rides_At_Weight_Zero()
    {
        var st = State("buy", 100.0, 1.0);
        var bars = Bars(60, 100.0, drift: 0.02, wick: 0.12);
        var report = FxProfitBrain.Evaluate(st, 100.6, bars, 0.5, 0.5, FxRegime.Trend);

        Assert.Equal(0.0, report.TargetTpVote.Weight);
        Assert.Equal("target-tp", report.TargetTpVote.Engine);
        Assert.InRange(report.TargetTpVote.Exit, 0, 1);
    }

    [Fact]
    public void Target_Tp_Vote_Gains_Conviction_When_Targets_Are_Breached()
    {
        var st = State("buy", 100.0, 1.0);
        var bars = Bars(60, 100.0, drift: 0.02, wick: 0.12);

        // Early in the trade: barely positive, nothing earned, no giveback.
        var before = FxProfitBrain.Evaluate(st, 100.1, bars, 0.5, 0.5, FxRegime.Trend);

        // Deep in profit: peak several R ahead of a +1R current.
        var at = FxProfitBrain.Evaluate(
            State("buy", 100.0, 1.0, mfeR: 3.0), 101.0, bars, 0.5, 0.5, FxRegime.Trend);

        Assert.True(at.TargetTpVote.Exit > before.TargetTpVote.Exit,
            "earned-then-given-back profit raises the shadow engine's conviction");
        Assert.True(at.TargetTpVote.Exit >= 0.7);
    }

    // ── determinism + the whole report ───────────────────────────────────

    [Fact]
    public void Evaluate_Is_Deterministic_And_Complete()
    {
        var st = State("buy", 100.0, 1.0, mfeR: 2.0);
        var bars = Bars(120, 100.0, drift: 0.015, wick: 0.12);
        var a = FxProfitBrain.Evaluate(st, 101.4, bars, 0.45, 0.45, FxRegime.Trend, prevFloorR: 1.0);
        var b = FxProfitBrain.Evaluate(st, 101.4, bars, 0.45, 0.45, FxRegime.Trend, prevFloorR: 1.0);

        Assert.Equal(a, b);   // record equality: same inputs, same report
        Assert.InRange(a.ProfitScore, 0, 100);
        Assert.InRange(a.ContinuationProbability + a.ReversalProbability, 0.999, 1.001);
        Assert.True(a.FloorR >= 1.0, "prev floor 1.0 + peak ≥ 2R → floor at least 1.0");
        Assert.False(string.IsNullOrEmpty(a.TrailingMode));
        Assert.NotNull(a.RecommendedAction);
    }

    [Fact]
    public void Peak_Giveback_Loop_Is_Sane_In_The_Report()
    {
        // Peak +4R (seeded), current +2.5R → giveback 1.5R = 37.5% (spec §14).
        var st = State("buy", 100.0, 1.0, mfeR: 4.0);
        var bars = Bars(120, 100.0, drift: 0.01, wick: 0.12);
        var report = FxProfitBrain.Evaluate(st, 102.5, bars, 0.45, 0.45, FxRegime.Trend);

        Assert.Equal(4.0, report.PeakR, 6);
        Assert.Equal(2.5, report.CurrentR, 6);
        Assert.Equal(1.5, report.GivebackR, 6);
        Assert.Equal(37.5, report.GivebackPct, 1);
    }
}
