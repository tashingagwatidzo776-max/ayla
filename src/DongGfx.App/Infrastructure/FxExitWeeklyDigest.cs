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

    /// <summary>TP1 graded-verdict ledger path (the watcher's
    /// <c>tp1-graded-verdicts.jsonl</c>). When set, each banked rung's line
    /// carries the graded verdict and its R versus the giveback baseline.
    /// Null skips the annotation. The verdicts are read, not recomputed, so
    /// the digest reports the exact arithmetic the watcher graded with.</summary>
    public string? Tp1VerdictsPath { get; set; }

    /// <summary>Override for tests: the graded verdicts keyed by ticket.
    /// Null = read Tp1VerdictsPath when set.</summary>
    public Func<IReadOnlyDictionary<long, Tp1GradedVerdict>>? Tp1VerdictsOverride { get; set; }

    /// <summary>TP1 plan-% review ledger path (the watcher's append-only
    /// <c>tp1-plan-reviews.jsonl</c>). When set, the digest summarizes the
    /// recommendations made since the last post and whether they were
    /// open, cleared, or acted on. Null skips the section.</summary>
    public string? PlanReviewsPath { get; set; }

    /// <summary>Override for tests: the plan-review rows. Null = read
    /// PlanReviewsPath when set.</summary>
    public Func<IReadOnlyList<PlanReview>>? PlanReviewsOverride { get; set; }

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
            var tp1Verdicts = Tp1VerdictsOverride?.Invoke() ?? ReadTp1Verdicts();
            var (message, markdown) = Build(entries, DateTimeOffset.UtcNow, tp1Verdicts);

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

            // The plan-% review ledger: how many recommendations the gate
            // made since the last post and what came of them — a weekly
            // audit that the human review step actually happened.
            var planReviews = PlanReviewsOverride?.Invoke() ?? ReadPlanReviews();
            if (planReviews.Count > 0)
            {
                var (reviewLine, reviewSection) =
                    PlanReviewSummary(planReviews, DateTimeOffset.UtcNow);
                if (reviewSection.Length > 0)
                {
                    message = message.Length == 0
                        ? reviewLine : $"{message} {reviewLine}";
                    markdown += reviewSection;
                }
            }

            if (message.Length == 0)
            {
                return string.Empty;
            }

            // The giveback engine's promotion review: when one of its
            // reassessment conditions is met (the FIRST verified save, or
            // 30 settled trades — docs/soak/GIVEBACK-PROMOTION-CASE.md),
            // the weekly digest auto-posts the case's current standing so
            // the reassessment actually happens instead of being remembered.
            var review = PromotionReview(entries);
            if (review is not null)
            {
                message = message.Length == 0
                    ? review
                    : $"{message} {review}";
                markdown += "\n### Giveback engine — promotion review triggered\n\n" +
                            review + "\n";
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

    private IReadOnlyList<PlanReview> ReadPlanReviews()
    {
        if (PlanReviewsPath is not { } path)
        {
            return [];
        }

        try
        {
            return ReadPlanReviewEvents(path);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Parses the append-only plan-review ledger and folds it into
    /// one row per recommendation, in order. Malformed lines are skipped;
    /// a missing file is the caller's concern.</summary>
    internal static IReadOnlyList<PlanReview> ReadPlanReviewEvents(string path)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, PlanReview>();
        foreach (var line in Core.Logging.TradeJournal.ReadLinesShared(path))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var ev = Str(r, "event");
                var id = Str(r, "id");
                if (ev == "recommended")
                {
                    if (id.Length == 0 || TryTime(r, "ts") is not { } ts)
                    {
                        continue;
                    }

                    byId[id] = new PlanReview(
                        id, Str(r, "direction"), ts,
                        NumberOrNull(r, "baseline_pct"),
                        NumberOrNull(r, "candidate_pct"),
                        NumberOrNull(r, "net_r"), "open", null, null, "",
                        NumberOrNull(r, "mean_vs_giveback_r"),
                        AsInt(NumberOrNull(r, "graded")),
                        AsInt(NumberOrNull(r, "trailed")),
                        AsInt(NumberOrNull(r, "beaten")));
                    order.Add(id);
                }
                else if (byId.TryGetValue(id, out var rec))
                {
                    if (ev == "cleared" && rec.Status == "open")
                    {
                        byId[id] = rec with
                        {
                            Status = "cleared",
                            ClearedAt = TryTime(r, "ts"),
                        };
                    }
                    else if (ev == "acted")
                    {
                        byId[id] = rec with
                        {
                            Status = "acted",
                            ActedAt = TryTime(r, "ts"),
                            ActedNote = Str(r, "note"),
                        };
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed event — skip it, keep the rest.
            }
        }

        return order.Select(id => byId[id]).ToList();
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static DateTimeOffset? TryTime(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var t)
            ? t : null;

    /// <summary>The plan-% review rollup since the last post (the digest's
    /// 7-day window): how many recommendations the gate made and how many
    /// are still open, cleared, or acted on, plus one line per review —
    /// the weekly audit of the whole gate-to-action path.</summary>
    internal static (string Message, string Markdown) PlanReviewSummary(
        IReadOnlyList<PlanReview> reviews, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(7);
        var recent = reviews.Where(r => r.Ts >= cutoff).OrderBy(r => r.Ts).ToList();
        if (recent.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var open = recent.Count(r => r.Status == "open");
        var cleared = recent.Count(r => r.Status == "cleared");
        var acted = recent.Count(r => r.Status == "acted");
        var message =
            $"TP1 plan-% reviews since the last post: {recent.Count} — " +
            $"{open} open, {cleared} cleared, {acted} acted.";
        var lines = recent.Select(r =>
        {
            var plan = r.BaselinePct is { } b && r.CandidatePct is { } c
                ? $"{b:0.#}%→{c:0.#}%" : "—";
            var net = r.NetR is { } n ? $"{n:+0.00;-0.00}R" : "—";
            var note = r.Status == "acted" && r.ActedNote.Length > 0
                ? $" ({r.ActedNote})" : string.Empty;
            return $"- {r.Ts:yyyy-MM-dd} {r.Direction} {plan} (net {net}) " +
                   $"— {r.Status}{note}";
        });
        var markdown = "\n### TP1 plan-% reviews (auto)\n\n" +
            $"- since the last post: {recent.Count} recommended — {open} open, " +
            $"{cleared} cleared, {acted} acted\n" +
            string.Join("\n", lines) + "\n";
        return (message, markdown);
    }

    private IReadOnlyDictionary<long, Tp1GradedVerdict> ReadTp1Verdicts()
    {
        if (Tp1VerdictsPath is not { } path)
        {
            return new Dictionary<long, Tp1GradedVerdict>();
        }

        try
        {
            return ReadVerdicts(path);
        }
        catch
        {
            return new Dictionary<long, Tp1GradedVerdict>();
        }
    }

    /// <summary>Parses the watcher's verdict ledger (one JSON object per
    /// banked ticket) into a ticket-keyed map. Malformed lines are skipped;
    /// a missing file is the caller's concern.</summary>
    internal static IReadOnlyDictionary<long, Tp1GradedVerdict> ReadVerdicts(string path)
    {
        var verdicts = new Dictionary<long, Tp1GradedVerdict>();
        foreach (var line in Core.Logging.TradeJournal.ReadLinesShared(path))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (!r.TryGetProperty("ticket", out var t) || !t.TryGetInt64(out var ticket))
                {
                    continue;
                }

                var verdict = r.TryGetProperty("verdict", out var v)
                    && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? string.Empty : string.Empty;
                verdicts[ticket] = new Tp1GradedVerdict(
                    verdict, NumberOrNull(r, "vs_giveback_r"),
                    NumberOrNull(r, "capture_pct"));
            }
            catch (JsonException)
            {
                // Malformed line — skip it, keep the rest.
            }
        }

        return verdicts;
    }

    private static double? NumberOrNull(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    private static int? AsInt(double? value) =>
        value is { } v ? (int)v : null;

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

    /// <summary>One plan-% review, folded from the watcher's append-only
    /// ledger event stream: the recommendation plus its lifecycle status
    /// (open / cleared / acted).</summary>
    public sealed record PlanReview(
        string Id, string Direction, DateTimeOffset Ts,
        double? BaselinePct, double? CandidatePct, double? NetR, string Status,
        DateTimeOffset? ClearedAt, DateTimeOffset? ActedAt, string ActedNote,
        double? MeanVsGivebackR = null, int? Graded = null,
        int? Trailed = null, int? Beaten = null);

    /// <summary>One banked rung's graded verdict, as emitted by
    /// scripts/watch_tp1_first_arm.py to the verdict ledger — the same
    /// arithmetic the digest's capture line mirrors, read rather than
    /// recomputed.</summary>
    public sealed record Tp1GradedVerdict(
        string Verdict, double? VsGivebackR, double? CapturePct);

    /// <summary>One decoded FX_EXIT journal payload.</summary>
    internal sealed record ExitPayload(
        string Action, string? Override, double MaeR, double MfeR, double ProfitR,
        IReadOnlyList<(string Engine, double Exit, double Weight)> Votes,
        long Ticket = 0);

    /// <summary>Pure builder: the last 7 days of FX_EXIT entries in,
    /// webhook message + markdown append out. Empty strings = nothing to
    /// report. The split: hard overrides (bridge, spread, equity-floor,
    /// drawdown) are counted separately from the consensus band actions —
    /// how often safety had to outrank the vote is itself evidence.</summary>
    internal static (string Message, string Markdown) Build(
        IReadOnlyList<JournalEntry> entries, DateTimeOffset now,
        IReadOnlyDictionary<long, Tp1GradedVerdict>? tp1Verdicts = null)
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
            Tp1CaptureMarkdown(entries, tp1Verdicts) +
            (profitStates.Count > 0
                ? $"\n### Profit brain (FX_PROFIT telemetry)\n\n" +
                  $"- reports: {profitStates.Count}; floor breaches: {profitStates.Count(p => p.Breach)}; " +
                  $"avg score: {profitStates.Average(p => p.Score):0}\n" +
                  $"- states: {string.Join(", ", profitStates.GroupBy(p => p.State)
                      .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"))}\n"
                : string.Empty) +
            // Every digest points newcomers at the full evidence arc:
            // one stable path, one line, indexed once in the soak README.
            "\n### Evidence index\n\n" +
            "- promotion cases, incidents, and week summaries: docs/soak/README.md\n";

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

    /// <summary>The TP1 prototype's grading section: for every ticket that
    /// BANKED a rung (FX_PROFIT TP1-EXEC with Executed true), compares its
    /// final capture (realized ÷ MFE from the settlement FX_EXIT row)
    /// against the fleet capture ratio of non-TP1 decisive exits — the
    /// rung-graded view beside the fleet number. Silent until the armed
    /// prototype produces rows; malformed rows never crash the digest.
    /// One line per graded ticket (latest EXEC per ticket), oldest first.
    /// </summary>
    internal static string Tp1CaptureMarkdown(
        IReadOnlyList<JournalEntry> entries,
        IReadOnlyDictionary<long, Tp1GradedVerdict>? verdicts = null)
    {
        var execTickets = new Dictionary<long, string>();   // ticket → exec summary
        foreach (var e in entries)
        {
            if (e.Category != "FX_PROFIT" || !e.Details.Contains("TP1-EXEC"))
            {
                continue;
            }

            var brace = e.Details.IndexOf('{');
            if (brace < 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(e.Details[brace..]);
                var r = doc.RootElement;
                if (r.TryGetProperty("Ticket", out var t) && t.TryGetInt64(out var ticket)
                    && r.TryGetProperty("Executed", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    execTickets[ticket] = e.Details[..brace].Trim();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (execTickets.Count == 0)
        {
            return string.Empty;   // silent until the prototype banks a rung
        }

        // The settled picture per TP1 ticket: its decisive FX_EXIT rows
        // (a banked rung followed by a full close produces settlement
        // evidence with the ticket's final MFE/realized R).
        var lines = new List<string>();
        var fleetWithoutTp1 = new List<(double Realized, double Mfe)>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_EXIT" || Decode(e.Details) is not { } p
                || p.Ticket <= 0 || p.MfeR < 0.5)
            {
                continue;
            }

            if (execTickets.ContainsKey(p.Ticket))
            {
                var graded = verdicts is { } map
                    && map.TryGetValue(p.Ticket, out var g) ? g : null;
                lines.Add($"- #{p.Ticket}: banked a rung, captured " +
                          $"{p.ProfitR:0.##}R of its {p.MfeR:0.##}R peak " +
                          $"({(p.MfeR > 0 ? p.ProfitR / p.MfeR : 0) * 100:0}% capture)" +
                          VerdictSuffix(graded));
            }
            else
            {
                fleetWithoutTp1.Add((p.ProfitR, p.MfeR));
            }
        }

        var fleet = fleetWithoutTp1.Count > 0
            ? fleetWithoutTp1.Sum(f => f.Realized) / fleetWithoutTp1.Sum(f => f.Mfe)
            : 0;
        var head = $"\n### TP1 banked-run capture\n\n" +
                   $"- fleet capture WITHOUT a TP1 rung (this window): {fleet * 100:0}% " +
                   $"({fleetWithoutTp1.Count} decisive exit(s))\n";
        return lines.Count > 0 ? head + string.Join("\n", lines) + "\n" : string.Empty;
    }

    /// <summary>The graded verdict trailing a banked-rung line: the verdict
    /// and its R versus the giveback baseline, or '' when the ticket has no
    /// graded verdict (the ledger was empty or predates the rung).</summary>
    private static string VerdictSuffix(Tp1GradedVerdict? v)
    {
        if (v is null || v.Verdict.Length == 0)
        {
            return string.Empty;
        }

        return v.VsGivebackR is { } d
            ? $" — {v.Verdict} ({d:+0.00;-0.00}R vs giveback)"
            : $" — {v.Verdict}";
    }

    /// <summary>The giveback engine's promotion review: when one of its
    /// reassessment conditions is met (docs/soak/GIVEBACK-PROMOTION-CASE.md),
    /// returns the review text; otherwise null — silence is correct, not an
    /// error. Conditions: (a) at least one VERIFIED profit-floor save has
    /// settled (an override or a decisive deep giveback vote followed by a
    /// close confirmation for the same ticket), or (b) 30+ decisive exits
    /// have settled overall. Read-only over the journal; fires every week
    /// while the condition holds — the weekly cadence IS the throttle.</summary>
    internal static string? PromotionReview(IReadOnlyList<JournalEntry> entries)
    {
        var saveKinds = new Dictionary<long, string>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_EXIT" || Decode(e.Details) is not { } p || p.Ticket == 0)
            {
                continue;
            }

            var isOverride = p.Override == "profit-floor";
            var isDeepVote = !isOverride
                && p.Action is "full" or "partial"
                && p.Votes.Any(v => v.Engine == "drawdown" && v.Exit >= 0.85);
            if (isOverride || isDeepVote)
            {
                saveKinds[p.Ticket] = isOverride ? "profit-floor override" : "deep giveback vote";
            }
        }

        long? savedTicket = null;
        foreach (var e in entries)
        {
            if (e.Category != "FX_EXIT")
            {
                continue;
            }
            foreach (var (ticket, kind) in saveKinds)
            {
                if (e.Details.Contains($"closed #{ticket}"))
                {
                    savedTicket ??= ticket;
                    _ = kind;
                }
            }
        }

        var decided = entries.Count(e => e.Category == "FX_EXIT"
            && Decode(e.Details) is { } p2 && p2.Ticket != 0
            && (p2.Action is "full" or "partial" || p2.Override is not null));

        if (savedTicket is null && decided < 30)
        {
            return null;
        }

        var saved = savedTicket is { } st
            ? $"first verified save: ticket {st} ({saveKinds[st]})"
            : "no verified save yet";
        return $"PROMOTION REVIEW — decisive settled exits {decided}/100, {saved}. " +
               "Reassess docs/soak/GIVEBACK-PROMOTION-CASE.md (giveback engine: " +
               "weight 0 until 100 trades @ 60% hit, Monte-Carlo STABLE).";
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
            File.WriteAllText(path, CaptureTrendSvg(
                weeks, WeeklySaves(entries, weeks), WeeklyTp1Graded(entries, weeks)), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves per ISO week, aligned with <paramref name="weeks"/>
    /// (index i is the saves count for weeks[i]). A save is ONE distinct
    /// (UTC day, ticket) event from either evidence family: an FX_EXIT row
    /// carrying the profit-floor override or a deep (≥0.85) giveback
    /// drawdown vote, or an FX_FLOOR "PROFIT FLOOR EXIT SUBMITTED" row
    /// (the hard floor's command). Retries, re-evaluations and the same
    /// ticket's confirmation never double-count.</summary>
    internal static IReadOnlyList<int> WeeklySaves(IReadOnlyList<JournalEntry> entries, IReadOnlyList<CaptureWeek> weeks)
    {
        var seen = new HashSet<(string Label, long Ticket)>
        ();
        foreach (var e in entries)
        {
            if ((TryGivebackOverrideSave(e, out var ticket)
                    || TryGuardSave(e, out ticket))
                && ticket > 0)
            {
                var (year, week) = IsoWeek(e.Timestamp);
                seen.Add((FormattableString.Invariant($"ISO {year}-W{week:00}"), ticket));
            }
        }

        return weeks.Select(w => seen.Count(s => s.Label == w.Label)).ToList();
    }

    /// <summary>TP1-graded tickets per ISO week, aligned with
    /// <paramref name="weeks"/> (index i is the count for weeks[i]). A
    /// graded ticket is a banked rung (an FX_PROFIT TP1-EXEC row with
    /// Executed true), counted once per (week, ticket) — the chart's blue
    /// outline makes the weeks the TP1 prototype actually engaged
    /// stand out beside the captures they produced.</summary>
    internal static IReadOnlyList<int> WeeklyTp1Graded(
        IReadOnlyList<JournalEntry> entries, IReadOnlyList<CaptureWeek> weeks)
    {
        var seen = new HashSet<(string Label, long Ticket)>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_PROFIT" || !e.Details.Contains("TP1-EXEC"))
            {
                continue;
            }

            var brace = e.Details.IndexOf('{');
            if (brace < 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(e.Details[brace..]);
                var r = doc.RootElement;
                if (r.TryGetProperty("Ticket", out var t) && t.TryGetInt64(out var ticket)
                    && ticket > 0
                    && r.TryGetProperty("Executed", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    var (year, week) = IsoWeek(e.Timestamp);
                    seen.Add((FormattableString.Invariant($"ISO {year}-W{week:00}"), ticket));
                }
            }
            catch (JsonException)
            {
            }
        }

        return weeks.Select(w => seen.Count(s => s.Label == w.Label)).ToList();
    }

    /// <summary>An FX_EXIT row whose payload carries the profit-floor
    /// override or a deep giveback drawdown vote — the ensemble-side save
    /// evidence (mirrors FxExitBrain.IsProfitFloorSave's markers).</summary>
    private static bool TryGivebackOverrideSave(JournalEntry e, out long ticket)
    {
        ticket = 0;
        if (e.Category != "FX_EXIT")
        {
            return false;
        }

        var brace = e.Details.IndexOf('{');
        if (brace < 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.Details[brace..]);
            var r = doc.RootElement;
            if (r.TryGetProperty("Ticket", out var t) && t.TryGetInt64(out ticket)
                && r.TryGetProperty("Override", out var ov)
                && ov.ValueKind == JsonValueKind.String && ov.GetString() == "profit-floor")
            {
                return true;
            }

            if (r.TryGetProperty("Votes", out var votes) && votes.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in votes.EnumerateArray())
                {
                    if (v.TryGetProperty("Engine", out var en) && en.GetString() == "drawdown"
                        && v.TryGetProperty("Exit", out var ex) && ex.TryGetDouble(out var exit) && exit >= 0.85
                        && v.TryGetProperty("Reason", out var re) && re.GetString() is { } reason
                        && (reason.Contains("given back") || reason.Contains("give-back")))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>An FX_FLOOR "PROFIT FLOOR EXIT SUBMITTED" row — the hard
    /// floor's own command (the guard's save evidence). Ticket comes from
    /// the payload or the "#123:" summary prefix.</summary>
    private static bool TryGuardSave(JournalEntry e, out long ticket)
    {
        ticket = 0;
        if (e.Category != "FX_FLOOR" || !e.Details.Contains("PROFIT FLOOR EXIT SUBMITTED"))
        {
            return false;
        }

        var brace = e.Details.IndexOf('{');
        if (brace >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Details[brace..]);
                if (doc.RootElement.TryGetProperty("Ticket", out var t) && t.TryGetInt64(out ticket))
                {
                    return true;
                }
            }
            catch
            {
                // fall through to the summary prefix
            }
        }

        var hash = e.Details.IndexOf('#');
        if (hash >= 0)
        {
            var digits = new string(e.Details[(hash + 1)..].TakeWhile(char.IsDigit).ToArray());
            _ = long.TryParse(digits, out ticket);
        }

        return ticket > 0;
    }

    /// <summary>The weekly profit-capture chart: one bar per ISO week,
    /// labeled with the capture ratio, the R banked of R available, and the
    /// trade count. Self-contained inline styles — renders in any viewer
    /// the soak doc travels with.</summary>
    internal static string CaptureTrendSvg(
        IReadOnlyList<CaptureWeek> weeks, IReadOnlyList<int>? savesPerWeek = null,
        IReadOnlyList<int>? tp1PerWeek = null)
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
        if (tp1PerWeek is not null && tp1PerWeek.Any(n => n > 0))
        {
            sb.Append($"<text x=\"{(W - Right).ToString(inv)}\" y=\"20\" text-anchor=\"end\" font-family=\"monospace\" font-size=\"10\" fill=\"#1565c0\">blue outline = TP1 rung graded</text>");
        }

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

            // Blue outline: this ISO week saw a TP1 rung banked — the
            // rung's weeks stand out beside the captures they produced.
            if (tp1PerWeek is { } tp1 && i < tp1.Count && tp1[i] > 0)
            {
                sb.Append($"<rect x=\"{x.ToString(inv)}\" y=\"{y.ToString(inv)}\" width=\"{barW.ToString(inv)}\" height=\"{barH.ToString(inv)}\" fill=\"none\" stroke=\"#1565c0\" stroke-width=\"2\"/>");
            }
            sb.Append($"<text x=\"{((x + barW / 2)).ToString(inv)}\" y=\"{(y - 5).ToString(inv)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"10\" fill=\"#333\">{(wk.CaptureRatio * 100).ToString("0", inv)}%</text>");
            sb.Append($"<text x=\"{((x + barW / 2)).ToString(inv)}\" y=\"{(Top + plotH + 16).ToString(inv)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"9\" fill=\"#555\">{wk.Label}</text>");
            sb.Append($"<text x=\"{((x + barW / 2)).ToString(inv)}\" y=\"{(Top + plotH + 29).ToString(inv)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"8\" fill=\"#999\">{wk.CapturedR.ToString("0.##", inv)}R/{wk.AvailableR.ToString("0.##", inv)}R · {wk.Trades} trade(s)</text>");

            // Saves-per-week strip (amber dots above the capture bar): one
            // dot per distinct (day, ticket) save — save frequency and
            // capture ratio read together, which is the whole point of the
            // chart: saves ARE the capture mechanism.
            if (savesPerWeek is { } saves && i < saves.Count && saves[i] > 0)
            {
                var n = Math.Min(saves[i], 8);   // strip cap: keep the layout stable
                var lastCx = x + barW / 2;
                for (var d = 0; d < n; d++)
                {
                    var cx = x + barW / 2 + (d - (n - 1) / 2.0) * 9;
                    lastCx = cx;
                    sb.Append($"<circle cx=\"{cx.ToString(inv)}\" cy=\"{y - 16}\" r=\"3\" fill=\"#ef6c00\"/>");
                }
                sb.Append($"<text x=\"{lastCx.ToString(inv)}\" y=\"{y - 24}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"8\" fill=\"#ef6c00\">{saves[i]} save(s)</text>");
            }
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
                votes,
                r.TryGetProperty("Ticket", out var tk) && tk.ValueKind == JsonValueKind.Number
                    ? tk.GetInt64() : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
