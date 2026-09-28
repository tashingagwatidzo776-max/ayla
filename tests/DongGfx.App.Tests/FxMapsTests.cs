using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DongGfx.App.Services;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Tests for the market maps (FxMaps) and the tick-archive read side
/// (TickArchiveReader): pure computations over synthetic bars/ticks, with
/// the empty-series honesty guarantees the Maps tab relies on (an empty or
/// degenerate input renders an empty/flat map — never a throw).
/// </summary>
[Trait("Category", "Unit")]
public class FxMapsTests
{
    private static List<FxBar> TrendBars(int count = 120, double start = 100.0)
    {
        var bars = new List<FxBar>();
        var price = start;
        for (var i = 0; i < count; i++)
        {
            var step = (i % 7) < 4 ? 0.4 : -0.3;
            var open = price;
            var close = price + step;
            bars.Add(new FxBar(
                Time: 1790000000L + i * 60,
                Open: open,
                High: Math.Max(open, close) + 0.1,
                Low: Math.Min(open, close) - 0.1,
                Close: close,
                Volume: 10 + (i % 5)));
            price = close;
        }
        return bars;
    }

    private static List<(double Price, double Vol, long TimeMs, int Direction)> TrendTicks(int count = 500)
    {
        var ticks = new List<(double, double, long, int)>();
        var price = 2650.0;
        var direction = 0;
        for (var i = 0; i < count; i++)
        {
            var mid = price + (i % 10) * 0.02;
            direction = i > 0 ? (mid > ticks[^1].Item1 ? 1 : mid < ticks[^1].Item1 ? -1 : direction) : 0;
            ticks.Add((mid, 1.0, 1790000000000L + i * 250, direction));
            price += 0.01;
        }
        return ticks;
    }

    [Fact]
    public void VolumeSurface_CellSums_TradeVolume()
    {
        var ticks = new List<(double, double, long)>
        {
            (100.0, 5, 0),
            (100.0, 7, 1_000),
            (200.0, 3, 1_000),
        };
        var map = FxMaps.VolumeSurface(ticks, priceBuckets: 2, timeBuckets: 2);
        // Price bucket 0 gets 5+7=12, bucket 1 gets 3.
        var col0 = 0.0;
        var col1 = 0.0;
        for (var r = 0; r < 2; r++)
        {
            col0 += map.Cells[0, r];
            col1 += map.Cells[1, r];
        }
        Assert.Equal(12, col0);
        Assert.Equal(3, col1);
        // Min/Max describe CELL values (the renderer normalizes per cell),
        // not column sums: the hottest single cell is the 7-lot tick.
        Assert.Equal(3, map.Min);
        Assert.Equal(7, map.Max);
    }

    [Fact]
    public void VolumeSurface_EmptyTicks_ZeroGrid()
    {
        var map = FxMaps.VolumeSurface([]);
        Assert.Equal(0, map.Min);
        Assert.Equal(0, map.Max);
        Assert.Equal(0, map.Cells[0, 0]);
    }

