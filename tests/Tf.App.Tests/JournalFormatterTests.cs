using System.IO;
using System.Text.Json;
using Tf.App.ViewModels;
using Tf.Core;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.Tests;

/// <summary>
/// Tests for the journal viewer's per-category detail formatting, the live
/// trade-insert path (TradeAdded), and the capped entry list. The date-range
/// and search filtering surface is covered in JournalViewModelTests.
/// </summary>
[Trait("Category", "Unit")]
public class JournalFormatterTests : IDisposable
{
    private readonly string _dir;
    private readonly Guid _accountId;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;

    public JournalFormatterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_jrnfmt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _accountId = Guid.NewGuid();
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(_dir);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private JournalViewModel CreateVm() => new(_journal, _store, () => false);

    private void WriteEntry(string category, object details)
    {
        var entry = new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = _accountId,
            Category = category,
            Details = JsonSerializer.Serialize(details)
        };
        File.AppendAllText(
            Path.Combine(_dir, $"journal_{DateTime.UtcNow:yyyyMMdd}.jsonl"),
            JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    // ─── Category formatters ──────────────────────────────

    [Fact]
    public void Refresh_FormatsBrainDecision_WithExtractedFields()
    {
        WriteEntry("BRAIN_DECISION", new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.5m, Confidence = 0.75, Reasoning = "oversold bounce" });
        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);

        var details = vm.Entries.Single().Details;
        Assert.Contains("Rise", details);
        Assert.Contains("$1.5", details);
        Assert.Contains("0.75", details);
        Assert.Contains("oversold bounce", details);
    }

    [Fact]
    public void Refresh_FormatsTradeSettlement_WithWinIcon()
    {
        WriteEntry("TRADE_SETTLEMENT", new { ContractId = "C-1", Won = true, Payout = 1.9m, Profit = 0.9m, NewBankroll = 5.9m });
        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);

        var details = vm.Entries.Single().Details;
        Assert.Contains("WIN", details);
        Assert.Contains("$0.9", details);
        Assert.Contains("$5.9", details);
    }

    [Fact]
    public void Refresh_FormatsGrowthState_AndAccountEvent()
    {
        WriteEntry("GROWTH_STATE", new { State = "started", Bankroll = 5m, LossStreak = 0, Reason = "boot" });
        WriteEntry("ACCOUNT_EVENT", new { AccountName = "Alpha", Event = "connected", Details = "demo" });
        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Entries.Count);
        // GetRecent returns newest-first; select by content, not position.
        Assert.Contains("started", vm.Entries.Single(e => e.Details.Contains("Growth") || e.Details.Contains("started")).Details);
        var accountEvent = vm.Entries.Single(e => e.Details.Contains("connected"));
        Assert.Contains("demo", accountEvent.Details);
    }

    [Fact]
    public void Refresh_UnknownCategory_ShowsRawDetails()
    {
        WriteEntry("SOMETHING_ELSE", new { Raw = "payload" });
        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);

        Assert.Contains("payload", vm.Entries.Single().Details);
    }

    // ─── Live trade insert ────────────────────────────────

    [Fact]
    public void TradeAdded_WinTrade_InsertsSettlementRow_AtTop()
    {
        var vm = CreateVm();

        _store.Add(new Trade(
            Guid.NewGuid(), "frxEURUSD", Direction.Rise, 1m, "USD", 1.17, 1700000300,
            "C-live", ContractStatus.Won, 0.9m, 1.165, 1700000600, DateTimeOffset.UtcNow,
            _accountId, "Live", TradeSource.Growth));

        Assert.Single(vm.Entries);
        var row = vm.Entries[0];
        Assert.Equal("TRADE_SETTLEMENT", row.Category);
        Assert.Contains("WIN", row.Details);
    }

    [Fact]
    public void TradeAdded_RespectsCategoryFilter()
    {
        var vm = CreateVm();
        vm.FilterCategory = "BRAIN_DECISION"; // not TRADE_SETTLEMENT → live inserts suppressed
        vm.RefreshCommand.Execute(null);

        _store.Add(new Trade(
            Guid.NewGuid(), "frxEURUSD", Direction.Fall, 1m, "USD", 1.17, 1700000300,
            "C-live-2", ContractStatus.Lost, -1m, 1.165, 1700000600, DateTimeOffset.UtcNow,
            _accountId, "Live", TradeSource.Growth));

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void TradeAdded_CapsListAtMaxEntries()
    {
        var vm = CreateVm();
        vm.MaxEntries = 3;

        for (int i = 0; i < 5; i++)
        {
            _store.Add(new Trade(
                Guid.NewGuid(), "frxEURUSD", Direction.Rise, 1m, "USD", 1.17, 1700000300 + i,
                $"C-{i}", ContractStatus.Won, 0.9m, 1.165, 1700000600 + i, DateTimeOffset.UtcNow,
                _accountId, "Live", TradeSource.Growth));
        }

        Assert.Equal(3, vm.Entries.Count);
    }

    [Fact]
    public void StatsText_WithSelectedAccount_ShowsJournalStats()
    {
        _journal.LogBrainDecision(_accountId, "Growth", "frxEURUSD", "Rise", 1m, 0.8, "d");
        _journal.LogTradeSettlement(_accountId, "C-1", won: true, 1.9m, 0.9m, 5.9m);
        _journal.Flush();

        var vm = CreateVm();
        vm.SelectedAccountId = _accountId;
        vm.RefreshCommand.Execute(null);

        Assert.Contains("Decisions: 1", vm.StatsText);
        Assert.Contains("Trades: 1", vm.StatsText);
    }
}
