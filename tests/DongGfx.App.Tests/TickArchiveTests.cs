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

    [Fact]
    public void Add_Writes_Invariant_Json_Lines_Per_Venue_And_Symbol()
    {
        using var archive = new TickArchive(_root);
        archive.Add("deriv", "EURUSD", 1.10234, 1.10256, 1790000000123);
        archive.Add("deriv", "EURUSD", 1.10240, 1.10261, 1790000000789);
        archive.Add("mt5-demo", "XAUUSDmicro", 2650.5, 2651.0, 1790000000400);

        archive.Dispose();   // flush deadline

        var eur = Path.Combine(_root, "deriv", $"EURUSD_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        Assert.True(File.Exists(eur));
        var lines = File.ReadAllLines(eur);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"b\":1.10234", lines[0]);
        Assert.Contains("\"a\":1.10256", lines[0]);
        Assert.Contains("\"t\":1790000000123", lines[0]);

        var xau = Path.Combine(_root, "mt5-demo", $"XAUUSDmicro_{DateTime.UtcNow:yyyyMMdd}.jsonl");
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
}
