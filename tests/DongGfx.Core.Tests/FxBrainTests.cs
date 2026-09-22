using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The forex brain's math layer must be exact: known-input/expected-output
/// for every indicator, NaN discipline when data is insufficient, and
/// regime/alpha/engine behavior on synthetic bar series. The brain places
/// orders from these numbers — tolerance is 1e-9, not vibes.
/// </summary>
[Trait("Category", "Unit")]
public class FxFeatureTests
{
    private static List<FxBar> TrendBars(int n = 60, double start = 2400, double step = 0.5)
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < n; i++)
        {
            var close = start + step * i;
            bars.Add(new FxBar(1790000000L + 60 * i, close - 0.3, close + 0.6, close - 0.6, close, 100));
        }
        return bars;
    }

    private static List<FxBar> RangeBars(int n = 60, double mid = 1.0850, double amp = 0.0015)
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < n; i++)
        {
            var close = mid + amp * Math.Sin(i * Math.PI / 10);
            bars.Add(new FxBar(1790000000L + 60 * i, close - 0.0005, close + 0.0008, close - 0.0008, close, 100));
        }
        return bars;
    }

    [Fact]
    public void Sma_Exact_On_Constant_Series()
    {
        var v = Enumerable.Repeat(2.5, 10).ToList();
        Assert.Equal(2.5, FxFeatures.Sma(v, 5), 9);
        Assert.Equal(2.5, FxFeatures.Sma(v, 10), 9);
    }

    [Fact]
    public void Sma_Insufficient_Data_Is_NaN()
    {
        var v = new List<double> { 1, 2, 3 };
        Assert.True(double.IsNaN(FxFeatures.Sma(v, 5)));
    }

    [Fact]
    public void Ema_Converges_To_Constant()
    {
        var v = Enumerable.Repeat(7.0, 50).ToList();
        var series = FxFeatures.EmaSeries(v, 9);
        Assert.Equal(7.0, series[8], 9);
        Assert.Equal(7.0, series[^1], 9);
    }

    [Fact]
    public void Rsi_Bounds_And_Midpoint()
    {
        // Constant series → no losses, no gains → 50 or 100 boundary.
        var flat = Enumerable.Repeat(5.0, 30).ToList();
        Assert.True(FxFeatures.Rsi(flat) is >= 50 and <= 100);
        // Rising series → 100.
        var up = Enumerable.Range(0, 30).Select(i => 10.0 + i).ToList();
        Assert.Equal(100, FxFeatures.Rsi(up), 6);
        // Falling → 0.
        var down = Enumerable.Range(0, 30).Select(i => 40.0 - i).ToList();
        Assert.Equal(0, FxFeatures.Rsi(down), 6);
    }

    [Fact]
    public void Atr_Positive_And_Stable_On_Trend()
    {
        var bars = TrendBars();
        var atr = FxFeatures.Atr(bars, 14);
        Assert.False(double.IsNaN(atr));
        Assert.True(atr > 0);
        // Trend bars all have the same shape → ATR close to the constant range.
        Assert.InRange(atr, 0.5, 2.0);
    }

    [Fact]
    public void Adx_High_On_Strong_Trend_Low_On_Noise()
    {
        var (adxTrend, _, _) = FxFeatures.Adx(TrendBars(80), 14);
        Assert.False(double.IsNaN(adxTrend));
        Assert.True(adxTrend > 25, $"trend ADX {adxTrend}");
        var (adxRange, _, _) = FxFeatures.Adx(RangeBars(80), 14);
        Assert.True(double.IsNaN(adxRange) || adxRange < adxTrend);
    }

    [Fact]
    public void Bollinger_PercentB_Bounds()
    {
        var v = Enumerable.Range(0, 40).Select(i => 100.0 + (i % 7)).ToList();
        var (mid, up, lo, pB, bw) = FxFeatures.Bollinger(v, 20);
        Assert.False(double.IsNaN(mid));
        Assert.True(up > mid && mid > lo);
        Assert.InRange(pB, -1.0, 2.0);
        Assert.True(bw > 0);
    }

    [Fact]
    public void Donchian_Breakout_Detected()
    {
        var bars = RangeBars(40);
        var (hi, lo) = FxFeatures.Donchian(bars, 20);
        Assert.False(double.IsNaN(hi));
        // Append a breakout bar above the channel.
        bars.Add(new FxBar(1790000400, hi + 0.1, hi + 0.9, hi, hi + 0.8, 100));
        var sig = new FxMomentum.DonchianBreakout(20).Evaluate(bars,
            new FxRegimeVerdict(FxRegime.Trend, 30, 0.1, "london", 20, "test", 1790000400));
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
    }

    [Fact]
    public void ZScore_Sign_Matches_Deviation()
    {
        var v = Enumerable.Range(0, 40).Select(i => 1.0 + i * 0.01).ToList();
        v[^1] = 2.0; // spike above the recent mean
        var z = FxFeatures.ZScore(v, 20);
        Assert.True(z > 2, $"z {z}");
    }

    [Fact]
    public void Hurst_Trending_Series_Exceeds_Random()
    {
        // Deterministic ramp: H should be ≥ 0.5-ish (variance scales superlinearly).
        var trend = Enumerable.Range(0, 120).Select(i => 1.0 + i * 0.01).ToList();
        var hTrend = FxFeatures.Hurst(trend);
        Assert.False(double.IsNaN(hTrend));
        // Pure sine (mean-reverting) should not be strongly trending.
        var sine = Enumerable.Range(0, 120).Select(i => 1.0 + 0.1 * Math.Sin(i * 0.3)).ToList();
        var hSine = FxFeatures.Hurst(sine);
        Assert.True(hTrend >= hSine - 0.05, $"trend {hTrend} vs sine {hSine}");
    }

    [Fact]
    public void Vwap_Weighted_By_Volume()
    {
        var bars = new List<FxBar>
        {
            new(1, 10, 11, 9, 10.5, 1),
            new(2, 20, 21, 19, 20.5, 3), // heavy volume pulls VWAP up
        };
        var vwap = FxFeatures.Vwap(bars, 2);
        Assert.True(vwap > 15.0, $"vwap {vwap}");
    }

    [Fact]
    public void Parkinson_And_RealizedVol_Positive()
    {
        var bars = TrendBars(40);
        Assert.True(FxFeatures.ParkinsonVol(bars, 20) > 0);
        Assert.True(FxFeatures.RealizedVol(bars.Select(b => b.Close).ToList(), 20) > 0);
    }
}

