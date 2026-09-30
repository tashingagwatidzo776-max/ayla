using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DongGfx.Core.Fx;

/// <summary>The promotion verdict for one shadow engine: how many settled
/// trades it has observed, how often it "helped", and the weight that
/// accuracy has earned (0 = stay in observation).</summary>
public sealed record FxShadowReport(
    string Engine,
    int Trades,
    double HitRate,
    double Weight,
    bool Eligible,
    string Verdict);

/// <summary>One settled trade's shadow observation, appended to the ledger
/// at close time: what the shadow engine said, what the ensemble did, and
/// how the trade ended. The substrate that promotion is computed from.</summary>
public sealed record FxShadowObservation(
    string Engine,
    long Ticket,
    string Symbol,
    double ExitAtClose,
    string ResolvedAction,
    bool Won);

/// <summary>Per-engine rollup of the ledger (what the weekly digest reads).</summary>
public sealed record FxShadowLedgerRow(
    string Engine,
    int Trades,
    int Helped,
    double HitRate,
    double SuggestedWeight);

/// <summary>
/// The engines-earn-votes loop (guardrail: "more engines ≠ more
/// robustness"). A future exit engine rides at weight 0 — journaled every
/// cycle, never moving the score. Each settled trade grades its final
/// shadow vote: the observation "helped" when the shadow showed real
/// conviction to exit a trade the (winning) ensemble held. When an engine
/// has ~100 settled trades AND a 60% hit rate it has EARNED a small voice —
/// shipped only as a reviewed PR after the Monte-Carlo harness reports the
/// weight change stable. Nothing here promotes anything by itself.
/// </summary>
public static class FxExitShadow
{
    /// <summary>Settled trades a shadow engine must observe before it can
    /// earn weight (the spec's ~100-trade evidence bar).</summary>
    public const int PromotionTrades = 100;

    /// <summary>Hit rate a shadow engine must sustain to earn weight.</summary>
    public const double PromotionHitRate = 0.6;

    /// <summary>A shadow vote "helped" in exactly two ways: (1) the classic
    /// rule — at the trade's final evaluation it showed real exit conviction
    /// (≥ half pressure) on a trade that went on to win while the ensemble
    /// held or merely watched; or (2) the trade was a PROFIT-FLOOR SAVE the
    /// giveback evidence itself drove home (override or deep giveback vote
    /// per <see cref="FxExitBrain.IsProfitFloorSave"/>) — then the giveback
    /// shadow voice and the drawdown engine that carried it are credited
    /// with the save. (Losing trades need no rescue: the drawdown/override
    /// tier owns them.)</summary>
    public static bool Helped(FxExitVote shadowVote, string resolvedAction, bool won,
        FxExitDecision? decision = null) =>
        won && shadowVote.Exit >= 0.5 && resolvedAction is "hold" or "monitor"
        || (decision is not null
            && FxExitBrain.IsProfitFloorSave(decision)
            && shadowVote is { Engine: "giveback" or "drawdown", Exit: >= 0.5 });

    /// <summary>The weight an engine's measured accuracy has earned: zero
    /// until BOTH bars are met, then proportional to the hit rate and capped
    /// at the lightest real engine's weight (1.0) — a new voice never jumps
    /// straight to a drawdown-grade vote.</summary>
    public static double WeightFor(double hitRate, int trades) =>
        trades >= PromotionTrades && hitRate >= PromotionHitRate
            ? Math.Min(1.0, hitRate)
            : 0;

    /// <summary>Grade one engine's ledger counts into a promotion verdict.</summary>
    public static FxShadowReport Grade(string engine, int helped, int trades)
    {
        var hit = trades > 0 ? (double)helped / trades : 0.0;
        var weight = WeightFor(hit, trades);
        var verdict = weight > 0
            ? $"earned weight {weight:0.##} — ship as a reviewed PR after the Monte-Carlo stability check"
            : trades < PromotionTrades
                ? $"observation only: {trades}/{PromotionTrades} settled trades recorded"
                : $"observation only: hit rate {hit:P0} below the {PromotionHitRate:P0} bar";
        return new FxShadowReport(engine, trades, hit, weight, weight > 0, verdict);
    }
}

