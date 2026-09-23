using System.IO;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Tests;

/// <summary>
/// Unit tests for the journal viewer's date-range filtering and full-text
/// search. Entries are written directly to the journal JSONL store with
/// controlled timestamps (TradeJournal stamps entries with UtcNow, so the
/// public API cannot produce spread-out dates).
/// </summary>
[Trait("Category", "Unit")]
public class JournalViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly Guid _accountId;
    private readonly TradeJournal _journal;

    public JournalViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_journalvm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _accountId = Guid.NewGuid();
        _journal = new TradeJournal(_dir);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes one entry with an exact timestamp (bypasses UtcNow stamping).</summary>
    private void WriteEntry(DateTimeOffset timestamp, string category, object details)
    {
        var entry = new JournalEntry
        {
            Timestamp = timestamp,
            AccountId = _accountId,
            Category = category,
            Details = JsonSerializer.Serialize(details)
        };
        var fileName = $"journal_{timestamp.ToUniversalTime():yyyyMMdd}.jsonl";
        File.AppendAllText(
            Path.Combine(_dir, fileName),
            JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    private JournalViewModel CreateVm() => new(_journal, () => false);

    // ─── Date range filtering ─────────────────────────────

    [Fact]
    public void DateFrom_ExcludesOlderEntries()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.0m, Confidence = 0.8, Reasoning = "september entry" });
        WriteEntry(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Fall", Stake = 1.0m, Confidence = 0.8, Reasoning = "august entry" });

        var vm = CreateVm();
        vm.FilterDateFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Contains("september", vm.Entries[0].Details);
        Assert.DoesNotContain("august", vm.Entries[0].Details);
    }

    [Fact]
    public void DateTo_IncludesWholeEndDay()
    {
        // End-day evening entry must pass a FilterDateTo set to that day.
        WriteEntry(new DateTimeOffset(2026, 9, 3, 18, 30, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "started", Bankroll = 5.0m, LossStreak = 0, Reason = "evening" });
        WriteEntry(new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "stopped", Bankroll = 6.0m, LossStreak = 1, Reason = "later" });

        var vm = CreateVm();
        vm.FilterDateTo = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Contains("started", vm.Entries[0].Details);
        Assert.DoesNotContain("later", vm.Entries[0].Details);
    }

    [Fact]
    public void DateFromAndTo_FormInclusiveWindow()
    {
        WriteEntry(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), "ACCOUNT_EVENT",
            new { AccountName = "A", Event = "too-early" });
        WriteEntry(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero), "ACCOUNT_EVENT",
            new { AccountName = "A", Event = "inside" });
        WriteEntry(new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero), "ACCOUNT_EVENT",
            new { AccountName = "A", Event = "too-late" });

        var vm = CreateVm();
        vm.FilterDateFrom = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        vm.FilterDateTo = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Contains("inside", vm.Entries[0].Details);
    }

    [Fact]
    public void NoDateFilter_ShowsAllEntries()
    {
        WriteEntry(new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero), "ACCOUNT_EVENT",
            new { AccountName = "A", Event = "old" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), "ACCOUNT_EVENT",
            new { AccountName = "A", Event = "new" });

        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void BoundaryEntry_AtStartOfFromDate_IsIncluded()
    {
        var at = new DateTimeOffset(2026, 6, 10, 0, 5, 0, TimeSpan.Zero);
        WriteEntry(at, "ACCOUNT_EVENT", new { AccountName = "A", Event = "boundary" });

        var vm = CreateVm();
        vm.FilterDateFrom = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
    }

    // ─── Full-text search ─────────────────────────────────

    [Fact]
    public void Search_MatchesFormattedDetails()
    {
        // FormatBrainDecision renders "🧠 Rise | Stake: $1 | Conf: 0.8 | RSI extreme".
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.0m, Confidence = 0.8, Reasoning = "RSI extreme bounce" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Fall", Stake = 1.0m, Confidence = 0.8, Reasoning = "no clear setup" });

        var vm = CreateVm();
        vm.SearchText = "bounce";
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Contains("Rise", vm.Entries[0].Details);
        Assert.Contains("bounce", vm.Entries[0].Details);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.0m, Confidence = 0.8, Reasoning = "OverSold Bounce Setup" });

        var vm = CreateVm();
        vm.SearchText = "OVERSOLD";
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void Search_MatchesCategoryName()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "TRADE_SETTLEMENT",
            new { ContractId = "C-1", Won = true, Payout = 1.9m, Profit = 0.9m, NewBankroll = 5.9m });

        var vm = CreateVm();
        vm.SearchText = "settle"; // matches the "TRADE_SETTLEMENT" category itself
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Equal("TRADE_SETTLEMENT", vm.Entries[0].Category);
    }

    [Fact]
    public void Search_NoMatch_YieldsEmptyList()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "started", Bankroll = 5.0m, LossStreak = 0, Reason = "session start" });

        var vm = CreateVm();
        vm.SearchText = "nonexistent-needle";
        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Entries);
        Assert.Contains("0 entries", vm.StatsText);
    }

    [Fact]
    public void EmptyOrWhitespaceSearch_ShowsEverything()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "started", Bankroll = 5.0m, LossStreak = 0, Reason = "a" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "stopped", Bankroll = 5.5m, LossStreak = 0, Reason = "b" });

        var vm = CreateVm();
        vm.SearchText = "   ";
        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void Search_CombinesWithDateFilter()
    {
        WriteEntry(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.0m, Confidence = 0.8, Reasoning = "oversold august" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.0m, Confidence = 0.8, Reasoning = "oversold september" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Fall", Stake = 1.0m, Confidence = 0.8, Reasoning = "no setup september" });

        var vm = CreateVm();
        vm.FilterDateFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        vm.SearchText = "oversold";
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Contains("september", vm.Entries[0].Details);
        Assert.Contains("oversold", vm.Entries[0].Details);
    }

    // ─── Category and account filters (supporting behavior) ──

    [Fact]
    public void CategoryFilter_RestrictsToMatchingCategory()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "BRAIN_DECISION",
            new { BrainType = "Growth", Symbol = "frxEURUSD", Direction = "Rise", Stake = 1.0m, Confidence = 0.8, Reasoning = "decision" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "TRADE_SETTLEMENT",
            new { ContractId = "C-9", Won = true, Payout = 1.9m, Profit = 0.9m, NewBankroll = 5.9m });

        var vm = CreateVm();
        vm.FilterCategory = "TRADE_SETTLEMENT";
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Equal("TRADE_SETTLEMENT", vm.Entries[0].Category);
    }

    [Fact]
    public void SelectedAccount_ExcludesOtherAccounts()
    {
        var other = Guid.NewGuid();
        var entry = new JournalEntry
        {
            Timestamp = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            AccountId = other,
            Category = "ACCOUNT_EVENT",
            Details = JsonSerializer.Serialize(new { AccountName = "B", Event = "other-account" })
        };
        File.AppendAllText(
            Path.Combine(_dir, "journal_20260901.jsonl"),
            JsonSerializer.Serialize(entry) + Environment.NewLine);
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "ACCOUNT_EVENT",
            new { AccountName = "A", Event = "main-account" });

        var vm = CreateVm();
        vm.SelectedAccountId = _accountId;
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Entries);
        Assert.Contains("main-account", vm.Entries[0].Details);
    }

    [Fact]
    public void ClearFilter_ResetsCategoryAndAccount()
    {
        var vm = CreateVm();
        vm.FilterCategory = "TRADE_SETTLEMENT";
        vm.SelectedAccountId = _accountId;

        vm.ClearFilterCommand.Execute(null);

        Assert.Equal("ALL", vm.FilterCategory);
        Assert.Null(vm.SelectedAccountId);
    }

    [Fact]
    public void StatsText_ReflectsLoadedEntryCount()
    {
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "started", Bankroll = 5.0m, LossStreak = 0, Reason = "a" });
        WriteEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), "GROWTH_STATE",
            new { State = "stopped", Bankroll = 5.5m, LossStreak = 0, Reason = "b" });

        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);

        Assert.Contains("2 entries", vm.StatsText);
    }
}
