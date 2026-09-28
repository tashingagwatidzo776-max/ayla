using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>A heatmap grid plus its axis labels, ready to render.</summary>
public sealed record FxHeatMap(
    string Name,
    string XAxisLabel,
    string YAxisLabel,
    int Columns,
    int Rows,
    double[,] Cells,
    double Min,
    double Max,
    string Unit)
{
    /// <summary>Normalized 0..1 intensity for rendering (min-max scaled).</summary>
    public double Normalized(int col, int row)
    {
        var span = Max - Min;
        return span <= 1e-12 ? 0.0 : (Cells[col, row] - Min) / span;
    }
}



/// <summary>
/// The DON G FX market maps: every surface is a PURE function of the bars
/// and/or ticks handed in — no I/O, no bridge, no clock. All maps tolerate
/// degenerate input (empty series → an empty 0×0 grid, never a throw) so a
/// map tab is always renderable, even mid-session.
///
/// Sources: bar OHLCV comes from the bridge candles; the tick-level maps
/// (order-flow, liquidity, volume surface) use the TickArchive jsonl ticks
/// converted by the caller into (Price, Volume, Time) tuples with the tick
/// rule classifying direction.
/// </summary>
public static class FxMaps
{
/// <summary>Builds a volume surface: price buckets × time buckets, cell
/// volume = traded volume whose price fell in the bucket. Direction split
/// (buy/sell via the tick rule) is applied when <paramref name="ticks"/>
/// arrive pre-classified; the plain surface sums |volume| per cell.</summary>
public static FxHeatMap VolumeSurface(
    IReadOnlyList<(double Price, double Vol, long TimeMs)> ticks,
    int priceBuckets = 24, int timeBuckets = 24)
{
    var (cells, written) = BuildSurfaceCells(ticks, priceBuckets, timeBuckets,
        (acc, _, vol) => acc + vol);
    return GridToHeatMap("Volume surface", "time →", "price ↑", cells, "vol", written);
}

/// <summary>Volume surface WITH the buy/sell split: returns the total-volume
/// heatmap plus a parallel grid of buy-share per cell (0..1; 0.5 where the
/// cell is empty — neutral, never fake direction).</summary>
public static (FxHeatMap Map, double[,] BuyShare) VolumeSurfaceSplit(
    IReadOnlyList<(double Price, double Vol, long TimeMs, int Direction)> ticks,
    int priceBuckets = 24, int timeBuckets = 24)
{
    var (totals, written) = BuildSurfaceCells(
        ticks.Select(t => (t.Price, t.Vol, t.TimeMs)).ToList(), priceBuckets, timeBuckets,
        (acc, _, vol) => acc + vol);
    var (buys, _) = BuildSurfaceCells(
        ticks.Where(t => t.Direction > 0).Select(t => (t.Price, t.Vol, t.TimeMs)).ToList(),
        priceBuckets, timeBuckets, (acc, _, vol) => acc + vol);

    var buyShare = new double[priceBuckets, timeBuckets];
    for (var c = 0; c < priceBuckets; c++)
    {
        for (var r = 0; r < timeBuckets; r++)
        {
            buyShare[c, r] = totals[c, r] > 0 ? buys[c, r] / totals[c, r] : 0.5;
        }
    }
    return (GridToHeatMap("Volume surface (buy/sell)", "time →", "price ↑", totals, "vol", written), buyShare);
}

/// <summary>Accumulates one double per (price, time) cell from the ticks;
/// empty series yields the full-size zero grid (a map is always renderable).
/// Also returns the written-mask so Min/Max can ignore never-touched
/// buckets instead of letting them pin the scale's floor to 0.</summary>
private static (double[,] Cells, bool[,] Written) BuildSurfaceCells(
    IReadOnlyList<(double Price, double Vol, long TimeMs)> ticks,
    int priceBuckets, int timeBuckets,
    Func<double, (double Price, double Vol, long TimeMs), double, double> accumulate)
{
    var cells = new double[priceBuckets, timeBuckets];
    var written = new bool[priceBuckets, timeBuckets];
    if (ticks.Count == 0)
    {
        return (cells, written);
    }

    var prices = ticks.Select(t => t.Price).ToList();
    var lo = prices.Min();
    var span = Math.Max(prices.Max() - lo, 1e-12);
    var tLo = ticks.Min(t => t.TimeMs);
    var tSpan = Math.Max(ticks.Max(t => t.TimeMs) - tLo, 1);

    foreach (var t in ticks)
    {
        var pc = Math.Clamp((int)((t.Price - lo) / span * priceBuckets), 0, priceBuckets - 1);
        var tc = Math.Clamp((int)((t.TimeMs - tLo) / (double)tSpan * timeBuckets), 0, timeBuckets - 1);
        cells[pc, tc] = accumulate(cells[pc, tc], t, t.Vol);
        written[pc, tc] = true;
    }
    return (cells, written);
}

private static FxHeatMap GridToHeatMap(
    string name, string xAxis, string yAxis, double[,] source, string unit,
    bool[,]? written = null)
{
    var cols = source.GetLength(0);
    var rows = source.GetLength(1);
    double min = double.PositiveInfinity, max = double.NegativeInfinity;
    for (var c = 0; c < cols; c++)
    {
        for (var r = 0; r < rows; r++)
        {
            if (double.IsNaN(source[c, r])
                || written is not null && !written[c, r])
            {
                continue;   // never-touched bucket: outside the value range
            }
            min = Math.Min(min, source[c, r]);
            max = Math.Max(max, source[c, r]);
        }
    }
    if (cols == 0 || rows == 0 || min > max)
    {
        min = max = 0;
    }
    return new FxHeatMap(name, xAxis, yAxis, cols, rows, source, min, max, unit);
}

