using System.IO;
using DongGfx.App.Services;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The tick archive: every accepted quote lands as an invariant-culture JSON
/// line under {venue}/{symbol}_{date}.jsonl, malformed ticks are silently
/// dropped, and Dispose flushes the writer queue before closing the files.
/// </summary>
[Trait("Category", "Unit")]
public class TickArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "donggfx-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static int LineCount(string path)
    {
        try
        {
            // The writer holds the file open for writing: read with
            // FileShare.ReadWrite or Windows refuses the open outright.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var n = 0;
            while (sr.ReadLine() is not null) n++;
            return n;
        }
        catch (IOException) { return 0; }   // not flushed yet / transient lock
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static void WaitForFile(TickArchive archive, string path, int lines)
    {
        // The writer flushes per tick, but under full-suite parallel load the
        // background task can lag; poll before disposing (Dispose drains the
        // queue before closing the files).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && LineCount(path) >= lines) return;
            Thread.Sleep(25);
        }

        // A timeout used to fail with a bare "file missing", which hid the
        // real cause (the writer loop dying on an uncaught exception). Fail
        // with what the writer actually recorded.
        Assert.True(
            File.Exists(path) && LineCount(path) >= lines,
            $"tick file never reached {lines} line(s) at {path}" +
            (archive.WriterError is null ? "" : $" — writer error: {archive.WriterError}"));
    }

    [Fact]
    public void Add_Writes_Invariant_Json_Lines_Per_Venue_And_Symbol()
    {
        using var archive = new TickArchive(_root);
        archive.Add("deriv", "EURUSD", 1.10234, 1.10256, 1790000000123);
        archive.Add("deriv", "EURUSD", 1.10240, 1.10261, 1790000000789);
        archive.Add("mt5-demo", "XAUUSDmicro", 2650.5, 2651.0, 1790000000400);

        var eur = Path.Combine(_root, "deriv", $"EURUSD_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var xau = Path.Combine(_root, "mt5-demo", $"XAUUSDmicro_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        WaitForFile(archive, eur, 2);
        WaitForFile(archive, xau, 1);
        archive.Dispose();   // close writers

        Assert.True(File.Exists(eur));
        var lines = File.ReadAllLines(eur);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"b\":1.10234", lines[0]);
        Assert.Contains("\"a\":1.10256", lines[0]);
        Assert.Contains("\"t\":1790000000123", lines[0]);

        Assert.True(File.Exists(xau));
        Assert.Contains("\"b\":2650.5", File.ReadAllLines(xau)[0]);
    }

    [Fact]
    public void Invalid_Ticks_Are_Dropped_Silently()
    {
        using var archive = new TickArchive(_root);
        archive.Add("deriv", "EURUSD", 0, 1.1, 1);        // bid <= 0
        archive.Add("deriv", "EURUSD", -1, 1.1, 1);       // negative bid
        archive.Add("deriv", "", 1.0, 1.1, 1);            // empty symbol
        archive.Dispose();

        Assert.False(Directory.Exists(Path.Combine(_root, "deriv")));
    }

    [Fact]
    public void Add_After_Dispose_Is_A_Safe_NoOp()
    {
        var archive = new TickArchive(_root);
        archive.Dispose();
        archive.Dispose();   // idempotent

        var ex = Record.Exception(() => archive.Add("deriv", "EURUSD", 1.0, 1.1, 1));
        Assert.Null(ex);
    }

    [Fact]
    public void Ctor_With_Unwritable_Root_Never_Throws_And_Add_Is_A_Safe_NoOp()
    {
        // Same contract as TradeJournal/AppLogger: the archive is resolved
        // eagerly from the DI graph, so an unusable root must degrade to a
        // drop-everything sink instead of throwing out of service resolution.
        var blocker = Path.Combine(_root, "archive_blocker");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocker, string.Empty);
        File.SetAttributes(blocker, FileAttributes.ReadOnly);
        var blocked = Path.Combine(blocker, "ticks");

        using var archive = new TickArchive(blocked);

        Assert.NotNull(archive.DirectoryError);
        Assert.False(Directory.Exists(blocked));

        archive.Add("deriv", "EURUSD", 1.0, 1.1, 1);   // must not throw
        archive.Dispose();                              // drains without throwing
        Assert.Null(archive.WriterError);               // the writer loop stays healthy
    }
}
