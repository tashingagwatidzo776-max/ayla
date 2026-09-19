using System.Collections.Concurrent;
using System.Text.Json;

namespace DongGfx.Core.Analytics;

/// <summary>
/// Records connection state changes over time for diagnosing intermittent
/// disconnects. Maintains a rolling log of state transitions per account,
/// persisted to disk as daily JSONL files.
/// </summary>
public sealed class HeartbeatLog
{
    private readonly string _logDir;
    private readonly ConcurrentQueue<HeartbeatEntry> _entries = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();

    public HeartbeatLog(string dataDirectory)
    {
        _logDir = Path.Combine(dataDirectory, "heartbeats");
        Directory.CreateDirectory(_logDir);
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Record a state change for an account.</summary>
    public void Record(Guid accountId, string accountName, string oldState, string newState, string? detail = null)
    {
        _entries.Enqueue(new HeartbeatEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            AccountName = accountName,
            OldState = oldState,
            NewState = newState,
            Detail = detail ?? ""
        });
    }

    /// <summary>Get recent heartbeat entries for an account.</summary>
    public IReadOnlyList<HeartbeatEntry> GetRecent(Guid? accountId = null, int count = 100)
    {
        var result = new List<HeartbeatEntry>();

        // Read from disk files (newest first)
        try
        {
            var files = Directory.GetFiles(_logDir, "heartbeat_*.jsonl")
                .OrderByDescending(f => f)
                .Take(5);

            foreach (var file in files)
            {
                // Read under the write lock: the flush timer may append to the
                // newest file while we read it, and Windows forbids concurrent
                // openers regardless of share mode (IOException).
                string[] lines;
                lock (_writeLock)
                {
                    lines = File.ReadAllLines(file);
                }
                foreach (var line in lines.Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<HeartbeatEntry>(line);
                        if (entry != null && (accountId == null || entry.AccountId == accountId))
                        {
                            result.Add(entry);
                            if (result.Count >= count) break;
                        }
                    }
                    catch { /* skip malformed */ }
                }
                if (result.Count >= count) break;
            }
        }
        catch { /* start empty */ }

        return result;
    }

    /// <summary>Get uptime percentage for an account over the last N hours.</summary>
    public double GetUptimePercent(Guid accountId, int hours = 24)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-hours);
        var entries = GetRecent(accountId, 1000)
            .Where(e => e.Timestamp >= cutoff)
            .ToList();

        if (entries.Count == 0) return 100; // no data = assume up

        var totalTime = (DateTimeOffset.UtcNow - cutoff).TotalMinutes;
        var downMinutes = 0.0;

        for (var i = 0; i < entries.Count - 1; i++)
        {
            if (entries[i].NewState.Contains("Disconnected") || entries[i].NewState.Contains("Error"))
            {
                var duration = (entries[i + 1].Timestamp - entries[i].Timestamp).TotalMinutes;
                downMinutes += Math.Min(duration, totalTime);
            }
        }

        return Math.Max(0, Math.Min(100, (1 - downMinutes / totalTime) * 100));
    }

    private void Flush()
    {
        if (_entries.IsEmpty) return;

        var fileName = $"heartbeat_{DateTime.UtcNow:yyyyMMdd}.jsonl";
        var filePath = Path.Combine(_logDir, fileName);

        // Dequeue and write under one lock so a timer flush cannot race the
        // final flush from Dispose onto the same file.
        lock (_writeLock)
        {
            if (_entries.IsEmpty) return;

            var batch = new List<HeartbeatEntry>();
            while (_entries.TryDequeue(out var entry))
                batch.Add(entry);

            if (batch.Count == 0) return;

            try
            {
                using var writer = new StreamWriter(filePath, append: true);
                foreach (var entry in batch)
                {
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                }
            }
            catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        Flush();
    }
}

public sealed class HeartbeatEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string OldState { get; set; } = "";
    public string NewState { get; set; } = "";
    public string Detail { get; set; } = "";
}
