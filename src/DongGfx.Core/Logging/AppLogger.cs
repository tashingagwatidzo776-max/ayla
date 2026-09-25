using System.Collections.Concurrent;
using System.Text.Json;

namespace DongGfx.Core.Logging;

/// <summary>
/// Structured logging service with rolling daily log files. Thread-safe;
/// writes are buffered and flushed periodically. Logs are stored under
/// %APPDATA%/tf/data/logs/ as JSONL files.
/// </summary>
public sealed class AppLogger : IDisposable
{
    private readonly string _logDir;
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();
    private bool _disposed;
    private readonly bool _logUsable;

    /// <summary>The error the constructor hit while preparing the log
    /// directory; null when the directory was usable (or null on purpose —
    /// see the ctor). Memory-only when set.</summary>
    public Exception? DirectoryError { get; private set; }

    /// <summary>Minimum log level to persist. Default: Info.</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    public AppLogger(string dataDirectory)
    {
        _logDir = Path.Combine(dataDirectory, "logs");
        // Same rule as TradeJournal: this is resolved eagerly from the DI
        // graph at startup, so the constructor must not throw — degrade to
        // memory-only (queue drains and drops on flush, reads start empty).
        _logUsable = TryPrepareDirectory(_logDir, out var error);
        DirectoryError = error;
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Create the directory and prove it is writable with a probe
    /// file (CreateDirectory alone succeeds on an existing read-only dir).</summary>
    private static bool TryPrepareDirectory(string dir, out Exception? error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".log_probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public void Debug(string message, string? category = null, Dictionary<string, object>? properties = null)
        => Log(LogLevel.Debug, message, category, properties);

    public void Info(string message, string? category = null, Dictionary<string, object>? properties = null)
        => Log(LogLevel.Info, message, category, properties);

    public void Warn(string message, string? category = null, Dictionary<string, object>? properties = null)
        => Log(LogLevel.Warn, message, category, properties);

    public void Error(string message, Exception? ex = null, string? category = null)
    {
        var props = new Dictionary<string, object>();
        if (ex != null)
        {
            props["Exception"] = ex.GetType().Name;
            props["Message"] = ex.Message;
            if (ex.StackTrace != null)
                props["StackTrace"] = ex.StackTrace.Length > 500 ? ex.StackTrace[..500] : ex.StackTrace;
        }
        Log(LogLevel.Error, message, category, props);
    }

    public void Log(LogLevel level, string message, string? category = null, Dictionary<string, object>? properties = null)
    {
        if (level < MinLevel) return;

        _queue.Enqueue(new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Message = message,
            Category = category ?? "",
            Properties = properties ?? new Dictionary<string, object>()
        });
    }

    /// <summary>Read recent log entries from disk.</summary>
    public IReadOnlyList<LogEntry> GetRecent(int count = 200, LogLevel? minLevel = null)
    {
        var result = new List<LogEntry>();
        try
        {
            var files = Directory.GetFiles(_logDir, "log_*.jsonl")
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
                        var entry = JsonSerializer.Deserialize<LogEntry>(line);
                        if (entry != null && (minLevel == null || entry.Level >= minLevel))
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

    private void Flush()
    {
        if (_queue.IsEmpty) return;
        if (!_logUsable) { _queue.Clear(); return; }   // memory-only: drain and drop

        var fileName = $"log_{DateTime.UtcNow:yyyyMMdd}.jsonl";
        var filePath = Path.Combine(_logDir, fileName);

        // Dequeue and write under one lock so a timer flush cannot race the
        // final flush from Dispose onto the same file.
        lock (_writeLock)
        {
            if (_queue.IsEmpty) return;

            var batch = new List<LogEntry>();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer?.Dispose();
        Flush();
    }
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
    public string Category { get; set; } = "";
    public Dictionary<string, object> Properties { get; set; } = new();
}
