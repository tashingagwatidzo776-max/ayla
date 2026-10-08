using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Read/write over the watcher's plan-% review ledger
/// (data/watcher/tp1-plan-reviews.jsonl) from the app. The reads drive the
/// operator-visible overdue list; the writes are the SAME <c>acted</c> /
/// <c>cleared</c> events <c>scripts/watch_tp1_first_arm.py</c> appends — so
/// acting on a review in-app releases the TP1 rung exactly as the CLI runbook
/// does, and both land on one audit trail. Append-only: the original
/// recommendation is never rewritten.
/// </summary>
public static class Tp1PlanReviewLedger
{
    public static string PathFor(string dataDir) =>
        Path.Combine(dataDir, "watcher", "tp1-plan-reviews.jsonl");

    /// <summary>One open review as the app needs it to render the action
    /// list.</summary>
    public sealed record OpenReview(
        string Id, string Direction, double? BaselinePct, double? CandidatePct,
        DateTimeOffset RecommendedAt, double AgeDays, bool IsStale)
    {
        /// <summary>Pre-formatted row label, e.g. "↓ 25%→15% · 10d old ·
        /// ⚠ overdue" — the VM binds plain text.</summary>
        public string Label
        {
            get
            {
                var arrow = Direction == "up" ? "↑" : "↓";
                var plan = BaselinePct is { } b && CandidatePct is { } c
                    ? $"{b:0.##}%→{c:0.##}%"
                    : "—";
                var age = AgeDays >= 1 ? $" · {AgeDays:0}d old" : "";
                var stale = IsStale ? " · ⚠ overdue" : "";
                return $"{arrow} {plan}{age}{stale}";
            }
        }
    }

    /// <summary>Open (unreviewed) reviews, newest first. Staleness is judged
    /// against <paramref name="now"/> and the shared stale threshold, so the
    /// list agrees with the breaker that holds the rung. A missing or
    /// unreadable ledger reads as no open reviews.</summary>
    public static IReadOnlyList<OpenReview> ReadOpen(string path, DateTimeOffset now)
    {
        List<FxExitWeeklyDigest.PlanReview> folded;
        try
        {
            folded = FxExitWeeklyDigest.ReadPlanReviewEvents(path).ToList();
        }
        catch
        {
            return [];
        }

        var stale = TimeSpan.FromDays(Tp1PlanGateConfig.StaleDays);
        return folded
            .Where(r => r.Status == "open")
            .Select(r => new OpenReview(
                r.Id, r.Direction, r.BaselinePct, r.CandidatePct, r.Ts,
                Math.Max(0, (now - r.Ts).TotalDays), now - r.Ts >= stale))
            .OrderByDescending(r => r.RecommendedAt)
            .ToList();
    }

    /// <summary>Append the <c>acted</c> event the watcher folds as
    /// status=acted — the in-app equivalent of
    /// <c>mark_plan_review_acted</c>. Returns true when it was written.</summary>
    public static bool MarkActed(string path, string id, string note = "") =>
        Append(path, new Dictionary<string, object?>
        {
            ["event"] = "acted",
            ["id"] = id,
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["note"] = note,
        });

    /// <summary>Append the <c>cleared</c> event (the condition resolved
    /// without a change) — the watcher folds it as status=cleared.</summary>
    public static bool MarkCleared(string path, string id) =>
        Append(path, new Dictionary<string, object?>
        {
            ["event"] = "cleared",
            ["id"] = id,
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        });

