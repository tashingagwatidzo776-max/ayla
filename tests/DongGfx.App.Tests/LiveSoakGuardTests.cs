using System;
using System.IO;
using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The live-soak guard's verdicts: a running app process, a fresh journal
/// entry, or a fresh journal file each reads as LIVE; stale history and
/// empty/absent directories read as NOT live; every detection failure
/// degrades to "not live" — the guard may never block a plain test run,
/// only a genuinely active soak. The collection fixture is the consumer
/// that aborts the Shared-DataDir suites before their teardowns can wipe a
/// running session.
/// </summary>
[Trait("Category", "Unit")]
public class LiveSoakGuardTests : IDisposable
{
    private readonly string _dir;

    public LiveSoakGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_guard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteJournal(DateTimeOffset entryAt)
    {
        var file = Path.Combine(_dir, "journal", $"journal_{entryAt:yyyyMMdd}.jsonl");
        File.WriteAllText(file,
            "{\"Timestamp\":\"" + entryAt.ToString("o") + "\",\"AccountId\":\"00000000-0000-0000-0000-000000000000\"," +
            "\"Category\":\"FX_REGIME\",\"Details\":\"XAUUSDmicro M1: Trend\"}\n");
        return file;
    }

    [Fact]
    public void Running_App_Process_Reads_As_Live_Regardless_Of_Journal()
    {
        WriteJournal(DateTimeOffset.UtcNow - TimeSpan.FromHours(3));   // stale history

        Assert.True(LiveSoakGuard.IsLiveSoakRunning(_dir, appProcessRunning: true));
    }

    [Fact]
    public void Fresh_Journal_Entry_Reads_As_Live()
    {
        var file = WriteJournal(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20));
        // Backdate the mtime so ONLY the entry timestamp can say "live".
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromSeconds(20));

        Assert.True(LiveSoakGuard.IsLiveSoakRunning(_dir, appProcessRunning: false));
    }

    [Fact]
    public void Stale_Journal_Reads_As_Not_Live()
    {
        var file = WriteJournal(DateTimeOffset.UtcNow - TimeSpan.FromHours(3));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromHours(3));   // old content AND old mtime

        Assert.False(LiveSoakGuard.IsLiveSoakRunning(_dir, appProcessRunning: false));
    }

    [Fact]
    public void Fresh_Mtime_With_Old_Entry_Still_Counts_As_Live()
    {
        // A DI-graph test creates files under DataDir without journaling:
        // for those the newest ENTRY timestamp says "old" but the file was
        // written moments ago — treat that as live (fail toward refusing).
        var file = WriteJournal(DateTimeOffset.UtcNow - TimeSpan.FromDays(2));
        File.WriteAllText(file, File.ReadAllText(file));   // fresh mtime, old content

        Assert.True(LiveSoakGuard.IsLiveSoakRunning(_dir, appProcessRunning: false));
    }

    [Fact]
    public void Absent_Or_Empty_Journal_Directory_Is_Not_Live()
    {
        Assert.False(LiveSoakGuard.IsLiveSoakRunning(_dir, appProcessRunning: false));   // exists, no files
        Assert.False(LiveSoakGuard.IsLiveSoakRunning(
            Path.Combine(_dir, "does", "not", "exist"), appProcessRunning: false));
    }

    [Fact]
    public void Silent_Window_Below_FreshWindow_Still_Reads_As_Live()
    {
        // The brain has silent windows (halt, warmup) — a last entry 5
        // minutes old (content and mtime, so ONLY the entry timestamp can
        // speak) with no process signal must still read as live
        // (FreshWindow=10 min covers the longest silent gap).
        var file = WriteJournal(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        Assert.True(LiveSoakGuard.IsLiveSoakRunning(_dir, appProcessRunning: false));
    }

    // ── fixture refusal + escape hatch (exit-code contract) ──────────

    [Fact]
    public void Fixture_Refuses_With_Explanatory_Message_When_Live()
    {
        LiveSoakFixture.ProbeOverride = _ => true;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new LiveSoakFixture());
            Assert.Contains("live soak", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TF_TESTS_ALLOW_LIVE_DATADIR", ex.Message);
        }
        finally
        {
            LiveSoakFixture.ProbeOverride = null;
        }
    }

    [Fact]
    public void Fixture_Override_Env_Var_Lets_Tests_Run_Against_A_Live_Dir()
    {
        LiveSoakFixture.ProbeOverride = _ => true;
        Environment.SetEnvironmentVariable("TF_TESTS_ALLOW_LIVE_DATADIR", "1");
        try
        {
            new LiveSoakFixture();   // must not throw
        }
        finally
        {
            Environment.SetEnvironmentVariable("TF_TESTS_ALLOW_LIVE_DATADIR", null);
            LiveSoakFixture.ProbeOverride = null;
        }
    }

    [Fact]
    public void Fixture_Passes_When_Nothing_Is_Live()
    {
        LiveSoakFixture.ProbeOverride = _ => false;
        try
        {
            new LiveSoakFixture();   // must not throw
        }
        finally
        {
            LiveSoakFixture.ProbeOverride = null;
        }
    }
}
