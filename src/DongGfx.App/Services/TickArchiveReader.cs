using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DongGfx.App.Services;

/// <summary>
/// Read-side of the TickArchive: loads a symbol's archived ticks back as
/// (price, volume, time, direction) tuples for the Maps surfaces. Writes
/// stay in <see cref="TickArchive"/>; this class never touches the files'
/// content, only reads them. Tick volume is unknown at quote level (MT5
/// quotes carry bid/ask/time only), so each archived quote counts as one
/// lot of flow and direction comes from the tick rule (uptick → buy,
/// downtick → sell, zero-range carries the previous direction).
/// </summary>
public static class TickArchiveReader
{
    public static IReadOnlyList<(double Price, double Vol, long TimeMs, int Direction)> LoadToday(
        string archiveRoot, string venue, string symbol, System.DateTimeOffset? now = null)
    {
        var date = (now ?? System.DateTimeOffset.UtcNow).ToString("yyyyMMdd");
        var path = Path.Combine(archiveRoot, venue, $"{symbol}_{date}.jsonl");
        return LoadFile(path);
    }

    /// <summary>Loads today's archived quotes as raw bid/ask pairs for the
    /// liquidity map (true spread, not mid-only). Same tolerance as
    /// <see cref="LoadFile"/>: missing file or unreadable snapshot renders
    /// as an empty map, never a crash.</summary>
    public static IReadOnlyList<(double Bid, double Ask, long TimeMs)> LoadQuotesToday(
        string archiveRoot, string venue, string symbol, System.DateTimeOffset? now = null)
    {
        var date = (now ?? System.DateTimeOffset.UtcNow).ToString("yyyyMMdd");
        return LoadQuotesFile(Path.Combine(archiveRoot, venue, $"{symbol}_{date}.jsonl"));
    }

    public static IReadOnlyList<(double Bid, double Ask, long TimeMs)> LoadQuotesFile(string path)
    {
        var quotes = new List<(double, double, long)>();
        if (!File.Exists(path))
        {
            return quotes;
        }

        try
        {
            using var reader = new StreamReader(path);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("b", out var bidEl)
                        || !root.TryGetProperty("a", out var askEl)
                        || !root.TryGetProperty("t", out var timeEl)
                        || bidEl.ValueKind != JsonValueKind.Number
                        || askEl.ValueKind != JsonValueKind.Number
                        || timeEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var bid = bidEl.GetDouble();
                    var ask = askEl.GetDouble();
                    if (bid <= 0 || ask <= 0 || ask < bid)
                    {
                        continue;
                    }

                    quotes.Add((bid, ask, timeEl.GetInt64()));
                }
                catch (JsonException)
                {
                    // skip malformed lines — same tolerance as the writer's readers
                }
            }
        }
        catch (IOException)
        {
            // The writer may hold the file open mid-flush; an unreadable
            // snapshot renders as an empty map rather than a crashed tab.
        }

        return quotes;
    }

    public static IReadOnlyList<(double Price, double Vol, long TimeMs, int Direction)> LoadFile(string path)
    {
        var ticks = new List<(double, double, long, int)>();
        if (!File.Exists(path))
        {
            return ticks;
        }

        try
        {
            using var reader = new StreamReader(path);
            string? line;
            double lastMid = 0;
            var lastDirection = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("b", out var bidEl)
                        || !root.TryGetProperty("a", out var askEl)
                        || !root.TryGetProperty("t", out var timeEl)
                        || bidEl.ValueKind != JsonValueKind.Number
                        || askEl.ValueKind != JsonValueKind.Number
                        || timeEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var bid = bidEl.GetDouble();
                    var ask = askEl.GetDouble();
                    var mid = (bid + ask) / 2.0;
                    if (bid <= 0 || ask <= 0 || mid <= 0)
                    {
                        continue;
                    }

                    // Tick rule with zero-range carryover (same convention
                    // as FxMicrostructure.OrderFlowImbalance).
                    if (lastMid == 0)
                    {
                        lastDirection = 0;   // first tick: unknown, stays neutral
                    }
                    else if (mid > lastMid)
                    {
                        lastDirection = 1;
                    }
                    else if (mid < lastMid)
                    {
                        lastDirection = -1;
                    }
                    lastMid = mid;

                    ticks.Add((mid, 1.0, timeEl.GetInt64(), lastDirection));
                }
                catch (JsonException)
                {
                    // skip malformed lines — same tolerance as the writer's readers
                }
            }
        }
        catch (IOException)
        {
            // The writer may hold the file open mid-flush; an unreadable
            // snapshot renders as an empty map rather than a crashed tab.
        }

        return ticks;
    }
}