    private static bool Append(string path, Dictionary<string, object?> record)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
            return true;
        }
        catch
        {
            // Best effort: a lost line must never take the app down (the
            // watcher keeps the raw journals).
            return false;
        }
    }

    // ── the latest recommendation (what the watcher last said) ────────

    /// <summary>The watcher's most recent gate recommendation with the
    /// evidence the app shows beside it. Independent of status: a closed
    /// review still tells the operator what the gate last said and how it
    /// ended.</summary>
    public sealed record Recommendation(
        string Id, string Direction, double? BaselinePct, double? CandidatePct,
        double? NetR, double? MeanVsGivebackR, int? Graded, int? Trailed,
        int? Beaten, DateTimeOffset RecommendedAt, double AgeDays, string Status)
    {
        /// <summary>One-line evidence, e.g. "↓ 25% → 15% · net -3.20R ·
        /// 3 graded (3 trailed) · open 10d".</summary>
        public string Summary
        {
            get
            {
                var arrow = Direction == "up" ? "↑" : "↓";
                var plan = BaselinePct is { } b && CandidatePct is { } c
                    ? $"{b:0.##}% → {c:0.##}%"
                    : "—";
                var net = NetR is { } n ? $"net {n:+0.00;-0.00}R" : "net —";
                // Only the counter that matters for the direction: how many
                // trailed a 'down' call, how many beat an 'up' call.
                var counter = Direction == "up"
                    ? (Beaten is { } bt ? $" ({bt} beat)" : "")
                    : (Trailed is { } t ? $" ({t} trailed)" : "");
                var graded = Graded is { } g ? g + " graded" + counter : "";
                var mean = MeanVsGivebackR is { } m
                    ? $"mean {m:+0.00;-0.00}R/rung"
                    : "";
                var state = Status == "open"
                    ? (AgeDays >= 1 ? $"open {AgeDays:0}d" : "open today")
                    : Status;
                var parts = new[] { plan, net, graded, mean, state }
                    .Where(p => p.Length > 0);
                return $"{arrow} " + string.Join(" · ", parts);
            }
        }
    }

    /// <summary>One row of the recommendation history: what the gate said
    /// (with its evidence) and how the review ended.</summary>
    public sealed record HistoryRow(string Id, string Direction,
        string Summary, string Status, DateTimeOffset RecommendedAt)
    {
        /// <summary>How the review ended, in the operator's terms.</summary>
        public string Outcome => Status switch
        {
            "acted" => "✔ acted on",
            "cleared" => "cleared (recovered)",
            _ => "open",
        };
    }

    /// <summary>The most recent recommendations, newest first — the history
    /// the dashboard shows, so the operator can see whether acting has been
    /// helping. Never throws; a missing ledger reads as none.</summary>
    public static IReadOnlyList<HistoryRow> Recent(string path,
        DateTimeOffset now, int count = 5)
    {
        List<FxExitWeeklyDigest.PlanReview> folded;
        try
        {
            folded = FxExitWeeklyDigest.ReadPlanReviewEvents(path).ToList();
        }
        catch
        {
            return [];
        }

        return folded
            .OrderByDescending(r => r.Ts)
            .Take(Math.Max(0, count))
            .Select(r => new HistoryRow(
                r.Id, r.Direction, Summarise(r, now), r.Status, r.Ts))
            .ToList();
    }

    /// <summary>The newest recommendation in the ledger (null when there is
    /// none). A missing or unreadable ledger reads as none — never throws.</summary>
    public static Recommendation? ReadLatest(string path, DateTimeOffset now)
    {
        List<FxExitWeeklyDigest.PlanReview> folded;
        try
        {
            folded = FxExitWeeklyDigest.ReadPlanReviewEvents(path).ToList();
        }
        catch
        {
            return null;
        }

        return folded
            .OrderByDescending(r => r.Ts)
            .Select(r => Project(r, now))
            .FirstOrDefault();
    }

    private static Recommendation Project(FxExitWeeklyDigest.PlanReview r,
        DateTimeOffset now) =>
        new(r.Id, r.Direction, r.BaselinePct, r.CandidatePct, r.NetR,
            r.MeanVsGivebackR, r.Graded, r.Trailed, r.Beaten, r.Ts,
            Math.Max(0, (now - r.Ts).TotalDays), r.Status);

    private static string Summarise(FxExitWeeklyDigest.PlanReview r,
        DateTimeOffset now) => Project(r, now).Summary;
}
