using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Weekly FX_EXIT digest: rolls the last 7 days of the exit brain's journal
/// entries into one evidence summary — the override-vs-consensus split, the
/// exit-reason distribution, the MAE-at-exit curve, and the round-trip
/// frequency — plus the shadow engines' promotion-ledger rollup. Posts to
/// the webhook (same channel as settlements) and appends markdown to the
/// soak evidence doc, where committing evidence stays a deliberate human
/// act. Journal-only by construction: it reads and posts, never trades.
/// Guardrails mirror <see cref="FxLabWeeklyDigest"/>: timer + Disabled + a
/// busy latch, and every failure is swallowed, never a crash. Silence with
/// no FX_EXIT entries is correct, not an error.
/// </summary>
public sealed class FxExitWeeklyDigest : IDisposable
{
    /// <summary>Time between digests. Default: weekly. Tests shrink this.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Delay before the first digest.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>True disables the service entirely (no timer, no posts).</summary>
    public bool Disabled { get; set; }

    /// <summary>Markdown file the digest appends to. Null (or a missing
    /// parent dir) skips the append leg — the webhook still posts.</summary>
    public string? SoakDocPath { get; set; }

    /// <summary>Shadow-ledger JSONL path. When set, the digest appends the
    /// per-engine promotion rollup (trades observed, hit rate, earned
    /// weight). Null skips that leg.</summary>
    public string? LedgerPath { get; set; }

    /// <summary>Override for tests: the entries the digest sees. Null =
    /// read the real journal.</summary>
    public Func<IReadOnlyList<JournalEntry>>? EntriesOverride { get; set; }

    /// <summary>Override for tests: the shadow-ledger rows. Null = read
    /// LedgerPath when set.</summary>
    public Func<IReadOnlyList<FxShadowLedgerRow>>? LedgerOverride { get; set; }

    private readonly TradeJournal _journal;
    private readonly WebhookService? _webhook;
    private System.Threading.Timer? _timer;
    private int _busy;

    public FxExitWeeklyDigest(TradeJournal journal, WebhookService? webhook = null)
    {
        _journal = journal;
        _webhook = webhook;
    }

