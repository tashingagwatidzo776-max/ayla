using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Pins the canonical 20-family roster (the engine, scorecard, and lab all
/// draw from it) and the colormap engine's integrity — endpoint colors are
/// the documented upstream values, so a corrupted LUT cannot ship silently.
/// </summary>
[Trait("Category", "Unit")]
public class FxFamiliesTests
{
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

    private static FxRegimeVerdict TrendVerdict => new(
        FxRegime.Trend, 30.0, 0.05, "london", 20, "trending", 1790000000L);

    private static FxRegimeVerdict RangeVerdict => new(
        FxRegime.Range, 15.0, 0.03, "london", 20, "ranging", 1790000000L);

    [Fact]
    public void Roster_Is_Twenty_Alphas_With_Unique_Names()
    {
        var all = FxFamilies.All();
        Assert.Equal(20, all.Count);
        Assert.Equal(all.Count, all.Select(a => a.Name).Distinct().Count());
    }

    [Fact]
    public void Roster_Carries_The_Original_Six()
    {
        var names = FxFamilies.All().Select(a => a.Name).ToList();
        Assert.Contains("ema-cross(9/21)", names);
        Assert.Contains("donchian(20)", names);
        Assert.Contains("roc(10)", names);
        Assert.Contains("z-rev(20)", names);
        Assert.Contains("bb-rev(20)", names);
        Assert.Contains("vwap-rev(30)", names);
    }

    [Fact]
    public void Every_Alpha_Declares_Tradeable_Regimes()
    {
        foreach (var alpha in FxFamilies.All())
        {
            Assert.NotEmpty(alpha.Regimes);
            // StandDown and LowLiquidity are vetoes — no alpha may speak there.
            Assert.DoesNotContain(FxRegime.StandDown, alpha.Regimes);
            Assert.DoesNotContain(FxRegime.LowLiquidity, alpha.Regimes);
        }
    }

    [Fact]
    public void New_Alphas_Evaluate_Cleanly_On_Trending_Tape()
    {
        // Smoke contract: on plausible tape each new alpha either passes
        // (legitimately — its conditions are not met) or returns a
        // well-formed signal. A throw or a malformed signal fails.
        IFxAlpha[] newAlphas =
        [
            new FxFamilies.MacdCross(),
            new FxFamilies.Rsi2Reversion(),
            new FxFamilies.KeltnerBreakout(),
            new FxFamilies.KalmanTrend(),
            new FxFamilies.AdxPullback(),
            new FxFamilies.DonchianPullback(),
            new FxFamilies.EmaSlopeAtr(),
            new FxFamilies.BollingerSqueeze(),
            new FxFamilies.OpenRangeBreakout(),
            new FxFamilies.VwapTrend(),
            new FxFamilies.RsiMomentum(),
            new FxFamilies.HurstTrend(),
        ];
        var bars = TrendBars();
        foreach (var alpha in newAlphas)
        {
            var regimes = alpha.Regimes.Contains(FxRegime.Range)
                ? new[] { RangeVerdict, TrendVerdict }
                : new[] { TrendVerdict };
            foreach (var verdict in regimes)
            {
                var sig = alpha.Evaluate(bars, verdict);
                if (sig is null)
                {
                    continue;
                }

                Assert.False(string.IsNullOrWhiteSpace(sig.Alpha));
                Assert.False(string.IsNullOrWhiteSpace(sig.Reason));
                Assert.True(sig.Confidence is > 0 and <= 1, $"{alpha.Name} confidence {sig.Confidence}");
                Assert.True(double.IsFinite(sig.StopDistanceHint), $"{alpha.Name} stop hint");
                Assert.True(sig.Direction is FxDirection.Buy or FxDirection.Sell);
            }
        }
    }

    [Fact]
    public void OpenRangeBreakout_Fires_On_The_Break()
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < 40; i++)
        {
            var c = 2400 + i * 0.05;
            bars.Add(new FxBar(1790000000L + 60 * i, c - 0.05, c + 0.05, c - 0.05, c, 100));
        }
        // Breakout bar: closes far above the whole window.
        bars.Add(new FxBar(1790000000L + 60 * 40, 2402.0, 2405.0, 2401.5, 2404.5, 100));

        var sig = new FxFamilies.OpenRangeBreakout().Evaluate(bars, TrendVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
    }

    [Fact]
    public void Rsi2Reversion_Fires_On_WashedOut_Tape()
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < 24; i++)
        {
            var c = 2410 - i * 0.5;   // steady decline
            bars.Add(new FxBar(1790000000L + 60 * i, c + 0.1, c + 0.2, c - 0.2, c, 100));
        }

        var sig = new FxFamilies.Rsi2Reversion().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
    }

    [Fact]
    public void MacdCross_Fires_On_The_Flip()
    {
        // 30 flat bars settle both EMAs at 2400 (histogram exactly 0); one
        // breakout bar then flips the histogram positive on that single bar
        // — the flip window the alpha evaluates ([-2] → [-1]).
        var bars = new List<FxBar>();
        for (var i = 0; i < 30; i++)
        {
            bars.Add(new FxBar(1790000000L + 60 * i, 2400.0, 2400.05, 2399.95, 2400.0, 100));
        }
        bars.Add(new FxBar(1790000000L + 60 * 30, 2400.0, 2406.0, 2400.0, 2405.5, 100));

        var sig = new FxFamilies.MacdCross().Evaluate(bars, TrendVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
    }

    // ── quiet-FX extension roster (FxFamilies.QuietFx) ─────────────────────

    [Fact]
    public void QuietFx_Roster_Is_Three_Unique_Voices_Outside_Production()
    {
        var quiet = FxFamilies.QuietFx();
        Assert.Equal(3, quiet.Count);
        Assert.Equal(quiet.Count, quiet.Select(a => a.Name).Distinct().Count());

        // Training-only: no overlap with the pinned production roster, so
        // mode-4 (All + QuietFx) really adds voices and mode 0 is untouched.
        var production = FxFamilies.All().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var alpha in quiet)
        {
            Assert.DoesNotContain(alpha.Name, production);
            Assert.NotEmpty(alpha.Regimes);
            Assert.DoesNotContain(FxRegime.StandDown, alpha.Regimes);
            Assert.DoesNotContain(FxRegime.LowLiquidity, alpha.Regimes);
        }
    }

    /// <summary>H1 tape on a UTC day: 7 overnight (asia) bars, then 8
    /// drive bars — enough for ATR(14) and the walk-back.</summary>
    private static List<FxBar> SessionDay(double asiaHigh, double asiaLow,
                                           double driveClose, int driveBars = 8)
    {
        var midnight = (1790000000L / 86400) * 86400;   // 00:00 UTC
        var bars = new List<FxBar>();
        for (var h = 0; h < 7; h++)   // asia 00–06
        {
            var mid = (asiaHigh + asiaLow) / 2;
            bars.Add(new FxBar(midnight + 3600L * h,
                mid, asiaHigh, asiaLow, mid, 100));
        }
        for (var h = 0; h < driveBars; h++)   // london onward
        {
            var t = midnight + 3600L * (7 + h);
            var c = h == driveBars - 1 ? driveClose : asiaHigh;
            bars.Add(new FxBar(t, asiaHigh, Math.Max(asiaHigh, c) + 0.0002,
                Math.Min(asiaLow, c) - 0.0002, c, 100));
        }
        return bars;
    }

    [Fact]
    public void AsiaBreak_Buys_The_Close_Above_The_Overnight_Range()
    {
        // Overnight coil at 1.0980–1.0990 (range 10 pips ≪ 4×ATR), the
        // final drive bar closes 1.1005 — outside the asia high.
        var bars = SessionDay(asiaHigh: 1.0990, asiaLow: 1.0980, driveClose: 1.1005);
        var sig = new FxFamilies.AsiaBreak().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
        Assert.True(sig.Confidence is > 0 and <= 1);
        Assert.True(double.IsFinite(sig.StopDistanceHint));
    }

    [Fact]
    public void AsiaBreak_Sells_The_Close_Below_The_Overnight_Range()
    {
        var bars = SessionDay(asiaHigh: 1.0990, asiaLow: 1.0980, driveClose: 1.0965);
        var sig = new FxFamilies.AsiaBreak().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig!.Direction);
    }

    [Fact]
    public void AsiaBreak_Passes_While_Still_In_Asia()
    {
        // Rewind the last (drive) bar into the overnight window: the
        // session gate must reject before any walk happens — no drive,
        // nothing to break. Full 15-bar tape so the ATR path stays live.
        var bars = SessionDay(asiaHigh: 1.0990, asiaLow: 1.0980, driveClose: 1.1005);
        var midnight = (1790000000L / 86400) * 86400;
        var last = bars[^1];
        bars[^1] = new FxBar(midnight + 3600L * 6, last.Open, last.High,
            last.Low, last.Close, last.Volume);   // stamped 06:00 UTC = asia
        var sig = new FxFamilies.AsiaBreak().Evaluate(bars, RangeVerdict);
        Assert.Null(sig);
    }

    [Fact]
    public void AsiaBreak_Passes_When_The_Overnight_Grind_Is_Not_Quiet()
    {
        // A 14-bar overnight CLIMB: the asia block spans ~72 pips while
        // each bar is ~7 — ATR(14) only sees the per-bar noise, so the
        // block range clears 4×ATR and the containment gate holds. The
        // drive then closes above the block high, so only the gate stands
        // between that close and a Buy signal.
        var midnight = (1790000000L / 86400) * 86400;
        var bars = new List<FxBar>();
        var c = 1.0980;
        for (var i = 0; i < 14; i++)   // 30-min asia stamps: hours 0–6.5
        {
            var o = c;
            c += 0.0005;
            bars.Add(new FxBar(midnight + 1800L * i, o, c + 0.0001,
                o - 0.0001, c, 100));
        }
        for (var h = 7; h <= 9; h++)   // drive session, small bars
        {
            var o = c;
            c += 0.0004;
            bars.Add(new FxBar(midnight + 3600L * h, o, c + 0.0001,
                o - 0.0001, c, 100));
        }

        var sig = new FxFamilies.AsiaBreak().Evaluate(bars, RangeVerdict);
        Assert.Null(sig);
    }

    [Fact]
    public void BandFade_Fades_A_Close_Pinned_At_The_Band_Top()
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < 54; i++)
        {
            // Contained coil 1.0980–1.0990, tiny bars inside it.
            var mid = 1.0985 + 0.0004 * Math.Sin(i);
            bars.Add(new FxBar(1790000000L + 60 * i, mid, mid + 0.0002,
                mid - 0.0002, mid, 100));
        }
        bars.Add(new FxBar(1790000000L + 60 * 54, 1.0987, 1.0990,
            1.0986, 1.0990, 100));   // close pinned at the band high

        var sig = new FxFamilies.BandFade().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig!.Direction);
    }

    [Fact]
    public void BandFade_Fades_A_Close_Pinned_At_The_Band_Floor()
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < 54; i++)
        {
            var mid = 1.0985 + 0.0004 * Math.Sin(i);
            bars.Add(new FxBar(1790000000L + 60 * i, mid, mid + 0.0002,
                mid - 0.0002, mid, 100));
        }
        bars.Add(new FxBar(1790000000L + 60 * 54, 1.0983, 1.0984,
            1.0980, 1.0980, 100));   // close pinned at the band low

        var sig = new FxFamilies.BandFade().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
    }

    [Fact]
    public void BandFade_Stand_Down_On_A_Trend_Sized_Window()
    {
        // Steady climb: 50-bar span dwarfs 10×ATR — that window is a
        // trend (donchian's job), the containment gate must pass.
        var bars = new List<FxBar>();
        for (var i = 0; i < 55; i++)
        {
            var c = 1.0000 + i * 0.0006;
            bars.Add(new FxBar(1790000000L + 60 * i, c - 0.0002, c + 0.0002,
                c - 0.0002, c, 100));
        }
        var sig = new FxFamilies.BandFade().Evaluate(bars, RangeVerdict);
        Assert.Null(sig);
    }

    [Fact]
    public void RangeDrift_Rides_A_Monotone_Glide_Up()
    {
        // Steady crawl: EMA21 rises bar over bar, price leads it, and the
        // 4-bar glide clears 0.4×ATR — the quiet-major's Range-classified
        // trend the reversion voices would fade against.
        var bars = new List<FxBar>();
        for (var i = 0; i < 40; i++)
        {
            var c = 1.1000 + i * 0.0006;
            bars.Add(new FxBar(1790000000L + 60 * i, c - 0.0002, c + 0.0003,
                c - 0.0003, c, 100));
        }
        var sig = new FxFamilies.RangeDrift().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Buy, sig!.Direction);
        Assert.True(sig.Confidence is > 0 and <= 1);
    }

    [Fact]
    public void RangeDrift_Rides_A_Monotone_Glide_Down()
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < 40; i++)
        {
            var c = 1.1000 - i * 0.0006;
            bars.Add(new FxBar(1790000000L + 60 * i, c + 0.0002, c + 0.0003,
                c - 0.0003, c, 100));
        }
        var sig = new FxFamilies.RangeDrift().Evaluate(bars, RangeVerdict);
        Assert.NotNull(sig);
        Assert.Equal(FxDirection.Sell, sig!.Direction);
    }

    [Fact]
    public void RangeDrift_Passes_On_A_Flat_Coil()
    {
        // Dead-flat tape: EMA neither rises nor falls — the glide check
        // (rising == falling) refuses instead of picking a side.
        var bars = new List<FxBar>();
        for (var i = 0; i < 40; i++)
        {
            bars.Add(new FxBar(1790000000L + 60 * i, 1.1000, 1.1002,
                1.0998, 1.1000, 100));
        }
        var sig = new FxFamilies.RangeDrift().Evaluate(bars, RangeVerdict);
        Assert.Null(sig);
    }
}

