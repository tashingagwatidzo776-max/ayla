using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Infrastructure;
using Tf.Core;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.ViewModels;

/// <summary>
/// ViewModel for the trade journal log viewer. Shows all brain decisions,
/// trade executions, and account activity across all accounts.
/// Auto-refreshes when new trades are recorded.
/// </summary>
public partial class JournalViewModel : ObservableObject
{
    private readonly TradeJournal _journal;
    private readonly TradeStore _store;
    private readonly Func<bool> _killSwitch;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private Guid? selectedAccountId;

    [ObservableProperty]
    private string filterCategory = "ALL";

    [ObservableProperty]
    private int maxEntries = 200;

    [ObservableProperty]
    private string statsText = "No data yet";

    public ObservableCollection<JournalEntryViewModel> Entries { get; } = new();

    public IReadOnlyList<string> Categories { get; } = new[]
    {
        "ALL", "BRAIN_DECISION", "TRADE_EXECUTION", "TRADE_SETTLEMENT",
        "GROWTH_STATE", "ACCOUNT_EVENT"
    };

    public JournalViewModel(TradeJournal journal, TradeStore store, Func<bool> killSwitch)
    {
        _journal = journal;
        _store = store;
        _killSwitch = killSwitch;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Auto-refresh when a new trade is recorded.
        _store.TradeAdded += OnTradeAdded;
    }

    [RelayCommand]
    private void Refresh()
    {
        Entries.Clear();
        var entries = _journal.GetRecent(SelectedAccountId, MaxEntries);

        foreach (var entry in entries)
        {
            if (FilterCategory != "ALL" && entry.Category != FilterCategory)
                continue;

            Entries.Add(new JournalEntryViewModel
            {
                Timestamp = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
                AccountId = entry.AccountId.ToString()[..8],
                Category = entry.Category,
                Details = FormatDetails(entry)
            });
        }

        UpdateStats();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterCategory = "ALL";
        SelectedAccountId = null;
        Refresh();
    }

    private string FormatDetails(JournalEntry entry)
    {
        try
        {
            return entry.Category switch
            {
                "BRAIN_DECISION" => FormatBrainDecision(entry),
                "TRADE_EXECUTION" => FormatTradeExecution(entry),
                "TRADE_SETTLEMENT" => FormatTradeSettlement(entry),
                "GROWTH_STATE" => FormatGrowthState(entry),
                "ACCOUNT_EVENT" => FormatAccountEvent(entry),
                _ => entry.Details
            };
        }
        catch
        {
            return entry.Details;
        }
    }

    private string FormatBrainDecision(JournalEntry entry)
    {
        // Simple formatting without System.Text.Json dependency
        var details = entry.Details;
        var direction = ExtractJsonValue(details, "Direction");
        var stake = ExtractJsonValue(details, "Stake");
        var confidence = ExtractJsonValue(details, "Confidence");
        var reasoning = ExtractJsonValue(details, "Reasoning");

        return $"🧠 {direction} | Stake: ${stake} | Conf: {confidence} | {reasoning}";
    }

    private string FormatTradeSettlement(JournalEntry entry)
    {
        var details = entry.Details;
        var won = ExtractJsonValue(details, "Won");
        var profit = ExtractJsonValue(details, "Profit");
        var newBankroll = ExtractJsonValue(details, "NewBankroll");

        var icon = won == "true" ? "✅" : "❌";
        return $"{icon} {(won == "true" ? "WIN" : "LOSS")} | Profit: ${profit} | Bankroll: ${newBankroll}";
    }

    private string FormatGrowthState(JournalEntry entry)
    {
        var details = entry.Details;
        var state = ExtractJsonValue(details, "State");
        var bankroll = ExtractJsonValue(details, "Bankroll");
        var lossStreak = ExtractJsonValue(details, "LossStreak");

        return $"💰 {state} | Bankroll: ${bankroll} | Loss streak: {lossStreak}";
    }

    private string FormatTradeExecution(JournalEntry entry)
    {
        var details = entry.Details;
        var status = ExtractJsonValue(details, "Status");
        var error = ExtractJsonValue(details, "Error");

        return $"📤 {status}" + (string.IsNullOrEmpty(error) ? "" : $" | Error: {error}");
    }

    private string FormatAccountEvent(JournalEntry entry)
    {
        var details = entry.Details;
        var eventName = ExtractJsonValue(details, "Event");
        var detailsText = ExtractJsonValue(details, "Details");

        return $"👤 {eventName}" + (string.IsNullOrEmpty(detailsText) ? "" : $" | {detailsText}");
    }

    private string ExtractJsonValue(string json, string key)
    {
        var search = $"\"{key}\":";
        var idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        idx += search.Length;
        while (idx < json.Length && json[idx] == ' ') idx++;

        if (idx >= json.Length) return "";

        if (json[idx] == '"')
        {
            // String value
            var start = idx + 1;
            var end = json.IndexOf('"', start);
            if (end < 0) return json[start..];
            return json[start..end];
        }

        // Number or boolean
        var valueEnd = idx;
        while (valueEnd < json.Length && json[valueEnd] != ',' && json[valueEnd] != '}')
            valueEnd++;

        return json[idx..valueEnd].Trim();
    }

    private void UpdateStats()
    {
        if (SelectedAccountId == null)
        {
            StatsText = $"{Entries.Count} entries loaded";
            return;
        }

        var stats = _journal.GetStats(SelectedAccountId.Value);
        StatsText = $"Decisions: {stats.TotalDecisions} | Trades: {stats.TotalSettlements} | " +
                   $"Win rate: {stats.WinRate:P0} | Period: {stats.PeriodStart:MM/dd HH:mm} - {stats.PeriodEnd:HH:mm}";
    }

    /// <summary>Auto-refresh the journal when a new trade is recorded.</summary>
    private void OnTradeAdded(Trade trade)
    {
        void Update()
        {
            // Prepend the new entry if it matches the current filter.
            if (FilterCategory != "ALL" && FilterCategory != "TRADE_SETTLEMENT")
                return;

            if (SelectedAccountId != null && trade.AccountId != SelectedAccountId)
                return;

            Entries.Insert(0, new JournalEntryViewModel
            {
                Timestamp = trade.SettledAt.ToLocalTime().ToString("HH:mm:ss.fff"),
                AccountId = (trade.AccountId ?? Guid.Empty).ToString()[..8],
                Category = "TRADE_SETTLEMENT",
                Details = trade.IsWin
                    ? $"✅ WIN | Profit: ${trade.Profit:0.##} | {trade.Symbol} {trade.Direction} stake {trade.Stake:0.##}"
                    : $"❌ LOSS | Profit: ${trade.Profit:0.##} | {trade.Symbol} {trade.Direction} stake {trade.Stake:0.##}"
            });

            // Cap the list size.
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(Entries.Count - 1);

            UpdateStats();
        }

        if (_dispatcher.CheckAccess())
            Update();
        else
            _dispatcher.BeginInvoke(Update);
    }
}

public class JournalEntryViewModel
{
    public string Timestamp { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Details { get; set; } = "";
}
