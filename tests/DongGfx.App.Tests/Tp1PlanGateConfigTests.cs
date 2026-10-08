using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The shared TP1 plan-% gate tunables (config/tp1-plan-gate.json) and the
/// app-side plan-review ledger. The config file must carry exactly the keys
/// the loader reads and the same values the watcher's constants hold — that
/// pair of assertions (this side and test_watch_tp1_first_arm.py's) is what
/// proves both languages read one source of truth. The file must also ship
/// with the build, hot-reload on edit, validate every tunable, and be
/// writable from the app.
/// </summary>
[Trait("Category", "Unit")]
[Collection("Shared-DataDir-Directory")]
public class Tp1PlanGateConfigTests
{
    private static string? RepoRoot()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            if (File.Exists(Path.Combine(probe, "DongGfx.sln")))
            {
                return probe;
            }

            probe = Path.GetDirectoryName(probe);
        }

        return null;
    }

    private static string ScratchDir(string tag) =>
        Path.Combine(Path.GetTempPath(), tag, Guid.NewGuid().ToString("N"));

    private static string RecLine(string id, string dir, DateTimeOffset ts,
        double baseline, double candidate, double net, int graded, int trailed)
    {
        string N(double v) =>
            v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "{\"event\":\"recommended\",\"id\":\"" + id + "\",\"ts\":\""
            + ts.ToString("o") + "\",\"direction\":\"" + dir
            + "\",\"baseline_pct\":" + N(baseline)
            + ",\"candidate_pct\":" + N(candidate)
            + ",\"net_r\":" + N(net)
            + ",\"mean_vs_giveback_r\":" + N(net / graded)
            + ",\"graded\":" + graded
            + ",\"trailed\":" + trailed + "}\n";
    }

    /// <summary>Rewrites a scratch file with a fresh timestamp so the
    /// hot-reload stamp check reliably sees the edit.</summary>
    private static void WriteStamp(string path, string json)
    {
        File.WriteAllText(path, json);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1 + Random.Shared.Next(60)));
    }

    // ── the shared file, key set and values ──────────────────────────

    [Fact]
    public void Shared_Config_Carries_Exactly_The_Keys_The_Loader_Reads()
    {
        var root = RepoRoot();
        if (root is null)
        {
            return;   // published artifacts — nothing to check
        }

        var path = Path.Combine(root, "config", Tp1PlanGateConfig.FileName);
        Assert.True(File.Exists(path), path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var keys = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !n.StartsWith('_'))   // _comment is documentation
            .ToHashSet();
        Assert.Equal(Tp1PlanGateConfig.Keys.ToHashSet(), keys);
    }

    [Fact]
    public void Loaded_Values_Match_The_Shared_File_And_Its_Accessors()
    {
        var root = RepoRoot();
        if (root is null)
        {
            return;
        }

        var path = Path.Combine(root, "config", Tp1PlanGateConfig.FileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Tp1PlanGateConfig.Reload(path);
        try
        {
            foreach (var key in Tp1PlanGateConfig.Keys)
            {
                Assert.True(Tp1PlanGateConfig.Loaded.TryGetValue(key, out var got), key);
                Assert.Equal(doc.RootElement.GetProperty(key).GetDouble(), got);
            }

            Assert.Equal(doc.RootElement.GetProperty("stale_days").GetInt32(),
                Tp1PlanGateConfig.StaleDays);
            Assert.Equal(doc.RootElement.GetProperty("step_pct").GetDouble(),
                Tp1PlanGateConfig.StepPct);
            Assert.Equal(doc.RootElement.GetProperty("step_max_drift").GetDouble(),
                Tp1PlanGateConfig.StepMaxDrift);
            Assert.Equal(doc.RootElement.GetProperty("min_graded").GetDouble(),
                Tp1PlanGateConfig.MinGraded);
            // The breaker reads the shared threshold, not a private constant.
            Assert.Equal(Tp1PlanGateConfig.StaleDays, Tp1PlanBreaker.StaleDays);
        }
        finally
        {
            Tp1PlanGateConfig.Reload();   // restore the default-path load
        }
    }

    [Fact]
    public void Missing_File_Leaves_The_Compiled_Defaults_Standing()
    {
        Tp1PlanGateConfig.Reload(Path.Combine(Path.GetTempPath(),
            "nope-" + Guid.NewGuid().ToString("N"), "gate.json"));
        try
        {
            Assert.Empty(Tp1PlanGateConfig.Loaded);
            Assert.Equal(7, Tp1PlanGateConfig.StaleDays);
            Assert.Equal(5.0, Tp1PlanGateConfig.StepPct);
            Assert.Equal(3.0, Tp1PlanGateConfig.StepMaxDrift);
            Assert.Equal(3, Tp1PlanGateConfig.MinGraded);
            Assert.Equal(7, Tp1PlanBreaker.StaleDays);
            Assert.Empty(Tp1PlanGateConfig.Validate());   // defaults are sane
        }
        finally
        {
            Tp1PlanGateConfig.Reload();
        }
    }

    // ── shipping with the build ──────────────────────────────────────

    [Fact]
    public void The_App_Project_Ships_The_Shared_Config()
    {
        var root = RepoRoot();
        if (root is null)
        {
            return;
        }

        var csproj = File.ReadAllText(Path.Combine(
            root, "src", "DongGfx.App", "DongGfx.App.csproj"));
        Assert.Contains("tp1-plan-gate.json", csproj);
        Assert.Contains("CopyToOutputDirectory", csproj);
        Assert.Contains("CopyToPublishDirectory", csproj);

        // And the build put it beside the binaries (the published-build
        // fallback when there is no checkout above the exe).
        var shipped = Path.Combine(AppContext.BaseDirectory, "config",
            Tp1PlanGateConfig.FileName);
        Assert.True(File.Exists(shipped), shipped);
    }

    // ── hot reload ───────────────────────────────────────────────────

    [Fact]
    public void Hot_Reload_Picks_Up_A_Config_Edit_Without_A_Restart()
    {
        var path = Path.Combine(ScratchDir("dg-tp1-hot"), "gate.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteStamp(path, "{\"stale_days\": 7, \"step_pct\": 5}");
        Tp1PlanGateConfig.Reload(path);
        try
        {
            Assert.Equal(7, Tp1PlanGateConfig.StaleDays);

            WriteStamp(path, "{\"stale_days\": 2, \"step_pct\": 7.5}");
            Tp1PlanGateConfig.Refresh();   // the hot-reload path, no restart

            Assert.Equal(2, Tp1PlanGateConfig.StaleDays);
            Assert.Equal(7.5, Tp1PlanGateConfig.StepPct);
            Assert.Equal(2, Tp1PlanBreaker.StaleDays);   // breaker follows
        }
        finally
        {
            Tp1PlanGateConfig.Reload();
            Tp1PlanBreaker.Invalidate();
        }
    }

    [Fact]
    public void Retuning_The_Stale_Threshold_Releases_The_Cached_Breaker()
    {
        var dataDir = ScratchDir("dg-tp1-cache");
        var watcher = Path.Combine(dataDir, "watcher");
        Directory.CreateDirectory(watcher);
        var ledger = Path.Combine(watcher, "tp1-plan-reviews.jsonl");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(ledger,
            "{\"event\":\"recommended\",\"id\":\"r1\",\"ts\":\""
            + now.AddDays(-10).ToString("o")
            + "\",\"direction\":\"down\"}\n");

        var cfg = Path.Combine(dataDir, "gate.json");
        WriteStamp(cfg, "{\"stale_days\": 7}");
        Tp1PlanGateConfig.Reload(cfg);
        try
        {
            Assert.True(Tp1PlanBreaker.IsHeldCached(dataDir, now));   // cached

            // Retune past the review's age: the cache is keyed by the config's
            // stamp, so the hold releases without an Invalidate call.
            WriteStamp(cfg, "{\"stale_days\": 30}");
            Tp1PlanGateConfig.Refresh();
            Assert.False(Tp1PlanBreaker.IsHeldCached(dataDir, now));
        }
        finally
        {
            Tp1PlanGateConfig.Reload();
            Tp1PlanBreaker.Invalidate();
        }
    }

    // ── validation consumes every tunable ────────────────────────────

    [Fact]
    public void Every_Tunable_Is_Consumed_By_Validation()
    {
        var baseline = new Dictionary<string, double>(Tp1PlanGateConfig.Loaded);
        Assert.Empty(Tp1PlanGateConfig.Validate(baseline));

        // Each key, broken in its own way, must be flagged BY NAME — so no
        // tunable is silently ignored by the app-side check.
        var breakages = new (string Key, double Bad)[]
        {
            ("min_graded", 0), ("net_r", 5), ("up_net_r", -5),
            ("step_r", 0), ("step_pct", -1), ("min_pct", -1), ("max_pct", 200),
            ("score_min_graded", 0), ("step_feedback_min", 0),
            ("step_feedback_pct", -1), ("step_min", 0), ("step_max", 0),
            ("step_max_drift", -1), ("stale_days", 0),
        };
        Assert.Equal(Tp1PlanGateConfig.Keys.OrderBy(k => k),
            breakages.Select(b => b.Key).OrderBy(k => k));

        foreach (var (key, bad) in breakages)
        {
            var broken = new Dictionary<string, double>(baseline) { [key] = bad };
            var problems = Tp1PlanGateConfig.Validate(broken);
            Assert.Contains(problems, p => p.Contains(key));
        }
    }

    // ── the writer ───────────────────────────────────────────────────

    [Fact]
    public void TrySave_Round_Trips_Through_The_Shared_File()
    {
        var path = Path.Combine(ScratchDir("dg-tp1-write"), "gate.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteStamp(path, "{\"stale_days\": 7, \"step_pct\": 5, \"_comment\": \"keep me\"}");
        Tp1PlanGateConfig.Reload(path);
        try
        {
            var edit = new Dictionary<string, double>(Tp1PlanGateConfig.Loaded)
            {
                ["stale_days"] = 3,
                ["step_max_drift"] = 1.5,
            };
            Assert.True(Tp1PlanGateConfig.TrySave(edit, out var error), error);

            // Reloaded in place...
            Assert.Equal(3, Tp1PlanGateConfig.StaleDays);
            Assert.Equal(1.5, Tp1PlanGateConfig.StepMaxDrift);
            // ...and the untouched keys survive, including the comment.
            Assert.Equal(5.0, Tp1PlanGateConfig.StepPct);
            Assert.Contains("keep me", File.ReadAllText(path));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(3, doc.RootElement.GetProperty("stale_days").GetInt32());
        }
        finally
        {
            Tp1PlanGateConfig.Reload();
        }
    }

    // ── the app-side plan-review ledger ──────────────────────────────

    [Fact]
    public void In_App_Mark_Acted_And_Cleared_Write_The_Watcher_Events()
    {
        var dir = ScratchDir("dg-tp1-ledger");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tp1-plan-reviews.jsonl");
        var now = DateTimeOffset.UtcNow;

        File.WriteAllText(path,
            "{\"event\":\"recommended\",\"id\":\"r1\",\"ts\":\""
            + now.AddDays(-10).ToString("o")
            + "\",\"direction\":\"down\",\"baseline_pct\":25,\"candidate_pct\":15}\n");

        var open = Tp1PlanReviewLedger.ReadOpen(path, now);
        var review = Assert.Single(open);
        Assert.Equal("r1", review.Id);
        Assert.Equal("down", review.Direction);
        Assert.True(review.IsStale);
        Assert.Contains("25%→15%", review.Label);

        Assert.True(Tp1PlanReviewLedger.MarkActed(path, "r1", "acted in-app"));
        Assert.Empty(Tp1PlanReviewLedger.ReadOpen(path, now));

        // The appended line is the same shape the watcher's reader folds.
        using var doc = JsonDocument.Parse(File.ReadAllLines(path).Last());
        Assert.Equal("acted", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("r1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("acted in-app", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void ReadLatest_Surfaces_The_Newest_Recommendation_With_Its_Evidence()
    {
        var dir = ScratchDir("dg-tp1-latest");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tp1-plan-reviews.jsonl");
        var now = DateTimeOffset.UtcNow;

        File.WriteAllText(path,
            Rec("r1", "down", now.AddDays(-20), 25, 10, -4.5, 3, 3)
            + "{\"event\":\"cleared\",\"id\":\"r1\",\"ts\":\""
            + now.AddDays(-19).ToString("o") + "\"}\n"
            + Rec("r2", "up", now.AddDays(-10), 20, 35, 2.4, 4, 0)
            + Rec("r3", "down", now.AddDays(-1), 35, 25, -2.5, 5, 5));

        var latest = Tp1PlanReviewLedger.ReadLatest(path, now);
        Assert.NotNull(latest);
        Assert.Equal("r3", latest!.Id);          // newest by ts
        Assert.Equal("down", latest.Direction);
        Assert.Equal(35, latest.BaselinePct);
        Assert.Equal(25, latest.CandidatePct);
        Assert.Equal(5, latest.Graded);
        Assert.Contains("↓ 35% → 25%", latest.Summary);
        Assert.Contains("net -2.50R", latest.Summary);
        Assert.Contains("5 graded", latest.Summary);
        Assert.Contains("(5 trailed)", latest.Summary);
        Assert.Contains("open 1d", latest.Summary);

        // A closed review is still reported (with its status), so the
        // operator sees what the gate last said and how it ended.
        var closed = Path.Combine(dir, "closed.jsonl");
        File.WriteAllText(closed,
            Rec("c1", "down", now.AddDays(-5), 25, 15, -3, 3, 3)
            + "{\"event\":\"cleared\",\"id\":\"c1\",\"ts\":\""
            + now.AddDays(-4).ToString("o") + "\"}\n");
        Assert.Equal("cleared", Tp1PlanReviewLedger.ReadLatest(closed, now)!.Status);

        // No ledger (or an unreadable one) reads as no recommendation.
        Assert.Null(Tp1PlanReviewLedger.ReadLatest(
            Path.Combine(dir, "missing.jsonl"), now));

        static string Rec(string id, string dir, DateTimeOffset ts,
            double baseline, double candidate, double net, int graded, int trailed)
        {
            string N(double v) =>
                v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "{\"event\":\"recommended\",\"id\":\"" + id + "\",\"ts\":\""
                + ts.ToString("o") + "\",\"direction\":\"" + dir
                + "\",\"baseline_pct\":" + N(baseline)
                + ",\"candidate_pct\":" + N(candidate)
                + ",\"net_r\":" + N(net)
                + ",\"mean_vs_giveback_r\":" + N(net / graded)
                + ",\"graded\":" + graded
                + ",\"trailed\":" + trailed + "}\n";
        }
    }

    [Fact]
    public void Recent_Returns_Newest_First_With_Outcomes()
    {
        var dir = ScratchDir("dg-tp1-history");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tp1-plan-reviews.jsonl");
        var now = DateTimeOffset.UtcNow;

        File.WriteAllText(path,
            RecLine("r1", "down", now.AddDays(-30), 25, 10, -4.5, 3, 3)
            + "{\"event\":\"acted\",\"id\":\"r1\",\"ts\":\""
            + now.AddDays(-29).ToString("o") + "\"}\n"
            + RecLine("r2", "up", now.AddDays(-20), 20, 35, 2.4, 4, 0)
            + "{\"event\":\"cleared\",\"id\":\"r2\",\"ts\":\""
            + now.AddDays(-19).ToString("o") + "\"}\n"
            + RecLine("r3", "down", now.AddDays(-1), 35, 25, -2.5, 5, 5));

        var rows = Tp1PlanReviewLedger.Recent(path, now);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "r3", "r2", "r1" }, rows.Select(r => r.Id));
        Assert.Equal("open", rows[0].Outcome);
        Assert.Equal("cleared (recovered)", rows[1].Outcome);
        Assert.Equal("✔ acted on", rows[2].Outcome);
        Assert.Contains("35% → 25%", rows[0].Summary);

        // Capped on request, and a missing ledger reads as no history.
        Assert.Single(Tp1PlanReviewLedger.Recent(path, now, count: 1));
        Assert.Empty(Tp1PlanReviewLedger.Recent(
            Path.Combine(dir, "missing.jsonl"), now));
    }

    // ── the override verdict the dashboard banner reads ─────────────────

    [Fact]
    public void Override_Check_Shows_A_Current_Verdict_And_Drops_A_Stale_One()
    {
        var dir = ScratchDir("dg-tp1-check");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tp1-override-verification.json");
        var now = DateTimeOffset.UtcNow;

        void Write(object rec) =>
            File.WriteAllText(path, JsonSerializer.Serialize(rec));

        // The watcher's own file name, beside the other artefacts.
        Assert.Equal(Path.Combine(dir, "watcher",
            "tp1-override-verification.json"),
            Tp1PlanOverrideCheck.PathFor(dir));

        // No file — and no override armed — reads as no verdict.
        Assert.Null(Tp1PlanOverrideCheck.Read(path, null, now));
        Assert.Null(Tp1PlanOverrideCheck.Read(path, 15, now));

        // A current verdict naming the live plan %: shown, with the
        // operator's line for it.
        Write(new { status = "waiting", plan_pct = 15,
                    armed_ts = now.AddHours(-6), age_days = 0.25,
                    partials = true, computed_at = now.AddMinutes(-2) });
        var waiting = Tp1PlanOverrideCheck.Read(path, 15, now);
        Assert.NotNull(waiting);
        Assert.Equal("waiting", waiting!.Status);
        Assert.Equal(15, waiting.PlanPct);
        Assert.True(waiting.Partials);
        Assert.Contains("waiting for a rung", waiting.Summary);

        // It speaks for 15%, not for an override armed at another figure.
        Assert.Null(Tp1PlanOverrideCheck.Read(path, 25, now));

        // The watcher saying "no override armed" shows nothing — the banner
        // already carries the armed value straight from the engine.
        Write(new { status = "none", plan_pct = (double?)null,
                    computed_at = now });
        Assert.Null(Tp1PlanOverrideCheck.Read(path, 15, now));

        // Stale: the watcher has not run in an hour, so it cannot describe
        // an override armed since.
        Write(new { status = "never-fired", plan_pct = 15, age_days = 3.2,
                    computed_at = now.AddHours(-2) });
        Assert.Null(Tp1PlanOverrideCheck.Read(path, 15, now));

        // Unreadable, malformed or absent never throws — a corrupt file
        // must not take the dashboard down.
        File.WriteAllText(path, "{ not json");
        Assert.Null(Tp1PlanOverrideCheck.Read(path, 15, now));
        File.Delete(path);
        Assert.Null(Tp1PlanOverrideCheck.Read(path, 15, now));

        // Every status the watcher can report renders a line.
        foreach (var (status, needle) in new[]
        {
            ("ok", "verified"), ("waiting", "waiting for a rung"),
            ("never-fired", "NEVER FIRED"), ("mismatch", "IGNORED"),
            ("partials-off", "CANNOT FIRE"), ("unsourced", "no arm row"),
        })
        {
            Write(new { status, plan_pct = 15, seen_pct = 25, age_days = 3.2,
                        computed_at = now });
            var verdict = Tp1PlanOverrideCheck.Read(path, 15, now);
            Assert.NotNull(verdict);
            Assert.Contains(needle, verdict!.Summary);
        }

        // The unmet precondition rides the same line in parentheses — and
        // is dropped when the watcher says it would merely restate the
        // status (that suppression happens on its side, once).
        Write(new { status = "never-fired", plan_pct = 15, age_days = 3.2,
                    reason = "engine-idle",
                    reason_text = "the brain loop is off",
                    computed_at = now });
        var withReason = Tp1PlanOverrideCheck.Read(path, 15, now);
        Assert.Equal("engine-idle", withReason!.Reason);
        Assert.Contains("(the brain loop is off)", withReason.Summary);

        Write(new { status = "waiting", plan_pct = 15, age_days = 0.3,
                    reason = "no-eligible-rung", reason_text = (string?)null,
                    computed_at = now });
        var withoutReason = Tp1PlanOverrideCheck.Read(path, 15, now);
        Assert.True(string.IsNullOrEmpty(withoutReason!.ReasonText));
        Assert.Equal("waiting for a rung — 0.3d since the arm, " +
                     "none has carried it yet", withoutReason.Summary);

        // A status this build does not know shows its raw name, never an
        // empty line.
        Write(new { status = "brand-new", plan_pct = 15, computed_at = now });
        Assert.Equal("brand-new",
            Tp1PlanOverrideCheck.Read(path, 15, now)!.Summary);

        File.Delete(path);
    }

    [Fact]
    public void ReadOpen_Judges_Staleness_By_The_Shared_Threshold()
    {
        var dir = ScratchDir("dg-tp1-ledger");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "reviews.jsonl");
        var now = DateTimeOffset.UtcNow;

        File.WriteAllText(path,
            Line("fresh", "down", now.AddDays(-1))
            + Line("stale", "down", now.AddDays(-30))
            + Line("cleared", "up", now.AddDays(-30))
            + "{\"event\":\"cleared\",\"id\":\"cleared\",\"ts\":\""
            + now.ToString("o") + "\"}\n");

        var byId = Tp1PlanReviewLedger.ReadOpen(path, now).ToDictionary(r => r.Id);
        Assert.False(byId["fresh"].IsStale);
        Assert.True(byId["stale"].IsStale);
        Assert.False(byId.ContainsKey("cleared"));   // cleared, so not open

        static string Line(string id, string dir, DateTimeOffset ts) =>
            "{\"event\":\"recommended\",\"id\":\"" + id + "\",\"ts\":\""
            + ts.ToString("o") + "\",\"direction\":\"" + dir + "\"}\n";
    }
}
