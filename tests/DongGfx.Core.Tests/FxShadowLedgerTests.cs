using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The shadow-ledger rollups: per-file (legacy) and per-directory (the
/// portfolio view the dashboard's promotion card reads). Hermetic — temp
/// dirs only, no journal, no venue.
/// </summary>
[Trait("Category", "Unit")]
public class FxShadowLedgerTests
{
    private static string Row(long ticket, string engine, double exit, bool helped, string symbol = "EURUSD") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow,
            Ticket = ticket,
            Symbol = symbol,
            Engine = engine,
            ExitAtClose = exit,
            ResolvedAction = "full",
            Won = true,
            Helped = helped,
        });

    private static string WriteLedger(string dir, string fileName, params string[] rows)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllLines(path, rows);
        return path;
    }

    [Fact]
    public void SummarizeDir_Merges_Per_Symbol_Ledgers_Per_Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-shadow-dir", Guid.NewGuid().ToString("N"));
        WriteLedger(dir, "fx-shadow-EURUSD.jsonl",
            Row(1, "giveback", 0.95, helped: true),
            Row(2, "giveback", 0.85, helped: true),
            Row(3, "counterfactual", 0.7, helped: false));
        WriteLedger(dir, "fx-shadow-XAUUSDmicro.jsonl",
            Row(4, "giveback", 0.9, helped: false, symbol: "XAUUSDmicro"),
            Row(5, "counterfactual", 0.6, helped: true, symbol: "XAUUSDmicro"));

        var rows = FxShadowLedger.SummarizeDir(dir).ToDictionary(r => r.Engine);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows["giveback"].Trades);
        Assert.Equal(2, rows["giveback"].Helped);
        Assert.Equal(2.0 / 3.0, rows["giveback"].HitRate, 6);
        Assert.Equal(2, rows["counterfactual"].Trades);
        Assert.Equal(1, rows["counterfactual"].Helped);
    }

    [Fact]
    public void Helped_Credits_A_Guard_Executed_Floor_Exit()
    {
        // 2026-09-30: the hard floor's saves bypassed the ensemble's
        // close-time append entirely, and even with an append the classic
        // rule (hold/monitor + won) could never credit them. resolvedAction
        // "floor-exit" + won is the save signature: the drawdown voice's
        // deep-giveback conviction drove a close that EXECUTED.
        var vote = new FxExitVote("drawdown", 0.95, 0.0, "hard profit floor save");

        Assert.True(FxExitShadow.Helped(vote, resolvedAction: "floor-exit", won: true));
        // The evidence bar holds: weak conviction or a loss is no credit.
        Assert.False(FxExitShadow.Helped(
            vote with { Exit = 0.4 }, resolvedAction: "floor-exit", won: true));
        Assert.False(FxExitShadow.Helped(vote, resolvedAction: "floor-exit", won: false));
        // Other engines do not ride the save — drawdown gave the evidence.
        Assert.False(FxExitShadow.Helped(
            vote with { Engine = "counterfactual" }, resolvedAction: "floor-exit", won: true));

        // The giveback mirror the guard appends rides the SAME save: the
        // peak→breach evidence is the giveback story, so starving giveback
        // on guard saves (2026-10-07) was wrong. Same bar applies.
        var mirror = vote with { Engine = "giveback" };
        Assert.True(FxExitShadow.Helped(mirror, resolvedAction: "floor-exit", won: true));
        Assert.False(FxExitShadow.Helped(
            mirror with { Exit = 0.4 }, resolvedAction: "floor-exit", won: true));
        Assert.False(FxExitShadow.Helped(
            mirror, resolvedAction: "floor-exit", won: false));
    }

    [Fact]
    public void SummarizeDir_Degrades_To_Empty_On_Missing_Directory()
    {
        Assert.Empty(FxShadowLedger.SummarizeDir(null));
        Assert.Empty(FxShadowLedger.SummarizeDir(
            Path.Combine(Path.GetTempPath(), "dg-shadow-dir", Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void SummarizeFile_Legacy_Rows_Without_Helped_Still_Count_As_Trades()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-shadow-dir", Guid.NewGuid().ToString("N"));
        var path = WriteLedger(dir, "fx-shadow-TEST.jsonl",
            "{\"At\":\"2026-09-29T10:00:00Z\",\"Ticket\":7,\"Symbol\":\"EURUSD\"," +
            "\"Engine\":\"giveback\",\"ExitAtClose\":0.9,\"ResolvedAction\":\"full\",\"Won\":true}");

        var rows = FxShadowLedger.SummarizeFile(path);

        var row = Assert.Single(rows);
        Assert.Equal("giveback", row.Engine);
        Assert.Equal(1, row.Trades);
        Assert.Equal(0, row.Helped);
    }

    [Fact]
    public void SummarizeDir_Ignores_Files_Outside_The_Shadow_Naming()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-shadow-dir", Guid.NewGuid().ToString("N"));
        WriteLedger(dir, "fx-shadow-EURUSD.jsonl", Row(1, "giveback", 0.95, helped: true));
        WriteLedger(dir, "unrelated.jsonl", Row(2, "giveback", 0.95, helped: true));

        var rows = FxShadowLedger.SummarizeDir(dir);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Trades);
    }
}
