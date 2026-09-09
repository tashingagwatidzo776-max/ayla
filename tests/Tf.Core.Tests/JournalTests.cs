using Tf.Core.Logging;

namespace Tf.Core.Tests;

[Trait("Category", "Unit")]
public class JournalTests : IDisposable
{
    private readonly string _dir;

    public JournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_journal_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void LogBrainDecision_PersistsToFile()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogBrainDecision(accountId, "Growth", "frxEURUSD", "Rise", 1.5m, 0.75, "oversold bounce");
        journal.Dispose(); // flush

        var entries = journal.GetRecent(accountId);
        Assert.Single(entries);
        Assert.Equal("BRAIN_DECISION", entries[0].Category);
        Assert.Contains("Rise", entries[0].Details);
        Assert.Contains("1.5", entries[0].Details);
    }

    [Fact]
    public void LogTradeSettlement_PersistsToFile()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogTradeSettlement(accountId, "contract-123", true, 2.5m, 1.5m, 6.5m);
        journal.Dispose(); // flush

        var entries = journal.GetRecent(accountId);
        Assert.Single(entries);
        Assert.Equal("TRADE_SETTLEMENT", entries[0].Category);
        Assert.Contains("contract-123", entries[0].Details);
        Assert.Contains("\"Won\":true", entries[0].Details);
    }

    [Fact]
    public void LogGrowthState_PersistsToFile()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogGrowthState(accountId, "started", 5.0m, 0, "target 10");
        journal.Dispose(); // flush

        var entries = journal.GetRecent(accountId);
        Assert.Single(entries);
        Assert.Equal("GROWTH_STATE", entries[0].Category);
        Assert.Contains("started", entries[0].Details);
    }

    [Fact]
    public void GetRecent_FiltersByAccount()
    {
        using var journal = new TradeJournal(_dir);
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        journal.LogBrainDecision(accountA, "Growth", "EURUSD", "Rise", 1m, 0.7, "reason A");
        journal.LogBrainDecision(accountB, "Growth", "EURUSD", "Fall", 2m, 0.8, "reason B");
        journal.LogBrainDecision(accountA, "Growth", "EURUSD", "Rise", 1.5m, 0.75, "reason A2");
        journal.Dispose();

        var entriesA = journal.GetRecent(accountA);
        var entriesB = journal.GetRecent(accountB);

        Assert.Equal(2, entriesA.Count);
        Assert.Single(entriesB);
        Assert.All(entriesA, e => Assert.Contains("reason", e.Details));
    }

    [Fact]
    public void GetRecent_RespectsMaxCount()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        for (int i = 0; i < 50; i++)
            journal.LogBrainDecision(accountId, "Growth", "EURUSD", "Rise", 1m, 0.7, $"entry {i}");
        journal.Dispose();

        var entries = journal.GetRecent(accountId, 10);
        Assert.Equal(10, entries.Count);
    }

    [Fact]
    public void GetStats_CalculatesWinRate()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogTradeSettlement(accountId, "c1", true, 2m, 1m, 6m);
        journal.LogTradeSettlement(accountId, "c2", false, 0m, -1m, 5m);
        journal.LogTradeSettlement(accountId, "c3", true, 2m, 1m, 6m);
        journal.Dispose();

        var stats = journal.GetStats(accountId);
        Assert.Equal(3, stats.TotalSettlements);
        Assert.Equal(2, stats.WinningTrades);
        Assert.Equal(1, stats.LosingTrades);
        Assert.Equal(2m / 3m, stats.WinRate, 2);
    }

    [Fact]
    public void MultipleLogs_InSameFile()
    {
        using var journal = new TradeJournal(_dir);
        var accountId = Guid.NewGuid();

        journal.LogBrainDecision(accountId, "Growth", "EURUSD", "Rise", 1m, 0.7, "reason1");
        journal.LogTradeSettlement(accountId, "c1", true, 2m, 1m, 6m);
        journal.LogGrowthState(accountId, "running", 6m, 0);
        journal.Dispose();

        var entries = journal.GetRecent(accountId);
        Assert.Equal(3, entries.Count);

        var categories = entries.Select(e => e.Category).ToList();
        Assert.Contains("BRAIN_DECISION", categories);
        Assert.Contains("TRADE_SETTLEMENT", categories);
        Assert.Contains("GROWTH_STATE", categories);
    }
}
