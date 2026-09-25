using DongGfx.Core.Logging;

namespace DongGfx.Core.Tests;

/// <summary>
/// The logging services are resolved eagerly from the app's DI graph, so a
/// constructor that throws kills the app (the FxScorecardService red run on
/// main, 2026-09-25: a parallel directory deletion turned
/// Directory.CreateDirectory into DirectoryNotFoundException out of service
/// resolution). These tests pin the contract: an unusable journal/log
/// directory degrades — retry, then fallback directory, then memory-only —
/// and never throws, while a healthy directory behaves exactly as before.
/// </summary>
[Trait("Category", "Unit")]
public class LoggingHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tf_logging_hardening_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            // Clear read-only flags left by the unusable-path tests so the
            // recursive delete succeeds.
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>A directory path that cannot be created: the parent exists
    /// but is a read-only FILE (works on Windows and Linux, no drive-letter
    /// games).</summary>
    private string BlockedDir(string name)
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, $"{name}_blocker");
        File.WriteAllText(blocker, string.Empty);
        File.SetAttributes(blocker, FileAttributes.ReadOnly);
        return Path.Combine(blocker, "journal");
    }

    [Fact]
    public void Ctor_With_Writable_Directory_Is_Unchanged_And_Silent()
    {
        var dir = Path.Combine(_root, "journal");
        using var journal = new TradeJournal(dir);

        Assert.Null(journal.DirectoryError);

        var accountId = Guid.NewGuid();
        journal.LogBrainDecision(accountId, "Growth", "EURUSD", "Rise", 1m, 0.5, "probe");
        journal.Dispose();   // flush

        Assert.True(Directory.Exists(dir));
        Assert.Single(journal.GetRecent(accountId));
    }

    [Fact]
    public void Ctor_With_Unwritable_Path_Never_Throws_And_Falls_Back()
    {
        var blocked = BlockedDir("fallback");
        var fallback = Path.Combine(_root, "fallback_landing");

        using var journal = new TradeJournal(blocked, fallback);

        // The requested directory failed; the fallback landing (used
        // verbatim as the journal directory) was created.
        Assert.NotNull(journal.DirectoryError);
        Assert.False(Directory.Exists(blocked));
        Assert.True(Directory.Exists(fallback));

        // And the fallback journal actually works end to end.
        var accountId = Guid.NewGuid();
        journal.LogBrainDecision(accountId, "Growth", "EURUSD", "Rise", 1m, 0.5, "probe");
        journal.Dispose();

        Assert.Single(journal.GetRecent(accountId));
    }

    [Fact]
    public void Ctor_With_Everything_Unusable_Runs_Memory_Only()
    {
        var blocked = BlockedDir("memory");
        var blockedFallback = BlockedDir("memory_fb");

        using var journal = new TradeJournal(blocked, blockedFallback);

        // Nothing writable anywhere: no throw, no directories created, and
        // the instance stays usable as an in-memory sink.
        Assert.NotNull(journal.DirectoryError);
        Assert.False(Directory.Exists(blocked));
        Assert.False(Directory.Exists(blockedFallback));

        var accountId = Guid.NewGuid();
        journal.LogBrainDecision(accountId, "Growth", "EURUSD", "Rise", 1m, 0.5, "probe");
        journal.Flush();   // must drain-and-drop, not throw

        Assert.Empty(journal.GetRecent(accountId));
    }

    [Fact]
    public void GetRecent_Returns_Empty_When_The_Directory_Vanishes_Mid_Life()
    {
        var dir = Path.Combine(_root, "vanishing");
        var journal = new TradeJournal(dir);

        try
        {
            Directory.Delete(dir, recursive: true);

            // The read path degrades to empty instead of throwing — a wipe
            // must never take down a reader (journal viewer, supervisor).
            Assert.Empty(journal.GetRecent());
        }
        finally
        {
            journal.Dispose();
        }
    }

    [Fact]
    public void AppLogger_With_Unwritable_Directory_Never_Throws()
    {
        var blocked = BlockedDir("logger");

        using var logger = new AppLogger(blocked);

        Assert.NotNull(logger.DirectoryError);
        logger.Info("probe");          // queue
        logger.Debug("probe2");
        Assert.Empty(logger.GetRecent());   // memory-only: reads start empty
        logger.Dispose();              // flush drains without throwing
    }
}
