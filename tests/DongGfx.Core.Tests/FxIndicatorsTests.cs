using System;
using System.Collections.Generic;
using System.Linq;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The Terminal chart's indicator overlays: EMA's SMA seed matches the
/// hand-computed value, Bollinger bands bracket the SMA at ±2σ, RSI hits
/// its canonical anchors (100 pure-gain, 0 pure-loss, 50 flat), and every
/// indicator is null through its warm-up. Period/count edge cases throw
/// or degrade rather than inventing values.
/// </summary>
[Trait("Category", "Unit")]
public class FxIndicatorsTests
{
    [Fact]
    public void Ema_SeedIsSma_ThenRecursesWithAlpha()
    {
        // period 3 over 1..6: seed = (1+2+3)/3 = 2; alpha = 0.5
        // EMA4 = 0.5*4 + 0.5*2 = 3; EMA5 = 0.5*5 + 0.5*3 = 4
        var ema = FxIndicators.Ema(new double[] { 1, 2, 3, 4, 5, 6 }, 3);

        Assert.Null(ema[0].Value);
        Assert.Null(ema[1].Value);
        Assert.Equal(2.0, ema[2].Value!.Value, 10);
        Assert.Equal(3.0, ema[3].Value!.Value, 10);
        Assert.Equal(4.0, ema[4].Value!.Value, 10);
    }

    [Fact]
    public void Ema_PeriodLongerThanSeries_AllNull()
    {
        var ema = FxIndicators.Ema(new double[] { 1, 2 }, 5);

        Assert.All(ema, p => Assert.Null(p.Value));
    }

    [Fact]
    public void Ema_PeriodOne_TracksTheCloseExactly()
    {
        var closes = new double[] { 10.5, 11.0, 9.5 };
        var ema = FxIndicators.Ema(closes, 1);

        // alpha = 1 → every point equals the close, no warm-up.
        Assert.All(ema.Zip(closes), p => Assert.Equal(p.Second, p.First.Value!.Value, 10));
    }

    [Fact]
    public void Ema_EmptySeries_ReturnsEmpty()
    {
        Assert.Empty(FxIndicators.Ema(Array.Empty<double>(), 5));
    }

    [Fact]
    public void Ema_InvalidPeriod_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FxIndicators.Ema(new double[] { 1 }, 0));
    }

    [Fact]
    public void Bollinger_BracketsTheMean_AtPlusMinusTwoSigma()
    {
        // Deterministic window: 2, 4, 6, 8, 10 → mean 6, population σ = √8
        var closes = new double[] { 2, 4, 6, 8, 10 };
        var bands = FxIndicators.Bollinger(closes, 5, 2.0);

        Assert.Null(bands[3].Middle);
        var (index, middle, upper, lower) = bands[4];
        Assert.Equal(4, index);
        Assert.Equal(6.0, middle!.Value, 10);
        var sd = Math.Sqrt(8.0);
        Assert.Equal(6.0 + 2 * sd, upper!.Value, 10);
        Assert.Equal(6.0 - 2 * sd, lower!.Value, 10);
    }

    [Fact]
    public void Bollinger_ConstantSeries_CollapsesBandsOntoTheMean()
    {
        var bands = FxIndicators.Bollinger(Enumerable.Repeat(100.0, 25).ToArray(), 20, 2.0);

        var last = bands[^1];
        Assert.Equal(100.0, last.Middle!.Value, 10);
        Assert.Equal(100.0, last.Upper!.Value, 10);
        Assert.Equal(100.0, last.Lower!.Value, 10);
    }

    [Fact]
    public void Bollinger_InvalidPeriod_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FxIndicators.Bollinger(new double[] { 1, 2 }, 1));
    }

    [Fact]
    public void Rsi_PureGains_Is100_PureLosses_Is0()
    {
        var up = FxIndicators.Rsi(Enumerable.Range(1, 20).Select(i => (double)i).ToArray(), 14);
        Assert.Equal(100.0, up[^1].Value!.Value, 6);

        var down = FxIndicators.Rsi(Enumerable.Range(1, 20).Select(i => (double)(20 - i)).ToArray(), 14);
        Assert.Equal(0.0, down[^1].Value!.Value, 6);
    }

    [Fact]
    public void Rsi_FlatSeries_Is100_BecauseLossIsZero()
    {
        // A truly flat series has zero loss AND zero gain → avgLoss == 0 → 100
        // (the classic degenerate case; MT5 renders the same).
        var flat = FxIndicators.Rsi(Enumerable.Repeat(50.0, 20).ToArray(), 14);
        Assert.Equal(100.0, flat[^1].Value!.Value, 6);
    }

    [Fact]
    public void Rsi_AlternatingAroundFlat_IsNear50()
    {
        var zigzag = new List<double> { 100 };
        for (var i = 0; i < 24; i++)
        {
            zigzag.Add(zigzag[^1] + (i % 2 == 0 ? 1 : -1));
        }

        var rsi = FxIndicators.Rsi(zigzag.ToArray(), 14);
        Assert.InRange(rsi[^1].Value!.Value, 45, 55);
    }

    [Fact]
    public void Rsi_NullUntilSeeded()
    {
        var rsi = FxIndicators.Rsi(new double[] { 1, 2, 3, 4, 5 }, 3);

        Assert.Null(rsi[0].Value);
        Assert.Null(rsi[1].Value);
        Assert.NotNull(rsi[3].Value);   // first computable after 3 deltas
    }

    [Fact]
    public void Rsi_InvalidPeriod_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FxIndicators.Rsi(new double[] { 1 }, 0));
    }
}
