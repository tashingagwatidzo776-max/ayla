using System;
using System.Collections.Generic;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The soak bar's provenance timeline: the engine journals every restore and
/// every restart (build change, account switch), and the timeline projects
/// those FX_MODE rows back into newest-first rows so the bar's history is
/// auditable in-app. Only the restore/reset sentences qualify.
/// </summary>
[Trait("Category", "Unit")]
public class SoakTimelineTests
{
    private static JournalEntry Entry(DateTimeOffset at, string category, string details) =>
        new() { Timestamp = at, Category = category, Details = details };

    [Fact]
    public void Reads_Restores_And_Resets_Newest_First_And_Skips_Other_Rows()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<JournalEntry>
        {
            // newest first, as TradeJournal.GetRecent returns them
            Entry(now, "FX_MODE",
                "paper soak restored 4/10 on EURUSD (build 0.9.0+abc123): {\"Symbol\":\"EURUSD\"}"),
            Entry(now.AddMinutes(-1), "FX_MODE",
                "go-live refused — paper soak incomplete (3/10 signals)"),
            Entry(now.AddMinutes(-2), "FX_MODE",
                "paper soak counters reset — account changed since 111"),
            Entry(now.AddMinutes(-3), "FX_ORDER",
                "paper soak restored 1/10 on XAUUSDmicro (build x)"),   // wrong category
            Entry(now.AddMinutes(-4), "FX_MODE",
                "paper soak counters reset — build changed since 0.9.0+old (now 0.9.0+abc123): {\"x\":1}"),
        };

        var rows = SoakTimeline.FromJournal(entries, max: 10);

        Assert.Equal(3, rows.Count);
        Assert.Equal("paper soak restored 4/10 on EURUSD (build 0.9.0+abc123)", rows[0].Text);
        Assert.False(rows[0].Restart);
        Assert.Equal("paper soak counters reset — account changed since 111", rows[1].Text);
        Assert.True(rows[1].Restart);
        Assert.Equal(
            "paper soak counters reset — build changed since 0.9.0+old (now 0.9.0+abc123)",
            rows[2].Text);
        Assert.True(rows[2].Restart);
    }

    [Fact]
    public void Caps_At_Max_And_Tolerates_Empty_Or_Unrelated_Input()
    {
        Assert.Empty(SoakTimeline.FromJournal(null));
        Assert.Empty(SoakTimeline.FromJournal(Array.Empty<JournalEntry>()));
        Assert.Empty(SoakTimeline.FromJournal(
            new[] { Entry(DateTimeOffset.UtcNow, "FX_MODE", "something else entirely") }));

        var many = new List<JournalEntry>();
        for (var i = 0; i < 20; i++)
        {
            many.Add(Entry(DateTimeOffset.UtcNow.AddSeconds(-i), "FX_MODE",
                $"paper soak restored {i}/10 on S (build b)"));
        }

        Assert.Equal(6, SoakTimeline.FromJournal(many).Count);          // default cap
        Assert.Equal(2, SoakTimeline.FromJournal(many, max: 2).Count);
        Assert.Empty(SoakTimeline.FromJournal(many, max: 0));
    }
}
