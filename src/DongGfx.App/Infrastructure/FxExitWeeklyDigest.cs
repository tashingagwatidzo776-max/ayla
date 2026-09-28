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
                await File.AppendAllTextAsync(
                    path, markdown, Encoding.UTF8).ConfigureAwait(false);
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

        // The split: override engines (hard safety) vs consensus bands.
        string[] overrideEngines = ["bridge", "spread", "equity-floor", "drawdown"];
        var overrides = payloads.Where(p => p.Override is { }).ToList();
        var consensus = payloads.Where(p => p.Override is null).ToList();
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
        var decisive = payloads
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

        var message =
            $"{payloads.Count} exit evaluation(s) this week: {overrides.Count} hard override(s) " +
            $"({string.Join(", ", overrideByEngine)}) vs {consensus.Count} consensus " +
            $"({string.Join(", ", consensusByAction)}). Decisive exits {decisive.Count}, " +
            $"round-trips {roundTrips} ({roundTripPct:P0}).";

        var markdown =
            $"\n\n## FX exit weekly digest — {now:yyyy-MM-dd}\n\n" +
            $"- evaluations: {payloads.Count} (override {overrides.Count} / consensus {consensus.Count})\n" +
            $"- overrides: {string.Join(", ", overrideByEngine)}\n" +
            $"- consensus bands: {string.Join(", ", consensusByAction)}\n" +
            $"- exit reasons (decisive): {string.Join(", ", reasons)}\n" +
            $"- MAE at exit: {string.Join(", ", maeCurve)}\n" +
            $"- round-trips (MFE ≥1R, closed ≤0.2R): {roundTrips}/{decisive.Count} ({roundTripPct:P0})\n";

        return (message, markdown);
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
