using System.Globalization;
using System.IO;

namespace DongGfx.App.Services;

/// <summary>
/// The journal's own book: open lots derived ONLY from the local trade
/// journal — every brain fill opens a ticket, every close confirmation
/// ("closed #N — deal …") retires one. Two fill shapes exist and BOTH
/// count: paper fills ("paper-exec fill (demo): buy 0.1 lots … — ticket
/// N") and REAL fills ("buy 0.1 lots XAUUSD @ 4108.84 — ticket N",
/// PaperExec:false — no "fill" substring at all). The real shape was
/// originally missed (2026-10-07: 9 of 10 real fills journaled without a
/// matching close row, none parsed), so this floor leg silently stood at
/// zero exactly when the venue read degraded around a REAL position —
/// the leak the journal book exists to survive. The bridge's /positions
/// read can degrade to an empty list under congestion (three cap failures
/// on 2026-09-29 rode exactly that), but the journal is written locally
/// before any venue round-trip can lie about it. Used as a FLOOR by the
/// exposure guards: conservative by construction — the worst case is
/// refusing a trade, never over-trading. Cached ~30 s.
/// </summary>
public sealed class FxJournalBook
{
    /// <summary>When a close's lots are unknown (the close confirmation
    /// carries no size), retire this much — the venue's plausible minimum.
    /// Under-retiring keeps the floor conservative-high.</summary>
    public const double UnknownCloseLots = 0.01;

    private readonly string _journalDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<long, double> _lotsByTicket = new();
    private double _cached = -1;   // -1 = never computed
    private DateTimeOffset _computedAt;

    public FxJournalBook(string journalDir, Func<DateTimeOffset>? clock = null)
    {
        _journalDir = journalDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Open lots per the journal: fills minus confirmed closes,
    /// by ticket. Never throws — an unreadable journal floors at 0 (the
    /// hosts' own book remains the other floor).</summary>
    public double OpenLots()
    {
        lock (_lock)
        {
            if (_cached >= 0 && _clock() - _computedAt < TimeSpan.FromSeconds(30))
            {
                return _cached;
            }

            _lotsByTicket.Clear();
            _cached = Compute(_lotsByTicket);
            _computedAt = _clock();
            return _cached;
        }
    }

    /// <summary>Every ticket the journal book still holds open, with its
    /// lots — the reconciliation view: tickets the venue provably no
    /// longer holds can be proof-closed against this set. Snapshot copy;
    /// same cache and fail-silent contract as <see cref="OpenLots"/>.</summary>
    public IReadOnlyDictionary<long, double> OpenTickets()
    {
        lock (_lock)
        {
            if (_cached < 0 || _clock() - _computedAt >= TimeSpan.FromSeconds(30))
            {
                _lotsByTicket.Clear();
                _cached = Compute(_lotsByTicket);
                _computedAt = _clock();
            }
            return new Dictionary<long, double>(_lotsByTicket);
        }
    }

    private double Compute(Dictionary<long, double> lots)
    {
        double open = 0;
        try
        {
            if (!Directory.Exists(_journalDir))
            {
                return 0;
            }

            foreach (var file in Directory.GetFiles(_journalDir, "journal_*.jsonl").OrderBy(f => f))
            {                    foreach (var line in DongGfx.Core.Logging.TradeJournal.ReadLinesShared(file))
                {
                    // "fill" catches the paper rows; "ticket " catches the
                    // real-exec rows, which carry no "fill" word (verified:
                    // all FX_ORDER rows containing "ticket " are fills of
                    // one of the two shapes — buy/sell N lots SYM @ P —
                    // ticket N). ParseFill returns (0,0) for anything that
                    // lacks a parsable ticket, so the wider gate is safe.
                    if (line.Contains("FX_ORDER")
                        && (line.Contains("fill") || line.Contains("ticket ")))
                    {
                        var (ticket, fillLots) = ParseFill(line);
                        if (ticket != 0 && fillLots > 0)
                        {
                            lots[ticket] = lots.TryGetValue(ticket, out var prev) && prev > 0
                                ? prev   // partial fills: first size wins (conservative-high)
                                : fillLots;
                            open += fillLots;
                        }
                    }
                    else if (line.Contains("FX_EXIT") && line.Contains("closed #"))
                    {
                        var ticket = ParseClosedTicket(line);
                        if (ticket != 0)
                        {
                            var retired = lots.TryGetValue(ticket, out var v) && v > 0 ? v : UnknownCloseLots;
                            open = Math.Max(0, open - retired);
                            lots.Remove(ticket);
                        }
                    }
                }
            }
        }
        catch
        {
            // An unreadable journal must never crash a veto leg.
        }

        return open;
    }

    /// <summary>The human-readable prefix of the Details field — the text
    /// between the "Details":" marker and the payload's opening brace. The
    /// journal line itself is a JSON envelope, so the FIRST brace of the
    /// line is the envelope's own (a parse against it reads empty and
    /// silently finds nothing — exactly what the first draft did).</summary>
    private static string DetailsText(string line)
    {
        var marker = line.IndexOf("\"Details\":\"", StringComparison.Ordinal);
        if (marker < 0)
        {
            return string.Empty;
        }

        var rest = line[(marker + "\"Details\":\"".Length)..];
        var brace = rest.IndexOf('{', StringComparison.Ordinal);
        return brace < 0 ? rest : rest[..brace];
    }

    /// <summary>"…fill (demo): buy 0.1 lots XAUUSDmicro @ 4145.53 — ticket 9820977524: …"</summary>
    private static (long Ticket, double Lots) ParseFill(string line)
    {
        var text = DetailsText(line);
        if (text.Length == 0)
        {
            return (0, 0);
        }
        var ticketMarker = text.LastIndexOf("ticket ", StringComparison.Ordinal);
        if (ticketMarker < 0)
        {
            return (0, 0);
        }

        var span = text[(ticketMarker + 7)..];
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end]))
        {
            end++;
        }

        if (end == 0 || !long.TryParse(span[..end], out var ticket))
        {
            return (0, 0);
        }

        var lotsMarker = text.IndexOf(" lots ", StringComparison.Ordinal);
        if (lotsMarker < 0)
        {
            return (ticket, 0);
        }

        var start = lotsMarker;
        while (start > 0 && (char.IsDigit(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        return double.TryParse(text[start..lotsMarker], NumberStyles.Float,
                   CultureInfo.InvariantCulture, out var lots)
            ? (ticket, lots)
            : (ticket, 0);
    }

    /// <summary>"closed #9820129160 — deal : …"</summary>
    private static long ParseClosedTicket(string line)
    {
        var text = DetailsText(line);
        var marker = text.IndexOf("closed #", StringComparison.Ordinal);
        if (marker < 0)
        {
            return 0;
        }

        var span = text[(marker + 8)..];
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end]))
        {
            end++;
        }

        return end > 0 && long.TryParse(span[..end], out var t) ? t : 0;
    }
}