    /// <summary>Liquidity map: per price bucket, the spread cost proxy —
    /// average (ask−bid) width when the mid traded in that bucket. Thin
    /// (high-cost) cells render hot; deep cells render cool.</summary>
    public static FxHeatMap Liquidity(
        IReadOnlyList<(double Bid, double Ask, long TimeMs)> quotes,
        int priceBuckets = 24)
    {
        var cells = new double[priceBuckets];
        var counts = new int[priceBuckets];
        if (quotes.Count > 0)
        {
            var mids = quotes.Select(q => (q.Bid + q.Ask) / 2.0).ToList();
            var lo = mids.Min();
            var span = Math.Max(mids.Max() - lo, 1e-12);
            foreach (var q in quotes)
            {
                var mid = (q.Bid + q.Ask) / 2.0;
                var c = Math.Clamp((int)((mid - lo) / span * priceBuckets), 0, priceBuckets - 1);
                cells[c] += q.Ask - q.Bid;
                counts[c]++;
            }
            for (var c = 0; c < priceBuckets; c++)
            {
                cells[c] = counts[c] > 0 ? cells[c] / counts[c] : double.NaN;
            }
        }
        return Finish("Liquidity (avg spread)", "price ↑", cols: priceBuckets, rows: 1, cells, "spread");
    }

    /// <summary>Volume profile: volume per price bucket across the whole
    /// series (the classic horizontal histogram, 1 row).</summary>
    public static FxHeatMap VolumeProfile(
        IReadOnlyList<FxBar> bars, int priceBuckets = 24)
    {
        var cells = new double[priceBuckets];
        if (bars.Count > 0)
        {
            var lo = bars.Min(b => b.Low);
            var span = Math.Max(bars.Max(b => b.High) - lo, 1e-12);
            foreach (var b in bars)
            {
                var c = Math.Clamp((int)((b.Typical - lo) / span * priceBuckets), 0, priceBuckets - 1);
                cells[c] += b.Volume;
            }
        }
        return Finish("Volume profile", "price ↑", cols: priceBuckets, rows: 1, cells, "vol");
    }

    /// <summary>Order-flow map: signed volume (tick rule) per price bucket —
    /// buy pressure renders positive, sell negative.</summary>
    public static FxHeatMap OrderFlow(
        IReadOnlyList<(double Price, double Vol, long TimeMs)> ticks,
        int priceBuckets = 24)
    {
        var cells = new double[priceBuckets];
        if (ticks.Count > 1)
        {
            var prices = ticks.Select(t => t.Price).ToList();
            var lo = prices.Min();
            var span = Math.Max(prices.Max() - lo, 1e-12);
            for (var i = 1; i < ticks.Count; i++)
            {
                var signed = ticks[i].Price > ticks[i - 1].Price ? ticks[i].Vol
                           : ticks[i].Price < ticks[i - 1].Price ? -ticks[i].Vol
                           : 0;
                if (signed == 0)
                {
                    continue;
                }
                var c = Math.Clamp((int)((ticks[i].Price - lo) / span * priceBuckets), 0, priceBuckets - 1);
                cells[c] += signed;
            }
        }
        return Finish("Order flow (signed volume)", "price ↑", cols: priceBuckets, rows: 1, cells, "signed vol");
    }

