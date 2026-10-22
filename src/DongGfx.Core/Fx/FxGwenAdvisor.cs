using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DongGfx.Core.Fx;

/// <summary>One confluence assessment GWEN returned for a proposed trade.
/// Advisory by law: the deterministic alphas still SOURCE every signal —
/// GWEN can only refuse to confirm one, never mint one.</summary>
public sealed record FxGwenVerdict(
    string Bias,            // long | short | flat
    int Confidence,         // 0-100, clamped
    bool RrOk,              // the model judges the 1:2 geometry supported
    IReadOnlyList<string> Reasons,
    double LatencyMs);

/// <summary>The snapshot GWEN is asked about: exactly what the deterministic
/// brains already know, in a compact deterministic prompt. Pure data — the
/// HTTP lives in the App-side client.</summary>
public sealed record FxGwenRequest(
    string Symbol,
    string Timeframe,
    string Session,
    string Regime,
    double Adx,
    double AtrPct,
    double SpreadPoints,
    string Side,            // buy | sell
    string Alpha,
    double AlphaConfidence,
    double StopPips,
    double TargetR,
    IReadOnlyList<FxBar> Bars);

/// <summary>The GWEN advisor: prompt construction, verdict parsing and the
/// confluence rules, all pure and deterministic so they can be tested
/// without a model, a socket or a clock.
///
/// LAW (same shape as every other brain in this desk): advisory-only with
/// veto power — GWEN may REFUSE a trade the deterministic brains proposed,
/// and that refusal is journaled like every other gate. It never places,
/// sizes, or schedules anything, and when it is unavailable the desk trades
/// exactly as it did before it existed (fail-neutral, never fail-closed:
/// a missing LLM must not become an unexplained trading halt).</summary>
public static class FxGwenAdvisor
{
    // ── confluence rules ────────────────────────────────────────────────
    // Thresholds are evidence-shaped, not feel-shaped: "flat" needs to be
    // spoken with conviction to veto, an outright opposite call vetoes at
    // slightly lower confidence (directional disagreement is stronger
    // evidence than indecision), and an "the 1:2 geometry is not there"
    // verdict must be confident before it costs the trade.
    public const int FlatVetoConfidence = 50;
    public const int OppositeVetoConfidence = 45;
    public const int RrVetoConfidence = 55;

    public const int MaxReasons = 3;
    public const int BarsInPrompt = 12;

    /// <summary>Compact deterministic snapshot prompt. Culture-invariant,
    /// fixed field order — the same state always renders the same text, so
    /// the client's assessment cache and the fine-tuning dataset built from
    /// the journal both key on something stable.</summary>
    public static string BuildPrompt(FxGwenRequest req)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(512);
        sb.Append("GWEN confluence check - DON G FX desk.\n");
        sb.Append("SNAPSHOT symbol=").Append(req.Symbol)
          .Append(" tf=").Append(req.Timeframe)
          .Append(" session=").Append(req.Session)
          .Append(" regime=").Append(req.Regime)
          .Append(" adx=").Append(req.Adx.ToString("0.#", inv))
          .Append(" atr_pct=").Append(req.AtrPct.ToString("0.00", inv))
          .Append(" spread_pts=").Append(req.SpreadPoints.ToString("0.#", inv))
          .Append('\n');
        sb.Append("PROPOSED side=").Append(req.Side)
          .Append(" alpha=").Append(req.Alpha)
          .Append(" conf=").Append(req.AlphaConfidence.ToString("0.00", inv))
          .Append(" stop_pips=").Append(req.StopPips.ToString("0.#", inv))
          .Append(" target_r=").Append(req.TargetR.ToString("0.0#", inv))
          .Append('\n');

        if (req.Bars is { Count: > 0 })
        {
            sb.Append("BARS ");
            foreach (var b in req.Bars.TakeLast(BarsInPrompt))
            {
                sb.Append(DateTimeOffset.FromUnixTimeSeconds(b.Time)
                    .UtcDateTime.ToString("HH:mm", inv))
                  .Append(':')
                  .Append(b.Open.ToString("0.#####", inv)).Append(',')
                  .Append(b.High.ToString("0.#####", inv)).Append(',')
                  .Append(b.Low.ToString("0.#####", inv)).Append(',')
                  .Append(b.Close.ToString("0.#####", inv))
                  .Append(' ');
            }
            sb.Append('\n');
        }

        sb.Append("Reply with EXACTLY one JSON object and nothing else: ")
          .Append("{\"bias\":\"long|short|flat\",\"confidence\":<0-100 integer>,")
          .Append("\"rr_ok\":<true|false>,\"reasons\":[\"<max 3 short reasons>\"]}.");
        return sb.ToString();
    }

    /// <summary>Tolerant verdict parse. The model is told to emit bare JSON,
    /// but a chat model occasionally wraps it in prose or code fences — the
    /// first '{' to the last '}' is extracted, and anything unparseable or
    /// missing the required bias field is a NULL verdict (fail-neutral:
    /// the caller proceeds as if GWEN had not spoken).</summary>
    public static FxGwenVerdict? Parse(string? reply, double latencyMs = 0)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var text = reply.Trim();
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close <= open) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[open..(close + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("bias", out var biasEl) ||
                biasEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var bias = (biasEl.GetString() ?? "").Trim().ToLowerInvariant();
            if (bias is not ("long" or "short" or "flat")) return null;

            var confidence = 0;
            if (root.TryGetProperty("confidence", out var confEl))
            {
                confidence = confEl.ValueKind switch
                {
                    JsonValueKind.Number => confEl.TryGetInt32(out var i) ? i : (int)confEl.GetDouble(),
                    JsonValueKind.String when int.TryParse(confEl.GetString(), out var s) => s,
                    _ => 0,
                };
            }
            confidence = Math.Clamp(confidence, 0, 100);

            var rrOk = root.TryGetProperty("rr_ok", out var rrEl) &&
                       (rrEl.ValueKind == JsonValueKind.True ||
                        (rrEl.ValueKind == JsonValueKind.String &&
                         bool.TryParse(rrEl.GetString(), out var b) && b));

            var reasons = new List<string>();
            if (root.TryGetProperty("reasons", out var reasonsEl) &&
                reasonsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in reasonsEl.EnumerateArray().Take(MaxReasons))
                {
                    if (r.ValueKind == JsonValueKind.String)
                    {
                        var s = r.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) reasons.Add(s.Trim());
                    }
                }
            }

            return new FxGwenVerdict(bias, confidence, rrOk, reasons, latencyMs);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The confluence gate: does GWEND's verdict pass the proposed
    /// side? Pure rule evaluation over an already-parsed verdict.</summary>
    public static (bool Pass, string Reason) Evaluate(FxGwenVerdict verdict, string side)
    {
        var isBuy = string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase);
        var agrees = verdict.Bias switch
        {
            "long" => isBuy,
            "short" => !isBuy,
            _ => (bool?)null,   // flat: neither agrees nor opposes
        };

        if (agrees == false && verdict.Confidence >= OppositeVetoConfidence)
        {
            return (false, $"GWEN opposes — {verdict.Bias} {verdict.Confidence}");
        }
        if (agrees is null && verdict.Confidence >= FlatVetoConfidence)
        {
            return (false, $"GWEN sees no edge — flat {verdict.Confidence}");
        }
        if (!verdict.RrOk && verdict.Confidence >= RrVetoConfidence)
        {
            return (false, $"GWEN rejects the 1:2 geometry — conf {verdict.Confidence}");
        }
        return (true, $"GWEN concurs — {verdict.Bias} {verdict.Confidence}");
    }
}
