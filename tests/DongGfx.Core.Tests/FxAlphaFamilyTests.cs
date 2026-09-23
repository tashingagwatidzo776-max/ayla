using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Every alpha family's Evaluate sits directly on the live decision path, so
/// each branch is pinned with a numerically-validated synthetic series — the
/// expected outcome was computed against exact ports of FxFeatures before
/// these tests were written. No "if a signal might fire" softness: a speak
/// branch asserts a signal, a gate branch asserts null.
/// </summary>
[Trait("Category", "Unit")]
public class FxAlphaFamilyTests
{
    private static FxRegimeVerdict Verdict(FxRegime regime = FxRegime.Trend) =>
        new(regime, 25.0, 0.20, "london", 2, "test", 1790000000L);

    private static List<FxBar> Bars(IReadOnlyList<double> closes, double highPad = 0.0008, double lowPad = 0.0003)
    {
        var bars = new List<FxBar>(closes.Count);
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            bars.Add(new FxBar(1790000000L + 60 * i, c, c + highPad, c - lowPad, c, 100));
        }
        return bars;
    }

    [Fact]
    public void EmaCross_Sell_When_The_Fall_Accelerates()
    {
        // Steady declines plateau the EMA gap (now == prev fails the widening
        // test); accelerating down is what the Sell branch is for.
        var closes = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            closes.Add(i < 45 ? 2500 - 0.5 * i : 2500 - 0.5 * 45 - 2.0 * (i - 44));
        }

        var sig = new FxMomentum.EmaCross().Evaluate(Bars(closes, 0.6, 0.6), Verdict());

        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig.Direction);
        Assert.Contains("below", sig.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(sig.StopDistanceHint >= 0);
    }

    [Fact]
    public void Donchian_Breakout_Sell_On_New_Low()
    {
        // Donchian's channel covers the n bars BEFORE the last one and uses
        // High/Low: the final close must undercut the prior 20-bar low.
        var closes = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            closes.Add(1.10 - 0.003 * i);
        }

        var sig = new FxMomentum.DonchianBreakout().Evaluate(Bars(closes, 0.0008, 0.0005), Verdict());

        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig.Direction);
        Assert.Contains("broke 20-bar low", sig.Reason);
    }

    private sealed class StubAlpha(params FxRegime[] regimes) : IFxAlpha
    {
        public string Name => "stub-fast";
        public FxRegime[] Regimes { get; } = regimes;
        public Exception? Throws { get; set; }
        public FxSignal? Next { get; set; } = new("stub-fast", FxDirection.Buy, 0.8, 0.001, "fast speaks", 1790000000L);

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (Throws is not null) throw Throws;
            return Next;
        }
    }

    private static List<double> LcgWalk(int n, long seed, double up = 0.01, double down = -0.008, double floor = 0.5)
    {
        var outList = new List<double>(n);
        var x = 1.0;
        for (var i = 0; i < n; i++)
        {
            seed = (seed * 1103515245L + 12345L) & 0x7FFFFFFFL;
            x = Math.Max(floor, x + (((seed >> 16) & 1) != 0 ? up : down));
            outList.Add(x);
        }
        return outList;
    }

    [Fact]
    public void MultiTimeframe_Passes_When_Higher_Timeframe_Walks_Persistently()
    {
        // Validated: H of this LCG walk is 0.60 (>= 0.5 gate), fast speaks.
        var fast = new StubAlpha(FxRegime.Trend);
        var mtf = new FxMomentum.MultiTimeframe(fast, Bars(LcgWalk(120, 12345), 0.01, 0.01));

        var sig = mtf.Evaluate(Bars(LcgWalk(120, 777), 0.01, 0.01), Verdict());

        Assert.NotNull(sig);
        Assert.Equal("fast speaks", sig.Reason);
    }

    [Fact]
    public void MultiTimeframe_Gates_When_Higher_Timeframe_Is_Indeterminate()
    {
        // < 32 closes -> Hurst is NaN -> the gate refuses the fast signal.
        var fast = new StubAlpha(FxRegime.Trend);
        var mtf = new FxMomentum.MultiTimeframe(fast, Bars(Enumerable.Repeat(1.0, 10).ToList()));

        Assert.Null(mtf.Evaluate(Bars(LcgWalk(60, 777), 0.01, 0.01), Verdict()));
    }

    private static List<double> WideThenTightBars()
    {
        var closes = new List<double>(50);
        for (var i = 0; i < 50; i++)
        {
            closes.Add(i < 25
                ? 1.0 + (i % 2 == 0 ? 0.02 : -0.02)
                : 1.0 + (i % 2 == 0 ? 0.001 : -0.001));
        }
        closes[48] = 0.999;
        closes[49] = 1.001;
        return closes;
    }

    private static List<double> QuietThenExpandedBars()
    {
        var closes = new List<double>(50);
        for (var i = 0; i < 50; i++)
        {
            closes.Add(i < 30
                ? 1.0 + (i % 2 == 0 ? 0.001 : -0.001)
                : 1.0 + 0.001 * (i - 29) + (i % 2 == 0 ? 0.001 : -0.001));
        }
        return closes;
    }

    [Fact]
    public void VolatilityBreakout_Fires_After_Compression_Release()
    {
        var sig = new FxMomentum.VolatilityBreakout().Evaluate(Bars(WideThenTightBars()), Verdict(FxRegime.Range));

        Assert.NotNull(sig);
        Assert.Equal("vol-breakout", sig.Alpha);
        Assert.Equal(FxDirection.Buy, sig.Direction); // last close 1.001 > 0.999
        Assert.Contains("squeeze", sig.Reason);
    }

    [Fact]
    public void VolatilityBreakout_Guards_Fresh_Expansion_And_Short_History()
    {
        var alpha = new FxMomentum.VolatilityBreakout();

        Assert.Null(alpha.Evaluate(Bars(Enumerable.Repeat(1.0, 10).ToList()), Verdict(FxRegime.Range)));
        Assert.Null(alpha.Evaluate(Bars(QuietThenExpandedBars()), Verdict(FxRegime.Range)));
    }

    [Fact]
    public void ZScore_Sell_On_High_Spike()
    {
        // Sine range series with the last close spiked: validated z = 2.77 > 2.
        var closes = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            closes.Add(1.0850 + 0.0015 * Math.Sin(i * Math.PI / 10));
        }
        closes[59] = 1.0890;

        var sig = new FxMeanReversion.ZScore().Evaluate(Bars(closes), Verdict(FxRegime.Range));

        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig.Direction);
        Assert.Contains("Z ", sig.Reason);
    }

    [Fact]
    public void BollingerReversion_Sell_On_Upper_Band_Rejection()
    {
        // 29 stable closes, spike above the band, then back inside — both
        // band comparisons validated against the exact Bollinger port.
        var closes = new List<double>();
        for (var i = 0; i < 29; i++)
        {
            closes.Add(1.0850 + 0.0001 * (i % 2 == 0 ? 1 : -1));
        }
        closes.Add(1.0890); // prior close outside the upper band
        closes.Add(1.0850); // current close back inside

        var sig = new FxMeanReversion.BollingerReversion().Evaluate(Bars(closes), Verdict(FxRegime.Range));

        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig.Direction);
        Assert.Contains("upper band", sig.Reason);
    }

    [Fact]
    public void OuHalfLife_Sell_When_Reversion_Is_Fast()
    {
        // AR(1) pull 0.2 with uncorrelated LCG noise: theta 0.92, half-life
        // 0.75 bars, final spike pushes z to 3.8 — all inside the trade gate.
        var spread = new double[60];
        var seed = 987654321L;
        for (var i = 1; i < 60; i++)
        {
            seed = (seed * 1103515245L + 12345L) & 0x7FFFFFFFL;
            spread[i] = 0.2 * spread[i - 1] + (((seed >> 16) & 1) != 0 ? 0.01 : -0.01);
        }
        spread[59] = 0.05;
        var closes = spread.Select(s => 1.085 + s).ToList();

        var sig = new FxMeanReversion.OuHalfLife(period: 40).Evaluate(Bars(closes), Verdict(FxRegime.Range));

        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig.Direction);
        Assert.Contains("half-life", sig.Reason);
    }

    [Fact]
    public void OuHalfLife_Null_On_Degenerate_Spread()
    {
        // Zero variance around the mean -> den <= 0 -> never trade.
        var closes = Enumerable.Repeat(1.085, 60).ToList();

        Assert.Null(new FxMeanReversion.OuHalfLife(period: 40).Evaluate(Bars(closes), Verdict(FxRegime.Range)));
    }
}

