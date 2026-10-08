using System.Globalization;
using System.Text.Json;

namespace DongGfx.Core.Fx;

/// <summary>
/// Rebuilds <see cref="FxRecentTape"/> windows from the trade journal, so a
/// deploy or restart never resets n to 0 (the windows take days of tape to
/// refill — a cold start after every deploy would make the exclusion bar
/// unreachable in practice).
///
/// One ordered pass over <c>journal_*.jsonl</c> (filename order = UTC day
/// order; rows inside a file are append-ordered):
/// - fills (<c>FX_ORDER</c> with "fill"/"ticket ") seed ticket →
///   (symbol, family, entry, stop, side) from the payload's
///   <c>Signal</c>/<c>Price</c>/<c>SizedStopDistance</c>/<c>Side</c>;
/// - hold rows (FX_EXIT <c>ProfitR</c>, FX_PROFIT <c>CurrentR</c>) keep the
///   last known R per ticket — the tier-2 outcome for closes journaled
///   before realized R existed;
/// - closes (FX_EXIT with "closed #") Record tier-1 <c>RealizedR</c> when
///   present, else the tier-2 snapshot; unknown outcomes are counted, never
///   guessed (spec §1: never fabricate).
///
/// Unparsable lines, fills without a family, and closes without a matching
/// fill are SKIPPED — the tape degrades to fewer samples, never to
/// mis-binned ones. Never throws: an unreadable journal reseeds nothing
/// (the filter then default-keeps, the safe direction).
/// </summary>
public static class FxRecentTapeReseed
{
    public sealed record Result(int FillsSeen, int ClosesRecorded, int ClosesNoOutcome, int Skipped);

    public static Result Run(string journalDir, FxRecentTape tape)
    {
        var fills = new Dictionary<long, (string Symbol, string Family)>();
        var lastR = new Dictionary<long, double>();
        var fillsSeen = 0;
        var closesRecorded = 0;
        var closesNoOutcome = 0;
        var skipped = 0;

        try
        {
            if (!Directory.Exists(journalDir))
            {
                return new Result(0, 0, 0, 0);
            }

            foreach (var file in Directory.GetFiles(journalDir, "journal_*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
            {
                foreach (var line in Logging.TradeJournal.ReadLinesShared(file))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(line);
                    }
                    catch (JsonException)
                    {
                        skipped++;
                        continue;
                    }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        var category = TryString(root, "Category");
                        var details = TryString(root, "Details");
                        if (category is null || details is null)
                        {
                            continue;
                        }

                        var brace = details.IndexOf('{');
                        var text = brace < 0 ? details : details[..brace];
                        var payload = ParsePayload(details, brace);

                        if (category == "FX_ORDER" && (details.Contains("fill") || details.Contains("ticket ")))
                        {
                            var fill = ParseFill(text, payload);
                            if (fill is { } f && f.Family.Length > 0)
                            {
                                fills[f.Ticket] = (f.Symbol, f.Family);
                                fillsSeen++;
                            }
                            else
                            {
                                skipped++;
                            }
                        }
                        else if (category == "FX_EXIT" && text.Contains("closed #", StringComparison.Ordinal))
                        {
                            var ticket = ParseClosedTicket(text);
                            if (ticket != 0 && fills.TryGetValue(ticket, out var fill))
                            {
                                double? r = null;
                                // Tier 1: a numeric RealizedR on the close
                                // row (null serializes as JSON null → falls
                                // through to the tier-2 snapshot below).
                                if (payload is { } p && p.TryGetProperty("RealizedR", out var rr)
                                    && rr.ValueKind == JsonValueKind.Number)
                                {
                                    r = rr.GetDouble();
                                }
                                // Tier 2: the last hold/progress R for this
                                // ticket (TryGetValue — a missing key must
                                // stay null, never default to 0R).
                                if (r is null && lastR.TryGetValue(ticket, out var snapshotted))
                                {
                                    r = snapshotted;
                                }

                                if (r is { } settled)
                                {
                                    tape.Record(fill.Symbol, fill.Family, settled);
                                    closesRecorded++;
                                }
                                else
                                {
                                    closesNoOutcome++;
                                }
                                fills.Remove(ticket);
                                lastR.Remove(ticket);
                            }
                            else
                            {
                                skipped++;
                            }
                        }
                        else if (payload is { } pay)
                        {
                            // Hold/progress rows keep the last known R per
                            // ticket — tier-2 outcome for legacy closes.
                            if (pay.TryGetProperty("ProfitR", out var pr) && pr.ValueKind == JsonValueKind.Number
                                && pay.TryGetProperty("Ticket", out var pt) && pt.ValueKind == JsonValueKind.Number)
                            {
                                lastR[pt.GetInt64()] = pr.GetDouble();
                            }
                            else if (pay.TryGetProperty("CurrentR", out var cr) && cr.ValueKind == JsonValueKind.Number
                                && pay.TryGetProperty("Ticket", out var ct) && ct.ValueKind == JsonValueKind.Number)
                            {
                                lastR[ct.GetInt64()] = cr.GetDouble();
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Unreadable journal: reseed nothing — default-keep is safe.
        }

        return new Result(fillsSeen, closesRecorded, closesNoOutcome, skipped);
    }

    private static string? TryString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement? ParsePayload(string details, int brace)
    {
        if (brace < 0)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(details[brace..]);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fill: prefix like "buy 0.1 lots XAUUSDmicro @ 4104.51 —
    /// ticket 8792169811" (paper shape: "paper-exec fill (demo): …"). The
    /// payload carries the family under "Signal".</summary>
    private static (long Ticket, string Symbol, string Family)? ParseFill(string text, JsonElement? payload)
    {
        var ticketMarker = text.LastIndexOf("ticket ", StringComparison.Ordinal);
        if (ticketMarker < 0 || !TryParseDigits(text[(ticketMarker + 7)..], out long ticket) || ticket == 0)
        {
            return null;
        }

        var lotsMarker = text.IndexOf(" lots ", StringComparison.Ordinal);
        if (lotsMarker < 0)
        {
            return null;
        }
        var symStart = lotsMarker + 6;
        var symEnd = symStart;
        while (symEnd < text.Length && text[symEnd] != ' ')
        {
            symEnd++;
        }
        if (symEnd == symStart)
        {
            return null;
        }
        var symbol = text[symStart..symEnd];

        var family = payload is { } p && p.TryGetProperty("Signal", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : "";
        if (family.Length == 0)
        {
            return null;
        }

        return (ticket, symbol, family);
    }

    /// <summary>"closed #9820129160 — …" → the N (0 when absent).</summary>
    private static long ParseClosedTicket(string text)
    {
        var marker = text.IndexOf("closed #", StringComparison.Ordinal);
        return marker < 0 || !TryParseDigits(text[(marker + 8)..], out var ticket) ? 0 : ticket;
    }

    private static bool TryParseDigits(string span, out long value)
    {
        value = 0;
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end]))
        {
            end++;
        }
        return end > 0 && long.TryParse(span[..end], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
