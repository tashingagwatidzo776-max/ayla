using System.IO;
using DongGfx.App.Services;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The tick archive: every accepted quote lands as an invariant-culture JSON
/// line under {venue}/{symbol}_{date}.jsonl, malformed ticks are silently
/// dropped, and Dispose closes the writers. The tests construct the archive
/// with the synchronous writer (synchronousWriter: true), so Add writes
/// inline — assertions are race-free by construction, no polling, no
/// background-thread scheduling under any load. The async production mode
/// shares the same WriteOne body (exercised via the dispose/no-op tests).
/// </summary>
[Trait("Category", "Unit")]
public class TickArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "donggfx-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string[] ReadAllLinesShared(string path)
    {
        // Mid-life reads: the archive holds the file open for writing until
        // Dispose — read with FileShare.ReadWrite or Windows refuses the
        // open outright.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    [Fact]
    public void Add_Writes_Invariant_Json_Lines_Per_Venue_And_Symbol()
    {
        using (var archive = new TickArchive(_root, synchronousWriter: true))
        {
            archive.Add("deriv", "EURUSD", 1.10234, 1.10256, 1790000000123);
            archive.Add("deriv", "EURUSD", 1.10240, 1.10261, 1790000000789);
            archive.Add("mt5-demo", "XAUUSDmicro", 2650.5, 2651.0, 1790000000400);
        }   // Dispose: close writers — no drain wait, everything was inline

        var eur = Path.Combine(_root, "deriv", $"EURUSD_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var xau = Path.Combine(_root, "mt5-demo", $"XAUUSDmicro_{DateTime.UtcNow:yyyyMMdd}.jsonl");

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
    public void Synchronous_Writer_Is_Visible_Immediately_Add_And_After()
    {
        // Pins the determinism contract: the file exists with all its lines
        // the moment Add returns — no Dispose, no wait, no background thread.
        using var archive = new TickArchive(_root, synchronousWriter: true);
        var eur = Path.Combine(_root, "deriv", $"EURUSD_{DateTime.UtcNow:yyyyMMdd}.jsonl");

        archive.Add("deriv", "EURUSD", 1.1, 1.2, 42);

        Assert.True(File.Exists(eur));
        Assert.Equal(1, ReadAllLinesShared(eur).Length);

        archive.Add("deriv", "EURUSD", 1.3, 1.4, 43);
        Assert.Equal(2, ReadAllLinesShared(eur).Length);
        Assert.Null(archive.WriterError);
    }

    [Fact]
    public void Invalid_Ticks_Are_Dropped_Silently()
    {
        using (var archive = new TickArchive(_root, synchronousWriter: true))
        {
            archive.Add("deriv", "EURUSD", 0, 1.1, 1);        // bid <= 0
            archive.Add("deriv", "EURUSD", -1, 1.1, 1);       // negative bid
            archive.Add("deriv", "", 1.0, 1.1, 1);            // empty symbol
        }

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

        using var archive = new TickArchive(blocked, synchronousWriter: true);

        Assert.NotNull(archive.DirectoryError);
        Assert.False(Directory.Exists(blocked));

        archive.Add("deriv", "EURUSD", 1.0, 1.1, 1);   // must not throw
        archive.Dispose();                              // drains without throwing
        Assert.Null(archive.WriterError);               // the writer stays healthy
    }
}
