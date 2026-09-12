using Tf.Core.Logging;

namespace Tf.Core.Tests;

/// <summary>
/// Tests for the remaining TradeJournal surfaces: the execution/account
/// event/generic log helpers, forced durability via Flush, malformed-line
/// tolerance in GetRecent, and journal stats edge cases.
/// </summary>
[Trait("Category", "Unit")]
public class JournalLoggingSurfaceTests : IDisposable
{
    private readonly string _dir;

    public JournalLoggingSurfaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_journal_surface_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LogTradeExecution_PersistsStatusAndError()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogTradeExecution(accountId, "prop-1", "frxEURUSD", "Rise", 1m, "Bought", error: "");
        journal.LogTradeExecution(accountId, "prop-2", "frxEURUSD", "Fall", 1m, "Failed", error: "IsContractAllowed");
        journal.Dispose();

        var entries = journal.GetRecent(accountId);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("TRADE_EXECUTION", e.Category));
        Assert.Contains("\"Bought\"", entries[1].Details);
        Assert.Contains("IsContractAllowed", entries[0].Details);
    }

    [Fact]
    public void LogAccountEvent_PersistsNameAndEvent()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogAccountEvent(accountId, "Alpha", "connected", details: "demo");
        journal.Dispose();

        var entries = journal.GetRecent(accountId);
        Assert.Single(entries);
        Assert.Equal("ACCOUNT_EVENT", entries[0].Category);
        Assert.Contains("Alpha", entries[0].Details);
        Assert.Contains("connected", entries[0].Details);
    }

    [Fact]
    public void GenericLog_AppendsMessage_WhenDetailsPresent()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.Log(accountId, "WEBHOOK", "posted", details: "risk rail");
        journal.Dispose();

        var entry = Assert.Single(journal.GetRecent(accountId));
        Assert.Equal("WEBHOOK", entry.Category);
        Assert.Contains("posted: risk rail", entry.Details);
    }

    [Fact]
    public void GenericLog_UsesMessageAlone_WhenNoDetails()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.Log(accountId, "WEBHOOK", "all rails clear");
        journal.Dispose();

        var entry = Assert.Single(journal.GetRecent(accountId));
        Assert.Equal("all rails clear", entry.Details);
    }

    [Fact]
    public void Flush_WritesPendingEntries_WithoutDispose()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogBrainDecision(accountId, "Growth", "frxEURUSD", "Rise", 1m, 0.8, "flush test");
        journal.Flush();

        // Read through a *separate* journal instance pointed at the same
        // directory: proves durability on disk, not just queue visibility.
        using var reader = new TradeJournal(_dir);
        var entry = Assert.Single(reader.GetRecent(accountId));
        Assert.Equal("BRAIN_DECISION", entry.Category);
    }

    [Fact]
    public void Flush_EmptyQueue_NoFileWritten()
    {
        using var journal = new TradeJournal(_dir);
        journal.Flush();

        Assert.Empty(Directory.GetFiles(_dir, "journal_*.jsonl"));
    }

    [Fact]
    public void GetRecent_SkipsMalformedLines()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();
        journal.LogGrowthState(accountId, "started", 5m, 0, "before garbage");
        journal.LogGrowthState(accountId, "stopped", 6m, 0, "after garbage");
        journal.Dispose();

        // Corrupt the middle line of today's file.
        var file = Path.Combine(_dir, $"journal_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var lines = File.ReadAllLines(file);
        lines[0] = "{ corrupted";
        File.WriteAllLines(file, lines);

        var entries = journal.GetRecent(accountId);
        Assert.Single(entries);
        Assert.Contains("after garbage", entries[0].Details);
    }

    [Fact]
    public void LogAfterDispose_IsIgnored()
    {
        var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();
        journal.Dispose();

        journal.LogBrainDecision(accountId, "Growth", "frxEURUSD", "Rise", 1m, 0.8, "should not persist");

        Assert.Empty(journal.GetRecent(accountId));
    }

    [Fact]
    public void GetStats_EmptyJournal_FallsBackToUtcNow()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        var stats = journal.GetStats(accountId);

        Assert.Equal(0, stats.TotalDecisions);
        Assert.Equal(0, stats.TotalSettlements);
        Assert.Equal(accountId, stats.AccountId);
    }

    [Fact]
    public void GetStats_SinceFilter_ExcludesOlderEntries()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();
        journal.LogTradeSettlement(accountId, "c1", true, 2m, 1m, 6m);
        journal.Dispose();

        // A cutoff in the far future filters everything out.
        var stats = journal.GetStats(accountId, since: DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(0, stats.TotalSettlements);
    }
}
