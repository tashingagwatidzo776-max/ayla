using System.Text.Json;
using System.Text.Json.Serialization;
using DongGfx.Core.Models;

namespace DongGfx.Core;

/// <summary>
/// Persistent JSON trade log under the app data directory
/// (%APPDATA%\tf\data\trades.json). Thread-safe; every mutation is
/// written through so the log survives crashes.
/// </summary>
public sealed class TradeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly List<Trade> _trades = new();
    private readonly object _sync = new();

    /// <summary>Raised (on the adding thread) after a trade is persisted.</summary>
    public event Action<Trade>? TradeAdded;

    public TradeStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "trades.json");
        Load();
    }

    public string FilePath => _filePath;

    /// <summary>All recorded trades, newest first.</summary>
    public IReadOnlyList<Trade> Trades
    {
        get
        {
            lock (_sync)
            {
                return _trades.OrderByDescending(t => t.SettledAt).ToArray();
            }
        }
    }

    public void Add(Trade trade)
    {
        lock (_sync)
        {
            _trades.Add(trade);
            SaveLocked();
        }

        TradeAdded?.Invoke(trade);
    }

    /// <summary>
    /// Trades for one account (null = primary account), optionally narrowed to
    /// a single source (see <see cref="TradeSource"/>), oldest first.
    /// </summary>
    public IReadOnlyList<Trade> ForAccount(Guid? accountId, string? source = null)
    {
        lock (_sync)
        {
            return _trades
                .Where(t => MatchesAccount(t, accountId) && (source is null || t.Source == source))
                .OrderBy(t => t.SettledAt)
                .ToArray();
        }
    }

    public DailySummary SummaryFor(DateTimeOffset day) => SummaryFor(day, null);

    public DailySummary SummaryFor(DateTimeOffset day, Guid? accountId, string? source = null)
    {
        lock (_sync)
        {
            var todays = _trades
                .Where(t => t.SettledAt.Date == day.Date)
                .Where(t => MatchesAccount(t, accountId))
                .Where(t => source is null || t.Source == source)
                .ToArray();
            var wins = todays.Count(t => t.IsWin);
            return new DailySummary(
                todays.Length,
                wins,
                todays.Length - wins,
                todays.Sum(t => t.Profit));
        }
    }

    private static bool MatchesAccount(Trade trade, Guid? accountId) =>
        accountId is null ? trade.AccountId is null : trade.AccountId == accountId;

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<Trade>>(json, JsonOptions);
            if (loaded is not null)
            {
                _trades.AddRange(loaded);
            }
        }
        catch
        {
            // A corrupt log must never crash the app; start empty.
            _trades.Clear();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_trades, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best-effort persistence; in-memory log still works.
        }
    }
}
