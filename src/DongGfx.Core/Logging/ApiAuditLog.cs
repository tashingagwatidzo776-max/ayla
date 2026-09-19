using System.Collections.Concurrent;
using System.Text.Json;

namespace DongGfx.Core.Logging;

/// <summary>
/// Immutable audit trail for every Deriv API request and response. Each entry
/// is appended to a daily JSONL file and cannot be modified after writing.
/// Used for dispute resolution and debugging.
/// </summary>
public sealed class ApiAuditLog : IDisposable
{
    private readonly string _logDir;
    private readonly ConcurrentQueue<AuditEntry> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();
    private bool _disposed;

    public ApiAuditLog(string dataDirectory)
    {
        _logDir = Path.Combine(dataDirectory, "api_audit");
        Directory.CreateDirectory(_logDir);
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Log an outgoing API request.</summary>
    public void LogRequest(string endpoint, string method, string? accountId, string payload)
    {
        if (_disposed) return;
        _queue.Enqueue(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Direction = "REQUEST",
            Endpoint = endpoint,
            Method = method,
            AccountId = accountId ?? "",
            Payload = Truncate(payload, 2000)
        });
    }

    /// <summary>Log an incoming API response.</summary>
    public void LogResponse(string endpoint, int statusCode, string? accountId, string body, long elapsedMs)
    {
        if (_disposed) return;
        _queue.Enqueue(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Direction = "RESPONSE",
            Endpoint = endpoint,
            Method = statusCode.ToString(),
            AccountId = accountId ?? "",
            Payload = Truncate(body, 2000),
            ElapsedMs = elapsedMs
        });
    }

    /// <summary>Log an error during an API call.</summary>
    public void LogError(string endpoint, string? accountId, string error)
    {
        if (_disposed) return;
        _queue.Enqueue(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Direction = "ERROR",
            Endpoint = endpoint,
            AccountId = accountId ?? "",
            Payload = Truncate(error, 2000)
        });
    }

    /// <summary>Read recent audit entries.</summary>
    public IReadOnlyList<AuditEntry> GetRecent(string? accountId = null, int count = 100)
    {
        var result = new List<AuditEntry>();
        try
        {
            var files = Directory.GetFiles(_logDir, "audit_*.jsonl")
                .OrderByDescending(f => f)
                .Take(3);

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
                        var entry = JsonSerializer.Deserialize<AuditEntry>(line);
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

    private void Flush(bool force = false)
    {
        if (_queue.IsEmpty || (!force && _disposed)) return;        var fileName = $"audit_{DateTime.UtcNow:yyyyMMdd}.jsonl";
        var filePath = Path.Combine(_logDir, fileName);

        // Dequeue and write under one lock so a timer flush cannot race the
        // final flush from Dispose onto the same file.
        lock (_writeLock)
        {
            if (_queue.IsEmpty) return;

            var batch = new List<AuditEntry>();
            while (_queue.TryDequeue(out var entry))
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

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer?.Dispose();
        Flush(force: true);
    }
}

public sealed class AuditEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Direction { get; set; } = "";  // REQUEST, RESPONSE, ERROR
    public string Endpoint { get; set; } = "";
    public string Method { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Payload { get; set; } = "";
    public long ElapsedMs { get; set; }
}
