using System;
using System.Collections.Generic;
using System.Linq;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>The GWEN advisor is pure, so its whole contract is testable
/// without a model: what prompt the desk sends, what verdicts parse out of
/// whatever the chat model actually replies, and which verdicts veto a
/// dispatch. The gate is advisory-with-veto — these rules are the only
/// place a model can stop a trade, so every branch is pinned here.</summary>
public class FxGwenAdvisorTests
{
    private static FxGwenRequest Req(string side = "buy", IReadOnlyList<FxBar>? bars = null) => new(
        Symbol: "EURUSD",
        Timeframe: "M1",
        Session: "london",
        Regime: "Trend",
        Adx: 27.3,
        AtrPct: 0.05,
        SpreadPoints: 6,
        Side: side,
        Alpha: "breakout",
        AlphaConfidence: 0.68,
        StopPips: 9.0,
        TargetR: 2.0,
        Bars: bars ?? new List<FxBar>
        {
            new(1_700_000_000L, 1.0831, 1.0834, 1.0829, 1.0832, 10),
            new(1_700_000_060L, 1.0832, 1.0835, 1.0830, 1.0833, 11),
        });

    // ── prompt ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildPrompt_Carries_The_Snapshot_The_Proposal_And_The_Json_Instruction()
    {
        var prompt = FxGwenAdvisor.BuildPrompt(Req());

        Assert.Contains("symbol=EURUSD", prompt);
        Assert.Contains("tf=M1", prompt);
        Assert.Contains("session=london", prompt);
        Assert.Contains("regime=Trend", prompt);
        Assert.Contains("adx=27.3", prompt);
        Assert.Contains("side=buy", prompt);
        Assert.Contains("alpha=breakout", prompt);
        Assert.Contains("conf=0.68", prompt);
        Assert.Contains("stop_pips=9", prompt);
        Assert.Contains("target_r=2", prompt);
        Assert.Contains("BARS ", prompt);
        // The reply contract: one JSON object, nothing else.
        Assert.Contains("\"bias\":\"long|short|flat\"", prompt);
        Assert.Contains("\"rr_ok\"", prompt);
    }