/// <summary>
/// Append-only JSONL ledger of shadow observations, one line per settled
/// trade per shadow engine. Best-effort by design: an IO failure silently
/// skips the line (the ledger is an accuracy substrate, never a trading
/// dependency — starving it degrades promotion evidence, not exits).
/// </summary>
public sealed class FxShadowLedger
{
    private readonly string _path;

    public FxShadowLedger(string path) => _path = path;

    /// <summary>Records one settled trade's shadow votes. The trade's
    /// winning/losing outcome and the ensemble's final action come from the
    /// caller (the exit pass knows both at close time). When the caller
    /// passes the settled <see cref="FxExitDecision"/>, profit-floor saves
    /// credit the giveback evidence that drove them (see
    /// <see cref="FxExitShadow.Helped"/>) — without it the promotion
    /// substrate only ever counts exits the ensemble declined.</summary>
    public void Append(
        long ticket, string symbol, IReadOnlyList<FxExitVote> shadowVotes,
        string resolvedAction, bool won, DateTimeOffset at,
        FxExitDecision? decision = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = shadowVotes.Select(v => JsonSerializer.Serialize(new
            {
                At = at,
                Ticket = ticket,
                Symbol = symbol,
                Engine = v.Engine,
                ExitAtClose = Math.Round(v.Exit, 4),
                ResolvedAction = resolvedAction,
                Won = won,
                Helped = FxExitShadow.Helped(v, resolvedAction, won, decision),
            }));
            File.AppendAllLines(_path, lines);
        }
        catch (IOException)
        {
            // Best-effort substrate: never let ledger IO touch the exit path.
        }
    }

    /// <summary>Reads the whole ledger and rolls it up per engine. Malformed
    /// or vanished files degrade to an empty summary — the digest then
    /// simply reports "not enough evidence yet".</summary>
    public IReadOnlyList<FxShadowLedgerRow> Summarize() => SummarizeFile(_path);

    /// <summary>Rolls up ONE ledger file.</summary>
    public static IReadOnlyList<FxShadowLedgerRow> SummarizeFile(string path)
    {
        var (helped, trades) = (new Dictionary<string, int>(), new Dictionary<string, int>());
        AccumulateFile(path, helped, trades);
        return RollUp(helped, trades);
    }

    /// <summary>Rolls up EVERY per-symbol ledger in a directory
    /// (fx-shadow-*.jsonl) into one merged per-engine verdict — the
    /// portfolio view: promotion evidence is per engine across symbols,
    /// never per file. Missing/unreadable directories degrade to empty.</summary>
    public static IReadOnlyList<FxShadowLedgerRow> SummarizeDir(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        var (helped, trades) = (new Dictionary<string, int>(), new Dictionary<string, int>());
        try
        {
            foreach (var file in Directory.GetFiles(directory, "fx-shadow-*.jsonl"))
            {
                AccumulateFile(file, helped, trades);
            }
        }
        catch (IOException)
        {
            // Degraded read: an empty rollup, never a crash.
        }
        return RollUp(helped, trades);
    }

    private static void AccumulateFile(
        string path,
        Dictionary<string, int> helped,
        Dictionary<string, int> trades)
    {
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var engine = r.GetProperty("Engine").GetString() ?? "";
                    trades[engine] = trades.GetValueOrDefault(engine) + 1;
                    if (r.TryGetProperty("Helped", out var h) && h.ValueKind == JsonValueKind.True)
                    {
                        helped[engine] = helped.GetValueOrDefault(engine) + 1;
                    }
                }
                catch (JsonException)
                {
                    // Malformed/legacy line — skip it, summarize the rest.
                }
            }
        }
        catch (IOException)
        {
            // No file (or unreadable): that symbol contributes nothing.
        }
    }

    private static IReadOnlyList<FxShadowLedgerRow> RollUp(
        Dictionary<string, int> helped, Dictionary<string, int> trades) =>
        trades.Count == 0
            ? []
            : [.. trades.Select(kv => kv.Key).OrderBy(e => e, StringComparer.Ordinal).Select(e =>
            {
                var report = FxExitShadow.Grade(e, helped.GetValueOrDefault(e), trades[e]);
                return new FxShadowLedgerRow(e, report.Trades, helped.GetValueOrDefault(e), report.HitRate, report.Weight);
            })];
}
