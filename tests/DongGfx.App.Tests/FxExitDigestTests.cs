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

    // ── The promotion ledger ─────────────────────────────────────────

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