    [Fact]
    public void BuildPrompt_Is_Culture_And_Time_Invariant()
    {
        // Same state, same bytes — the client cache and the fine-tuning
        // dataset both key on the prompt text, so culture or clock drift
        // must never change it.
        var a = FxGwenAdvisor.BuildPrompt(Req());
        var b = FxGwenAdvisor.BuildPrompt(Req());
        Assert.Equal(a, b);
        var barClock = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L)
            .UtcDateTime.ToString("HH:mm");
        Assert.Contains(barClock + ":", a);   // bar time rendered as HH:mm UTC
    }

    [Fact]
    public void BuildPrompt_Renders_Only_The_Last_Twelve_Bars()
    {
        var bars = Enumerable.Range(0, 40)
            .Select(i => new FxBar(1_700_000_000L + i * 60, 1.1, 1.11, 1.09, 1.105, 5))
            .ToList();
        var prompt = FxGwenAdvisor.BuildPrompt(Req(bars: bars));

        var tokens = prompt.Split('\n').Last(l => l.StartsWith("BARS "))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "BARS" label + exactly the last 12 of the 40 bars.
        Assert.Equal(FxGwenAdvisor.BarsInPrompt + 1, tokens.Length);
    }

    // ── parse ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Reads_Headline_Json()
    {
        var v = FxGwenAdvisor.Parse(
            "{\"bias\":\"long\",\"confidence\":74,\"rr_ok\":true,\"reasons\":[\"trend aligned\",\"hh breakout\"]}",
            latencyMs: 812);

        Assert.NotNull(v);
        Assert.Equal("long", v!.Bias);
        Assert.Equal(74, v.Confidence);
        Assert.True(v.RrOk);
        Assert.Equal(new[] { "trend aligned", "hh breakout" }, v.Reasons);
        Assert.Equal(812, v.LatencyMs);
    }

    [Fact]
    public void Parse_Extracts_Json_Wrapped_In_Prose_Or_Fences()
    {
        var prose = FxGwenAdvisor.Parse(
            "Here is my read: {\"bias\":\"flat\",\"confidence\":61,\"rr_ok\":false,\"reasons\":[\"chop\"]} — hope that helps!");
        Assert.NotNull(prose);
        Assert.Equal("flat", prose!.Bias);

        var fenced = FxGwenAdvisor.Parse(
            "```\n{\"bias\":\"short\",\"confidence\":50,\"rr_ok\":true,\"reasons\":[]}\n```");
        Assert.NotNull(fenced);
        Assert.Equal("short", fenced!.Bias);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here")]
    [InlineData("{\"bias\":")]          // truncated
    [InlineData("{unclosed")]          // brace never closes
    [InlineData("[\"long\",\"short\"]")] // no object at all
    public void Parse_Is_Fail_Neutral_On_Unusable_Replies(string? reply)
        => Assert.Null(FxGwenAdvisor.Parse(reply));

    [Theory]
    [InlineData("{}", false)]                                  // no bias at all
    [InlineData("{\"bias\":42}", false)]                       // wrong type
    [InlineData("{\"bias\":\"sideways\"}", false)]             // unknown value
    [InlineData("{\"bias\":\"LONG\"}", true)]                  // case-insensitive
    [InlineData("{\"bias\":\"long\"}", true)]                  // defaults fill in
    public void Parse_Requires_A_Valid_Bias(string reply, bool ok)
    {
        var v = FxGwenAdvisor.Parse(reply);
        Assert.Equal(ok, v is not null);
    }

    [Fact]
    public void Parse_Clamps_Confidence_And_Caps_Reasons()
    {
        var hot = FxGwenAdvisor.Parse(
            "{\"bias\":\"long\",\"confidence\":150,\"rr_ok\":true,\"reasons\":[\"a\",\"b\",\"c\",\"d\",\"e\"]}");
        Assert.Equal(100, hot!.Confidence);
        Assert.Equal(FxGwenAdvisor.MaxReasons, hot.Reasons.Count);

        var cold = FxGwenAdvisor.Parse("{\"bias\":\"short\",\"confidence\":-20}");
        Assert.Equal(0, cold!.Confidence);

        var spoken = FxGwenAdvisor.Parse("{\"bias\":\"flat\",\"confidence\":\"63\"}");
        Assert.Equal(63, spoken!.Confidence);
    }

    // ── confluence gate (the veto matrix) ────────────────────────────────

    private static FxGwenVerdict V(string bias, int conf, bool rrOk = true) =>
        new(bias, conf, rrOk, new List<string>(), 0);

    [Theory]
    // Agreement always passes.
    [InlineData("long", 80, true, "buy", true)]
    [InlineData("short", 80, true, "sell", true)]
    // Opposite direction vetoes at 45+, passes below (indecision, not opposition).
    [InlineData("short", 90, true, "buy", false)]
    [InlineData("short", 44, true, "buy", true)]
    // Flat vetoes at 50+, passes below (a quiet read is not a refusal).
    [InlineData("flat", 60, true, "buy", false)]
    [InlineData("flat", 40, true, "buy", true)]
    // rr_ok=false vetoes at 55+ — a confident "no 1:2 here" kills the trade,
    // a low-confidence one does not.
    [InlineData("long", 80, false, "buy", false)]
    [InlineData("long", 40, false, "buy", true)]
    public void Evaluate_Maps_Every_Verdict_To_A_Gate_Decision(
        string bias, int conf, bool rrOk, string side, bool pass)
    {
        var (result, _) = FxGwenAdvisor.Evaluate(V(bias, conf, rrOk), side);
        Assert.Equal(pass, result);
    }

    [Fact]
    public void Evaluate_Reasons_Are_Self_Describing_For_The_Journal()
    {
        Assert.Contains("opposes", FxGwenAdvisor.Evaluate(V("short", 90), "buy").Reason);
        Assert.Contains("no edge", FxGwenAdvisor.Evaluate(V("flat", 60), "buy").Reason);
        Assert.Contains("1:2", FxGwenAdvisor.Evaluate(V("long", 80, rrOk: false), "buy").Reason);
        Assert.Contains("concurs", FxGwenAdvisor.Evaluate(V("long", 70), "buy").Reason);
    }

    // ── per-pair veto floor (go-live plan Step 4) ──────────────────────

    [Fact]
    public void Evaluate_Pair_Veto_Floor_Binds_Above_It_And_Abstains_Below()
    {
        // At/above the floor the verdict binds exactly as before.
        Assert.False(FxGwenAdvisor.Evaluate(V("short", 90), "buy", vetoFloor: 80).Pass);

        // Below the floor GWEN abstains on THIS pair: a low-confidence
        // opposition is advisory, never a veto — the fail-neutral
        // direction (calibration may loosen to the floor, never invent a
        // refusal GWEN did not make).
        var (pass, reason) = FxGwenAdvisor.Evaluate(V("short", 90), "buy", vetoFloor: 95);
        Assert.True(pass);
        Assert.Contains("veto floor 95", reason);
        Assert.Contains("abstention", reason);
    }

    [Fact]
    public void Evaluate_Without_A_Pair_Floor_Is_The_Unchanged_Matrix()
    {
        Assert.False(FxGwenAdvisor.Evaluate(V("short", 90), "buy").Pass);   // opposed, confident
        Assert.True(FxGwenAdvisor.Evaluate(V("short", 44), "buy").Pass);    // opposed, unsure
        Assert.False(FxGwenAdvisor.Evaluate(V("flat", 60), "buy").Pass);    // flat, quiet read
    }
}
