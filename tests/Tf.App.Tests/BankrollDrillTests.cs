using System.Diagnostics;
using System.IO;
using Tf.App.Infrastructure;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.Tests;

/// <summary>
/// Weekly drill for the bankroll auto-publish path, end to end, against a
/// synthetic trade store (the live %APPDATA% store is never touched):
///
///   synthetic TradeStore → BankrollCsvFile export (per-account rows) →
///   BankrollCsvPublisher git cycle (commit + push to "main") →
///   the committed Pages CSV, parsed exactly like the trend page reads it.
///
/// The unit tests cover the pieces in isolation (BankrollCsvExporterTests,
/// BankrollCsvPublisherTests); this drill proves the assembled path still
/// works when the pieces move independently — the weekly workflow re-runs it
/// so silent decay is triaged like any other drift.
/// </summary>
[Trait("Category", "Unit")]
public class BankrollDrillTests : IDisposable
{
    private readonly string _dir;
    private readonly string _repo;
    private readonly string _remote;
    private readonly string _docsCsv;
    private readonly string _docsPulse;
    private readonly string _appDataCsv;

    public BankrollDrillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bankroll-drill-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        _docsCsv = Path.Combine(_repo, "docs", "growth-bankroll.csv");
        _docsPulse = Path.Combine(_repo, "docs", "growth-pulse.json");
        _appDataCsv = Path.Combine(_dir, "appdata", "growth-bankroll.csv");
        _remote = Path.Combine(_dir, "remote.git");
        Git($"init --bare \"{_remote}\"");
        Git($"init \"{_repo}\"");
        Git($"-C \"{_repo}\" symbolic-ref HEAD refs/heads/main"); // portable across git versions
        Git($"-C \"{_repo}\" config user.email drill@t");
        Git($"-C \"{_repo}\" config user.name drill");
        Git($"-C \"{_repo}\" remote add origin \"{_remote}\"");
        File.WriteAllText(_docsCsv, "epoch_seconds,account,bankroll\n");
        Git($"-C \"{_repo}\" add .");
        Git($"-C \"{_repo}\" commit -m init");
        Git($"-C \"{_repo}\" push -u origin main");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Runs git and returns combined output; throws on failure so the
    /// drill fails loudly on harness breakage too.</summary>
    private static string Git(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    /// <summary>A settled growth trade for a named account, stamped noon UTC
    /// <paramref name="daysAgo"/> days back — noon keeps the daily grouping
    /// midnight-safe wherever in the day the drill runs.</summary>
    private static Trade GrowthTrade(string account, decimal profit, int daysAgo) => new(
        Guid.NewGuid(), "frxEURUSD",
        profit >= 0 ? Direction.Rise : Direction.Fall,
        1.00m, "USD", 1.17, 1700000300, $"DRILL-{Guid.NewGuid():N}",
        profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
        profit, 1.165, 1700000600,
        DateTimeOffset.UtcNow.Date.AddDays(-daysAgo).AddHours(12),
        Guid.NewGuid(), account, TradeSource.Growth);

    /// <summary>Verifies the CSV exactly the way generate_trend.py reads it:
    /// exact header, every row parses (int epoch, float bankroll), and the
    /// (account, bankroll) pairs match the expected set.</summary>
    private static void AssertTrendCsvShape(string csv, params (string Account, double Bankroll)[] expected)
    {
        var lines = csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("epoch_seconds,account,bankroll", lines[0]);

        var parsed = new HashSet<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            Assert.Equal(3, parts.Length); // the trend reader indexes exactly these columns
            Assert.True(long.TryParse(parts[0], out _), $"epoch '{parts[0]}' must parse as an integer");
            Assert.True(double.TryParse(parts[2], out var bankroll),
                $"bankroll '{parts[2]}' must parse as a float");
            Assert.NotEmpty(parts[1]);
            parsed.Add((parts[1], bankroll.ToString("g")));
        }

        Assert.Equal(
            expected.Select(e => (e.Account, e.Bankroll.ToString("g"))).ToHashSet(),
            parsed);
    }

    [Fact]
    public void Drill_SyntheticStore_FlowsThroughExportPublish_ToCommittedPagesCsv()
    {
        var store = new TradeStore(Path.Combine(_dir, "store"));
        using var journal = new TradeJournal(Path.Combine(_dir, "store", "journal"));

        // Seed day-1 settlements for two accounts, then construct the exporter:
        // it writes the initial export on construction.
        store.Add(GrowthTrade("Drill Alpha", +0.90m, daysAgo: 2));
        store.Add(GrowthTrade("Drill Alpha", +0.90m, daysAgo: 2));
        store.Add(GrowthTrade("Drill Beta", +0.90m, daysAgo: 2));

        using var exporter = new BankrollCsvFile(store, _docsCsv, _appDataCsv);

        // A later settlement must refresh the export WITHOUT a manual Refresh —
        // the TradeAdded hook is what keeps the money axis live in production.
        store.Add(GrowthTrade("Drill Alpha", -1.00m, daysAgo: 1));
        store.Add(GrowthTrade("Drill Beta", +1.44m, daysAgo: 1));

        // Both copies carry the per-account daily closing rows (Alpha compounds
        // 1.80 → 0.80; Beta 0.90 → 2.34) — the trend page's drill-down inputs.
        AssertTrendCsvShape(File.ReadAllText(_docsCsv),
            ("Drill Alpha", 1.80), ("Drill Alpha", 0.80),
            ("Drill Beta", 0.90), ("Drill Beta", 2.34));
        Assert.Equal(File.ReadAllText(_docsCsv), File.ReadAllText(_appDataCsv));

        // The pulse rides the same refresh: the store's settled picture,
        // stamped with the day-1 noon-UTC settlement. Its content only changes
        // when a NEW settled trade lands — that is what keeps the publish
        // cycle at one commit per settlement instead of churning on ticks.
        var lastSettled = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(-1).AddHours(12))
            .ToUnixTimeSeconds();
        Assert.Equal(
            $"{{\"last_settled_epoch\":{lastSettled},\"settled_trades\":5,\"accounts\":[\"Drill Alpha\",\"Drill Beta\"]}}\n",
            File.ReadAllText(_docsPulse));

