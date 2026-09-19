using System.Text.Json;
using DongGfx.Core.Models;

namespace DongGfx.Core.Analytics;

/// <summary>
/// Persists received tick history to disk so the strategy optimizer can
/// backtest on real market data instead of synthetic random walks. Ticks are
/// stored per symbol in daily JSONL files under the data directory.
/// </summary>
public sealed class TickHistoryCache
{
    private readonly string _cacheDir;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Tick>> _inMemory = new();

    public TickHistoryCache(string dataDirectory)
    {
        _cacheDir = Path.Combine(dataDirectory, "tick_history");
        Directory.CreateDirectory(_cacheDir);
        LoadAll();
    }

    /// <summary>Append a batch of ticks (typically from a history backfill).</summary>
    public void AddTicks(string symbol, IReadOnlyList<Tick> ticks)
    {
        if (ticks.Count == 0) return;

        lock (_sync)
        {
            if (!_inMemory.TryGetValue(symbol, out var list))
            {
                list = new List<Tick>();
                _inMemory[symbol] = list;
            }

            // Deduplicate by epoch (ticks are unique per timestamp).
            var existingEpochs = new HashSet<long>(list.Select(t => t.Epoch));
            var newTicks = ticks.Where(t => !existingEpochs.Contains(t.Epoch)).ToList();
            if (newTicks.Count == 0) return;

            list.AddRange(newTicks);
            list.Sort((a, b) => a.Epoch.CompareTo(b.Epoch));
        }

        Persist(symbol, ticks);
    }

    /// <summary>Get cached ticks for a symbol, optionally limited to the last N days.</summary>
    public IReadOnlyList<Tick> GetTicks(string symbol, int? maxDays = null)
    {
        lock (_sync)
        {
            if (!_inMemory.TryGetValue(symbol, out var list))
                return Array.Empty<Tick>();

            if (maxDays is null)
                return list.ToArray();

            var cutoff = DateTimeOffset.UtcNow.AddDays(-maxDays.Value).ToUnixTimeMilliseconds();
            return list.Where(t => t.Epoch >= cutoff).ToArray();
        }
    }

    /// <summary>Get all available symbols in the cache.</summary>
    public IReadOnlyList<string> GetSymbols()
    {
        lock (_sync)
        {
            return _inMemory.Keys.ToArray();
        }
    }

    /// <summary>Get total tick count for a symbol.</summary>
    public int GetTickCount(string symbol)
    {
        lock (_sync)
        {
            return _inMemory.TryGetValue(symbol, out var list) ? list.Count : 0;
        }
    }

    private void Persist(string symbol, IReadOnlyList<Tick> newTicks)
    {
        try
        {
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            var filePath = Path.Combine(_cacheDir, $"{symbol}_{today}.jsonl");

            lock (_sync)
            {
                using var writer = new StreamWriter(filePath, append: true);
                foreach (var tick in newTicks)
                {
                    var line = JsonSerializer.Serialize(new
                    {
                        tick.Symbol,
                        tick.Quote,
                        tick.Ask,
                        tick.Bid,
                        tick.Epoch,
                        tick.PipSize
                    });
                    writer.WriteLine(line);
                }
            }
        }
        catch
        {
            // Best-effort persistence; in-memory cache still works.
        }
    }

    private void LoadAll()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;

            foreach (var file in Directory.GetFiles(_cacheDir, "*.jsonl"))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var symbol = fileName[..fileName.LastIndexOf('_')];
                    var lines = File.ReadAllLines(file);

                    lock (_sync)
                    {
                        if (!_inMemory.TryGetValue(symbol, out var list))
                        {
                            list = new List<Tick>();
                            _inMemory[symbol] = list;
                        }

                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                var doc = JsonDocument.Parse(line);
                                var root = doc.RootElement;
                                var tick = new Tick(
                                    root.GetProperty("Symbol").GetString() ?? symbol,
                                    root.GetProperty("Quote").GetDouble(),
                                    root.GetProperty("Ask").GetDouble(),
                                    root.GetProperty("Bid").GetDouble(),
                                    root.GetProperty("Epoch").GetInt64(),
                                    root.GetProperty("PipSize").GetInt32());
                                list.Add(tick);
                            }
                            catch { /* skip malformed lines */ }
                        }

                        list.Sort((a, b) => a.Epoch.CompareTo(b.Epoch));
                    }
                }
                catch { /* skip corrupt files */ }
            }
        }
        catch { /* start empty */ }
    }
}