/// <summary>
/// The engine's cycle outcomes, asserted unconditionally (the old tests
/// wrapped every post-signal assertion in "if a signal fired", which let the
/// Paper/Ordered paths ship with zero coverage). Fixed clock (10:00 UTC —
/// never "late"), stable sub-60-point spread, 120 monotone bars -> the
/// detector deterministically lands Trend.
/// </summary>
[Trait("Category", "Unit")]
public class FxEngineDecisionTests
{
    private static readonly DateTimeOffset London10 = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Late2230 = new(2026, 9, 22, 22, 30, 0, TimeSpan.Zero);

    private static List<FxBar> TrendBars()
    {
        var bars = new List<FxBar>(120);
        for (var i = 0; i < 120; i++)
        {
            var close = 2400 + i * 0.5;
            bars.Add(new FxBar(1790000000L + 60 * i, close - 0.3, close + 0.6, close - 0.6, close, 100));
        }
        return bars;
    }

    private static FxEngine Engine(List<string> journal, double equity = 10_000, bool withEquity = true) =>
        new("XAUUSD", "M1", (_, _, _) => { journal.Add("x"); },
            lotsCap: 1.0,
            equityProvider: withEquity ? () => equity : null);

    private static readonly FxRegime[] AllRegimes =
        [FxRegime.Trend, FxRegime.Range, FxRegime.HighVol, FxRegime.LowLiquidity, FxRegime.StandDown];

