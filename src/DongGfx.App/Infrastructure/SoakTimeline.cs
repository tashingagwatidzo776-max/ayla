using System;
using System.Collections.Generic;
using DongGfx.Core.Logging;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// The paper-soak bar's provenance history, read back from the journal's
/// FX_MODE rows. Every restore ("paper soak restored …"), every restart on a
/// build change and every restart on an account change is journaled by the
/// engine hosts; this projects those rows into a small newest-first timeline
/// so the bar's history is auditable in-app rather than only stated in one
/// sentence. Read-only: the journal is the record; this only surfaces it.
/// </summary>
public static class SoakTimeline
{
    /// <summary>One provenance event: when it happened, the sentence the
    /// engine journaled, and whether it RESTARTED the bar (a build or account
    /// change) rather than carried it over.</summary>
    public sealed record Row(DateTimeOffset At, string Text, bool Restart);

    private const string RestoredToken = "paper soak restored";
    private const string ResetToken = "paper soak counters reset";

    /// <summary>Newest-first provenance rows from journal entries (as
    /// returned by <c>TradeJournal.GetRecent</c>, itself newest-first). Only
    /// the restore/reset sentences qualify; anything else in FX_MODE is
    /// ignored. Never throws.</summary>
    public static IReadOnlyList<Row> FromJournal(
        IEnumerable<JournalEntry>? entries, int max = 6)
    {
        var rows = new List<Row>();
        if (entries is null || max <= 0)
        {
            return rows;
        }

        foreach (var entry in entries)
        {
            if (entry is null
                || !string.Equals(entry.Category, "FX_MODE", StringComparison.Ordinal))
            {
                continue;
            }

            var text = entry.Details ?? "";
            var restart = text.Contains(ResetToken, StringComparison.Ordinal);
            if (!restart && !text.Contains(RestoredToken, StringComparison.Ordinal))
            {
                continue;
            }

            // Details is stored as "message" or "message: {json}"; the
            // timeline shows the human sentence only.
            var cut = text.IndexOf(": {", StringComparison.Ordinal);
            rows.Add(new Row(entry.Timestamp, cut > 0 ? text[..cut] : text, restart));
            if (rows.Count >= max)
            {
                break;
            }
        }

        return rows;
    }
}
