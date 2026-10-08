using System;
using System.IO;
using System.Linq;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// The TP1 plan-% review circuit breaker: while a plan-% recommendation is
/// still OPEN and has aged past the review threshold, the TP1 rung must not
/// arm — an overdue recommendation means the allocation is under review, and
/// banking more at it before that review happens compounds the very problem
/// the gate flagged. Read-only over the watcher's ledger; a missing or
/// unreadable ledger holds nobody (no false trips). The breaker clears the
/// moment the review is acted on or cleared.
/// </summary>
public static class Tp1PlanBreaker
{
    /// <summary>Days an OPEN review must age before the breaker holds. Read
    /// from the shared tunables (config/tp1-plan-gate.json) so it can never
    /// drift from PLAN_REVIEW_STALE_DAYS in scripts/watch_tp1_first_arm.py.</summary>
    public static int StaleDays => Tp1PlanGateConfig.StaleDays;

    private static readonly object CacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static string? _cachedPath;
    private static DateTimeOffset _cachedAt;
    private static bool _cachedHeld;
    private static long _cachedConfigStamp;

    /// <summary>Uncached check for UI surfaces (the settings hint), where
    /// the read happens only on load, toggle, or tab-show.</summary>
    public static bool IsHeld(string dataDir, DateTimeOffset now) =>
        IsHeldAt(Path.Combine(dataDir, "watcher", "tp1-plan-reviews.jsonl"), now);

    /// <summary>True when an overdue OPEN plan-% review exists under
    /// <paramref name="dataDir"/>. Cached briefly: the engine consults this
    /// every cycle and the ledger only changes when the watcher runs. The
    /// cache is also keyed by the shared config's stamp, so retuning the
    /// stale threshold takes effect immediately instead of waiting out the
    /// TTL.</summary>
    public static bool IsHeldCached(string dataDir, DateTimeOffset now)
    {
        var path = Path.Combine(dataDir, "watcher", "tp1-plan-reviews.jsonl");
        var stamp = Tp1PlanGateConfig.Stamp;
        lock (CacheLock)
        {
            if (_cachedPath == path && _cachedConfigStamp == stamp
                && now - _cachedAt < CacheTtl)
            {
                return _cachedHeld;
            }

            _cachedHeld = IsHeldAt(path, now);
            _cachedPath = path;
            _cachedAt = now;
            _cachedConfigStamp = stamp;
            return _cachedHeld;
        }
    }

    /// <summary>Drops the cached verdict so the next check re-reads the
    /// ledger. Called when the operator acts on or clears a review in-app,
    /// so the rung releases on the very next engine cycle rather than
    /// waiting out the cache TTL.</summary>
    public static void Invalidate()
    {
        lock (CacheLock)
        {
            _cachedPath = null;
            _cachedAt = default;
        }
    }

    /// <summary>Pure check over one ledger path: held when any review is
    /// still 'open' at least <see cref="StaleDays"/> old. No ledger or a
    /// malformed one reads as not held.</summary>
    internal static bool IsHeldAt(string path, DateTimeOffset now)
    {
        try
        {
            var cutoff = now - TimeSpan.FromDays(StaleDays);
            return FxExitWeeklyDigest.ReadPlanReviewEvents(path)
                .Any(r => r.Status == "open" && r.Ts <= cutoff);
        }
        catch
        {
            // No ledger / unreadable: never a false trip.
            return false;
        }
    }
}
