using System;
using System.Collections.Generic;
using DongGfx.Core.Logging;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Contained crash-guard faults recorded in the journal (category
/// <see cref="Category"/>). The app writes one row every time a global handler
/// contains an unhandled exception; this projects the recent ones so the
/// dashboard can say a fault happened, and the shell can hold the engine loop
/// back rather than auto-restoring into a fault loop. Read-only.
/// </summary>
public static class CrashFaults
{
    /// <summary>Journal category the crash guards write.</summary>
    public const string Category = "APP_FAULT";

    /// <summary>Category the safe-mode notice itself uses — deliberately NOT
    /// <see cref="Category"/>, or announcing the hold-back would add a fault
    /// and keep safe mode sticky forever.</summary>
    public const string SafeModeCategory = "APP_SAFE_MODE";

    /// <summary>The detail text the release path logs when an operator
    /// explicitly resumes the engine loop. A release ACKNOWLEDGES every fault
    /// older than it: the next launch re-enters safe mode only when a NEWER
    /// fault has occurred since the release — so one deliberate human act
    /// survives restarts instead of re-latching on every launch until the
    /// 24h window drains.</summary>
    public const string ReleaseMarker = "safe mode released by operator";

    /// <summary>How recent a fault must be to count. A fault older than this
    /// is history, not a reason to distrust the current session.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    /// <summary>Recent contained faults: how many, the newest, and the newest
    /// one's text. Count is zero (Latest empty) when nothing qualifies.</summary>
    public sealed record Summary(int Count, DateTimeOffset? LatestAt, string Latest);

    /// <summary>The startup gate: true when a contained fault inside the
    /// window is NEWER than the latest operator release. A release
    /// acknowledges the faults that preceded it (an explicit human act the
    /// design already requires), so a restart after a release auto-starts the
    /// brain; a fault after the release re-arms the hold-back. Never throws,
    /// never counts the release row itself as a fault.</summary>
    public static bool SafeModeNeeded(
        IEnumerable<JournalEntry>? entries,
        DateTimeOffset now,
        TimeSpan? window = null)
    {
        if (entries is null)
        {
            return false;
        }

        var from = now - (window ?? DefaultWindow);
        DateTimeOffset? latestFault = null;
        DateTimeOffset? latestRelease = null;
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (string.Equals(entry.Category, Category, StringComparison.Ordinal)
                && entry.Timestamp >= from
                && (latestFault is null || entry.Timestamp > latestFault))
            {
                latestFault = entry.Timestamp;
            }

            if (string.Equals(entry.Category, SafeModeCategory, StringComparison.Ordinal)
                && (entry.Details ?? "").Contains(ReleaseMarker, StringComparison.Ordinal)
                && (latestRelease is null || entry.Timestamp > latestRelease))
            {
                latestRelease = entry.Timestamp;
            }
        }

        return latestFault is not null
            && (latestRelease is null || latestFault > latestRelease);
    }

    /// <summary>Newest-first journal entries in, recent-fault summary out.
    /// Never throws.</summary>
    public static Summary FromJournal(
        IEnumerable<JournalEntry>? entries,
        DateTimeOffset now,
        TimeSpan? window = null,
        int cap = 200)
    {
        if (entries is null)
        {
            return new Summary(0, null, "");
        }

        var from = now - (window ?? DefaultWindow);
        var count = 0;
        DateTimeOffset? latestAt = null;
        var latest = "";
        foreach (var entry in entries)
        {
            if (entry is null
                || !string.Equals(entry.Category, Category, StringComparison.Ordinal)
                || entry.Timestamp < from)
            {
                continue;
            }

            count++;
            if (latestAt is null || entry.Timestamp > latestAt)
            {
                latestAt = entry.Timestamp;
                latest = entry.Details ?? "";
            }

            if (count >= cap)
            {
                break;
            }
        }

        return new Summary(count, latestAt, latest);
    }
}
