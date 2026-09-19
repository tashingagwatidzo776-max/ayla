using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.Core;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.ViewModels;

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
    private DateTimeOffset? filterDateFrom;

    [ObservableProperty]
    private DateTimeOffset? filterDateTo;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string statsText = "No data yet";

    public ObservableCollection<JournalEntryViewModel> Entries { get; } = new();

    public IReadOnlyList<string> Categories { get; } = new[]
    {
        "ALL", "BRAIN_DECISION", "TRADE_EXECUTION", "TRADE_SETTLEMENT",
        "GROWTH_STATE", "ACCOUNT_EVENT", "REAL_MONEY_UNLOCK_ARMED",
        "REAL_MONEY_UNLOCK_STALE"
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

            if (FilterDateFrom.HasValue && entry.Timestamp < FilterDateFrom.Value)
                continue;
            if (FilterDateTo.HasValue && entry.Timestamp > FilterDateTo.Value.AddDays(1))
                continue;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var details = FormatDetails(entry);
                if (!details.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

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

    [RelayCommand]
    private void ExportJournal()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
                DefaultExt = ".csv",
                FileName = $"tf_journal_{DateTime.UtcNow:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() != true) return;

            var entries = _journal.GetRecent(SelectedAccountId, MaxEntries * 10);
            using var writer = new StreamWriter(dialog.FileName);

            if (dialog.FileName.EndsWith(".json"))
            {
                // JSON export
                var json = System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                writer.Write(json);
            }
            else
            {
                // CSV export
                writer.WriteLine("Timestamp,Account,Category,Details");
                foreach (var e in entries)
                {
                    var details = e.Details.Replace("\"", "\"\"");
                    writer.WriteLine($"{e.Timestamp:O},{e.AccountId},{e.Category},\"{details}\"");
                }
            }

            StatsText = $"Exported {entries.Count} entries to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatsText = $"Export failed: {ex.Message}";
        }
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
                "REAL_MONEY_UNLOCK_ARMED" => FormatRealMoneyUnlockArmed(entry),
                "REAL_MONEY_UNLOCK_STALE" => FormatRealMoneyUnlockStale(entry),
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

    /// <summary>The moment real trading became possible this session —
    /// formatted like a settlement so the audit trail reads at a glance:
    /// who was armed, whether the manual surfaces joined, and how many of
    /// the accounts are API-verified real.</summary>
    private string FormatRealMoneyUnlockArmed(JournalEntry entry)
    {
        var details = entry.Details;
        var countText = ExtractJsonValue(details, "AccountCount");
        var verifiedText = ExtractJsonValue(details, "VerifiedReal");
        var manual = ExtractJsonValue(details, "ManualSurfaces");
        var armedBy = ExtractJsonValue(details, "ArmedBy");

        int.TryParse(countText, out var accountCount);
        int.TryParse(verifiedText, out var verifiedCount);

        var scopes = new List<string>();
        if (accountCount > 0)
        {
            var names = ExtractJsonArray(details, "Accounts");
            scopes.Add(!string.IsNullOrEmpty(names) ? names : $"{accountCount} account(s)");
        }

        if (manual == "true")
        {
            scopes.Add("manual surfaces");
        }

        if (scopes.Count == 0)
        {
            return $"🔓 {details}"; // malformed payload — show it raw rather than an empty arm line
        }

        var verified = accountCount > 0 ? $" ({verifiedCount}/{accountCount} API-verified real)" : "";
        var via = string.IsNullOrEmpty(armedBy) ? "" : $" | via {armedBy}";
        return "🔓 REAL-MONEY UNLOCK ARMED | " + string.Join(" + ", scopes) + verified + via;
    }

    /// <summary>An unlock left armed past the staleness threshold. Logged
    /// through the journal's plain-message path, so the details string IS
    /// the line — no JSON to unpack.</summary>
    private string FormatRealMoneyUnlockStale(JournalEntry entry) =>
        $"⏰ STALE UNLOCK | {entry.Details}";

    /// <summary>Extracts a JSON string array as comma-joined values, using
    /// the same hand-rolled parsing as <see cref="ExtractJsonValue"/> (the
    /// formatter deliberately avoids a JSON DOM dependency).</summary>
    private string ExtractJsonArray(string json, string key)
    {
        var search = $"\"{key}\":";
        var idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        idx += search.Length;
        while (idx < json.Length && json[idx] == ' ') idx++;

        if (idx >= json.Length || json[idx] != '[') return "";

        var start = idx + 1;
        var end = json.IndexOf(']', start);
        if (end < 0) return "";

        var items = json[start..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim('"'))
            .Where(item => item.Length > 0);
        return string.Join(", ", items);
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