    private sealed class CycleStub(params FxRegime[] regimes) : IFxAlpha
    {
        public string Name => "cycle-stub";
        public FxRegime[] Regimes { get; } = regimes;
        public Exception? Throws { get; set; }

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (Throws is not null) throw Throws;
            return new FxSignal("cycle-stub", FxDirection.Buy, 0.8, 0.005, "stub speaks", regime.TimeUtc);
        }
    }

    [Fact]
    public void RunOnce_Paper_When_Signal_Speaks_With_Verified_Equity()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.SetVenueSpec(new FxVenueSymbolSpec(100.0, 0.01, 0.01, 100.0));
        engine.AddAlpha(new CycleStub(AllRegimes));

        var d = engine.RunOnce(London10, TrendBars(), 2464.0, 2465.0);

        // budget 200 / (0.005 x 100) = 400 -> cap 1.0 -> 1.0 lot exactly.
        Assert.Equal(FxDecisionAction.Paper, d.Action);
        Assert.Equal(1.0, d.SuggestedLots, 9);
    }

    [Fact]
    public void RunOnce_Orders_When_Live_With_Signal()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.SetVenueSpec(new FxVenueSymbolSpec(100.0, 0.01, 0.01, 100.0));
        engine.AddAlpha(new CycleStub(AllRegimes));
        engine.GoLive();

        var d = engine.RunOnce(London10, TrendBars(), 2464.0, 2465.0);

        Assert.Equal(FxDecisionAction.Ordered, d.Action);
        Assert.InRange(d.SuggestedLots, 0.01, 1.0);
    }

    [Fact]
    public void RunOnce_NoSignal_When_Regime_Gates_The_Alpha()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.AddAlpha(new CycleStub(FxRegime.HighVol)); // detector says Trend

        var d = engine.RunOnce(London10, TrendBars(), 2464.0, 2465.0);

        Assert.Equal(FxDecisionAction.NoSignal, d.Action);
        Assert.Equal("no alpha spoke in this regime", d.Detail);
    }

    [Fact]
    public void RunOnce_Skips_Sizing_When_Equity_Unverified()
    {
        var j = new List<string>();
        var engine = Engine(j, withEquity: false);
        engine.SetVenueSpec(new FxVenueSymbolSpec(100.0, 0.01, 0.01, 100.0));
        engine.AddAlpha(new CycleStub(AllRegimes));

        var d = engine.RunOnce(London10, TrendBars(), 2464.0, 2465.0);

        Assert.Equal(FxDecisionAction.SkippedSizing, d.Action);
        Assert.Equal(0, d.SuggestedLots);
    }

    [Fact]
    public void RunOnce_CycleError_When_Alpha_Throws()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.AddAlpha(new CycleStub(AllRegimes) { Throws = new InvalidOperationException("boom") });

        var d = engine.RunOnce(London10, TrendBars(), 2464.0, 2465.0);

        Assert.Equal(FxDecisionAction.CycleError, d.Action);
        Assert.StartsWith("cycle error", d.Detail);
        Assert.Contains("boom", d.Detail);
    }

    [Fact]
    public void RunOnce_Skips_Regime_In_Late_Session()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.AddAlpha(new CycleStub(AllRegimes));

        var d = engine.RunOnce(Late2230, TrendBars(), 2464.0, 2465.0);

        Assert.Equal(FxDecisionAction.SkippedRegime, d.Action);
        Assert.Contains("late session", d.Detail);
    }

    [Fact]
    public void Size_Fails_Closed_On_Invalid_Stop_Hints_And_Prices()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.SetVenueSpec(new FxVenueSymbolSpec(100.0, 0.01, 0.01, 100.0));

        Assert.Equal(0, engine.Size(new FxSignal("t", FxDirection.Buy, 0.8, double.NaN, "nan", 0), 2450));
        Assert.Equal(0, engine.Size(new FxSignal("t", FxDirection.Buy, 0.8, -1, "neg", 0), 2450));
        Assert.Equal(0, engine.Size(new FxSignal("t", FxDirection.Buy, 0.8, 5.0, "ok", 0), 0));
        Assert.Equal(0, engine.Size(new FxSignal("t", FxDirection.Buy, 0.8, 5.0, "ok", 0), -10));
    }

    [Fact]
    public void Size_Fails_Closed_On_Non_Finite_Equity()
    {
        var j = new List<string>();
        var engine = Engine(j, equity: double.NaN);
        engine.SetVenueSpec(new FxVenueSymbolSpec(100.0, 0.01, 0.01, 100.0));

        Assert.Equal(0, engine.Size(new FxSignal("t", FxDirection.Buy, 0.8, 5.0, "ok", 0), 2450));
    }

    [Fact]
    public void GoPaper_Journals_And_Lowers_The_Flag()
    {
        var j = new List<string>();
        var engine = Engine(j);
        engine.GoLive();
        Assert.True(engine.IsLive);

        engine.GoPaper();

        Assert.False(engine.IsLive);
    }
}

