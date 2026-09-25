using System.Collections.Concurrent;
using System.IO;

namespace DongGfx.App.Services;

/// <summary>
/// Append-only tick archive: every MT5 bridge quote lands as one JSON line
/// under %APPDATA%\tf\data\ticks\{venue}\{symbol}_{date}.jsonl.
/// This is the raw material for the tick/microstructure alpha family and for
/// post-session analysis. Writes happen on ONE background task consuming a
/// bounded queue — the UI thread never touches the filesystem, and a slow
/// disk can only drop the newest quotes (TryAdd with no wait), never stall
/// the terminal.
/// </summary>
public sealed class TickArchive : IDisposable
{
    private readonly string _root;
    private readonly BlockingCollection<(string Venue, string Symbol, string Line)> _queue = new(8192);
    private readonly Task _writer;
    private readonly Dictionary<string, StreamWriter> _open = new();
    private bool _disposed;
    private readonly bool _archiveUsable;

    /// <summary>The error the constructor hit while preparing the archive
    /// root; null when the directory was usable. Memory-only (drops ticks)
    /// when set.</summary>
    public Exception? DirectoryError { get; private set; }

    /// <summary>The last error the writer loop hit; null when every write
    /// succeeded. Kept so a dead-write surface is diagnosable (tests assert
    /// on it when a timeout would otherwise just say "file missing").</summary>
    public Exception? WriterError { get; private set; }

    public TickArchive(string? root = null)
    {
        _root = root ?? Path.Combine(DongGfx.App.Infrastructure.SettingsService.DataDir, "ticks");
        // Same rule as TradeJournal: resolved eagerly from the DI graph at
        // startup, so the constructor must not throw — degrade to a sink
        // that drops ticks instead.
        _archiveUsable = TryPrepareDirectory(_root, out var error);
        DirectoryError = error;
        _writer = Task.Run(WriterLoop);
    }

    /// <summary>Create the directory and prove it is writable with a probe
    /// file (CreateDirectory alone succeeds on an existing read-only dir).</summary>
    private static bool TryPrepareDirectory(string dir, out Exception? error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".archive_probe");
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

    /// <summary>Enqueue one tick. Fire-and-forget: never throws, never blocks;
    /// when the queue is full (disk stall) the newest tick is dropped.</summary>
    public void Add(string venue, string symbol, double bid, double ask, long epochMs)
    {
        if (_disposed || bid <= 0 || string.IsNullOrEmpty(symbol))
        {
            return;
        }

        var line = $"{{\"b\":{bid.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"\"a\":{ask.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"\"t\":{epochMs}}}";
        _queue.TryAdd((venue, symbol, line), 0);
    }

    private void WriterLoop()
    {
        foreach (var (venue, symbol, line) in _queue.GetConsumingEnumerable())
        {
            if (!_archiveUsable)
            {
                continue;   // memory-only: the queue drains, the ticks drop
            }

            try
            {
                var path = Path.Combine(_root, venue, $"{symbol}_{DateTime.UtcNow:yyyyMMdd}.jsonl");
                var key = path.ToLowerInvariant();
                if (!_open.TryGetValue(key, out var w))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    w = new StreamWriter(path, append: true) { AutoFlush = false };
                    _open[key] = w;
                }

                w.WriteLine(line);
                w.Flush();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Transient disk trouble (IOException etc.) or anything
                // unexpected: drop this tick and keep the loop alive. A
                // dead writer task would silently swallow every later tick
                // while Add keeps accepting them into a queue nobody drains.
                WriterError = ex;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        // CompleteAdding guarantees the loop terminates once the queue
        // drains — wait for that instead of a fixed 2s deadline so a slow
        // disk flushes rather than silently dropping (the writers below
        // close whatever made it).
        try { _writer.Wait(TimeSpan.FromSeconds(10)); } catch { /* flush deadline — files close below */ }
        foreach (var w in _open.Values)
        {
            try { w.Dispose(); } catch { }
        }
        _open.Clear();
        _queue.Dispose();
    }
}
