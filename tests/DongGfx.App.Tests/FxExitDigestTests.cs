using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The exit brain's evidence tooling: the weekly FX_EXIT digest (override vs
/// consensus split, exit reasons, MAE curve, round-trips), the shadow
/// engines' promotion ledger, and the settings round-trip of the brain's
/// persisted running state. Journal-only by construction.
/// </summary>
[Trait("Category", "Unit")]
public class FxExitDigestTests
{
    /// <summary>An FX_EXIT payload shaped exactly like the engine host
    /// writes it (message prefix + JSON with Action/Override/MfeR/MaeR/
    /// ProfitR/Votes[Engine,Exit,Weight]).</summary>
    private static JournalEntry ExitEntry(
        string action, string? overrideEngine, double maeR, double mfeR,
        double profitR, int daysAgo = 0,
        params (string Engine, double Exit, double Weight)[] votes) => new()
    {
        Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysAgo),
        Category = "FX_EXIT",
        Details = $"XAUUSD #42: {action} score 61 — "
            + JsonSerializer.Serialize(new
            {
                Ticket = 42L,
                Symbol = "XAUUSDmicro",
                Side = "buy",
                Action = action,
                Score = 61.0,
                MfeR = mfeR,
                MaeR = maeR,
                ProfitR = profitR,
                Override = overrideEngine,
                Votes = votes.Select(v => new
                {
                    Engine = v.Engine,
                    Exit = v.Exit,
                    Weight = v.Weight,
                    Reason = "r",
                }).ToList(),
            }),
    };

    [Fact]
    public void Build_Empty_When_No_Exit_Entries()
    {
        var (msg, md) = FxExitWeeklyDigest.Build([], DateTimeOffset.UtcNow);
        Assert.Equal(string.Empty, msg);
        Assert.Equal(string.Empty, md);
    }

    /// <summary>An FX_PROFIT payload shaped exactly like the engine host
    /// writes it (message prefix + State/ProfitScore/FloorBreached JSON).</summary>
    private static JournalEntry ProfitEntry(string state, double score, bool breached, int daysAgo = 0) => new()
    {
        Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysAgo),
        Category = "FX_PROFIT",
        Details = $"XAUUSD #42: {state} +1.0R — "
            + System.Text.Json.JsonSerializer.Serialize(new
            {
                State = state,
                ProfitScore = score,
                FloorBreached = breached,
            }),
    };

    [Fact]
    public void Build_Reports_Profit_Capture_And_Profit_Brain_Telemetry()
    {
        // Capture: banked 1.25R of 3.0R available = 42%.
        var entries = new List<JournalEntry>
        {
            ExitEntry("full", "drawdown", maeR: 0.4, mfeR: 2.0, profitR: 1.0),
            ExitEntry("full", null, maeR: 0.3, mfeR: 1.0, profitR: 0.25,
                votes: [("structure", 0.9, 1.6)]),
            ProfitEntry("PROFIT_PROTECTED", 80, breached: false),
            ProfitEntry("PROFIT_EXIT_READY", 45, breached: true),
        };
        var (msg, md) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);

        Assert.Contains("profit capture 42%", msg);
        Assert.Contains("Profit brain: 2 report(s)", msg);
        Assert.Contains("floor breached 1", msg);
        Assert.Contains("avg score", msg);
        Assert.Contains("profit capture", md);
        Assert.Contains("PROFIT_EXIT_READY ×1", md);
        Assert.Contains("PROFIT_PROTECTED ×1", md);
    }

    [Fact]
    public void Build_Profit_Telemetry_Silent_Without_FX_PROFIT_Entries()
    {
        var (msg, md) = FxExitWeeklyDigest.Build(
            new[] { ExitEntry("hold", null, 0.1, 0.2, 0.1) }, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("Profit brain", msg);
        Assert.DoesNotContain("Profit brain", md);
        Assert.Contains("profit capture", md);   // the capture line always shows
    }

    [Fact]
    public void Build_Every_Digest_Points_At_The_Soak_Index()
    {
        var (msg, md) = FxExitWeeklyDigest.Build(
            new[] { ExitEntry("hold", null, 0.1, 0.2, 0.1) }, DateTimeOffset.UtcNow);

        Assert.Contains("### Evidence index", md);
        Assert.Contains("docs/soak/README.md", md);
        Assert.DoesNotContain("Evidence index", msg); // webhook message stays lean
    }

    // ── the giveback engine's promotion review ───────────────────────

    private static JournalEntry CloseEntry(long ticket, int daysAgo = 0) => new()
    {
        Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysAgo),
        Category = "FX_EXIT",
        Details = $"XAUUSD: closed #{ticket} — deal 555",
    };

    [Fact]
    public void PromotionReview_Silent_Below_Both_Conditions()
    {
        var entries = new[] { ExitEntry("hold", null, 0.1, 3.2, 2.9) };
        Assert.Null(FxExitWeeklyDigest.PromotionReview(entries));
    }

    [Fact]
    public void PromotionReview_Fires_On_The_First_Verified_Save()
    {
        var entries = new[]
        {
            ExitEntry("full", "profit-floor", maeR: 0.1, mfeR: 8.0, profitR: 0.4),
            CloseEntry(42),
        };
        var review = FxExitWeeklyDigest.PromotionReview(entries);

        Assert.NotNull(review);
        Assert.Contains("first verified save: ticket 42 (profit-floor override)", review);
        Assert.Contains("decisive settled exits 1/100", review);
        Assert.Contains("GIVEBACK-PROMOTION-CASE.md", review);
    }

    [Fact]
    public void PromotionReview_Fires_At_30_Decisive_Exits_Without_A_Save()
    {
        var entries = new List<JournalEntry>();
        for (var i = 0; i < 30; i++)
        {
            entries.Add(ExitEntry("full", null, 0.2, 3.0, 2.0));
        }

        var review = FxExitWeeklyDigest.PromotionReview(entries);
        Assert.NotNull(review);
        Assert.Contains("no verified save yet", review);
        Assert.Contains("decisive settled exits 30/100", review);
    }

    [Fact]
    public void PromotionReview_DeepVote_Save_Needs_A_Decisive_Close()
    {
        // A 0.85 giveback vote on a HOLD row is watch pressure, not a save —
        // no close for that ticket either, so nothing fires.
        var entries = new[]
        {
            ExitEntry("hold", null, 0.1, 8.0, 1.5, 0,
                ("drawdown", 0.85, 2.0)),
            ExitEntry("full", null, 0.2, 3.0, 2.0),
        };
        Assert.Null(FxExitWeeklyDigest.PromotionReview(entries));
    }

    [Fact]
    public void Build_Splits_Overrides_From_Consensus()
    {
        var entries = new[]
        {
            ExitEntry("full", "drawdown", maeR: 1.7, mfeR: 0.1, profitR: -1.7,
                votes: [("drawdown", 1.0, 2.0)]),
            ExitEntry("full", "bridge", maeR: 0.2, mfeR: 0.1, profitR: -0.2,
                votes: [("structure", 0.9, 1.6)]),
            ExitEntry("tighten", null, maeR: 1.05, mfeR: 1.2, profitR: -0.08,
                votes: [("structure", 0.9, 1.6), ("time", 0.7, 1.1)]),
            ExitEntry("hold", null, maeR: 0.1, mfeR: 0.3, profitR: 0.2,
                votes: [("momentum", 0.1, 1.0)]),
        };
        var (msg, md) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);

        Assert.Contains("2 hard override(s)", msg);
        Assert.Contains("drawdown ×1", msg);
        Assert.Contains("bridge ×1", msg);
        Assert.Contains("2 consensus", msg);
        Assert.Contains("tighten ×1", msg);
        // The markdown carries the full evidence breakdown.
        Assert.Contains("## FX exit weekly digest", md);
        Assert.Contains("exit reasons (decisive)", md);
    }

    [Fact]
    public void Build_Ignores_Close_Confirmation_Lines()
    {
        var entries = new JournalEntry[]
        {
            ExitEntry("hold", null, maeR: 0.1, mfeR: 0.3, profitR: 0.2,
                votes: [("momentum", 0.1, 1.0)]),
            // The engine host's bookkeeping line shares the FX_EXIT
            // category but carries no Action — it must not become a
            // phantom empty band in the consensus split.
            new()
            {
                Timestamp = DateTimeOffset.UtcNow,
                Category = "FX_EXIT",
                Details = "closed #99 — deal : {\"Ticket\":99,\"Partial\":false,\"Retcode\":10009}",
            },
        };
        var (msg, md) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);

        Assert.Contains("1 exit evaluation(s)", msg);
        Assert.Contains("1 consensus", msg);
        Assert.DoesNotContain(" ×0", msg);   // no phantom empty band
        Assert.Contains("close confirmations (no evaluation): 1", md);
    }

    [Fact]
    public void Build_Reasons_Come_From_The_Heaviest_Voice()
    {
        var entries = new[]
        {
            // Consensus full: drawdown's 0.9×2.0 dwarfs structure's 0.9×1.6.
            ExitEntry("full", null, maeR: 1.1, mfeR: 0.2, profitR: -1.1,
                votes: [("structure", 0.9, 1.6), ("drawdown", 0.9, 2.0)]),
        };
        var (_, md) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);
        Assert.Contains("drawdown ×1", md);
    }

    [Fact]
    public void Build_Counts_Round_Trips_And_Buckets_Mae()
    {
        var entries = new[]
        {
            // Paid 1.5R of MFE, closed at 0.1R — a round trip.
            ExitEntry("full", null, maeR: 0.4, mfeR: 1.5, profitR: 0.1,
                votes: [("structure", 0.9, 1.6)]),
            // Deep MAE, emergency-bar exit.
            ExitEntry("full", "drawdown", maeR: 1.8, mfeR: 0.1, profitR: -1.8,
                votes: [("drawdown", 1.0, 2.0)]),
        };
        var (_, md) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);
        Assert.Contains("round-trips (MFE ≥1R, closed ≤0.2R): 1/2 (50%)", md);
        Assert.Contains("≥1.6R ×1", md);
        Assert.Contains("<0.5R ×1", md);
    }

    [Fact]
    public void Build_Ignores_Entries_Older_Than_A_Week()
    {
        var entries = new[]
        {
            ExitEntry("full", "drawdown", 1.7, 0.1, -1.7, daysAgo: 9,
                votes: [("drawdown", 1.0, 2.0)]),
        };
        var (msg, _) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);
        Assert.Equal(string.Empty, msg);
    }

    [Fact]
    public void Build_Skips_Malformed_Entries()
    {
        var entries = new[]
        {
            new JournalEntry { Timestamp = DateTimeOffset.UtcNow, Category = "FX_EXIT", Details = "not json" },
            ExitEntry("hold", null, 0.1, 0.2, 0.1, votes: [("momentum", 0.1, 1.0)]),
        };
        var (msg, _) = FxExitWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);
        Assert.Contains("1 exit evaluation(s)", msg);
    }

    [Fact]
    public void Digest_Runs_End_To_End_And_Appends_The_Doc()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-exit-digest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var doc = Path.Combine(dir, "EXIT-WEEKLY.md");

        var digest = new FxExitWeeklyDigest(
            new TradeJournal(dir), webhook: null)
        {
            EntriesOverride = () => new[]
            {
                ExitEntry("full", "drawdown", 1.7, 0.1, -1.7, votes: [("drawdown", 1.0, 2.0)]),
            },
            SoakDocPath = doc,
            LedgerOverride = () => new[]
            {
                new FxShadowLedgerRow("giveback", 120, 80, 0.667, 0.667),
            },
        };

        var message = digest.RunOnceAsync().GetAwaiter().GetResult();

        Assert.Contains("1 exit evaluation(s)", message);
        Assert.Contains("giveback: 120 settled trade(s)", message);
        Assert.Contains("EARNED weight 0.67", message);
        var md = File.ReadAllText(doc);
        Assert.Contains("## FX exit weekly digest", md);
        Assert.Contains("### Shadow engines (promotion ledger)", md);
    }

    // ── The weekly profit-capture trend ─────────────────────────────

    /// <summary>An FX_EXIT entry pinned to an explicit instant (the series
    /// groups by ISO week — relative day offsets would smear across
    /// boundaries depending on when the suite runs).</summary>
    private static JournalEntry ExitAt(
        DateTimeOffset ts, string action, string? overrideEngine,
        double maeR, double mfeR, double profitR) => new()
    {
        Timestamp = ts,
        Category = "FX_EXIT",
        Details = $"XAUUSD #42: {action} score 61 — "
            + JsonSerializer.Serialize(new
            {
                Ticket = 42L,
                Action = action,
                Override = overrideEngine,
                MfeR = mfeR,
                MaeR = maeR,
                ProfitR = profitR,
            }),
    };

    [Fact]
    public void CaptureSeries_Groups_Decisive_Exits_By_Iso_Week()
    {
        // W39: two decisive exits bank 3R of 5R available = 60%.
        // W40: one drawdown override banks 4R of 4R = 100%.
        // Noise that must never count: a hold (no exit), a decisive exit
        // with sub-0.5R MFE, a close confirmation with no Action.
        var w39 = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var w40 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<JournalEntry>
        {
            ExitAt(w39, "full", null, 0.2, 3.0, 1.0),
            ExitAt(w39, "partial", null, 0.2, 2.0, 2.0),
            ExitAt(w40, "hold", null, 0.1, 9.0, 0.0),
            ExitAt(w40, "full", null, 0.1, 0.4, 0.4),
            ExitAt(w40, string.Empty, null, 0.0, 0.0, 0.0),
            ExitAt(w40, "full", "drawdown", 1.7, 4.0, 4.0),
        };

        var series = FxExitWeeklyDigest.WeeklyCaptureSeries(entries);

        Assert.Equal(2, series.Count);
        Assert.Equal("ISO 2026-W39", series[0].Label);
        Assert.Equal(2, series[0].Trades);
        Assert.Equal(3.0, series[0].CapturedR, 6);
        Assert.Equal(5.0, series[0].AvailableR, 6);
        Assert.Equal(0.6, series[0].CaptureRatio, 6);
        Assert.Equal(1, series[1].Trades);
        Assert.Equal(1.0, series[1].CaptureRatio, 6);
    }

    [Fact]
    public void IsoWeek_Handles_The_Year_Boundaries_Correctly()
    {
        // Hand-checked against the ISO-8601 calendar: 2026-W01 begins
        // Mon 2025-12-29, and 2026 is a 53-week year (2027-W01 begins
        // Mon 2027-01-04) — the year follows the week's THURSDAY.
        Assert.Equal((2026, 1), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal((2026, 1), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2025, 12, 30, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal((2026, 53), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal((2026, 53), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal((2027, 1), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2027, 1, 4, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal((2026, 39), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal((2026, 40), FxExitWeeklyDigest.IsoWeek(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CaptureTrend_Renders_Sparkline_And_Empty_State()
    {
        var w39 = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var w40 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<JournalEntry>
        {
            ExitAt(w39, "full", null, 0.2, 3.0, 1.0),
            ExitAt(w40, "full", "drawdown", 1.7, 4.0, 4.0),
        };

        var md = FxExitWeeklyDigest.CaptureTrendMarkdown(entries);
        Assert.Contains("### Profit-capture trend", md);
        Assert.Contains("ISO 2026-W39 33%", md);
        Assert.Contains("ISO 2026-W40 100%", md);
        Assert.Contains("spark: ", md);
        // Two distinct values must map to distinct sparkline blocks.
        var spark = md.Split("spark: ")[1].Split('\n')[0].Trim();
        Assert.NotEqual(spark[0], spark[1]);

        var empty = FxExitWeeklyDigest.CaptureTrendMarkdown([]);
        Assert.Contains("no decisive exits with meaningful MFE yet", empty);
    }

    [Fact]
    public void CaptureTrendSvg_Writes_A_SelfContained_Chart_And_Survives_A_Bad_Directory()
    {
        var w39 = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var w40 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<JournalEntry>
        {
            ExitAt(w39, "full", null, 0.2, 3.0, 1.0),
            ExitAt(w40, "partial", null, 0.3, 2.0, 1.5),
        };

        var dir = Path.Combine(Path.GetTempPath(), "dg-capture-svg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = FxExitWeeklyDigest.WriteCaptureTrendSvg(entries, dir);

        Assert.NotNull(path);
        var svg = File.ReadAllText(path!);
        Assert.StartsWith("<svg xmlns=", svg);
        Assert.Contains("ISO 2026-W39", svg);
        Assert.Contains("ISO 2026-W40", svg);
        Assert.Contains("fill=\"#2e7d32\"", svg);

        // No series → no chart; an unwritable directory never throws.
        Assert.Null(FxExitWeeklyDigest.WriteCaptureTrendSvg([], dir));
        Assert.Null(FxExitWeeklyDigest.WriteCaptureTrendSvg(entries, Path.Combine(dir, "missing", "deeper")));
    }

    [Fact]
    public void WeeklySaves_Dedupes_By_Ticket_And_Day_And_Renders_Amber_Dots()
    {
        // One override save and one guard save on the same ticket+day must
        // count ONCE (two evidence families, one event); a different ticket
        // counts separately. The chart renders an amber dot strip for
        // weeks with saves and nothing for weeks without.
        var w40 = new DateTimeOffset(2026, 9, 30, 6, 5, 0, TimeSpan.Zero);
        var overrideEntry = new JournalEntry
        {
            Timestamp = w40,
            Category = "FX_EXIT",
            Details = $"XAUUSDmicro #501: full score 100 — "
                + JsonSerializer.Serialize(new
                {
                    Ticket = 501L,
                    Action = "full",
                    Override = "profit-floor",
                    MfeR = 9.3,
                    MaeR = 0.2,
                    ProfitR = 1.6,
                }),
        };
        var guardEntry = new JournalEntry
        {
            Timestamp = w40.AddHours(5),
            Category = "FX_FLOOR",
            Details = "XAUUSDmicro #501: PROFIT FLOOR EXIT SUBMITTED — deal 501 "
                + JsonSerializer.Serialize(new { Ticket = 501L, EventId = "501|1|1" }),
        };
        var otherGuard = new JournalEntry
        {
            Timestamp = w40.AddHours(5),
            Category = "FX_FLOOR",
            Details = "XAUUSDmicro #502: PROFIT FLOOR EXIT SUBMITTED — deal 502 "
                + JsonSerializer.Serialize(new { Ticket = 502L, EventId = "502|1|1" }),
        };
        var plainExit = ExitAt(w40, "full", null, 0.1, 2.0, 1.5);   // not a save
        var entries = new List<JournalEntry> { overrideEntry, guardEntry, otherGuard, plainExit };

        var weeks = FxExitWeeklyDigest.WeeklyCaptureSeries(entries);
        var saves = FxExitWeeklyDigest.WeeklySaves(entries, weeks);

        Assert.Equal(2, saves[^1]);   // #501 once (override+guard same day), #502 once

        var svg = FxExitWeeklyDigest.CaptureTrendSvg(weeks, saves);
        Assert.Contains("fill=\"#ef6c00\"", svg);
        Assert.Contains("2 save(s)", svg);

        // A week without saves renders no dot strip at all.
        var bare = FxExitWeeklyDigest.CaptureTrendSvg(weeks);
        Assert.DoesNotContain("fill=\"#ef6c00\"", bare);
    }

    [Fact]
    public void Tp1Capture_Grades_Banked_Tickets_Beside_The_Fleet_Ratio()
    {
        // Ticket 601 banked a rung (TP1-EXEC ok) and settled with decisive
        // evidence; ticket 602 settled decisively without ever arming — the
        // comparison the section exists to show. A malformed EXEC row is
        // skipped, never crashes.
        var t = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<JournalEntry>
        {
            new()
            {
                Timestamp = t, Category = "FX_PROFIT",
                Details = "XAUUSDmicro #601: TP1-EXEC: banked 0.25 lots (25% plan) of #601 at the armed rung 1.1622 — "
                    + JsonSerializer.Serialize(new { Ticket = 601L, Executed = true, Lots = 0.25, PlanPct = 25, ArmedPrice = 1.1622 }),
            },
            new()
            {
                Timestamp = t.AddMinutes(5), Category = "FX_PROFIT",
                Details = "XAUUSDmicro #602: TP1-EXEC: garbage — not json",
            },
            ExitAt(t.AddMinutes(9), "full", null, 0.2, 8.0, 5.0),   // #42, no rung — fleet
        };
        // Ticket 601's own decisive settlement row (payload ticket 601).
        entries.Add(new JournalEntry
        {
            Timestamp = t.AddMinutes(10),
            Category = "FX_EXIT",
            Details = "XAUUSDmicro #601: full score 61 — "
                + JsonSerializer.Serialize(new
                {
                    Ticket = 601L,
                    Action = "full",
                    Override = (string?)null,
                    MfeR = 4.0,
                    MaeR = 0.3,
                    ProfitR = 3.0,
                }),
        });

        var md = FxExitWeeklyDigest.Tp1CaptureMarkdown(entries);

        Assert.Contains("TP1 banked-run capture", md);
        Assert.Contains("#601: banked a rung, captured 3R of its 4R peak (75% capture)", md);
        Assert.Contains("fleet capture WITHOUT a TP1 rung", md);
        Assert.DoesNotContain("garbage", md);

        // Silent until rows exist — the fleet-only window renders nothing.
        var bare = FxExitWeeklyDigest.Tp1CaptureMarkdown(
            new List<JournalEntry> { ExitAt(t, "full", null, 0.1, 2.0, 1.0) });
        Assert.Equal(string.Empty, bare);
    }

    [Fact]
    public void Tp1Capture_Appends_The_Graded_Verdict_And_Vs_Giveback()
    {
        // The watcher's ledger supplies the verdict; the digest reports it
        // verbatim beside the capture line. A ticket with no ledger row
        // still renders, just without the suffix.
        var t = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<JournalEntry>
        {
            new()
            {
                Timestamp = t, Category = "FX_PROFIT",
                Details = "XAUUSDmicro #601: TP1-EXEC: banked 0.25 lots — "
                    + JsonSerializer.Serialize(new { Ticket = 601L, Executed = true, Lots = 0.25 }),
            },
            new()
            {
                Timestamp = t.AddMinutes(10), Category = "FX_EXIT",
                Details = "XAUUSDmicro #601: full score 61 — "
                    + JsonSerializer.Serialize(new
                    {
                        Ticket = 601L, Action = "full", Override = (string?)null,
                        MfeR = 4.0, MaeR = 0.3, ProfitR = 3.0,
                    }),
            },
        };

        var md = FxExitWeeklyDigest.Tp1CaptureMarkdown(entries,
            new Dictionary<long, FxExitWeeklyDigest.Tp1GradedVerdict>
            {
                [601] = new("beat", 0.75, 75.0),
            });
        Assert.Contains("(75% capture) — beat (+0.75R vs giveback)", md);

        var trailed = FxExitWeeklyDigest.Tp1CaptureMarkdown(entries,
            new Dictionary<long, FxExitWeeklyDigest.Tp1GradedVerdict>
            {
                [601] = new("trailed", -1.90, 40.0),
            });
        Assert.Contains("— trailed (-1.90R vs giveback)", trailed);

        // No ledger → the bare capture line, unchanged.
        Assert.DoesNotContain("vs giveback", FxExitWeeklyDigest.Tp1CaptureMarkdown(entries));
    }

    [Fact]
    public void Tp1Verdicts_Read_From_The_Ledger_And_Skip_Malformed_Lines()
    {
        var path = Path.Combine(Path.GetTempPath(), "dg-tp1-verdicts",
            Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            "{\"ticket\": 601, \"verdict\": \"beat\", \"vs_giveback_r\": 0.75, \"capture_pct\": 75.0}",
            "{not json",
            "{\"ticket\": 602, \"verdict\": \"pending\", \"vs_giveback_r\": null}",
        ]);

        var parsed = FxExitWeeklyDigest.ReadVerdicts(path);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("beat", parsed[601].Verdict);
        Assert.Equal(0.75, parsed[601].VsGivebackR!.Value);
        Assert.Equal("pending", parsed[602].Verdict);
        Assert.Null(parsed[602].VsGivebackR);
    }

    [Fact]
    public void WeeklyTp1Graded_Counts_Banked_Tickets_And_Outlines_The_Week()
    {
        // Only banked rungs count (a refused EXEC banks nothing), and the
        // week they land in gets a blue outline plus the legend.
        var w40 = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<JournalEntry>
        {
            new()
            {
                Timestamp = w40, Category = "FX_PROFIT",
                Details = "XAUUSDmicro #601: TP1-EXEC: banked — "
                    + JsonSerializer.Serialize(new { Ticket = 601L, Executed = true }),
            },
            new()
            {
                Timestamp = w40, Category = "FX_PROFIT",
                Details = "XAUUSDmicro #602: TP1-EXEC refused — "
                    + JsonSerializer.Serialize(new { Ticket = 602L, Executed = false }),
            },
            ExitAt(w40, "full", null, 0.2, 3.0, 1.0),
        };

        var weeks = FxExitWeeklyDigest.WeeklyCaptureSeries(entries);
        var tp1 = FxExitWeeklyDigest.WeeklyTp1Graded(entries, weeks);
        Assert.Single(tp1);
        Assert.Equal(1, tp1[0]);

        var svg = FxExitWeeklyDigest.CaptureTrendSvg(weeks, savesPerWeek: null, tp1PerWeek: tp1);
        Assert.Contains("stroke=\"#1565c0\"", svg);
        Assert.Contains("blue outline = TP1 rung graded", svg);

        // A week with no banked rung renders no outline and no legend.
        var bare = FxExitWeeklyDigest.CaptureTrendSvg(weeks);
        Assert.DoesNotContain("#1565c0", bare);
    }

    [Fact]
    public void PlanReviewSummary_Rolls_Up_Open_Cleared_Acted_Since_The_Last_Post()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var reviews = new List<FxExitWeeklyDigest.PlanReview>
        {
            new("r1", "down", now.AddDays(-2), 25, 15, -5.93, "open", null, null, ""),
            new("r2", "up", now.AddDays(-3), 25, 30, 2.33, "acted", null, now.AddDays(-1), "raised to 30"),
            new("r3", "down", now.AddDays(-10), 25, 15, -4.0, "open", null, null, ""),   // outside window
        };

        var (message, markdown) = FxExitWeeklyDigest.PlanReviewSummary(reviews, now);

        Assert.Contains("since the last post: 2", message);
        Assert.Contains("1 open, 0 cleared, 1 acted", message);
        Assert.Contains("### TP1 plan-% reviews (auto)", markdown);
        Assert.Contains("25%→15%", markdown);
        Assert.Contains("— acted (raised to 30)", markdown);
        Assert.DoesNotContain("r3", markdown);

        // Nothing recent → the whole section is silent.
        var bare = FxExitWeeklyDigest.PlanReviewSummary(reviews, now.AddDays(30));
        Assert.Equal(string.Empty, bare.Markdown);
        Assert.Equal(string.Empty, bare.Message);
    }

    [Fact]
    public void PlanReviewEvents_Fold_From_The_Ledger_And_Skip_Malformed()
    {
        var path = Path.Combine(Path.GetTempPath(), "dg-plan-reviews",
            Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            "{\"event\": \"recommended\", \"id\": \"r1\", \"ts\": \"2026-10-01T10:00:00+00:00\", \"direction\": \"down\", \"baseline_pct\": 25, \"candidate_pct\": 15, \"net_r\": -5.93}",
            "{not json",
            "{\"event\": \"cleared\", \"id\": \"r1\", \"ts\": \"2026-10-02T10:00:00+00:00\"}",
            "{\"event\": \"recommended\", \"id\": \"r2\", \"ts\": \"2026-10-03T10:00:00+00:00\", \"direction\": \"up\", \"baseline_pct\": 25, \"candidate_pct\": 30}",
            "{\"event\": \"acted\", \"id\": \"r2\", \"ts\": \"2026-10-04T10:00:00+00:00\", \"note\": \"raised to 30\"}",
        ]);

        var reviews = FxExitWeeklyDigest.ReadPlanReviewEvents(path);
        Assert.Equal(2, reviews.Count);
        Assert.Equal("cleared", reviews[0].Status);
        Assert.Equal("acted", reviews[1].Status);
        Assert.Equal("raised to 30", reviews[1].ActedNote);
        Assert.Equal(30d, reviews[1].CandidatePct!.Value);
    }

    [Fact]
    public void Tp1PlanBreaker_Holds_Only_For_An_Old_Open_Review()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(Path.GetTempPath(), "dg-breaker",
            Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        static string Line(object rec) => JsonSerializer.Serialize(rec);

        // A fresh open review does not hold yet.
        File.WriteAllLines(path,
            [Line(new { @event = "recommended", id = "r1", ts = now.AddDays(-1) })]);
        Assert.False(Tp1PlanBreaker.IsHeldAt(path, now));

        // An OPEN review past the threshold holds.
        File.WriteAllLines(path,
            [Line(new { @event = "recommended", id = "r1", ts = now.AddDays(-10) })]);
        Assert.True(Tp1PlanBreaker.IsHeldAt(path, now));

        // Acted: the breaker resumes.
        File.WriteAllLines(path,
        [
            Line(new { @event = "recommended", id = "r1", ts = now.AddDays(-10) }),
            Line(new { @event = "acted", id = "r1", ts = now.AddDays(-9) }),
        ]);
        Assert.False(Tp1PlanBreaker.IsHeldAt(path, now));

        // Missing ledger never trips.
        Assert.False(Tp1PlanBreaker.IsHeldAt(path + ".missing", now));
    }

    // ── The promotion ledger ────────────────────────────────────────

    [Fact]
    public void Ledger_Appends_One_Line_Per_Shadow_Vote_And_Rolls_Up()
    {
        var path = Path.Combine(Path.GetTempPath(), "dg-shadow", Guid.NewGuid().ToString("N") + ".jsonl");
        var ledger = new FxShadowLedger(path);

        var shadows = new List<FxExitVote>
        {
            new("counterfactual", 0.9, 0, "cf"),
            new("giveback", 0.8, 0, "gb"),
        };
        ledger.Append(7, "XAUUSDmicro", shadows, "hold", won: true, DateTimeOffset.UtcNow);
        ledger.Append(8, "XAUUSDmicro", shadows, "hold", won: false, DateTimeOffset.UtcNow);

        var rows = ledger.Summarize().ToDictionary(r => r.Engine);
        Assert.Equal(2, rows["counterfactual"].Trades);
        Assert.Equal(2, rows["giveback"].Trades);
        // Trade 7 was a held winner both shadows wanted to exit: helped.
        // Trade 8 lost: helping is not credited on losers.
        Assert.Equal(1, rows["counterfactual"].Helped);
        Assert.Equal(1, rows["giveback"].Helped);
        Assert.Equal(0.5, rows["giveback"].HitRate, 6);
        Assert.Equal(0, rows["giveback"].SuggestedWeight);   // 50% < 60% bar
    }

    [Fact]
    public void Ledger_Survives_A_Missing_Or_Corrupt_File()
    {
        var path = Path.Combine(Path.GetTempPath(), "dg-shadow", Guid.NewGuid().ToString("N") + ".jsonl");
        Assert.Empty(new FxShadowLedger(path).Summarize());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, ["{not json", "also broken"]);
        Assert.Empty(new FxShadowLedger(path).Summarize());
    }

    // ── The brain toggle's persistence ───────────────────────────────

    [Fact]
    public void Brain_Running_State_Round_Trips_Through_Settings()
    {
        // No assertion on the initial value: other tests in this run toggle
        // the brain and persist into the same TF_DATA_DIR, so the file's
        // current state is whatever ran last. The contract under test is
        // the round-trip itself, both directions.
        var service = new SettingsService();

        var on = service.Load();
        on.FxBrainRunning = true;
        service.Save(on);
        Assert.True(service.Load().FxBrainRunning);

        var off = service.Load();
        off.FxBrainRunning = false;
        service.Save(off);
        Assert.False(service.Load().FxBrainRunning);
    }
}
