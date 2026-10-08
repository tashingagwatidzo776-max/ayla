using System.IO;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The weekly FX_LAB digest: pure builder aggregation, cutoff semantics,
/// and the webhook + soak-doc append legs. Journal-only by construction.
/// </summary>
[Trait("Category", "Unit")]
public class FxLabDigestTests
{
    private static JournalEntry LabEntry(string symbol, bool approved, int daysAgo) => new()
    {
        Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysAgo),
        Category = "FX_LAB",
        Details = JsonSerializer.Serialize(new
        {
            Symbol = symbol,
            Bars = 140,
            Folds = 3,
            Positive = approved ? 3 : 1,
            Approved = approved,
            Verdict = approved ? "APPROVED-FOR-REVIEW" : "not approved",
        }),
    };

    [Fact]
    public void Build_Empty_When_No_Lab_Entries()
    {
        var (msg, md) = FxLabWeeklyDigest.Build([], DateTimeOffset.UtcNow);
        Assert.Equal(string.Empty, msg);
        Assert.Equal(string.Empty, md);
    }

    [Fact]
    public void Build_Aggregates_The_Week()
    {
        var entries = new[]
        {
            LabEntry("XAUUSDmicro", approved: true, daysAgo: 1),
            LabEntry("EURUSD", approved: false, daysAgo: 2),
            LabEntry("GBPUSD", approved: true, daysAgo: 5),
        };
        var (msg, md) = FxLabWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);

        Assert.Contains("3 lab run(s)", msg);
        Assert.Contains("2 approved-for-review", msg);
        Assert.Contains("XAUUSDmicro", msg);
        Assert.Contains("EURUSD", msg);
        // Markdown carries the per-run lines for the evidence doc.
        Assert.Contains("APPROVED-FOR-REVIEW", md);
        Assert.Contains("not approved", md);
        Assert.Contains("## FX lab weekly digest", md);
    }

    [Fact]
    public void Build_Ignores_Entries_Older_Than_A_Week()
    {
        var entries = new[]
        {
            LabEntry("XAUUSDmicro", approved: true, daysAgo: 8),
            LabEntry("EURUSD", approved: false, daysAgo: 30),
        };
        var (msg, _) = FxLabWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);
        Assert.Equal(string.Empty, msg);
    }

    [Fact]
    public void Build_Skips_Malformed_Entries()
    {
        var entries = new[]
        {
            new JournalEntry { Timestamp = DateTimeOffset.UtcNow, Category = "FX_LAB", Details = "not json" },
            LabEntry("XAUUSDmicro", approved: true, daysAgo: 0),
        };
        var (msg, _) = FxLabWeeklyDigest.Build(entries, DateTimeOffset.UtcNow);
        Assert.Contains("1 lab run(s)", msg);
    }

    [Fact]
    public async Task RunOnce_Posts_And_Appends_When_There_Is_Material()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-lab-digest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var docPath = Path.Combine(dir, "SOAK-TEST.md");
        await File.WriteAllTextAsync(docPath, "# soak\n");

        var entry = LabEntry("XAUUSDmicro", approved: true, daysAgo: 0);
        var digest = new FxLabWeeklyDigest(new TradeJournal(Path.Combine(dir, "journal")))
        {
            EntriesOverride = () => [entry],
            SoakDocPath = docPath,
        };

        var msg = await digest.RunOnceAsync();

        Assert.Contains("1 lab run(s)", msg);
        var doc = await File.ReadAllTextAsync(docPath);
        Assert.Contains("## FX lab weekly digest", doc);
    }

    [Fact]
    public async Task RunOnce_Silent_When_No_Runs()
    {
        var digest = new FxLabWeeklyDigest(new TradeJournal(Path.Combine(
            Path.GetTempPath(), "dg-lab-digest-tests", Guid.NewGuid().ToString("N"))))
        {
            EntriesOverride = () => [],
        };

        Assert.Equal(string.Empty, await digest.RunOnceAsync());
    }

    [Fact]
    public async Task Lab_RunNow_Bypasses_Toggle_And_Guards_Thin_Journal()
    {
        var journal = new TradeJournal(Path.Combine(
            Path.GetTempPath(), "dg-lab-digest-tests", Guid.NewGuid().ToString("N")));
        var lab = new FxLabService(journal) { Disabled = true };

        // RunNow is the operator's explicit click: Disabled does not stop it.
        var results = await lab.RunNowAsync();
        Assert.Empty(results);   // thin journal (0 FX_DECISION) → silent refusal

        // And the nightly path IS stopped by Disabled.
        Assert.Empty(await lab.RunOnceAsync());
    }
}