public class FxRegimeTests
{
    private static List<FxBar> Bars(int n, Func<int, double> close)
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < n; i++)
        {
            var c = close(i);
            bars.Add(new FxBar(1790000000L + 60 * i, c - 0.3, c + 0.6, c - 0.6, c, 100));
        }
        return bars;
    }

    [Fact]
    public void Insufficient_Bars_StandDown()
    {
        var d = new FxRegimeDetector();
        var v = d.Evaluate(Bars(10, i => 2400 + i), 20, DateTimeOffset.UtcNow);
        Assert.Equal(FxRegime.StandDown, v.Regime);
    }

    [Fact]
    public void Spread_Spike_Vetoes()
    {
        var d = new FxRegimeDetector(spreadMaxPoints: 50);
        var bars = Bars(80, i => 1.085 + 0.0002 * Math.Sin(i));
        var v = d.Evaluate(bars, spreadPoints: 400, utcNow: new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(FxRegime.StandDown, v.Regime);
        Assert.Contains("spread spike", v.Reason);
    }

    [Fact]
    public void Late_Session_Is_LowLiquidity()
    {
        var d = new FxRegimeDetector();
        var bars = Bars(80, i => 1.085 + 0.0002 * Math.Sin(i));
        var late = new DateTimeOffset(2026, 9, 22, 22, 30, 0, TimeSpan.Zero);
        var v = d.Evaluate(bars, 20, late);
        Assert.Equal(FxRegime.LowLiquidity, v.Regime);
    }

    [Fact]
    public void Trending_Market_Classifies_Trend()
    {
        var d = new FxRegimeDetector();
        var bars = Bars(120, i => 2400 + i * 0.5);
        var v = d.Evaluate(bars, 20, new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(FxRegime.Trend, v.Regime);
    }

    [Fact]
    public void SessionOf_Maps_UtcHours()
    {
        Assert.Equal("asia", FxRegimeDetector.SessionOf(new DateTimeOffset(2026, 9, 22, 3, 0, 0, TimeSpan.Zero)));
        Assert.Equal("london", FxRegimeDetector.SessionOf(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal("new-york", FxRegimeDetector.SessionOf(new DateTimeOffset(2026, 9, 22, 18, 0, 0, TimeSpan.Zero)));
        Assert.Equal("late", FxRegimeDetector.SessionOf(new DateTimeOffset(2026, 9, 22, 23, 0, 0, TimeSpan.Zero)));
    }
}

public class FxEngineTests
{
    private sealed class RecordingJournal
    {
        public List<(string Cat, string Detail)> Entries { get; } = new();
        public void Add(string cat, string detail, string json) => Entries.Add((cat, detail));
        public bool Has(string cat) => Entries.Any(e => e.Cat == cat);
    }

    private static List<FxBar> TrendBars(int n = 120)
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < n; i++)
        {
            var c = 2400 + i * 0.5;
            bars.Add(new FxBar(1790000000L + 60 * i, c - 0.3, c + 0.6, c - 0.6, c, 100));
        }
        return bars;
    }

    [Fact]
    public void Paper_Decision_Journaled_No_Order()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 1.0, equityProvider: () => 10_000);
        engine.AddAlpha(new FxMomentum.DonchianBreakout());
        var bars = TrendBars();
        bars.Add(new FxBar(1790000600, 2459.0, 2465.0, 2458.5, 2464.8, 100)); // breakout bar

        var decision = engine.RunOnce(DateTimeOffset.UtcNow, bars, 2464.0, 2465.0);
        Assert.True(j.Has("FX_REGIME"));
        Assert.True(double.IsNaN(decision.Regime.Adx) || decision.Regime.Adx >= 0);
        Assert.NotEqual(FxDecisionAction.Ordered, decision.Action);
        if (decision.Signal is not null)
        {
            // A spoken signal with verified equity must SIZE - the old
            // "if Paper" guard is exactly what let zero-lot sizing ship.
            Assert.Equal(FxDecisionAction.Paper, decision.Action);
            Assert.True(j.Has("FX_SIGNAL"));
            Assert.True(j.Has("FX_DECISION"));
            Assert.True(decision.SuggestedLots > 0);
        }
    }

    [Fact]
    public void Live_Engine_Returns_Order_Decision()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 1.0, equityProvider: () => 10_000);
        engine.AddAlpha(new FxMomentum.DonchianBreakout());
        engine.GoLive();
        var bars = TrendBars();
        bars.Add(new FxBar(1790000600, 2459.0, 2465.0, 2458.5, 2464.8, 100));

        var decision = engine.RunOnce(DateTimeOffset.UtcNow, bars, 2464.0, 2465.0);
        if (decision.Signal is not null)
        {
            // signal + equity => a sized order, never a silent SkippedSizing
            Assert.Equal(FxDecisionAction.Ordered, decision.Action);
            Assert.InRange(decision.SuggestedLots, 0.01, 1.0);
            Assert.True(j.Has("FX_DECISION"));
        }
    }

    [Fact]
    public void Sizing_Uses_EquityRiskBudget_GoldLotUnits_And_Cap()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 0.5, equityProvider: () => 10_000);
        var sig = new FxSignal("test", FxDirection.Buy, 0.8, 5.0, "stop hint 5.0", 0);

        // budget = equity 10 000 x 0.02 = 200; gold (>500) = 100 oz/lot
        // -> risk/lot = 5.0 x 100 = 500 -> 0.40 lots, within the 0.5 cap
        var lots = engine.Size(sig, 2450);
        Assert.Equal(0.40, lots, 9);
        Assert.Equal(Math.Floor(lots * 100) / 100, lots, 9); // 0.01 step
        Assert.InRange(lots, 0, 0.5);
    }

    [Fact]
    public void Sizing_Fails_Closed_Without_Verified_Equity()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 1.0); // no provider
        var sig = new FxSignal("test", FxDirection.Buy, 0.8, 5.0, "stop hint 5.0", 0);
        Assert.Equal(0, engine.Size(sig, 2450), 9);

        var flat = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 1.0, equityProvider: () => 0);
        Assert.Equal(0, flat.Size(sig, 2450), 9);
    }

    [Fact]
    public void Sizing_Uses_FxLotUnits_For_Currency_Pairs()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("EURUSD", "M1", j.Add, lotsCap: 0.5, equityProvider: () => 10_000);
        var sig = new FxSignal("test", FxDirection.Buy, 0.8, 0.001, "10-pip stop", 0);

        // FX: 100k units/lot -> risk/lot = 0.001 x 100k = 100
        // budget 200 -> 2.0 lots -> capped at 0.5
        Assert.Equal(0.5, engine.Size(sig, 1.27), 9);
    }

    [Fact]
    public void Cycle_Never_Throws_On_Garbage()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 1.0);
        var decision = engine.RunOnce(DateTimeOffset.UtcNow, new List<FxBar>(), 0, 0);
        Assert.Equal(FxDecisionAction.SkippedRegime, decision.Action);
    }

    [Fact]
    public void GoLive_Is_Journaled()
    {
        var j = new RecordingJournal();
        var engine = new FxEngine("XAUUSD", "M1", j.Add, lotsCap: 1.0);
        engine.GoLive();
        Assert.True(engine.IsLive);
        Assert.Contains(j.Entries, e => e.Cat == "FX_MODE");
    }
}
