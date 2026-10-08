using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// The watcher's verdict on the armed plan-% override: did the engine
/// actually reach a rung with it?
/// <c>scripts/watch_tp1_first_arm.py</c> computes the verdict (and pages on
/// it) once per pass and writes one JSON object here; the dashboard reads it
/// back beside the Arm button so "waiting for a rung" / "NEVER FIRED"
/// appears where the operator clicked — without the app re-implementing the
/// check, so the pager and the banner cannot disagree.
///
/// Read-only. A missing, stale or mismatched verdict simply shows nothing:
/// the banner already states the armed value straight from the engine, and
/// showing a wrong status is worse than showing none.
/// </summary>
public static class Tp1PlanOverrideCheck
{
    /// <summary>How old a verdict may be before it stops speaking for the
    /// live override. The watcher rewrites it every pass on a 5-minute
    /// scheduled task, so an hour without a refresh means the watcher is not
    /// running — and a verdict from a dead watcher can misdescribe an
    /// override armed since.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public static string PathFor(string dataDir) =>
        Path.Combine(dataDir, "watcher", "tp1-override-verification.json");

    /// <summary>One verdict as the banner renders it. <see cref="Summary"/>
    /// is the operator's line; <c>Status</c> stays the watcher's own
    /// vocabulary (ok / waiting / never-fired / mismatch / partials-off /
    /// unsourced) so a status this build does not know degrades to its raw
    /// name rather than a wrong sentence. <c>Reason</c> names the unmet
    /// precondition and <c>ReasonText</c> is the watcher's own sentence for
    /// it — rendered there so the CLI, the webhook page and this banner
    /// cannot phrase the same finding differently.</summary>
    public sealed record Verdict(string Status, double PlanPct,
        DateTimeOffset? ArmedAt, double? AgeDays, bool Partials,
        double? SeenPct, DateTimeOffset ComputedAt,
        string? Reason, string? ReasonText)
    {
        /// <summary>One line for the banner, e.g. "⚠ NEVER FIRED — no rung
        /// has carried it in 3.2d (the brain loop is off)". The reason is
        /// appended only when the watcher supplied one — it nulls the
        /// sentence where it would merely restate the status.</summary>
        public string Summary
        {
            get
            {
                var head = Status switch
                {
                    "ok" => SeenPct is { } s
                        ? $"✔ verified — the newest rung was sized at {s:0.##}%"
                        : "✔ verified — the newest rung carried it",
                    "waiting" => AgeDays is { } a
                        ? $"waiting for a rung — {a:0.#}d since the arm, none has carried it yet"
                        : "waiting for a rung — none has carried it yet",
                    "never-fired" => AgeDays is { } d
                        ? $"⚠ NEVER FIRED — no rung has carried it in {d:0.#}d"
                        : "⚠ NEVER FIRED — no rung has carried it",
                    "mismatch" => SeenPct is { } m
                        ? $"⚠ IGNORED — the newest rung was sized at {m:0.##}%, not {PlanPct:0.##}%"
                        : "⚠ IGNORED — the newest rung was sized at another plan %",
                    "partials-off" => "⚠ CANNOT FIRE — TP1 partial execution is OFF",
                    "unsourced" => "ℹ armed in settings only — no arm row to date it against",
                    _ => Status,
                };
                return string.IsNullOrEmpty(ReasonText)
                    ? head : $"{head} ({ReasonText})";
            }
        }
    }

    /// <summary>The watcher's latest verdict, or null when there is none to
    /// show: no file, nothing armed, unreadable, stale, or written about a
    /// different plan % than the one live now. Never throws — a corrupt file
    /// must not take the dashboard down.</summary>
    public static Verdict? Read(string path, double? livePlanPct,
                                DateTimeOffset now)
    {
        if (livePlanPct is not { } live)
        {
            return null;   // nothing armed — the banner is already hidden
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var status = Str(r, "status");
            if (status.Length == 0 || status == "none")
            {
                return null;   // the watcher's last word: no override armed
            }

            // A verdict only speaks for the plan % it was computed against:
            // a file left over from an earlier arm (or a hand-edited value)
            // says nothing about the override live now, so drop it rather
            // than label this one with someone else's answer.
            if (NumberOrNull(r, "plan_pct") is not { } pct
                || Math.Abs(pct - live) > 0.005)
            {
                return null;
            }

            if (TryTime(r, "computed_at") is not { } at || now - at > MaxAge)
            {
                return null;   // stale: the watcher is not keeping up
            }

            return new Verdict(status, pct, TryTime(r, "armed_ts"),
                NumberOrNull(r, "age_days"), Bool(r, "partials"),
                NumberOrNull(r, "seen_pct"), at,
                Str(r, "reason"), Str(r, "reason_text"));
        }
        catch
        {
            // Missing / unreadable / malformed: show nothing, never throw.
            return null;
        }
    }

    private static string Str(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object
        && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static double? NumberOrNull(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object
        && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    private static DateTimeOffset? TryTime(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object
        && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var t)
            ? t : null;

    private static bool Bool(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object
        && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
