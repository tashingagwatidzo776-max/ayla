using System.Globalization;
using System.Text;
using System.Text.Json;
using DongGfx.Core.Fx;

// ────────────────────────────────────────────────────────────────────────────
// Monte-Carlo fire drill: replay the settled FX_EXIT trail through the real
// FxExitMonteCarlo gate, baseline vs one proposed weight change, and write
// the verdict as a soak evidence artifact. The gate the drill exercises is
// the SAME class the shipping code uses — no reimplementation, no drift
// between drill and production.
//
// Usage: dotnet run --project tools/McDrill -- <journal-dir> <out-markdown>
// Journal-only by construction: reads FX_EXIT entries, never trades.
// ────────────────────────────────────────────────────────────────────────────

var journalDir = args.Length > 0 ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data", "journal");
var outPath = args.Length > 1 ? args[1] : "docs/soak/MC-DRILL.md";

// The proposed change the drill gates. Small, honest, reviewable: tighten
// the drawdown voice slightly toward structure. Any change to EngineWeights
// must re-run this drill and land its verdict before shipping.
var proposed = new Dictionary<string, double>(FxExitBrain.EngineWeights);
proposed["drawdown"] = 2.2;

var evaluations = DecodeEvaluations(journalDir);
Console.WriteLine($"decoded settled evaluations: {evaluations.Count}");

var baseline = FxExitMonteCarlo.Run(evaluations, iterations: 2000);
Console.WriteLine($"baseline: flips {baseline.ActionFlipRate:P2}, mean |score delta| {baseline.MeanAbsScoreDelta:0.###} — {baseline.Verdict}");

var perturbed = evaluations.Select(t => new FxSettledExit(
    t.Ticket, t.Symbol, t.ResolvedAction,
    t.Votes.Select(v => proposed.TryGetValue(v.Engine, out var w)
        ? v with { Weight = w }
        : v).ToList()).WithResolved(t.ResolvedAction)).ToList();

// Replay the proposed roster: same harness, votes re-weighted. The harness
// perturbs around whatever weights the votes carry, so the proposed roster
// is applied to the votes before the run.
var proposedReport = FxExitMonteCarlo.Run(perturbed, iterations: 2000);
Console.WriteLine($"proposed: flips {proposedReport.ActionFlipRate:P2}, mean |score delta| {proposedReport.MeanAbsScoreDelta:0.###} — {proposedReport.Verdict}");

var sb = new StringBuilder();
sb.AppendLine("\n## Monte-Carlo fire drill — 2026-09-29");
sb.AppendLine();
sb.AppendLine($"- settled evaluations replayed: {evaluations.Count} (2000 trials, sigma 20%, seed 20260928)");
sb.AppendLine($"- baseline roster: flip rate {baseline.ActionFlipRate:P2}, mean |Δscore| {baseline.MeanAbsScoreDelta:0.###} → **{baseline.Verdict}**");
sb.AppendLine($"- proposed roster (drawdown 2.0 → 2.2): flip rate {proposedReport.ActionFlipRate:P2}, mean |Δscore| {proposedReport.MeanAbsScoreDelta:0.###} → **{proposedReport.Verdict}**");
sb.AppendLine($"- stability bar: flip rate ≤ {FxExitMonteCarlo.StabilityFlipRate:P0}");
sb.AppendLine();
sb.AppendLine(proposedReport.ActionFlipRate <= FxExitMonteCarlo.StabilityFlipRate
    ? "- verdict: the proposed change is INSIDE the stability bar — eligible to ship after human review."
    : "- verdict: the proposed change is OUTSIDE the stability bar — must not ship.");
await File.AppendAllTextAsync(outPath, sb.ToString(), Encoding.UTF8);
Console.WriteLine($"evidence appended: {outPath}");

return;

static List<FxSettledExit> DecodeEvaluations(string journalDir)
{
    var byTicket = new Dictionary<long, FxSettledExit>();
    foreach (var file in Directory.GetFiles(journalDir, "journal_*.jsonl").OrderBy(f => f))
    {
        foreach (var line in File.ReadLines(file))
        {
            if (!line.Contains("FX_EXIT"))
            {
                continue;
            }

            JournalEntry? entry = null;
            try
            {
                var row = JsonSerializer.Deserialize<JsonElement>(line);
                if (row.TryGetProperty("Category", out var cat) && cat.GetString() == "FX_EXIT"
                    && row.TryGetProperty("Timestamp", out var ts) && row.TryGetProperty("Details", out var det))
                {
                    entry = new JournalEntry(ts.GetString() ?? "", det.GetString() ?? "");
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            var brace = entry.Value.Details.IndexOf('{');
            if (brace < 0)
            {
                continue;
            }

            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize<JsonElement>(entry.Value.Details[brace..]);
            }
            catch (JsonException)
            {
                continue;
            }

            if (!payload.TryGetProperty("Action", out var actionEl) || actionEl.GetString() is not { } action
                || action.Length == 0)
            {
                continue;   // close confirmations are not evaluations
            }

            var ticket = payload.TryGetProperty("Ticket", out var tEl) && tEl.ValueKind == JsonValueKind.Number
                ? tEl.GetInt64() : 0;
            if (ticket == 0)
            {
                continue;
            }

            var votes = new List<FxExitVote>();
            if (payload.TryGetProperty("Votes", out var votesEl) && votesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in votesEl.EnumerateArray())
                {
                    votes.Add(new FxExitVote(
                        v.TryGetProperty("Engine", out var eEl) ? eEl.GetString() ?? "" : "",
                        v.TryGetProperty("Exit", out var xEl) && xEl.ValueKind == JsonValueKind.Number ? xEl.GetDouble() : 0,
                        v.TryGetProperty("Weight", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetDouble() : 0,
                        v.TryGetProperty("Reason", out var rEl) ? rEl.GetString() ?? "" : ""));
                }
            }

            // Keep the LATEST evaluation per ticket (the moment the exit resolved).
            byTicket[ticket] = new FxSettledExit(ticket,
                payload.TryGetProperty("Symbol", out var sEl) ? sEl.GetString() ?? "" : "",
                action, votes);
        }
    }

    return byTicket.Values.Where(t => t.Votes.Count > 0).ToList();
}

/// <summary>Minimal journal row shape the drill needs.</summary>
internal readonly record struct JournalEntry(string Timestamp, string Details);

/// <summary>Small helper so the proposed-roster replay keeps the resolved action.</summary>
internal static class Extensions
{
    public static FxSettledExit WithResolved(this FxSettledExit t, string action) =>
        new(t.Ticket, t.Symbol, action, t.Votes);
}