    /// <summary>Starts the weekly loop.</summary>
    public void Start()
    {
        if (Disabled)
        {
            return;
        }

        _timer = new System.Threading.Timer(
            _ => _ = RunOnceAsync(), null, InitialDelay, Interval);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One digest pass: read, build, post, append. Returns the
    /// summary text (empty when there was nothing to report).</summary>
    public async Task<string> RunOnceAsync()
    {
        if (Disabled || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return string.Empty;
        }

        try
        {
            var entries = EntriesOverride?.Invoke() ?? _journal.GetRecent(null, 20_000);
            var (message, markdown) = Build(entries, DateTimeOffset.UtcNow);

            // The shadow engines' promotion rollup rides along: how many
            // settled trades each weight-0 engine has observed, its hit
            // rate, and the weight that accuracy has (not yet) earned.
            var ledger = LedgerOverride?.Invoke() ?? ReadLedger();
            if (ledger.Count > 0)
            {
                var lines = ledger.Select(r =>
                    $"{r.Engine}: {r.Trades} settled trade(s), hit {r.HitRate:P0}" +
                    (r.SuggestedWeight > 0
                        ? $" — EARNED weight {r.SuggestedWeight:0.##} (ship after the Monte-Carlo check)"
                        : " — observation only"));
                var section = "Shadow engines: " + string.Join("; ", lines);
                message = message.Length == 0 ? section : $"{message} {section}";
                markdown += "\n### Shadow engines (promotion ledger)\n\n" +
                            string.Join("\n", lines.Select(l => $"- {l}")) + "\n";
            }

            if (message.Length == 0)
            {
                return string.Empty;
            }

            _webhook?.PostStatus("FX exit weekly digest", message);

            if (SoakDocPath is { } path
                && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            {
                // The standalone chart lives next to the soak doc and is
                // referenced relatively — the pair travels together.
                var svgWritten = WriteCaptureTrendSvg(entries, dir) is not null
                    ? "\n![profit-capture trend](fx-capture-trend.svg)\n"
                    : string.Empty;
                await File.AppendAllTextAsync(
                    path, markdown + svgWritten, Encoding.UTF8).ConfigureAwait(false);
            }

            return message;
        }
        catch
        {
            // A digest is an observability nicety — never a crash surface.
            return string.Empty;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private IReadOnlyList<FxShadowLedgerRow> ReadLedger()
    {
        if (LedgerPath is not { } path)
        {
            return [];
        }

        try
        {
            return new FxShadowLedger(path).Summarize();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>One decoded FX_EXIT journal payload.</summary>
    internal sealed record ExitPayload(
        string Action, string? Override, double MaeR, double MfeR, double ProfitR,
        IReadOnlyList<(string Engine, double Exit, double Weight)> Votes);

    /// <summary>Pure builder: the last 7 days of FX_EXIT entries in,
    /// webhook message + markdown append out. Empty strings = nothing to
    /// report. The split: hard overrides (bridge, spread, equity-floor,
    /// drawdown) are counted separately from the consensus band actions —
    /// how often safety had to outrank the vote is itself evidence.</summary>
    internal static (string Message, string Markdown) Build(
        IReadOnlyList<JournalEntry> entries, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(7);
        var payloads = new List<ExitPayload>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_EXIT" || e.Timestamp < cutoff)
            {
                continue;
            }

            var p = Decode(e.Details);
            if (p is { } payload)
            {
                payloads.Add(payload);
            }
        }

        if (payloads.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        // Order-confirmation lines ("closed #N — deal …") share the FX_EXIT
        // category but carry no Action: they are bookkeeping, not brain
        // evaluations, so they never enter the override/consensus split —
        // they only count in the confirmation tally in the markdown.
        var evaluations = payloads.Where(p => !string.IsNullOrEmpty(p.Action)).ToList();
        var closeConfirmed = payloads.Count - evaluations.Count;

        // The split: override engines (hard safety) vs consensus bands.
        string[] overrideEngines = ["bridge", "spread", "equity-floor", "drawdown"];
        var overrides = evaluations.Where(p => p.Override is { }).ToList();
        var consensus = evaluations.Where(p => p.Override is null).ToList();
        var overrideByEngine = overrides
            .GroupBy(p => p.Override ?? "?")
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ×{g.Count()}");
        var consensusByAction = consensus
            .GroupBy(p => p.Action)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ×{g.Count()}");

        // Exit-reason distribution: for decisive exits (full/partial and
        // overrides), the leading engine is the heaviest exit × weight voice.
        var decisive = evaluations
            .Where(p => p.Action is "full" or "partial" || p.Override is not null)
            .ToList();
        var reasons = decisive
            .Select(p => p.Override is { } o
                ? o
                : p.Votes.Where(v => v.Weight > 0)
                    .OrderByDescending(v => v.Exit * v.Weight)
                    .FirstOrDefault().Engine ?? "none")
            .Where(r => r != "none")
            .GroupBy(r => r, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ×{g.Count()}");

        // MAE-at-exit curve: how deep trades were against us when the brain
        // finally acted (decisive exits only — monitor rows are noise here).
        var maeBuckets = new (string Label, Func<double, bool> In)[]
        {
            ("<0.5R", m => m < 0.5),
            ("0.5–1R", m => m >= 0.5 && m < 1.0),
            ("1–1.6R", m => m >= 1.0 && m < FxExitBrain.MaeEmergencyR),
            ($"≥{FxExitBrain.MaeEmergencyR:0.#}R", m => m >= FxExitBrain.MaeEmergencyR),
        };
        var maeCurve = maeBuckets
            .Select(b => $"{b.Label} ×{decisive.Count(p => b.In(p.MaeR))}");

        // Round-trip frequency: paid ≥1R of MFE then closed ≤0.2R — the
        // "should have banked it" pattern the tighten trail exists to cut.
        var roundTrips = decisive.Count(p => p.MfeR >= 1.0 && p.ProfitR <= 0.2);
        var roundTripPct = decisive.Count > 0 ? (double)roundTrips / decisive.Count : 0;

        // Profit-capture ratio (the HWARANG headline metric, spec §33):
        // how much of the favorable excursion the brain actually banked.
        // Decisive exits with meaningful MFE only — sub-0.5R MFE is noise.
        var captureTrades = decisive.Where(p => p.MfeR >= 0.5).ToList();
        var captured = captureTrades.Sum(p => Math.Max(p.ProfitR, 0));
        var available = captureTrades.Sum(p => p.MfeR);
        var profitCapture = available > 0 ? captured / available : 0;

        // Profit Brain telemetry (FX_PROFIT advisory reports): state
        // distribution, floor breaches, average score. Empty until the
        // profit brain is live — silence is correct, not an error.
        var profitStates = new List<(string State, double Score, bool Breach)>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_PROFIT" || e.Timestamp < cutoff)
            {
                continue;
            }
            if (DecodeProfit(e.Details) is { } row)
            {
                profitStates.Add((row.State, row.Score, row.FloorBreached));
            }
        }

        var message =
            $"{evaluations.Count} exit evaluation(s) this week: {overrides.Count} hard override(s) " +
            $"({string.Join(", ", overrideByEngine)}) vs {consensus.Count} consensus " +
            $"({string.Join(", ", consensusByAction)}). Decisive exits {decisive.Count}, " +
            $"round-trips {roundTrips} ({roundTripPct:P0}), profit capture {profitCapture:P0}.";
        if (profitStates.Count > 0)
        {
            message += $" Profit brain: {profitStates.Count} report(s), " +
                       $"floor breached {profitStates.Count(p => p.Breach)}, " +
                       $"avg score {profitStates.Average(p => p.Score):0}.";
        }

        var markdown =
            $"\n\n## FX exit weekly digest — {now:yyyy-MM-dd}\n\n" +
            $"- evaluations: {evaluations.Count} (override {overrides.Count} / consensus {consensus.Count})\n" +
            (closeConfirmed > 0
                ? $"- close confirmations (no evaluation): {closeConfirmed}\n"
                : string.Empty) +
            $"- overrides: {string.Join(", ", overrideByEngine)}\n" +
            $"- consensus bands: {string.Join(", ", consensusByAction)}\n" +
            $"- exit reasons (decisive): {string.Join(", ", reasons)}\n" +
            $"- MAE at exit: {string.Join(", ", maeCurve)}\n" +
            $"- round-trips (MFE ≥1R, closed ≤0.2R): {roundTrips}/{decisive.Count} ({roundTripPct:P0})\n" +
            $"- profit capture (realized/MFE, decisive ≥0.5R MFE): {profitCapture:P0}\n" +
            CaptureTrendMarkdown(entries) +
            (profitStates.Count > 0
                ? $"\n### Profit brain (FX_PROFIT telemetry)\n\n" +
                  $"- reports: {profitStates.Count}; floor breaches: {profitStates.Count(p => p.Breach)}; " +
                  $"avg score: {profitStates.Average(p => p.Score):0}\n" +
                  $"- states: {string.Join(", ", profitStates.GroupBy(p => p.State)
                      .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"))}\n"
                : string.Empty);

        return (message, markdown);
    }

    /// <summary>The all-weeks capture trend as markdown: one line per ISO
    /// week plus a sparkline — the at-a-glance version of the SVG chart.
    /// Gaps dropped (a zero-trade week is absence of evidence, not 0%).
    /// </summary>
    internal static string CaptureTrendMarkdown(IReadOnlyList<JournalEntry> entries)
    {
        var series = WeeklyCaptureSeries(entries);
        if (series.Count == 0)
        {
            return "\n### Profit-capture trend\n\n- no decisive exits with meaningful MFE yet\n";
        }

        return "\n### Profit-capture trend\n\n" +
            "- weekly capture: " + string.Join(", ", series.Select(w =>
                $"{w.Label} {w.CaptureRatio * 100:0}% ({w.CapturedR:0.##}R of {w.AvailableR:0.##}R, {w.Trades} trade(s))")) + "\n" +
            "- spark: " + Sparkline(series.Select(w => w.CaptureRatio).ToList()) + "\n";
    }

    /// <summary>Decodes one FX_PROFIT Details line ("{summary}: {json}").
    /// Malformed lines are skipped, never thrown.</summary>
    internal sealed record ProfitRow(string State, double Score, bool FloorBreached);

    internal static ProfitRow? DecodeProfit(string details)
    {
        try
        {
            var brace = details.IndexOf('{');
            if (brace < 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(details[brace..]);
            var r = doc.RootElement;
            return new ProfitRow(
                r.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? "" : "",
                r.TryGetProperty("ProfitScore", out var ps) && ps.ValueKind == JsonValueKind.Number
                    ? ps.GetDouble() : 0,
                r.TryGetProperty("FloorBreached", out var b) && b.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One ISO week of profit-capture history: how much of the
    /// favorable excursion the brain banked that week (the HWARANG headline
    /// metric, spec §33) — the series the profit-floor tier must bend up
    /// over time.</summary>
    internal sealed record CaptureWeek(
        string Label, int Trades, double CapturedR, double AvailableR, double CaptureRatio);

    /// <summary>The profit-capture series over the WHOLE journal (not just
    /// the digest's 7-day window): one row per ISO week that saw at least
    /// one decisive exit with meaningful MFE. Ascending by week, gaps
    /// dropped — a zero-trade week is absence of evidence, not a 0% week.</summary>
    internal static IReadOnlyList<CaptureWeek> WeeklyCaptureSeries(IReadOnlyList<JournalEntry> entries)
    {
        var grouped = new SortedDictionary<(int Year, int Week), (int Trades, double Captured, double Available)>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_EXIT" || Decode(e.Details) is not { } p)
            {
                continue;
            }

            // Same population as the digest's capture metric: decisive
            // exits (full/partial or hard override) with MFE ≥ 0.5R —
            // order-confirmation rows and sub-noise MFE never count.
            var decisive = p.Action is "full" or "partial" || p.Override is not null;
            if (!decisive || p.MfeR < 0.5)
            {
                continue;
            }

            var (year, week) = IsoWeek(e.Timestamp);
            var (t, c, a) = grouped.TryGetValue((year, week), out var g) ? g : (0, 0.0, 0.0);
            grouped[(year, week)] = (t + 1, c + Math.Max(p.ProfitR, 0), a + p.MfeR);
        }

        return grouped.Select(kv =>
        {
            var ((year, week), (trades, captured, available)) = kv;
            return new CaptureWeek(
                $"ISO {year}-W{week:00}", trades, captured, available,
                available > 0 ? captured / available : 0);
        }).ToList();
    }

    /// <summary>ISO-8601 week number (weeks start Monday, W01 holds the
    /// first Thursday) with the year the WEEK belongs to. Implemented via
    /// the week's Thursday — GetWeekOfYear(FirstFourDayWeek) does NOT
    /// implement ISO (it answers 53 where ISO says 1 around year ends),
    /// and the Thursday's own year resolves the Dec/Jan boundaries.</summary>
    internal static (int Year, int Week) IsoWeek(DateTimeOffset t)
    {
        var d = t.UtcDateTime.Date;
        var isoDay = d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
        var thursday = d.AddDays(4 - isoDay);   // the ISO week's Thursday
        var year = thursday.Year;
        var week = (thursday.DayOfYear - 1) / 7 + 1;
        return (year, week);
    }

    /// <summary>Unicode block sparkline of a value series (▁ lowest … █
    /// highest). Flat series render full-height; empty renders empty.</summary>
    internal static string Sparkline(IReadOnlyList<double> values)
    {
        const string blocks = "▁▂▃▄▅▆▇█";
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var min = values.Min();
        var max = values.Max();
        return string.Concat(values.Select(v =>
            max - min < 1e-9
                ? blocks[^1]
                : blocks[Math.Clamp((int)Math.Round((v - min) / (max - min) * (blocks.Length - 1)), 0, blocks.Length - 1)]));
    }

    /// <summary>Writes the standalone capture-trend chart (self-contained
    /// SVG) into <paramref name="directory"/> as <c>fx-capture-trend.svg</c>.
    /// Returns the path, or null when there is no series or the write
    /// failed — an observability nicety is never a crash surface.</summary>
    internal static string? WriteCaptureTrendSvg(IReadOnlyList<JournalEntry> entries, string directory)
    {
        try
        {
            var weeks = WeeklyCaptureSeries(entries);
            if (weeks.Count == 0)
            {
                return null;
            }

            var path = Path.Combine(directory, "fx-capture-trend.svg");
            File.WriteAllText(path, CaptureTrendSvg(weeks), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The weekly profit-capture chart: one bar per ISO week,
    /// labeled with the capture ratio, the R banked of R available, and the
    /// trade count. Self-contained inline styles — renders in any viewer
    /// the soak doc travels with.</summary>
    internal static string CaptureTrendSvg(IReadOnlyList<CaptureWeek> weeks)
    {
        if (weeks.Count == 0)
        {
            return string.Empty;
        }

        const double W = 640, H = 260, Left = 46, Right = 16, Top = 36, Bottom = 46;
        var plotW = W - Left - Right;
        var plotH = H - Top - Bottom;
        var slot = Math.Min(64, plotW / weeks.Count);
        var barW = slot * 0.55;
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W.ToString(inv)}\" height=\"{H.ToString(inv)}\" viewBox=\"0 0 {W.ToString(inv)} {H.ToString(inv)}\">");
        sb.Append($"<rect width=\"{W.ToString(inv)}\" height=\"{H.ToString(inv)}\" fill=\"#fafafa\"/>");
        sb.Append($"<text x=\"{Left.ToString(inv)}\" y=\"20\" font-family=\"monospace\" font-size=\"13\" fill=\"#333\">profit capture — realized/MFE per ISO week</text>");

        // Grid: 0/25/50/75/100%.
        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            var y = Top + plotH * (1 - pct / 100.0);
            sb.Append($"<line x1=\"{Left.ToString(inv)}\" y1=\"{y.ToString(inv)}\" x2=\"{(W - Right).ToString(inv)}\" y2=\"{y.ToString(inv)}\" stroke=\"#dddddd\"/>");
            sb.Append($"<text x=\"4\" y=\"{(y + 3).ToString(inv)}\" font-family=\"monospace\" font-size=\"9\" fill=\"#999\">{pct.ToString(inv)}%</text>");
        }

        for (var i = 0; i < weeks.Count; i++)
        {
            var wk = weeks[i];
            var x = Left + plotW * (i + 0.5) / weeks.Count - barW / 2;
            var barH = plotH * Math.Clamp(wk.CaptureRatio, 0, 1);
            var y = Top + plotH - barH;
            sb.Append($"<rect x=\"{x.ToString(inv)}\" y=\"{y.ToString(inv)}\" width=\"{barW.ToString(inv)}\" height=\"{barH.ToString(inv)}\" fill=\"#2e7d32\"/>");
            sb.Append($"<text x=\"{((x + barW / 2)).ToString(inv)}\" y=\"{(y - 5).ToString(inv)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"10\" fill=\"#333\">{(wk.CaptureRatio * 100).ToString("0", inv)}%</text>");
            sb.Append($"<text x=\"{((x + barW / 2)).ToString(inv)}\" y=\"{(Top + plotH + 16).ToString(inv)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"9\" fill=\"#555\">{wk.Label}</text>");
            sb.Append($"<text x=\"{((x + barW / 2)).ToString(inv)}\" y=\"{(Top + plotH + 29).ToString(inv)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"8\" fill=\"#999\">{wk.CapturedR.ToString("0.##", inv)}R/{wk.AvailableR.ToString("0.##", inv)}R · {wk.Trades} trade(s)</text>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Decodes one FX_EXIT Details line ("{summary}: {json}").
    /// Malformed or legacy lines are skipped, never thrown.</summary>
    internal static ExitPayload? Decode(string details)
    {
        try
        {
            var brace = details.IndexOf('{');
            if (brace < 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(details[brace..]);
            var r = doc.RootElement;

            double Num(string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetDouble() : 0;

            var votes = new List<(string Engine, double Exit, double Weight)>();
            if (r.TryGetProperty("Votes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                {
                    votes.Add((
                        v.TryGetProperty("Engine", out var en) ? en.GetString() ?? "" : "",
                        v.TryGetProperty("Exit", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetDouble() : 0,
                        v.TryGetProperty("Weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0));
                }
            }

            return new ExitPayload(
                r.TryGetProperty("Action", out var a) ? a.GetString() ?? "" : "",
                r.TryGetProperty("Override", out var o) && o.ValueKind == JsonValueKind.String
                    ? o.GetString() : null,
                Num("MaeR"), Num("MfeR"), Num("ProfitR"),
                votes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