    /// <summary>Volatility map: per time bucket, the Parkinson high-low
    /// estimator of the bars inside it. Quiet cells cool, violent cells hot.</summary>
    public static FxHeatMap Volatility(IReadOnlyList<FxBar> bars, int timeBuckets = 24)
    {
        var cells = new double[timeBuckets];
        var counts = new int[timeBuckets];
        if (bars.Count > 0)
        {
            var tLo = bars.Min(b => b.Time);
            var tSpan = Math.Max(bars.Max(b => b.Time) - tLo, 1);
            foreach (var b in bars)
            {
                var c = Math.Clamp((int)((b.Time - tLo) / (double)tSpan * timeBuckets), 0, timeBuckets - 1);
                if (b.Low > 0)
                {
                    cells[c] += Math.Log(b.High / b.Low);
                    counts[c]++;
                }
            }
            for (var c = 0; c < timeBuckets; c++)
            {
                if (counts[c] > 0)
                {
                    cells[c] = Math.Sqrt(Math.Max(cells[c] / counts[c], 0)); // RMS-ish Parkinson
                }
            }
        }
        return Finish("Volatility (Parkinson)", "time →", cols: timeBuckets, rows: 1, cells, "σ");
    }

    /// <summary>Correlation map: rolling correlation of each pair of
    /// symbols' bar closes (columns = pairs, one row). Requires equal-length
    /// close series; shorter series are padded by repeating the first close.</summary>
    public static FxHeatMap Correlation(
        IReadOnlyList<(string Symbol, IReadOnlyList<double> Closes)> series,
        int window = 50)
    {
        var pairs = new List<(string A, string B)>();
        for (var i = 0; i < series.Count; i++)
        {
            for (var j = i + 1; j < series.Count; j++)
            {
                pairs.Add((series[i].Symbol, series[j].Symbol));
            }
        }

        var cells = new double[Math.Max(pairs.Count, 1)];
        for (var p = 0; p < pairs.Count; p++)
        {
            var a = series.First(s => s.Symbol == pairs[p].A).Closes;
            var b = series.First(s => s.Symbol == pairs[p].B).Closes;
            cells[p] = Pearson(a, b, window);
        }
        if (pairs.Count == 0)
        {
            cells[0] = 0;
        }
        var name = pairs.Count > 0
            ? "Correlation: " + string.Join(", ", pairs.Select(p => $"{p.A}/{p.B}"))
            : "Correlation (no pairs)";
        return Finish(name, "pair →", cols: Math.Max(pairs.Count, 1), rows: 1, cells, "ρ");
    }