        // The real publisher cycle: dirty CSV on main → commit → push. The
        // remote stands in for GitHub; whatever it holds after the push is
        // exactly what the next Pages checkout would see.
        var publisher = new BankrollCsvPublisher(_docsCsv);
        var failures = new List<string>();
        publisher.PublishFailed += f => failures.Add(f);
        publisher.PushOnce();

        Assert.Empty(failures); // a healthy publish stays silent
        Assert.Equal("", Git($"-C \"{_repo}\" status --porcelain").Trim()); // nothing left behind

        var committedCsv = Git($"-C \"{_remote}\" show main:docs/growth-bankroll.csv");
        AssertTrendCsvShape(committedCsv,
            ("Drill Alpha", 1.80), ("Drill Alpha", 0.80),
            ("Drill Beta", 0.90), ("Drill Beta", 2.34));
        Assert.Equal(File.ReadAllText(_docsPulse),
            Git($"-C \"{_remote}\" show main:docs/growth-pulse.json"));

        // The auto-publish commit touched only the export pair (never sweeps
        // unrelated work) and is marked as the app's auto-publish.
        var changed = Git($"-C \"{_remote}\" show --name-only --format= main")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "docs/growth-bankroll.csv", "docs/growth-pulse.json" }, changed);
        var msg = Git($"-C \"{_remote}\" log -1 --format=%s main");
        Assert.Contains("Auto-refresh growth-bankroll export", msg);

        // A follow-up tick on the now-clean checkout stays silent: no second
        // commit, no failure reports.
        var remoteHead = Git($"-C \"{_remote}\" rev-parse main").Trim();
        publisher.PushOnce();
        Assert.Equal(remoteHead, Git($"-C \"{_remote}\" rev-parse main").Trim());
        Assert.Empty(failures);
    }

    [Fact]
    public void Drill_BrokenPublish_LeavesTheCommittedPagesCsvStale_AndRaisesTheFailure()
    {
        var store = new TradeStore(Path.Combine(_dir, "store"));
        using var journal = new TradeJournal(Path.Combine(_dir, "store", "journal"));

        store.Add(GrowthTrade("Drill Alpha", +0.90m, daysAgo: 2));
        using var exporter = new BankrollCsvFile(store, _docsCsv, _appDataCsv);

        // Publish once so the remote holds a known-good (now stale) export
        // pair — CSV and pulse in lockstep.
        new BankrollCsvPublisher(_docsCsv).PushOnce();
        var staleCsv = Git($"-C \"{_remote}\" show main:docs/growth-bankroll.csv");
        var stalePulse = Git($"-C \"{_remote}\" show main:docs/growth-pulse.json");

        // New settlements refresh the local export, then the push breaks
        // (origin repointed at a missing remote): the failure must surface —
        // once, with a push-shaped reason — and the committed CSV the Pages
        // deploy would use stays frozen at the stale content. This is the
        // exact failure mode the risk rail now latches on.
        store.Add(GrowthTrade("Drill Alpha", +0.90m, daysAgo: 1));
        // Local export advanced past the stale committed copy: Alpha compounds
        // 0.9 → 1.8 while the remote still holds only the 0.9 day.
        AssertTrendCsvShape(File.ReadAllText(_docsCsv),
            ("Drill Alpha", 0.9), ("Drill Alpha", 1.8));

        var publisher = new BankrollCsvPublisher(_docsCsv);
        var failures = new List<string>();
        publisher.PublishFailed += f => failures.Add(f);
        Git($"-C \"{_repo}\" remote set-url origin \"{Path.Combine(_dir, "missing.git")}\"");
        publisher.PushOnce();

        var failure = Assert.Single(failures);
        Assert.Contains("push", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(staleCsv, Git($"-C \"{_remote}\" show main:docs/growth-bankroll.csv"));

        // And the frozen-axis signature is exactly what the watchdog alerts
        // on: the machine-side pulse advanced past the committed pair (a new
        // settled trade landed), while the Pages-side CSV and pulse stay
        // frozen at the stale content because the push broke.
        Assert.NotEqual(stalePulse, File.ReadAllText(_docsPulse));
        Assert.Equal(stalePulse, Git($"-C \"{_remote}\" show main:docs/growth-pulse.json"));
    }
}
