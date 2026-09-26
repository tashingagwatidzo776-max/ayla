using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

// xunit runs test classes in parallel collections (see AssemblyInfo.cs), and
// three suites touch the REAL %APPDATA%\tf\data directory that the app's DI
// graph resolves eagerly: AppStartupWiringTests builds the graph (creating
// journal/logs/ticks/analytics under DataDir), while LoginPrefillSettingsTests
// and CoverageSprintTests delete/clean it in their teardown. Run the whole
// suite and those two interleave: a teardown deletes DataDir between another
// class's existence check and Directory.CreateDirectory, and the ctor throws
// DirectoryNotFoundException out of service resolution (the FxScorecardService
// red run on main, 2026-09-25). One collection serializes them; everything
// else keeps running in parallel.
//
// LIVE SOAK GUARD: a soak session (FX brain in paper, app running) journals
// every few seconds into that same directory - and one full-suite run wiped
// a live session's journal mid-soak (2026-09-26). The collection fixture
// below refuses to initialize while the journal shows fresh writes, which
// aborts every test in this collection BEFORE any teardown can run. Override
// deliberately with TF_TESTS_ALLOW_LIVE_DATADIR=1.
[CollectionDefinition("Shared-DataDir-Directory", DisableParallelization = false)]
public sealed class SharedDataDirDirectoryCollection : ICollectionFixture<LiveSoakFixture>
{
}

/// <summary>Collection fixture for the DataDir-touching suites: throws at
/// initialization when a live soak session is journaling into the real
/// data dir, so the collection's tests never start (and never delete).
/// All detection failures degrade to "not live" — the guard must never
/// block a plain test run, only a genuinely active one.</summary>
public sealed class LiveSoakFixture
{
    /// <summary>Test seam: when set, replaces the real liveness probe so the
    /// fixture tests are deterministic regardless of a locally running app.</summary>
    internal static Func<string, bool>? ProbeOverride;

    public LiveSoakFixture()
    {
        if (Environment.GetEnvironmentVariable("TF_TESTS_ALLOW_LIVE_DATADIR") == "1")
        {
            return;   // operator override: test against the live dir deliberately
        }

        var dir = SettingsService.DataDir;
        var live = ProbeOverride is { } probe ? probe(dir) : LiveSoakGuard.IsLiveSoakRunning(dir);
        if (live)
        {
            throw new InvalidOperationException(
                "A live soak session appears to be writing to " + dir + " (a DongGfx.exe " +
                "instance is running, or the journal was updated within the last " +
                (int)LiveSoakGuard.FreshWindow.TotalMinutes + " minutes). These tests delete that " +
                "directory in their teardowns and would destroy the running session's " +
                "journal and settings. Close the app (or stop the FX brain) before running " +
                "this suite - or set TF_TESTS_ALLOW_LIVE_DATADIR=1 to override deliberately.");
        }
    }
}

/// <summary>Evidence-based liveness detection with three independent
/// signals, ANY of which reads as live: a running DongGfx.exe (the brain
/// has silent windows — a sub-35-candle warmup or a supervisor halt — where
/// a fully-running app journals nothing for minutes), a journal ENTRY
/// younger than the freshness window, or a journal FILE written within it
/// (test-built DI graphs touch the files without journaling).</summary>
public static class LiveSoakGuard
{
    /// <summary>Wider than the engine's 60 s cycle and its silent windows;
    /// far shorter than any real gap between sessions.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(10);

    private static readonly Regex TimestampRegex = new("\"Timestamp\":\"([^\"]+)\"",
        RegexOptions.Compiled);

    public static bool IsLiveSoakRunning(string dataDir) =>
        IsLiveSoakRunning(dataDir, DongGfxAppRunning());

    /// <summary>Explicit-process overload: tests pin the process signal so
    /// verdicts on synthetic journals are deterministic.</summary>
    public static bool IsLiveSoakRunning(string dataDir, bool appProcessRunning)
    {
        if (appProcessRunning)
        {
            return true;
        }

        try
        {
            var journalDir = Path.Combine(dataDir, "journal");
            if (!Directory.Exists(journalDir))
            {
                return false;
            }

            var files = Directory.GetFiles(journalDir, "journal_*.jsonl");
            if (files.Length == 0)
            {
                return false;
            }

            var newest = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            var now = DateTimeOffset.UtcNow;
            var mtime = File.GetLastWriteTimeUtc(newest);
            if (now - mtime < FreshWindow)
            {
                return true;
            }

            var entryAt = NewestEntryTimestamp(newest);
            return entryAt is { } at && now - at < FreshWindow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // unreadable dir: assume not live, never block tests
        }
    }

    /// <summary>Any running DongGfx.exe counts as live — same precedent as
    /// the UIA smoke lane skipping when a developer's own instance is up.</summary>
    internal static bool DongGfxAppRunning()
    {
        try
        {
            return Process.GetProcessesByName("DongGfx").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The last journal line's timestamp, or null when unreadable
    /// (a torn final write, say) — the caller then falls back to mtime.</summary>
    private static DateTimeOffset? NewestEntryTimestamp(string file)
    {
        try
        {
            var lastLine = File.ReadAllLines(file).LastOrDefault(l => l.Length > 0);
            if (lastLine is null)
            {
                return null;
            }

            var m = TimestampRegex.Match(lastLine);
            if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return at;
            }
        }
        catch (IOException)
        {
            // torn read mid-append: caller falls back to mtime
        }

        return null;
    }
}
