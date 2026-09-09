using Tf.Core;
using Tf.Core.Models;

namespace Tf.Core.Tests;

[Trait("Category", "Unit")]
public class TradeStoreTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Trade SampleTrade(Guid id, ContractStatus outcome, decimal profit, DateTimeOffset settledAt) =>
        new(id, "frxEURUSD", Direction.Rise, 1.00m, "USD",
            1.10000, 1700000000, "CONTRACT-" + id.ToString("N")[..8], outcome, profit,
            outcome == ContractStatus.Lost ? 1.09 : 1.11, 1700000300, settledAt);

    [Fact]
    public void Add_Persists_And_Reloads()
    {
        var dir = NewTempDir();
        var settled = DateTimeOffset.Now.AddMinutes(-5);

        var store = new TradeStore(dir);
        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Won, 0.95m, settled));
        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Lost, -1.00m, settled));

        var reloaded = new TradeStore(dir);
        Assert.Equal(2, reloaded.Trades.Count);
        Assert.Equal("frxEURUSD", reloaded.Trades[0].Symbol);
        Assert.True(File.Exists(Path.Combine(dir, "trades.json")));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void SummaryFor_Today_CountsWinsLossesAndNet()
    {
        var dir = NewTempDir();
        var store = new TradeStore(dir);
        var now = DateTimeOffset.Now;

        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Won, 0.95m, now.AddMinutes(-3)));
        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Won, 0.90m, now.AddMinutes(-2)));
        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Lost, -1.00m, now.AddMinutes(-1)));
        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Lost, -1.00m, now.AddDays(-1))); // yesterday

        var summary = store.SummaryFor(now);

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.Wins);
        Assert.Equal(1, summary.Losses);
        Assert.Equal(0.85m, summary.NetProfit);
        Assert.Equal(2.0 / 3.0, summary.WinRate, 5);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ForAccount_FiltersByAccountAndSource()
    {
        var dir = NewTempDir();
        var store = new TradeStore(dir);
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var settled = DateTimeOffset.Now.AddMinutes(-2);

        Trade T(Guid id, Guid? account, string source) => SampleTrade(id, ContractStatus.Won, 0.95m, settled) with
        {
            AccountId = account,
            AccountName = account is null ? null : "acct-" + account.Value.ToString("N")[..6],
            Source = source
        };

        store.Add(T(Guid.NewGuid(), accountA, TradeSource.Growth));
        store.Add(T(Guid.NewGuid(), accountA, TradeSource.Growth));
        store.Add(T(Guid.NewGuid(), accountA, TradeSource.Manual));
        store.Add(T(Guid.NewGuid(), accountB, TradeSource.Growth));
        store.Add(T(Guid.NewGuid(), null, TradeSource.Manual));

        Assert.Equal(2, store.ForAccount(accountA, TradeSource.Growth).Count);
        Assert.Single(store.ForAccount(accountB, TradeSource.Growth));
        Assert.Equal(3, store.ForAccount(accountA).Count);
        Assert.Single(store.ForAccount(null, TradeSource.Manual));
        Assert.Single(store.ForAccount(null));

        // Per-account daily summary sees only that account's growth trades.
        var growthSummaryA = store.SummaryFor(DateTimeOffset.Now, accountA, TradeSource.Growth);
        Assert.Equal(2, growthSummaryA.Count);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void TradeAdded_RaisesForEveryAdd()
    {
        var dir = NewTempDir();
        var store = new TradeStore(dir);
        var raised = 0;
        store.TradeAdded += _ => raised++;

        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Won, 0.95m, DateTimeOffset.Now));
        store.Add(SampleTrade(Guid.NewGuid(), ContractStatus.Lost, -1.00m, DateTimeOffset.Now));

        Assert.Equal(2, raised);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void CorruptFile_StartsEmpty_WithoutThrowing()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "trades.json"), "{ this is not valid json !!!");

        var store = new TradeStore(dir);
        Assert.Empty(store.Trades);

        Directory.Delete(dir, recursive: true);
    }
}