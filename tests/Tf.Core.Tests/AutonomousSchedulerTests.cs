using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.Core.Tests;

[Trait("Category", "Unit")]
public class AutonomousSchedulerTests
{
    private static Tick MakeTick(int i) =>
        new("frxEURUSD", 1.10 + i * 0.0001, 1.10 + i * 0.0001, 1.10 + i * 0.0001, 1700000000L + i, 5);

    private static TradingBrain BuildBrain(string llmJson, TradingBrain.DerivAbstraction deriv)
    {
        var llm = new StubLlm(llmJson);
        return new TradingBrain(llm, () => new AppSettings
        {
            DecisionIntervalMinutes = 1,
            AutonomyEnabled = false,
            Currency = "USD"
        }, deriv);
    }

    private static TradingBrain.DerivAbstraction StubDeriv() => new()
    {
        GetProposal = (_, _, _, _, _, _) =>
            Task.FromResult(new Proposal("P", "frxEURUSD", Direction.Rise, 1m, "USD", 5, 1.1, "", 0)),
        Buy = (_, _, _) => Task.FromResult(new BuyResult("C", 1m, 100m, "")),
        WaitForSettlement = (_, _, _) =>
            Task.FromResult(new ContractInfo("C", ContractStatus.Open, 0, 0, 0, 0, 0, 0, "USD", false))
    };

    /// <summary>LLM that answers instantly with a canned decision.</summary>
    private sealed class StubLlm : LlmClient
    {
        private readonly string _json;

        public StubLlm(string json) => _json = json;

        public override Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(_json);
    }

    [Fact]
    public async Task KillSwitchEngaged_ExitsImmediately_WithoutCycles()
    {
        var cycles = 0;
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0,\"stake\":0,\"reasoning\":\"test\"}", StubDeriv()),
            () => new AppSettings { DecisionIntervalMinutes = 1, AutonomyEnabled = true },
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => new RiskContext(KillSwitchEngaged: true, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>(),
            _ => cycles++);

        await scheduler.StartAsync();

        Assert.False(scheduler.IsRunning);
        Assert.Equal(0, cycles);
    }

    [Fact]
    public async Task RunsOneCycle_ThenWaitsForNextInterval()
    {
        var cycles = 0;
        BrainCycleResult? last = null;
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.5,\"stake\":0,\"reasoning\":\"flat\"}", StubDeriv()),
            () => new AppSettings { DecisionIntervalMinutes = 1, AutonomyEnabled = true },
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => new RiskContext(KillSwitchEngaged: false, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>(),
            r => { cycles++; last = r; });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        // First cycle runs immediately; subsequent ones wait for the 1-minute
        // interval. Wait for the first cycle via deadline polling instead of a
        // fixed sleep (a loaded CI runner can exceed 1.5s before the callback
        // runs), then stop before the next interval — the interval is far
        // longer than any plausible first-cycle delay, so exactly one cycle.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (cycles == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }
        scheduler.Stop();
        await run;

        Assert.Equal(1, cycles);
        Assert.NotNull(last);
        Assert.Equal(BrainDirection.Hold, last!.Decision.Direction);
    }

    [Fact]
    public async Task Stop_EndsTheLoop()
    {
        var cycles = 0;
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.3,\"stake\":0,\"reasoning\":\"x\"}", StubDeriv()),
            () => new AppSettings { DecisionIntervalMinutes = 1, AutonomyEnabled = true },
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => new RiskContext(KillSwitchEngaged: false, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>(),
            _ => cycles++);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        // Let the first cycle fire via deadline polling (no fixed sleep),
        // then stop. StartAsync returns when the loop exits.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (cycles == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }
        scheduler.Stop();
        await run;

        Assert.False(scheduler.IsRunning);
        Assert.Equal(1, cycles);         // stopped before the next interval
    }
}