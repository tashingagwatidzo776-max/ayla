using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// The shared TP1 plan-% gate tunables, read from
/// <c>config/tp1-plan-gate.json</c> — the SAME file
/// <c>scripts/watch_tp1_first_arm.py</c> reads, so the gate the watcher
/// recommends and the breaker the app enforces can never drift apart.
/// Key names are mirrored 1:1 by <c>PLAN_GATE_CONFIG_KEYS</c> in the watcher.
///
/// The path is resolved ONCE to the checkout's repo-root config when the app
/// runs from a checkout (the file the watcher reads, so an in-app edit moves
/// both sides together); a published build falls back to a copy beside the
/// exe (shipped by the csproj). Every value getter first checks the file for
/// changes (throttled), so an edit takes effect without a restart. A missing
/// or malformed file leaves the compiled defaults standing — a bad edit can
/// never take the app down.
/// </summary>
public static class Tp1PlanGateConfig
{
    /// <summary>The shared tunables file name under the repo's config/ dir.</summary>
    public const string FileName = "tp1-plan-gate.json";

    /// <summary>The exact key set the shared file carries — kept in lockstep
    /// with PLAN_GATE_CONFIG_KEYS in the watcher. A key added on one side
    /// without the other is a parity bug, so the set is asserted in tests.</summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "min_graded", "net_r", "up_net_r", "step_r", "step_pct",
        "min_pct", "max_pct", "score_min_graded", "step_feedback_min",
        "step_feedback_pct", "step_min", "step_max", "step_max_drift",
        "stale_days",
    };

    private static readonly object Gate = new();
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(2);

    private static string? _path;
    private static IReadOnlyDictionary<string, double> _values =
        new Dictionary<string, double>();
    private static long _stamp;
    private static DateTime _lastFreshCheck = DateTime.MinValue;

    static Tp1PlanGateConfig() => Reload(null);

    /// <summary>The canonical file the app reads and writes (may be null when
    /// there is no checkout and no copy beside the exe).</summary>
    public static string? ResolvedPath
    {
        get { lock (Gate) { return _path; } }
    }

    /// <summary>Changes whenever the loaded file changes (its last-write
    /// ticks). The breaker caches against this, so an edit releases the cache
    /// immediately rather than waiting out its TTL.</summary>
    public static long Stamp
    {
        get { EnsureFresh(); lock (Gate) { return _stamp; } }
    }

    /// <summary>The values actually loaded — empty when the file is absent,
    /// unreadable, or malformed.</summary>
    public static IReadOnlyDictionary<string, double> Loaded
    {
        get { EnsureFresh(); lock (Gate) { return _values; } }
    }

    /// <summary>The config path the app would use, given a start directory:
    /// the checkout's repo-root config first (the watcher's file), then a
    /// copy beside the exe for published builds. Null when neither exists.</summary>
    public static string? FindPath(string startDirectory)
    {
        for (var dir = Path.GetFullPath(startDirectory); dir is not null;
             dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "config", FileName);
            if (File.Exists(candidate) && Directory.Exists(Path.Combine(dir, ".git")))
            {
                return candidate;
            }
        }

        var local = Path.Combine(AppContext.BaseDirectory, "config", FileName);
        return File.Exists(local) ? local : null;
    }

    /// <summary>Re-reads the shared tunables (null = resolve the canonical
    /// path). Exposed so a scratch file can drive the loader in tests.</summary>
    public static void Reload(string? path = null)
    {
        var target = path ?? FindPath(AppContext.BaseDirectory);
        lock (Gate)
        {
            _path = target;
            _values = Read(target, out var stamp);
            _stamp = stamp;
            _lastFreshCheck = DateTime.UtcNow;
        }
    }

    /// <summary>Re-reads unconditionally when the file's timestamp moved —
    /// the hot-reload path (throttled). Safe to call often.</summary>
    public static void EnsureFresh()
    {
        if (DateTime.UtcNow - _lastFreshCheck < FreshWindow)
        {
            return;
        }

        Refresh();
    }

    /// <summary>Re-reads unconditionally (no throttle). Called by
    /// <see cref="EnsureFresh"/> and directly by tests.</summary>
    public static void Refresh()
    {
        _lastFreshCheck = DateTime.UtcNow;
        string? target;
        long stored;
        lock (Gate)
        {
            target = _path;
            stored = _stamp;
        }

        if (target is null || !File.Exists(target))
        {
            return;
        }

        long current;
        try
        {
            current = File.GetLastWriteTimeUtc(target).Ticks;
        }
        catch
        {
            return;
        }

        if (current != stored)
        {
            Reload(target);
        }
    }

    /// <summary>Consumes every tunable to reject a config that would make the
    /// gate nonsensical (an inverted band, a negative step, a min above the
    /// max). Returns the problems; empty means the config is coherent.</summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyDictionary<string, double>? values = null)
    {
        var v = values ?? Loaded;
        double G(string key, double fallback) =>
            v.TryGetValue(key, out var x) ? x : fallback;

        var problems = new List<string>();
        if (G("min_graded", 3) < 1)
            problems.Add("min_graded must be at least 1 graded rung");
        if (G("net_r", -1) >= G("up_net_r", 1))
            problems.Add("net_r must sit below up_net_r (the gate's two bands)");
        if (G("step_r", 1) <= 0)
            problems.Add("step_r must be positive");
        if (G("step_pct", 5) <= 0)
            problems.Add("step_pct must be positive");
        if (G("min_pct", 0) < 0)
            problems.Add("min_pct must not be negative");
        if (G("max_pct", 60) <= G("min_pct", 0))
            problems.Add("max_pct must exceed min_pct");
        if (G("max_pct", 60) > 100)
            problems.Add("max_pct must not exceed 100%");
        if (G("score_min_graded", 1) < 1)
            problems.Add("score_min_graded must be at least 1");
        if (G("step_feedback_min", 3) < 1)
            problems.Add("step_feedback_min must be at least 1 scored review");
        if (G("step_feedback_pct", 2) < 0)
            problems.Add("step_feedback_pct must not be negative");
        if (G("step_min", 1) <= 0)
            problems.Add("step_min must be positive");
        if (G("step_max", 15) < G("step_min", 1))
            problems.Add("step_max must be at least step_min");
        if (G("step_max_drift", 3) < 0)
            problems.Add("step_max_drift must not be negative");
        if (G("stale_days", 7) < 1)
            problems.Add("stale_days must be at least 1 day");
        return problems;
    }

    /// <summary>Writes the given tunables back to the canonical file
    /// (preserving keys it does not touch, e.g. the _comment), then reloads.
    /// Atomic (temp + move) so the watcher never reads a half-written file.
    /// Returns false with a reason on failure.</summary>
    public static bool TrySave(IReadOnlyDictionary<string, double> values,
                               out string error)
    {
        error = "";
        var target = ResolvedPath;
        if (target is null)
        {
            error = "no shared config file to write";
            return false;
        }

        try
        {
            JsonObject root;
            if (File.Exists(target))
            {
                root = JsonNode.Parse(File.ReadAllText(target)) as JsonObject
                    ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            }

            foreach (var kv in values)
            {
                if (Keys.Contains(kv.Key))
                {
                    root[kv.Key] = kv.Value;
                }
            }

            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, json + "\n");
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Reload(target);
        return true;
    }

    private static Dictionary<string, double> Read(string? path, out long stamp)
    {
        stamp = 0;
        var result = new Dictionary<string, double>();
        if (path is null || !File.Exists(path))
        {
            return result;
        }

        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var key in Keys)
            {
                if (doc.RootElement.TryGetProperty(key, out var el)
                    && el.ValueKind == JsonValueKind.Number
                    && el.TryGetDouble(out var value))
                {
                    result[key] = value;
                }
            }
        }
        catch
        {
            // Missing / unreadable / malformed: the defaults stand.
            result.Clear();
        }

        return result;
    }

    private static double Get(string key, double fallback)
    {
        EnsureFresh();
        lock (Gate)
        {
            return _values.TryGetValue(key, out var v) ? v : fallback;
        }
    }

    // Mirrors PLAN_GATE_* in scripts/watch_tp1_first_arm.py.
    public static double MinGraded => Get("min_graded", 3);
    public static double NetR => Get("net_r", -1.0);
    public static double UpNetR => Get("up_net_r", 1.0);
    public static double StepR => Get("step_r", 1.0);
    public static double StepPct => Get("step_pct", 5.0);
    public static double MinPct => Get("min_pct", 0.0);
    public static double MaxPct => Get("max_pct", 60.0);
    public static double ScoreMinGraded => Get("score_min_graded", 1);
    public static double StepFeedbackMin => Get("step_feedback_min", 3);
    public static double StepFeedbackPct => Get("step_feedback_pct", 2.0);
    public static double StepMin => Get("step_min", 1.0);
    public static double StepMax => Get("step_max", 15.0);
    public static double StepMaxDrift => Get("step_max_drift", 3.0);

    /// <summary>Days an OPEN plan-% review must age before the breaker holds
    /// (mirrors PLAN_REVIEW_STALE_DAYS).</summary>
    public static int StaleDays => (int)Get("stale_days", 7);
}
