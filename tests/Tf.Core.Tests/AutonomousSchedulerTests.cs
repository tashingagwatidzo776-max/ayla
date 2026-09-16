using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.Core.Tests;

/// <summary>
/// Deterministic tests for the autonomous decision loop. The scheduler takes
/// an injected <see cref="TimeProvider"/>, so every test drives the shared
/// <see cref="TestVirtualClock"/> — interval gating, failure backoff, kill
/// switch interrupts and stop semantics are all verified on fake time that
/// moves only when the test advances it, with exact timestamps. No wall-clock
/// bound anywhere: nothing here can flake under CI load, and the whole suite
/// runs in milliseconds instead of waiting out real intervals.
/// </summary>
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

    /// <summary>AppSettings with the 1-minute interval every test here assumes.</summary>
    private static AppSettings IntervalSettings() =>
        new() { DecisionIntervalMinutes = 1, AutonomyEnabled = true };

    private static RiskContext Risk(bool killSwitch) =>
        new(KillSwitchEngaged: killSwitch, 0, 1000m, 0m, 0, null, null);

    /// <summary>Pumps queued await continuations (xUnit's sync context defers
    /// them) until the condition holds. Purely cooperative — no sleeps — so
    /// observation is immediate and load-independent; the 5s wall-clock bound
    /// exists only to fail fast instead of hanging if the loop wedges.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            for (var i = 0; i < 10; i++) await Task.Yield();
        }
    }

    // ─── Kill switch ─────────────────────────────────────

    [Fact]
    public async Task KillSwitchEngaged_ExitsImmediately_WithoutCycles()
    {
        var cycles = 0;
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0,\"stake\":0,\"reasoning\":\"test\"}", StubDeriv()),
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: true),
            () => Array.Empty<string>(),
            _ => cycles++,
            timeProvider: new TestVirtualClock());

        await scheduler.StartAsync();

        Assert.False(scheduler.IsRunning);
        Assert.Equal(0, cycles);
    }

    [Fact]
    public async Task KillSwitchEngagedMidInterval_EndsTheWaitAtNextSlice()
    {
        var cycles = 0;
        var kill = false;
        var clock = new TestVirtualClock();
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.5,\"stake\":0,\"reasoning\":\"flat\"}", StubDeriv()),
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(kill),
            () => Array.Empty<string>(),
            _ => cycles++,
            timeProvider: clock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // hang safety net only
        var run = scheduler.StartAsync(cts.Token);

        await WaitUntilAsync(() => cycles == 1);
        clock.Advance(5_000);          // five virtual seconds of the interval pass — still waiting
        await WaitUntilAsync(() => clock.CurrentMs() >= 5_000);

        kill = true;                   // engage mid-interval
        clock.Advance(1_000);          // the next ≤1s switch-check slice fires the check
        await run;                     // loop exits without running a second cycle

        Assert.False(scheduler.IsRunning);
        Assert.Equal(1, cycles);
    }

    // ─── Interval gating ─────────────────────────────────

    [Fact]
    public async Task RunsOneCycle_ThenWaitsForNextInterval()
    {
        var cycles = 0;
        BrainCycleResult? last = null;
        var clock = new TestVirtualClock();
        var cycleMs = new List<long>();
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.5,\"stake\":0,\"reasoning\":\"flat\"}", StubDeriv()),
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: false),
            () => Array.Empty<string>(),
            r => { cycles++; last = r; cycleMs.Add(clock.CurrentMs()); },
            timeProvider: clock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        await WaitUntilAsync(() => cycles == 1); // first cycle runs immediately at t=0
        scheduler.Stop();                        // stop before the interval elapses (fake time hasn't moved)
        await run;

        Assert.Equal(1, cycles);
        Assert.NotNull(last);
        Assert.Equal(BrainDirection.Hold, last!.Decision.Direction);
        Assert.Equal(0, cycleMs[0]);             // first cycle exactly at virtual t=0
    }

    // ─── Cycle telemetry ─────────────────────────────────

    [Fact]
    public async Task SuccessfulCycle_RecordsLatencySample_NoError()
    {
        var cycles = 0;
        var latencies = new List<double>();
        var errors = new List<string>();
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.5,\"stake\":0,\"reasoning\":\"flat\"}", StubDeriv()),
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: false),
            () => Array.Empty<string>(),
            _ => cycles++,
            timeProvider: new TestVirtualClock(),
            onCycleLatencyMs: latencies.Add,
            onCycleError: errors.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        // The latency callback fires before onCycle, so the first cycle's
        // sample is already recorded when the cycle callback observes it.
        await WaitUntilAsync(() => cycles == 1);
        scheduler.Stop();
        await run;

        Assert.Single(latencies);
        Assert.True(latencies[0] >= 0 && latencies[0] < 60_000, $"implausible latency {latencies[0]}ms");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task FailedCycle_RecordsErrorAndLatency_SuccessCallbackNotFired()
    {
        var attempts = 0;
        var successes = 0;
        var latencies = new List<double>();
        var errors = new List<string>();
        var brain = new TradingBrain(
            (_, _, _, _) =>
            {
                attempts++;
                return Task.FromException<BrainDecision>(new InvalidOperationException("boom"));
            },
            IntervalSettings,
            StubDeriv());
        var scheduler = new AutonomousScheduler(
            brain,
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: false),
            () => Array.Empty<string>(),
            _ => successes++,
            failureBackoff: TimeSpan.Zero,   // no virtual-clock driving needed
            timeProvider: new TestVirtualClock(),
            onCycleLatencyMs: latencies.Add,
            onCycleError: errors.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        await WaitUntilAsync(() => errors.Count >= 1);
        scheduler.Stop();
        await run;

        Assert.True(errors.Count >= 1);
        Assert.All(errors, e => Assert.Equal("InvalidOperationException", e));
        Assert.True(latencies.Count >= errors.Count, "the failure path must also surface latency");
        Assert.Equal(0, successes);
        Assert.True(scheduler.ConsecutiveFailures >= 1);
        Assert.True(attempts >= 1);
    }

    [Fact]
    public async Task SecondCycle_WaitsForFullInterval_ThenRunsExactlyAtGate()
    {
        var cycles = 0;
        var clock = new TestVirtualClock();
        var cycleMs = new List<long>();
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.5,\"stake\":0,\"reasoning\":\"flat\"}", StubDeriv()),
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: false),
            () => Array.Empty<string>(),
            _ => { cycles++; cycleMs.Add(clock.CurrentMs()); },
            timeProvider: clock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        await WaitUntilAsync(() => cycles == 1);

        clock.Advance(59_999);                   // 1ms short of the 1-minute gate
        await WaitUntilAsync(() => clock.CurrentMs() >= 59_999);
        Assert.Equal(1, cycles);                 // gate holds: no early cycle

        clock.Advance(1);                        // land exactly on the 60s gate
        await WaitUntilAsync(() => cycles == 2);
        scheduler.Stop();
        await run;

        Assert.Equal(2, cycles);
        Assert.Equal(0, cycleMs[0]);
        Assert.Equal(60_000, cycleMs[1]);        // second cycle exactly one interval later
    }

    // ─── Stop semantics ──────────────────────────────────

    [Fact]
    public async Task Stop_EndsTheLoop()
    {
        var cycles = 0;
        var clock = new TestVirtualClock();
        var scheduler = new AutonomousScheduler(
            BuildBrain("{\"direction\":\"HOLD\",\"confidence\":0.3,\"stake\":0,\"reasoning\":\"x\"}", StubDeriv()),
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: false),
            () => Array.Empty<string>(),
            _ => cycles++,
            timeProvider: clock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        await WaitUntilAsync(() => cycles == 1); // first cycle fired
        scheduler.Stop();                        // before the next interval
        await run;                               // StartAsync returns when the loop exits

        Assert.False(scheduler.IsRunning);
        Assert.Equal(1, cycles);
    }

    // ─── Failure backoff ─────────────────────────────────

    /// <summary>
    /// Replaces the wall-clock backoff behavior with exact virtual timestamps:
    /// a decide that always throws retries on the default 5s backoff schedule
    /// (t=0, 5000, 10000) and exits after the default 3 consecutive failures.
    /// StartAsync completes on its own — no Stop needed.
    /// </summary>
    [Fact]
    public async Task FailedCycles_BackOffOnSchedule_ThenExitAfterMaxConsecutiveFailures()
    {
        var attempts = 0;
        var attemptMs = new List<long>();
        var clock = new TestVirtualClock();
        var brain = new TradingBrain(
            (_, _, _, _) =>
            {
                attempts++;
                attemptMs.Add(clock.CurrentMs());
                return Task.FromException<BrainDecision>(new InvalidOperationException("boom"));
            },
            IntervalSettings,
            StubDeriv());
        var scheduler = new AutonomousScheduler(
            brain,
            IntervalSettings,
            () => Enumerable.Range(0, 30).Select(MakeTick).ToArray(),
            () => Risk(killSwitch: false),
            () => Array.Empty<string>(),
            onCycle: null,
            timeProvider: clock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = scheduler.StartAsync(cts.Token);

        // Drive the virtual clock. The loop waits in ≤1s one-shot slice timers
        // that fire only when the test advances, and the backoff deadlines sit
        // on the 5s schedule — so steps of 1s can never overshoot a deadline.
        // Advancing ONLY while a timer is pending (and pumping continuations
        // between advances) keeps fake time pinned to the loop's real state:
        // a blind advance loop would run whole batches with nothing due — the
        // continuation drain is lazy — and smear the timestamps under test.
        while (!run.IsCompleted)
        {
            await WaitUntilAsync(() => clock.PendingTimerCount > 0 || run.IsCompleted);
            if (run.IsCompleted) break;
            clock.Advance(1_000);
            for (var i = 0; i < 10; i++) await Task.Yield();
        }
        await run;

        Assert.False(scheduler.IsRunning);
        Assert.Equal(3, scheduler.ConsecutiveFailures);
        Assert.Equal(3, attempts);
        Assert.Equal(new long[] { 0, 5_000, 10_000 }, attemptMs);
    }
}