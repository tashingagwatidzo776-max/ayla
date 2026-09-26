using System.Collections.Concurrent;
using System.IO;

namespace DongGfx.App.Services;

/// <summary>
/// Append-only tick archive: every MT5 bridge quote lands as one JSON line
/// under %APPDATA%\tf\data\ticks\{venue}\{symbol}_{date}.jsonl.
/// This is the raw material for the tick/microstructure alpha family and for
/// post-session analysis. Writes happen on ONE background thread consuming a
/// bounded queue — the UI thread never touches the filesystem, and a slow
/// disk can only drop the newest quotes (TryAdd with no wait), never stall
/// the terminal. Tests can construct the archive with a synchronous writer
/// instead: Add then writes inline, so assertions are race-free by design.
/// </summary>
public sealed class TickArchive : IDisposable
{
    private readonly string _root;
    private readonly bool _synchronous;
    private readonly BlockingCollection<(string Venue, string Symbol, string Line)> _queue = new(8192);
    private readonly Task? _writer;
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

    /// <param name="root">Archive root; defaults to the app data dir.</param>
    /// <param name="synchronousWriter">Test mode: write inline on Add with
    /// no background thread — assertions can never race the writer. The
    /// drain-on-Dispose and error-recording semantics are identical.</param>
    public TickArchive(string? root = null, bool synchronousWriter = false)
    {
        _synchronous = synchronousWriter;
        _root = root ?? Path.Combine(DongGfx.App.Infrastructure.SettingsService.DataDir, "ticks");
        // Same rule as TradeJournal: resolved eagerly from the DI graph at
        // startup, so the constructor must not throw — degrade to a sink
        // that drops ticks instead.
        _archiveUsable = TryPrepareDirectory(_root, out var error);
        DirectoryError = error;
        if (_synchronous)
        {
            return;   // no writer thread: Add writes inline
        }

        // A dedicated thread (LongRunning), not the bare pool: under CI's
        // parallel load + coverage collector the pool can starve, and a
        // writer task that never gets scheduled is indistinguishable from
        // a silent drop (observed: WriterError null, no file after 30 s).
        _writer = Task.Factory.StartNew(
            WriterLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
        if (_synchronous)
        {
            WriteOne((venue, symbol, line));   // inline — deterministic for tests
            return;
        }

        _queue.TryAdd((venue, symbol, line), 0);
    }

    /// <summary>Write one tick, opening its file on first touch. Returns
    /// false only when the writer must stop (archive disposed mid-write).
    /// Every other failure is recorded in WriterError and the loop lives on.</summary>
    private bool WriteOne((string Venue, string Symbol, string Line) tick)
    {
        if (!_archiveUsable)
        {
            return true;   // memory-only: the ticks drop, the instance lives
        }

        try
        {
            var path = Path.Combine(_root, tick.Venue, $"{tick.Symbol}_{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var key = path.ToLowerInvariant();
            if (!_open.TryGetValue(key, out var w))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                w = new StreamWriter(path, append: true) { AutoFlush = false };
                _open[key] = w;
            }

            w.WriteLine(tick.Line);
            w.Flush();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Transient disk trouble (IOException etc.) or anything
            // unexpected: drop this tick and keep the loop alive. A
            // dead writer task would silently swallow every later tick
            // while Add keeps accepting them into a queue nobody drains.
            WriterError = ex;
        }

        return true;
    }

    private void WriterLoop()
    {
        foreach (var tick in _queue.GetConsumingEnumerable())
        {
            if (!WriteOne(tick))
            {
                return;
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
        if (_writer is { } writer)
        {
            // CompleteAdding guarantees the loop terminates once the queue
            // drains — wait for that instead of a fixed deadline so a slow
            // disk flushes rather than silently dropping (the writers below
            // close whatever made it).
            try { writer.Wait(TimeSpan.FromSeconds(10)); } catch { /* flush deadline — files close below */ }
        }   // synchronous mode wrote everything inline; nothing to drain
        foreach (var w in _open.Values)
        {
            try { w.Dispose(); } catch { }
        }
        _open.Clear();
        _queue.Dispose();
    }
}
