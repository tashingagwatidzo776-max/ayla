using System.IO;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.Core;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;
using static Tf.App.Tests.GrowthTestHarness;

namespace Tf.App.Tests;

/// <summary>
/// Mid-session real-money enforcement: a growth runner that started legally
/// must stop itself at the next settlement once the account state (config
/// flag, API-verified type, session unlock) no longer passes the gate — and
/// the hub must drop the runner so no restart ladder re-launches it.
/// </summary>
[Trait("Category", "Integration")]
public class RealMoneyGateMidSessionTests : IDisposable
{
    private readonly string _dir;

    public RealMoneyGateMidSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_gate_mid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Runner_StartAsync_RefusesLockedRealAccount_BeforeAnythingElse()
    {
        // The structural gate: the runner itself refuses to start on a real
        // account whose session unlock is not armed — even when constructed
        // directly (the old ObserveRunner bypass) and never connected. The
        // gate fires before the connection check, so a locked account gets
        // the gate refusal, not "Account not connected".
        var store = new TradeStore(_dir);
        using var journal = new TradeJournal(Path.Combine(_dir, "journal"));
        var hub = new MultiAccountHub(new EmptyVault(), store, journal);
        var connection = hub.AddAccount(new AccountConfig
        {
            Label = "Locked Acct", ApiToken = "tok", IsDemo = false, BrainKey = "Growth"
        });
        connection.ApiVerifiedVirtual = false; // API-verified real, locked

        var runner = new GrowthRunner(
            connection, store, () => new AppSettings { AutonomyEnabled = true },
            () => false, journal, realMoneyUnlocked: () => false);

        runner.StartAsync(GrowthPlan.Default);

        Assert.False(runner.IsRunning, "a locked real account must not start");
        Assert.Null(runner.Engine);
        Assert.Contains("REFUSED", runner.LastActivity);
        Assert.Contains("real-money gate", runner.SessionStateText);

        // The refusal is journaled for audit.
        journal.Dispose();
        var entries = journal.GetRecent(count: 200);
        Assert.Contains(entries, e => e.Category == "GROWTH_STATE" &&
            e.Details.Contains("runner start refused"));
    }

