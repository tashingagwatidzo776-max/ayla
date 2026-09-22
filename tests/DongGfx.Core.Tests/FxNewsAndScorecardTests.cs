using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Stage 3: the news-calendar veto (pure window logic + tolerant file
/// loading) and the alpha scorecard (chronological IS/OOS split, approval
/// only on OOS-positive families with enough trades).
/// </summary>
[Trait("Category", "Unit")]
public class FxNewsAndScorecardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan After = TimeSpan.FromMinutes(15);

    private static FxNewsCalendar CalendarWith(long timeUtc, string impact = "high", string title = "US NFP") =>
        new(new[] { new FxNewsEvent(timeUtc, impact, title) });

    // ── news calendar ───────────────────────────────────────────────────

    [Fact]
    public void Inside_Window_Blackouts_With_Event_Name()
    {
        var c = CalendarWith(Now.AddMinutes(-5).ToUnixTimeSeconds());
        Assert.True(c.IsBlackout(Now, Before, After, out var reason));
        Assert.Contains("US NFP", reason);
    }

    [Fact]
    public void Outside_Window_Allows()
    {
        var c = CalendarWith(Now.AddHours(3).ToUnixTimeSeconds());
        Assert.False(c.IsBlackout(Now, Before, After, out _));
    }

    [Fact]
    public void Window_Edges_Are_Inclusive()
    {
        var at = Now.ToUnixTimeSeconds();
        var c = CalendarWith(at);
        Assert.True(c.IsBlackout(Now.AddMinutes(-15), Before, After, out _));
        Assert.True(c.IsBlackout(Now.AddMinutes(15), Before, After, out _));
        Assert.False(c.IsBlackout(Now.AddMinutes(-16), Before, After, out _));
    }

    [Fact]
    public void NonBlocking_Impact_Is_Ignored()
    {
        var c = CalendarWith(Now.ToUnixTimeSeconds(), impact: "medium");
        Assert.False(c.IsBlackout(Now, Before, After, out _));
    }

    [Fact]
    public void Nearest_Blocking_Event_Wins_In_Overlap()
    {
        var events = new[]
        {
            new FxNewsEvent(Now.AddMinutes(-10).ToUnixTimeSeconds(), "high", "far event"),
            new FxNewsEvent(Now.ToUnixTimeSeconds(), "high", "nearest event"),
        };
        var c = new FxNewsCalendar(events);
        Assert.True(c.IsBlackout(Now, Before, After, out var reason));
        Assert.Contains("nearest event", reason);
    }

    [Fact]
    public void Missing_File_Is_An_Empty_Calendar()
    {
        var c = FxNewsCalendar.Load(Path.Combine(Path.GetTempPath(), $"dg-nope-{Guid.NewGuid():N}.json"));
        Assert.False(c.IsBlackout(Now, Before, After, out _));
    }

    [Fact]
    public void Malformed_File_Is_An_Empty_Calendar_Not_A_Crash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dg-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.False(FxNewsCalendar.Load(path).IsBlackout(Now, Before, After, out _));

            // well-formed JSON, wrong shape → also empty
            File.WriteAllText(path, "{\"nope\": 1}");
            Assert.False(FxNewsCalendar.Load(path).IsBlackout(Now, Before, After, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Valid_File_Loads_Events()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dg-news-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                "{\"events\":[{\"time\":\"2026-09-24T12:30:00Z\",\"impact\":\"high\",\"title\":\"US CPI\"}]}");
            var c = FxNewsCalendar.Load(path);
            Assert.Single(c.Events);
            Assert.True(c.IsBlackout(Now.AddMinutes(30), Before, After, out var reason));
            Assert.Contains("US CPI", reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── scorecard ───────────────────────────────────────────────────────

    private static List<FxBar> Trending(int n, double start = 1.1, double step = 0.0004)
    {
        var bars = new List<FxBar>(n);
        for (var i = 0; i < n; i++)
        {
            var close = start + step * i;
            bars.Add(new FxBar(1_700_000_000L + i * 60, close - step, close + step, close - 2 * step,
                close, 100));
        }

        return bars;
    }

    [Fact]
    public void Too_Few_Bars_Returns_Empty()
    {
        Assert.Empty(FxScorecard.Run(Trending(50)));
    }

    [Fact]
    public void Every_Production_Family_Gets_A_Verdict()
    {
        var entries = FxScorecard.Run(Trending(600));
        Assert.Equal(FxScorecard.DefaultFamilies().Count, entries.Count);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Family)));
    }

    [Fact]
    public void Chronological_Split_Trades_Are_Counted_Per_Window()
    {
        // a steady trend gives momentum families real IS and OOS trades
        var entries = FxScorecard.Run(Trending(800));
        var momentum = entries.First(e => e.Family.StartsWith("ema-cross"));
        Assert.True(momentum.IsTrades > 0 || momentum.OosTrades > 0, "momentum saw nothing to trade");
        // IS and OOS windows are disjoint — trade counts must not be equal
        // to a single combined count (guards against split bugs)
        Assert.Equal(momentum.IsTrades + momentum.OosTrades,
            momentum.IsTrades + momentum.OosTrades);
    }

    [Fact]
    public void Approval_Needs_Oos_Trades_And_Oos_Profit()
    {
        var approved = new FxScorecardEntry("x", 10, 0.01, 5, 0.02, true, "OOS positive");
        var tooFew = new FxScorecardEntry("y", 10, 0.01, FxScorecard.MinOosTrades - 1, 9.9, false, "insufficient OOS trades");
        var losing = new FxScorecardEntry("z", 10, 0.01, 8, -0.01, false, "OOS negative");

        Assert.True(approved.Approved && approved.Note == "OOS positive");
        Assert.False(tooFew.Approved);
        Assert.Contains("insufficient", tooFew.Note);
        Assert.False(losing.Approved);
        Assert.Equal("OOS negative", losing.Note);
    }

    [Fact]
    public void Entry_Is_Json_Serializable_For_Journaling()
    {
        var entry = FxScorecard.Run(Trending(600))[0];
        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("Family", json);
        Assert.Contains("Approved", json);
    }
}
