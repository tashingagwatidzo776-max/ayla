using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The chronic flush crash (APP_FAULT 2026-10-06 ×3, 10-07 ×3, 10-08):
/// the 5-second flush timer opened the journal with a share mode that
/// FAILED against any reader holding the file (File.ReadLines opens with
/// FileShare.Read — denying writes), and only DirectoryNotFoundException
/// was caught — the escaping IOException killed the flush thread.
/// Two laws, pinned here:
/// 1. Flush NEVER throws and NEVER drops — a share-denying reader costs a
///    bounded retry, then the batch goes back on the queue and lands once
///    the lock clears (no loss, no duplication).
/// 2. ReadLinesShared opens with FileShare.ReadWrite — a held shared read
///    tolerates a concurrent append, where the legacy File.ReadLines
///    reader demonstrably blocks the writer (the bug's exact shape).
/// </summary>
[Trait("Category", "Unit")]
public class TradeJournalSharingTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-journal-sharing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string JournalFile(string dir) =>
        Directory.GetFiles(dir, "journal_*.jsonl").Single();

    [Fact]
    public void Flush_Survives_A_Share_Denying_Reader_Without_Losing_Or_Duplicating_Rows()
    {
        var dir = TempDir();
        try
        {
            using var journal = new TradeJournal(dir);
            journal.Log(Guid.Empty, "FX_ORDER", "row-a");
            journal.Flush();
            var file = JournalFile(dir);

            // The legacy/external reader shape: FileShare.Read DENIES
            // writes — exactly what made the old appender's open throw.
            using (var blocker = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                journal.Log(Guid.Empty, "FX_ORDER", "row-b");

                // The law: never throw out of Flush (the timer callback).
                var ex = Record.Exception(() => journal.Flush());
                Assert.Null(ex);
            }

            // Lock cleared: the requeued batch lands — nothing lost,
            // nothing written twice.
            journal.Flush();
            var text = File.ReadAllText(file);
            Assert.Contains("row-b", text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(text, "row-a"));
            Assert.Equal(1, CountOccurrences(text, "row-b"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Flush_Requeues_The_Batch_While_Locked_So_A_Later_Flush_Lands_It()
    {
        var dir = TempDir();
        try
        {
            using var journal = new TradeJournal(dir);
            journal.Log(Guid.Empty, "FX_EXIT", "seed");
            journal.Flush();
            var file = JournalFile(dir);

            using (var blocker = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                journal.Log(Guid.Empty, "FX_EXIT", "deferred-row");
                journal.Flush();   // retries, then requeues — must not throw
            }

            // The requeued batch lands on the next flush: durable, ordered
            // relative to itself, exactly once.
            journal.Flush();
            var text = File.ReadAllText(file);
            Assert.Equal(1, CountOccurrences(text, "deferred-row"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadLinesShared_Tolerates_A_Concurrent_Writer_Where_Legacy_Reader_Blocks_It()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "journal_20261008.jsonl");
            File.WriteAllLines(file, new[] { "line-1", "line-2" });

            // The bug, demonstrated: while a legacy File.ReadLines reader
            // holds the file, the appender's open throws a sharing
            // violation (IOException).
            using (var legacy = File.ReadLines(file).GetEnumerator())
            {
                Assert.True(legacy.MoveNext());
                Assert.ThrowsAny<IOException>(() =>
                {
                    using var blocked = new StreamWriter(file, append: true);
                    blocked.WriteLine("never-lands");
                });
            }

            // The fix: ReadLinesShared holds the file with FileShare.ReadWrite,
            // so a concurrent append succeeds while the read is in flight.
            using (var shared = TradeJournal.ReadLinesShared(file).GetEnumerator())
            {
                Assert.True(shared.MoveNext());
                using var writer = new StreamWriter(file, append: true);
                writer.WriteLine("lands-concurrently");
            }

            var lines = TradeJournal.ReadLinesShared(file).ToList();
            Assert.Equal(new[] { "line-1", "line-2", "lands-concurrently" }, lines);
            Assert.DoesNotContain(lines, l => l.Contains("never-lands"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
