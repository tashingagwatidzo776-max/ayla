using DongGfx.Core.Brain;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.Core.Tests;

/// <summary>
/// Milestone-3 verification: a full brain round-trip against a REAL local
/// Ollama server — real market history → indicators → LLM decision → risk
/// check. Skipped unless <c>TF_OLLAMA_MODEL</c> is set (e.g. llama3.2).
/// </summary>
[Trait("Category", "Integration")]
public class BrainLiveIntegrationTests
{
    private static readonly string OllamaModel =
        Environment.GetEnvironmentVariable("TF_OLLAMA_MODEL") ?? "";

    private static readonly string OllamaUrl =
        Environment.GetEnvironmentVariable("TF_OLLAMA_URL") ?? "http://localhost:11434/v1";

    [Fact]
    public async Task FullCycle_RoundTripsThroughRealOllama()
    {
        if (string.IsNullOrEmpty(OllamaModel))
        {
            Console.WriteLine(
                "TF_OLLAMA_MODEL not set (e.g. llama3.2) — skipping live LLM round-trip. " +
                "Set it to verify the M3 brain against a real model.");
            return;
        }

        // 1. Real market data (unauthenticated history backfill works).
        await using var client = new DerivClient { AppId = AppSettings.DefaultAppId };
        await client.ConnectAsync();
        var history = await client.GetTicksHistoryAsync(AppSettings.DefaultSymbol, 120);
        Assert.True(history.Count > 20, "need a meaningful tick window");

        // 2. Brain cycle against Ollama.
        var llm = new LlmClient { BaseUrl = OllamaUrl, Model = OllamaModel };
        var settings = new AppSettings { Currency = "USD", MinConfidence = 0.6 };
        var brain = new TradingBrain(llm, () => settings, new TradingBrain.DerivAbstraction
        {
            GetProposal = (_, _, _, _, _, _) => Task.FromResult(new Proposal("x", "", Direction.Rise, 0, "USD", 5, 0, "", 0)),
            Buy = (_, _, _) => Task.FromResult(new BuyResult("x", 0, 0, "")),
            WaitForSettlement = (_, _, _) => Task.FromResult(new ContractInfo("x", ContractStatus.Open, 0, 0, 0, 0, 0, 0, "USD", false))
        });

        var risk = new RiskContext(false, 0, 1000m, 0m, 0, null, null);
        var result = await brain.RunCycleAsync(history, risk, new List<string>(), allowTrading: false);

        // 3. The decision must parse to a real direction and the risk engine must not throw.
        Assert.True(result.Decision.Direction is BrainDirection.Rise or BrainDirection.Fall or BrainDirection.Hold);
        Assert.InRange(result.Decision.Confidence, 0.0, 1.0);
        Assert.NotNull(result.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(result.RawLlmResponse));
        Console.WriteLine($"LLM said: {result.Decision.Direction} @ {result.Decision.Confidence:P0} — {result.Decision.Reasoning}");
        Console.WriteLine($"Risk: {(result.Verdict.Allowed ? "ALLOWED" : "BLOCKED: " + result.Verdict.Reason)}");
    }
}