    private static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b, int window)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n < 5)
        {
            return 0;
        }
        var use = Math.Min(window, n);
        var xs = a.Skip(n - use).ToList();
        var ys = b.Skip(n - use).ToList();
        var mx = xs.Average();
        var my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < use; i++)
        {
            var dx = xs[i] - mx;
            var dy = ys[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        return sxx > 1e-18 && syy > 1e-18
            ? Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1, 1)
            : 0;
    }

    /// <summary>SMC/ICT map: per bar bucket, a 0..1 score for the classic
    /// liquidity-sweep shape — a wick beyond the prior N-bar extreme that
    /// closes back inside (stop-run rejection). Columns = bars; hot cells
    /// mark candidate sweep/reversal zones.</summary>
    public static FxHeatMap SmcIct(IReadOnlyList<FxBar> bars, int columns = 48, int swingLookback = 10)
    {
        var cells = new double[columns];
        var counts = new int[columns];
        if (bars.Count > swingLookback + 1)
        {
            var tLo = bars[0].Time;
            var tSpan = Math.Max(bars[^1].Time - tLo, 1);
            for (var i = swingLookback; i < bars.Count; i++)
            {
                var priorHigh = bars.Skip(i - swingLookback).Take(swingLookback).Max(b => b.High);
                var priorLow = bars.Skip(i - swingLookback).Take(swingLookback).Min(b => b.Low);
                var b = bars[i];
                var sweepUp = b.High > priorHigh && b.Close < priorHigh;   // ran stops, closed back in
                var sweepDn = b.Low < priorLow && b.Close > priorLow;
                var score = sweepUp || sweepDn ? 1.0 : 0.0;
                var c = Math.Clamp((int)((b.Time - tLo) / (double)tSpan * columns), 0, columns - 1);
                cells[c] += score;
                counts[c]++;
            }
            for (var c = 0; c < columns; c++)
            {
                cells[c] = counts[c] > 0 ? 1.0 : double.NaN;   // presence: this window swept or not
            }
        }
        return Finish("SMC/ICT liquidity sweeps", "time →", cols: columns, rows: 1, cells, "density");
    }

    /// <summary>Multi-timeframe confluence map: one ROW per timeframe
    /// (oldest → newest), one COLUMN per time slice. Each cell scores
    /// −1..+1 for that timeframe's trend over its bars in that slice:
    /// EMA(fast) vs EMA(slow) sign, weighted by how far apart they are.
    /// Row-level agreement across timeframes (all rows same sign) is the
    /// classic MTF confluence signal — visible at a glance as solid rows.
    /// Timeframes with no bars in a slice render 0 (neutral), never fake
    /// agreement.</summary>
    public static FxHeatMap Confluence(
        IReadOnlyList<(string Timeframe, IReadOnlyList<FxBar> Bars)> frames,
        int columns = 24, int fast = 8, int slow = 21)
    {
        var rows = Math.Max(frames.Count, 1);
        var cells = new double[columns, rows];
        double min = 0, max = 0;

        for (var r = 0; r < frames.Count; r++)
        {
            var bars = frames[r].Bars;
            if (bars.Count < slow + 1)
            {
                continue;   // row stays neutral — not enough history for the slow EMA
            }

            var tLo = bars[0].Time;
            var tSpan = Math.Max(bars[^1].Time - tLo, 1);
            var closes = bars.Select(b => b.Close).ToList();
            var emaFast = Ema(closes, fast);
            var emaSlow = Ema(closes, slow);

            for (var c = 0; c < columns; c++)
            {
                // Bars whose timestamps fall in this slice.
                var lo = tLo + (long)((double)tSpan * c / columns);
                var hi = tLo + (long)((double)tSpan * (c + 1) / columns);
                var idx = IndexOfRange(bars, lo, hi);
                if (idx < slow)
                {
                    continue;   // warm-up: neutral cell
                }
                var gap = emaFast[idx] - emaSlow[idx];
                var scale = Math.Max(Math.Abs(emaSlow[idx]), 1e-12);
                var score = Math.Clamp(gap / (scale * 0.005), -1, 1);  // 0.5% gap = full conviction
                cells[c, r] = score;
                min = Math.Min(min, score);
                max = Math.Max(max, score);
            }
        }

        var name = frames.Count > 0
            ? "MTF confluence: " + string.Join("/", frames.Select(f => f.Timeframe))
            : "MTF confluence (no frames)";
        return new FxHeatMap(name, "time →", "tf (old→new)", columns, rows, cells, min, max, "trend −1..+1");
    }

    /// <summary>Index of the last bar whose Time is in [lo, hi); falls back
    /// to the bar nearest the slice end so thin tapes still score.</summary>
    private static int IndexOfRange(IReadOnlyList<FxBar> bars, long lo, long hi)
    {
        for (var i = bars.Count - 1; i >= 0; i--)
        {
            if (bars[i].Time < hi)
            {
                return i;
            }
        }
        return -1;
    }

    private static double[] Ema(IReadOnlyList<double> v, int period)
    {
        var outSeries = new double[v.Count];
        if (v.Count < period || period <= 0)
        {
            return outSeries;
        }
        var k = 2.0 / (period + 1);
        var sum = 0.0;
        for (var i = 0; i < period; i++)
        {
            sum += v[i];
        }
        outSeries[period - 1] = sum / period;
        for (var i = period; i < v.Count; i++)
        {
            outSeries[i] = outSeries[i - 1] + k * (v[i] - outSeries[i - 1]);
        }
        return outSeries;
    }

    /// <summary>AI-probability map: the alpha scorecard's per-symbol edge
    /// estimate laid out as one cell per symbol — what the brain currently
    /// believes about each market, on the same canvas. Values come from the
    /// caller (the scorecard service), keeping this file UI-free.</summary>
    public static FxHeatMap AiProbability(IReadOnlyList<(string Symbol, double Probability)> beliefs)
    {
        var cols = Math.Max(beliefs.Count, 1);
        var cells = new double[cols];
        var names = beliefs.Select(b => b.Symbol).ToList();
        for (var i = 0; i < beliefs.Count; i++)
        {
            cells[i] = beliefs[i].Probability;
        }
        if (beliefs.Count == 0)
        {
            cells[0] = 0;
        }
        var name = beliefs.Count > 0
            ? "AI probability: " + string.Join(", ", names)
            : "AI probability (no beliefs yet)";
        return Finish(name, "symbol →", cols: cols, rows: 1, cells, "P(win)");
    }

    private static FxHeatMap Finish(
        string name, string xAxis, int cols, int rows, double[] flat, string unit)
    {
        var cells = new double[cols, rows];
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var c = 0; c < cols; c++)
        {
            cells[c, 0] = flat[c];
            if (double.IsNaN(flat[c]))
            {
                continue;   // never-touched bucket: outside the value range
            }
            min = Math.Min(min, flat[c]);
            max = Math.Max(max, flat[c]);
        }
        if (cols == 0 || rows == 0 || min > max)
        {
            min = max = 0;   // no data at all: a flat, honest zero range
        }
        return new FxHeatMap(name, xAxis, "—", cols, rows, cells, min, max, unit);
    }
}