/// <summary>Colormap engine integrity: endpoints are the documented
/// upstream colors, sampling is monotone in index, clamped in t.</summary>
[Trait("Category", "Unit")]
public class FxCmapTests
{
    [Fact]
    public void Turbo_Endpoints_Match_Upstream()
    {
        var (r0, g0, b0) = FxCmap.Sample(FxCmapKind.Turbo, 0);
        Assert.Equal((0x30, 0x12, 0x3B), (r0, g0, b0));
        var (r1, g1, b1) = FxCmap.Sample(FxCmapKind.Turbo, 1);
        Assert.Equal((0x7A, 0x04, 0x03), (r1, g1, b1));
    }

    [Fact]
    public void Viridis_Endpoints_Match_Upstream()
    {
        var (r0, g0, b0) = FxCmap.Sample(FxCmapKind.Viridis, 0);
        Assert.Equal((0x44, 0x01, 0x54), (r0, g0, b0));   // #440154
        var (r1, g1, b1) = FxCmap.Sample(FxCmapKind.Viridis, 1);
        Assert.Equal((0xFD, 0xE7, 0x25), (r1, g1, b1));   // #FDE725
    }

    [Fact]
    public void Inferno_Starts_Near_Black()
    {
        var (r, g, b) = FxCmap.Sample(FxCmapKind.Inferno, 0);
        Assert.True(r <= 2 && g <= 2 && b <= 6);
    }

