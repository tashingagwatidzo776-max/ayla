using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Pins the canonical 20-family roster (the engine, scorecard, and lab all
/// draw from it) and the colormap engine's integrity — endpoint colors are
/// the documented upstream values, so a corrupted LUT cannot ship silently.
/// </summary>
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
}

/// <summary>Colormap engine integrity: endpoints are the documented
/// upstream colors, sampling is monotone in index, clamped in t.</summary>
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
