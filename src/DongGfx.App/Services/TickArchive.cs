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

    public TickArchive(string? root = null)
    {
        _root = root ?? Path.Combine(DongGfx.App.Infrastructure.SettingsService.DataDir, "ticks");
        Directory.CreateDirectory(_root);
        _writer = Task.Run(WriterLoop);
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
            catch (IOException)
            {
                // transient disk trouble — drop this tick, keep the loop alive
            }
            catch (ObjectDisposedException)
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
        try { _writer.Wait(2000); } catch { /* flush deadline — files close below */ }
        foreach (var w in _open.Values)
        {
            try { w.Dispose(); } catch { }
        }
        _open.Clear();
        _queue.Dispose();
    }
}
