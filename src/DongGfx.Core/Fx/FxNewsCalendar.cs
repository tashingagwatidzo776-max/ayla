using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DongGfx.Core.Fx;

/// <summary>One scheduled macro event (UTC instant).</summary>
public sealed record FxNewsEvent(long TimeUtc, string Impact, string Title);

/// <summary>
/// The economic-calendar veto: high-impact windows (NFP, CPI, FOMC…) where
/// the FX brain must not open new positions. Events come from a JSON file in
/// the data dir (docs/news-calendar.example.json documents the format) —
/// maintained by the operator or refreshed later by a scheduler. A missing
/// file means "no known events", never a crash; the calendar is data, the
/// veto logic is pure.
/// </summary>
public sealed class FxNewsCalendar
{
    private readonly List<FxNewsEvent> _events;
    private readonly HashSet<string> _blockingImpacts;

    public FxNewsCalendar(IEnumerable<FxNewsEvent>? events = null, IEnumerable<string>? blockingImpacts = null)
    {
        _events = events is null ? new List<FxNewsEvent>() : new List<FxNewsEvent>(events);
        _events.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        _blockingImpacts = new HashSet<string>(
            blockingImpacts ?? new[] { "high" }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All known events (for tests/UI).</summary>
    public IReadOnlyList<FxNewsEvent> Events => _events;

    /// <summary>Load from the operator-maintained JSON file. Missing file →
    /// empty calendar; malformed entries are skipped, never thrown.</summary>
    public static FxNewsCalendar Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FxNewsCalendar();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("events", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return new FxNewsCalendar();
            }

            var events = new List<FxNewsEvent>();
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("time", out var t) ||
                    !e.TryGetProperty("title", out var title))
                {
                    continue;
                }

                var timeText = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                if (!DateTimeOffset.TryParse(timeText, out var when))
                {
                    continue;
                }

                events.Add(new FxNewsEvent(
                    when.ToUnixTimeSeconds(),
                    e.TryGetProperty("impact", out var i) ? i.GetString() ?? "high" : "high",
                    title.GetString() ?? "event"));
            }

            return new FxNewsCalendar(events);
        }
        catch (JsonException)
        {
            return new FxNewsCalendar();
        }
        catch (IOException)
        {
            return new FxNewsCalendar();
        }
    }

    /// <summary>True when <paramref name="now"/> falls inside a blocking
    /// event's window [event - before, event + after]. Reason names the
    /// nearest blocking event for the journal.</summary>
    public bool IsBlackout(DateTimeOffset now, TimeSpan before, TimeSpan after, out string reason)
    {
        var nowSec = now.ToUnixTimeSeconds();
        var beforeSec = (long)before.TotalSeconds;
        var afterSec = (long)after.TotalSeconds;

        FxNewsEvent? nearest = null;
        var nearestDistance = long.MaxValue;
        foreach (var e in _events)
        {
            if (!_blockingImpacts.Contains(e.Impact))
            {
                continue;
            }

            if (nowSec < e.TimeUtc - beforeSec || nowSec > e.TimeUtc + afterSec)
            {
                continue;
            }

            var distance = Math.Abs(nowSec - e.TimeUtc);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = e;
            }
        }

        if (nearest is null)
        {
            reason = "";
            return false;
        }

        var when = DateTimeOffset.FromUnixTimeSeconds(nearest.TimeUtc);
        reason = $"news blackout: {nearest.Title} at {when:HH:mm} UTC";
        return true;
    }
}