    [Fact]
    public void Sampling_Clamps_And_Is_Uniform()
    {
        Assert.Equal(FxCmap.Sample(FxCmapKind.Turbo, 0), FxCmap.Sample(FxCmapKind.Turbo, -1));
        Assert.Equal(FxCmap.Sample(FxCmapKind.Turbo, 1), FxCmap.Sample(FxCmapKind.Turbo, 5));
        // Adjacent samples on a 256-entry LUT never jump more than a
        // perceptual step (no banding cliffs from a corrupted table).
        var prev = FxCmap.Sample(FxCmapKind.Viridis, 0);
        for (var i = 1; i <= 32; i++)
        {
            var cur = FxCmap.Sample(FxCmapKind.Viridis, i / 32.0);
            var jump = Math.Abs(cur.R - prev.R) + Math.Abs(cur.G - prev.G) + Math.Abs(cur.B - prev.B);
            Assert.True(jump <= 60, $"viridis banding at step {i}: jump {jump}");
            prev = cur;
        }
    }

    [Fact]
    public void Defaults_And_TryParse()
    {
        Assert.Equal(FxCmapKind.Turbo, FxCmap.DefaultFor("MTF confluence"));
        Assert.Equal(FxCmapKind.Turbo, FxCmap.DefaultFor("Order flow"));
        Assert.Equal(FxCmapKind.Inferno, FxCmap.DefaultFor("AI probability"));
        Assert.Equal(FxCmapKind.Viridis, FxCmap.DefaultFor("Volume surface"));
        Assert.True(FxCmap.TryParse("Plasma", out var plasma));
        Assert.Equal(FxCmapKind.Plasma, plasma);
        Assert.False(FxCmap.TryParse("NeonRainbow", out _));
        Assert.True(FxCmap.TryParse("CoolHot", out var cool));
        Assert.Equal(FxCmapKind.CoolHot, cool);
    }
}
