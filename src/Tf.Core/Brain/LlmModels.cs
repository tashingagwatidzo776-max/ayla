using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>The brain's directional call.</summary>
public enum BrainDirection
{
    Rise,
    Fall,
    Hold
}

/// <summary>Structured decision the LLM must return as JSON.</summary>
public sealed record LlmDecision(
    BrainDirection Direction,
    double Confidence,
    decimal Stake,
    string Reasoning)
{
    public static LlmDecision Hold(string reasoning) =>
        new(BrainDirection.Hold, 0, 0, reasoning);
}

/// <summary>A decision plus the raw text that produced it (LLM reply / rule trace).</summary>
public sealed record BrainDecision(LlmDecision Decision, string Raw);

/// <summary>Everything a single brain cycle produced.</summary>
public sealed record BrainCycleResult(
    MarketContext Context,
    LlmDecision Decision,
    RiskVerdict Verdict,
    string RawLlmResponse,
    Trade? ExecutedTrade = null);

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message)
    {
    }
}