using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
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
        "ALL", "FX_ORDER", "FX_RISK", "FX_MODE", "FX_SCORECARD",
        "MT5_ORDER", "TRADE_SETTLEMENT",
        "REAL_MONEY_UNLOCK_ARMED", "REAL_MONEY_UNLOCK_STALE"
    };

    public JournalViewModel(TradeJournal journal, Func<bool> killSwitch)
    {
        _journal = journal;
        _killSwitch = killSwitch;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Auto-refresh when the FX engine, the supervisor or the deal feed
        // writes an entry (the journal is the app's single append path).
        _journal.EntryAdded += OnEntryAdded;
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
                "TRADE_SETTLEMENT" => FormatTradeSettlement(entry),
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

    private string FormatTradeSettlement(JournalEntry entry)
    {
        var details = entry.Details;
        var won = ExtractJsonValue(details, "Won");
        var profit = ExtractJsonValue(details, "Profit");
        var contractId = ExtractJsonValue(details, "ContractId");

        var icon = won == "true" ? "✅" : "❌";
        var leg = string.IsNullOrEmpty(contractId) ? "" : $" | deal {contractId}";
        return $"{icon} {(won == "true" ? "WIN" : "LOSS")} | P/L: {profit}{leg}";
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

    /// <summary>Auto-refresh the viewer when any journal entry lands.</summary>
    private void OnEntryAdded(JournalEntry entry)
    {
        void Update()
        {
            // Honour the current filter; an unmatched entry is simply not
            // prepended (the next manual Refresh re-evaluates everything).
            if (FilterCategory != "ALL" && entry.Category != FilterCategory)
                return;

            if (FilterDateFrom.HasValue && entry.Timestamp < FilterDateFrom.Value)
                return;
            if (FilterDateTo.HasValue && entry.Timestamp > FilterDateTo.Value.AddDays(1))
                return;

            Entries.Insert(0, new JournalEntryViewModel
            {
                Timestamp = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
                AccountId = entry.AccountId.ToString()[..8],
                Category = entry.Category,
                Details = FormatDetails(entry)
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