    [Fact]
    public void VolumeSurfaceSplit_BuyShare_InZeroOne()
    {
        var ticks = TrendTicks();
        var (map, share) = FxMaps.VolumeSurfaceSplit(ticks);
        Assert.Equal(map.Columns, share.GetLength(0));
        for (var c = 0; c < share.GetLength(0); c++)
        {
            for (var r = 0; r < share.GetLength(1); r++)
            {
                Assert.InRange(share[c, r], 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void OrderFlow_UpticksAccumulatePositive()
    {
        var ticks = new List<(double, double, long)>
        {
            (100.0, 1, 0),
            (101.0, 5, 1),   // uptick → +5
            (102.0, 4, 2),   // uptick → +4
            (101.5, 3, 3),   // downtick → −3
        };
        var map = FxMaps.OrderFlow(ticks, priceBuckets: 2);
        var total = 0.0;
        for (var c = 0; c < 2; c++)
        {
            total += map.Cells[c, 0];
        }
        Assert.Equal(6, total, 6);
    }

    [Fact]
    public void Liquidity_AveragesSpreadPerBucket()
    {
        var quotes = new List<(double Bid, double Ask, long TimeMs)>
        {
            (99.9, 100.1, 0),   // mid 100.0, spread 0.2
            (99.9, 100.3, 1),   // mid 100.1, spread 0.4
            (199.9, 200.1, 2),  // mid 200.0, spread 0.2
        };
        var map = FxMaps.Liquidity(quotes, priceBuckets: 2);
        // Direct per-bucket asserts (min/max wrappers can't distinguish them).
        Assert.Equal(0.3, map.Cells[0, 0], 6);   // bucket 0: avg(0.2, 0.4)
        Assert.Equal(0.2, map.Cells[1, 0], 6);   // bucket 1: single quote
        Assert.Equal(0.2, map.Min, 6);
        Assert.Equal(0.3, map.Max, 6);
    }

    [Fact]
    public void Volatility_RmsParkinson_NonNegative()
    {
        var map = FxMaps.Volatility(TrendBars());
        var anyPositive = false;
        for (var c = 0; c < map.Columns; c++)
        {
            Assert.True(map.Cells[c, 0] >= 0);
            anyPositive |= map.Cells[c, 0] > 0;
        }
        Assert.True(anyPositive);
    }

    [Fact]
    public void Correlation_PerfectlyCorrelatedSeries_IsOne()
    {
        var a = Enumerable.Range(0, 60).Select(i => 100.0 + i).ToList();
        var b = Enumerable.Range(0, 60).Select(i => 50.0 + 2 * i).ToList();
        var map = FxMaps.Correlation(
            new (string, IReadOnlyList<double>)[] { ("A", a), ("B", b) });
        Assert.Equal(1.0, map.Cells[0, 0], 6);
    }

    [Fact]
    public void SmcIct_DetectsSweepShape()
    {
        // Prior 10-bar high = 110. A bar that wicks to 111 and closes at 109
        // is a sweep; a bar entirely inside is not.
        var bars = new List<FxBar>();
        for (var i = 0; i < 10; i++)
        {
            bars.Add(new FxBar(i, 105, 110, 100, 106, 1));
        }
        bars.Add(new FxBar(10, 108, 111, 107, 109, 1));   // sweep up, closes back in
        bars.Add(new FxBar(11, 108, 109, 104, 105, 1));   // normal bar
        var map = FxMaps.SmcIct(bars, columns: 4, swingLookback: 10);
        var sweepSeen = false;
        for (var c = 0; c < 4; c++)
        {
            sweepSeen |= map.Cells[c, 0] > 0;
        }
        Assert.True(sweepSeen);
        Assert.Equal(1.0, map.Max, 6);
    }

    [Fact]
    public void AiProbability_BeliefsLandInCells()
    {
        var map = FxMaps.AiProbability(
            new (string, double)[] { ("XAUUSDmicro", 0.8), ("EURUSD", 0.3) });
        Assert.Equal(0.8, map.Cells[0, 0], 6);
        Assert.Equal(0.3, map.Cells[1, 0], 6);
    }

    [Fact]
    public void AllMaps_EmptyInputs_NeverThrow()
    {
        var empty = Array.Empty<FxBar>();
        _ = FxMaps.VolumeSurface([]);
        _ = FxMaps.VolumeProfile(empty);
        _ = FxMaps.Liquidity([]);
        _ = FxMaps.OrderFlow([]);
        _ = FxMaps.Volatility(empty);
        _ = FxMaps.Correlation([]);
        _ = FxMaps.SmcIct(empty);
        _ = FxMaps.AiProbability([]);
        _ = FxMaps.Confluence([]);
    }

    [Fact]
    public void Confluence_EmptyFrames_NeutralMap()
    {
        var map = FxMaps.Confluence([]);
        Assert.Equal(1, map.Rows);
        Assert.Equal(0, map.Cells.Cast<double>().Select(Math.Abs).Sum(), 6);
    }

    [Fact]
    public void Confluence_TrendFrame_ScoresSignOfTrend()
    {
        var bars = TrendBars(120);   // net uptrend by construction
        var map = FxMaps.Confluence(new[] { ("M1", (IReadOnlyList<FxBar>)bars) });
        Assert.Equal(1, map.Rows);
        var scored = map.Cells.Cast<double>().Where(v => v != 0).ToList();
        Assert.NotEmpty(scored);                      // some cells actually scored
        Assert.True(scored.Average() > 0,             // net trend sign visible
            $"expected net positive, got {scored.Average():0.####}");
    }

    [Fact]
    public void Confluence_MultiTimeframe_AllFramesGetRows()
    {
        var bars = TrendBars(120);
        var map = FxMaps.Confluence(
        [
            ("M1", (IReadOnlyList<FxBar>)bars),
            ("M5", (IReadOnlyList<FxBar>)bars),
            ("M15", Array.Empty<FxBar>()),            // thin tf: neutral row
        ]);
        Assert.Equal(3, map.Rows);
        // Row 2 (empty frame) stays all-zero: honest neutral, never fake.
        Assert.Equal(0, Enumerable.Range(0, map.Columns).Sum(c => Math.Abs(map.Cells[c, 2])), 6);
    }

    [Fact]
    public void Confluence_ThinHistory_NeutralNotFake()
    {
        var thin = TrendBars(10);   // below slow-EMA warm-up (21)
        var map = FxMaps.Confluence(new[] { ("M1", (IReadOnlyList<FxBar>)thin) });
        Assert.Equal(0, map.Cells.Cast<double>().Select(Math.Abs).Sum(), 6);
    }
}

[Trait("Category", "Unit")]
public class TickArchiveReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tf_ticks_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LoadFile_ParsesAndClassifies()
    {
        var venueDir = Path.Combine(_root, "mt5");
        Directory.CreateDirectory(venueDir);
        var path = Path.Combine(venueDir, "XAUUSDmicro_20260927.jsonl");
        File.WriteAllLines(path,
        [
            """{"b":100.0,"a":100.2,"t":1000}""",
            """{"b":100.3,"a":100.5,"t":1100}""",   // uptick → buy
            """{"b":100.1,"a":100.3,"t":1200}""",   // downtick → sell
            "not json",
            """{"b":0,"a":0,"t":1300}""",           // degenerate, skipped
        ]);

        var ticks = TickArchiveReader.LoadFile(path);
        Assert.Equal(3, ticks.Count);
        Assert.Equal(0, ticks[0].Direction);    // first tick: unknown
        Assert.Equal(1, ticks[1].Direction);
        Assert.Equal(-1, ticks[2].Direction);
        Assert.All(ticks, t => Assert.Equal(1.0, t.Vol));
    }

    [Fact]
    public void LoadFile_MissingFile_EmptyList()
    {
        Assert.Empty(TickArchiveReader.LoadFile(Path.Combine(_root, "nope.jsonl")));
    }

    [Fact]
    public void LoadToday_BuildsPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mt5"));
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        File.WriteAllText(
            Path.Combine(_root, "mt5", $"XAUUSDmicro_{today}.jsonl"),
            """{"b":1.0,"a":1.1,"t":5}""" + "\n");
        var ticks = TickArchiveReader.LoadToday(_root, "mt5", "XAUUSDmicro");
        Assert.Single(ticks);
        Assert.Equal(1.05, ticks[0].Price, 6);
        Assert.Equal(5, ticks[0].TimeMs);
    }

        [Fact]
    public void LoadQuotesToday_TrueSpread()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mt5"));
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        File.WriteAllLines(Path.Combine(_root, "mt5", $"XAUUSDmicro_{today}.jsonl"),
        [
            """{"b":2650.1,"a":2650.5,"t":1000}""",
            """{"b":0,"a":0,"t":1100}""",               // degenerate, skipped
            """{"b":2650.2,"a":2650.1,"t":1200}""",     // crossed, skipped
        ]);

        var quotes = TickArchiveReader.LoadQuotesToday(_root, "mt5", "XAUUSDmicro");
        Assert.Single(quotes);
        Assert.Equal(2650.1, quotes[0].Bid, 6);
        Assert.Equal(2650.5, quotes[0].Ask, 6);
        Assert.Equal(1000, quotes[0].TimeMs);
        Assert.Empty(TickArchiveReader.LoadQuotesToday(_root, "mt5", "NOSUCH"));
    }
}