    [Fact]
    public void Runner_StartAsync_DemoPassthrough_IsUnaffected()
    {
        // Demo accounts must pass the runner-level gate untouched (the
        // existing no-broker refusal paths — not connected / paused — stay
        // reachable for demo configs).
        var store = new TradeStore(_dir);
        using var journal = new TradeJournal(Path.Combine(_dir, "journal"));
        var hub = new MultiAccountHub(new EmptyVault(), store, journal);
        var connection = hub.AddAccount(new AccountConfig
        {
            Label = "Demo Acct", ApiToken = "tok", IsDemo = true, BrainKey = "Growth"
        });

        var runner = new GrowthRunner(
            connection, store, () => new AppSettings { AutonomyEnabled = true },
            () => false, journal, realMoneyUnlocked: () => false);

        runner.StartAsync(GrowthPlan.Default);

        Assert.False(runner.IsRunning);
        Assert.Null(runner.Engine);
        // The gate did not refuse — the connection check did.
        Assert.Contains("not connected", runner.LastActivity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REFUSED", runner.LastActivity);
    }

    [Fact]
    public async Task ObserveRunner_CannotStartOnLockedRealAccount()
    {
        // The bypass this whole change closes: an externally started runner
        // registered with the hub via ObserveRunner used to start freely —
        // now the runner's own gate refuses it.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_gate_obs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            await using var server = RealBroker("CR900300", cts.Token);
            _ = server.RunAsync(cts.Token);

            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var connection = hub.AddAccount(new AccountConfig
            {
                Label = "Obs Acct", ApiToken = "tok", IsDemo = false, BrainKey = "Growth"
            });
            connection.Client.Endpoint = server.WsUrl;
            await hub.ConnectAllAsync();
            Assert.False(connection.ApiVerifiedVirtual, "the fake broker verifies REAL");

            var runner = new GrowthRunner(
                connection, store, () => new AppSettings { AutonomyEnabled = true },
                () => false, journal, realMoneyUnlocked: () => false);
            hub.ObserveRunner(runner, GrowthPlan.Default);

            await runner.StartAsync(GrowthPlan.Default);

            Assert.False(runner.IsRunning, "an observed runner on a locked real account must refuse to start");
            Assert.Null(runner.Engine);
            Assert.Contains("REFUSED", runner.LastActivity);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Runner_StopsItself_WhenGateRefusesAfterSettlement()
    {
        var store = new TradeStore(_dir);
        using var journal = new TradeJournal(Path.Combine(_dir, "journal"));
        var hub = new MultiAccountHub(new EmptyVault(), store, journal);

        var connection = hub.AddAccount(new AccountConfig
        {
            Label = "Mid Acct", ApiToken = "tok", IsDemo = false, BrainKey = "Growth"
        });

        var runner = new GrowthRunner(
            connection, store, () => new AppSettings { AutonomyEnabled = true },
            () => false, journal, realMoneyUnlocked: () => true);
        var engine = new GrowthSessionEngine(GrowthPlan.Default, GrowthPlan.Default.StartBudget);
        runner.TestAttachEngine(engine);
        runner.TestSetRunningState(running: true, "session running");

        // Gate passes before the flip (unlocked real account, API-verified).
        connection.Config.IsDemo = false;
        connection.ApiVerifiedVirtual = false;
        Assert.Null(runner.EnforceRealMoneyGateAfterSettlement());
        Assert.True(runner.IsRunning, "a passing gate must not stop the engine");

        // ── The unlock is revoked mid-session (user closed it in another ──
        // ── surface / hub state reset): the next settlement must stop. ──
        hub.UnlockRealMoney(connection.Config.Id); // arm…
        // …simulate the session-scoped unlock being gone (new process, or the
        // runner was created before the hub armed it — fail closed here).
        var revoked = new GrowthRunner(
            connection, store, () => new AppSettings { AutonomyEnabled = true },
            () => false, journal, realMoneyUnlocked: () => false);
        revoked.TestAttachEngine(new GrowthSessionEngine(GrowthPlan.Default, GrowthPlan.Default.StartBudget));
        revoked.TestSetRunningState(running: true, "session running");

        var refusal = revoked.EnforceRealMoneyGateAfterSettlement();
        Assert.NotNull(refusal);
        Assert.Contains("REFUSED", refusal);
        Assert.False(revoked.IsRunning, "the engine must stop itself");
        Assert.Contains("real-money gate", revoked.SessionStateText);

        // The webhook/toast rails were informed via the gate services (nulls
        // here — the journal audit line is the observable proof).
        journal.Dispose();
        var entries = journal.GetRecent(count: 200);
        Assert.Contains(entries, e => e.Category == "GROWTH_STATE" &&
            e.Details.Contains("real-money-gate") &&
            e.Details.Contains("settlement re-check refused"));
    }

    [Fact]
    public async Task Hub_DropsGateStoppedRunner_NoRestartLadder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_gate_drop_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            await using var server = RealBroker("CR900200", cts.Token);
            _ = server.RunAsync(cts.Token);

            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var connection = hub.AddAccount(new AccountConfig
            {
                Label = "Drop Acct", ApiToken = "tok", IsDemo = false, BrainKey = "Growth"
            });
            connection.Client.Endpoint = server.WsUrl;
            await hub.ConnectAllAsync();

            // Start a legal (unlocked) real-account session.
            hub.UnlockRealMoney(connection.Config.Id);
            var runner = hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false);
            Assert.NotNull(runner);

            // The account state turns hostile mid-session: the config now
            // claims demo while the API verified real — a BlockedConfigMismatch
            // the unlock cannot paper over. The engine must stop itself.
            connection.Config.IsDemo = true;
            runner.EnforceRealMoneyGateAfterSettlement();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (hub.Runners.ContainsKey(connection.Config.Id) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.False(hub.Runners.ContainsKey(connection.Config.Id),
                "the hub must drop a gate-stopped runner");

            // The next start re-evaluates from scratch: with the config fixed
            // (and the unlock still armed), a fresh runner is created — not
            // the stopped zombie.
            connection.Config.IsDemo = false;
            var again = hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false);
            Assert.NotNull(again);
            Assert.NotSame(runner, again);

            // The stop consumed no restart budget.
            Assert.False(hub.IsGivenUp(connection.Config.Id));
            Assert.True(hub.RestartAttempts.TryGetValue(connection.Config.Id, out var attempts)
                ? attempts == 0
                : true, "the gate stop must not consume restart budget");

            hub.StopGrowth(connection.Config.Id);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void GrowthCycle_RiskContext_CarriesTheGateVerdict()
    {
        // The runner's per-cycle risk context must carry the gate's verdict
        // so the RiskEngine blocks a trade even if the start-time gate were
        // bypassed. Drive the internal context builder via a runner wired to
        // a locked real account: a BlockedLocked context must be produced.
        var store = new TradeStore(_dir);
        using var journal = new TradeJournal(Path.Combine(_dir, "journal"));
        var hub = new MultiAccountHub(new EmptyVault(), store, journal);
        var connection = hub.AddAccount(new AccountConfig
        {
            Label = "Ctx Acct", ApiToken = "tok", IsDemo = false, BrainKey = "Growth"
        });
        connection.ApiVerifiedVirtual = false; // API-verified real, locked

        var runner = new GrowthRunner(
            connection, store, () => new AppSettings { AutonomyEnabled = true },
            () => false, journal, realMoneyUnlocked: () => false);

        var context = runner.TestBuildRiskContext();
        Assert.Equal(RealMoneyDecision.BlockedLocked, context.RealMoney);

        // And the RiskEngine must refuse to trade on it.
        var verdict = new RiskEngine(new AppSettings()).Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, 2m, "test"), context);
        Assert.False(verdict.Allowed);
        Assert.Contains("real-money gate", verdict.Reason);
    }

    /// <summary>Fake broker authorizing a REAL (non-virtual) account.</summary>
    private static FakeDerivServer RealBroker(string loginId, CancellationToken ct) =>
        new(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();
            if (req.TryGetProperty("authorize", out _))
            {
                return $@"{{""msg_type"":""authorize"",""req_id"":{reqId},""authorize"":{{""balance"":250.00,""currency"":""USD"",""loginid"":""{loginId}"",""is_virtual"":0}}}}";
            }
            if (req.TryGetProperty("ticks_history", out _))
            {
                var (prices, times) = History();
                return $@"{{""msg_type"":""history"",""req_id"":{reqId},""history"":{{""prices"":[{prices}],""times"":[{times}]}},""pip_size"":5}}";
            }
            if (req.TryGetProperty("ticks", out _))
            {
                return $@"{{""msg_type"":""ticks"",""req_id"":{reqId},""subscription"":{{""id"":""sub-real""}}}}";
            }
            return "";
        }, ct);

    private sealed class EmptyVault : IAccountVault
    {
        public IReadOnlyList<AccountConfig> Load() => [];
        public void Save(IReadOnlyList<AccountConfig> accounts) { }
    }

    private sealed class MemoryVault : IAccountVault
    {
        public List<AccountConfig> Items { get; } = new();
        public IReadOnlyList<AccountConfig> Load() => Items.ToArray();
        public void Save(IReadOnlyList<AccountConfig> accounts)
        {
            Items.Clear();
            Items.AddRange(accounts);
        }
    }
}