/// <summary>Small pure helpers the cycle leans on — cheap to pin, on every path.</summary>
[Trait("Category", "Unit")]
public class FxJsonAndSpecTests
{
    [Fact]
    public void Sanitize_Nulls_Everything_Not_Finite()
    {
        Assert.Equal(1.5, FxJson.Sanitize(1.5));
        Assert.Null(FxJson.Sanitize(double.NaN));
        Assert.Null(FxJson.Sanitize(double.PositiveInfinity));
        Assert.Null(FxJson.Sanitize(double.NegativeInfinity));
    }

    [Fact]
    public void Heuristic_Picks_The_Contract_By_Price()
    {
        var gold = FxVenueSymbolSpec.Heuristic(2450);
        Assert.Equal(100.0, gold.ContractSize);
        Assert.Equal(0.01, gold.VolumeStep);

        var fx = FxVenueSymbolSpec.Heuristic(1.08);
        Assert.Equal(100_000.0, fx.ContractSize);
        Assert.Equal(0.01, fx.VolumeMin);
        Assert.Equal(100.0, fx.VolumeMax);
    }

    [Fact]
    public void Alphas_Register_Through_AddAlpha()
    {
        var j = new List<string>();
        var engine = new FxEngine("EURUSD", "M1", (_, _, _) => { j.Add("x"); }, lotsCap: 1.0);

        Assert.Empty(engine.Alphas);
        engine.AddAlpha(new FxMomentum.Roc());
        Assert.Single(engine.Alphas);
        Assert.Contains("roc", engine.Alphas[0].Name);
    }
}
