using System.Globalization;
using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core;
using DongGfx.Core.Brain;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using static DongGfx.App.Tests.GrowthTestHarness;

namespace DongGfx.App.Tests;

/// <summary>
/// The real-money gate enforced at the hub level: a growth engine must not
/// start on an API-verified real-money account unless the session unlock was
/// armed, and the gate must re-apply to every start path including the
/// automatic restart ladder. Demo accounts pass through untouched, and the
/// refusal is journaled + announced.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RealMoney")]
public class RealMoneyGateHubTests
{
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Fake broker authorizing a REAL (non-virtual) Deriv account.</summary>
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

    /// <summary>Fake broker authorizing a virtual (demo) account.</summary>
    private static FakeDerivServer DemoBroker(string loginId, CancellationToken ct) =>
        new(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();
            if (req.TryGetProperty("authorize", out _))
            {
                return $@"{{""msg_type"":""authorize"",""req_id"":{reqId},""authorize"":{{""balance"":100.00,""currency"":""USD"",""loginid"":""{loginId}"",""is_virtual"":1}}}}";
            }

            if (req.TryGetProperty("ticks_history", out _))
            {
                var (prices, times) = History();
                return $@"{{""msg_type"":""history"",""req_id"":{reqId},""history"":{{""prices"":[{prices}],""times"":[{times}]}},""pip_size"":5}}";
            }

            if (req.TryGetProperty("ticks", out _))
            {
                return $@"{{""msg_type"":""ticks"",""req_id"":{reqId},""subscription"":{{""id"":""sub-demo""}}}}";
            }

            return "";
        }, ct);

    [Fact]
    public async Task RealMoneyGate_DemoAccount_PassesThrough()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            await using var server = DemoBroker("CR900001", cts.Token);
            _ = server.RunAsync(cts.Token);

            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var connection = hub.AddAccount(new AccountConfig
            {
                Label = "Demo Acct", ApiToken = "tok", IsDemo = true, BrainKey = "Growth"
            });
            connection.Client.Endpoint = server.WsUrl;
            await hub.ConnectAllAsync();

            Assert.True(connection.ApiVerifiedVirtual, "the fake broker reports is_virtual=1");
            Assert.Equal("demo (API)", connection.VerifiedText);

            var runner = hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false);
            Assert.NotNull(runner);

            hub.StopGrowth(connection.Config.Id);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RealMoneyGate_RefusesRealAccount_UntilUnlocked()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            await using var server = RealBroker("CR900002", cts.Token);
            _ = server.RunAsync(cts.Token);

            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var refusals = new List<string>();
            hub.RealMoneyRefused += (_, explanation) => refusals.Add(explanation);

            // Config claims demo but the API verifies a REAL account — the
            // loudest refusal: the config itself is wrong.
            var connection = hub.AddAccount(new AccountConfig
            {
                Label = "Sneaky Config", ApiToken = "tok", IsDemo = true, BrainKey = "Growth"
            });
            connection.Client.Endpoint = server.WsUrl;
            await hub.ConnectAllAsync();

            Assert.False(connection.ApiVerifiedVirtual, "the fake broker reports is_virtual=0 (real)");
            Assert.Equal("REAL (API)", connection.VerifiedText);

            // ── Locked (config demo + API real): refused, journaled, announced. ──
            var runner = hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false);
            Assert.Null(runner);
            Assert.Empty(hub.Runners);
            Assert.NotEmpty(refusals);
            Assert.Contains("marked demo", refusals[0]);

            // ── After fixing the config (real account marked real), the ──
            // ── API verdict is consistent but the session is still locked. ──
            connection.Config.IsDemo = false;
            Assert.Null(hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false));
            Assert.Empty(hub.Runners);
            Assert.Contains(refusals, e => e.Contains("locked", StringComparison.OrdinalIgnoreCase));

            // ── Unlock armed → Allowed. ──
            hub.UnlockRealMoney(connection.Config.Id);
            runner = hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false);
            Assert.NotNull(runner);
            hub.StopGrowth(connection.Config.Id);
            Assert.Empty(hub.Runners);

            // Both refusals (mismatch + locked) are journaled for audit.
            journal.Dispose();
            var entries = journal.GetRecent(count: 200);
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" &&
                e.Details.Contains("real-money-gate") && e.Details.Contains("BlockedConfigMismatch"));
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" &&
                e.Details.Contains("real-money-gate") && e.Details.Contains("BlockedLocked"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RealMoneyGate_UnverifiedAccount_IsRefused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            // Connect against an ordinary demo broker, then wipe the API
            // verdict to simulate an account that connected but was never
            // authorized/verified (no is_virtual seen). The gate must refuse
            // a real-config engine even though the connection is live.
            await using var server = DemoBroker("CR900004", cts.Token);
            _ = server.RunAsync(cts.Token);

            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var refusals = new List<string>();
            hub.RealMoneyRefused += (_, explanation) => refusals.Add(explanation);

            var connection = hub.AddAccount(new AccountConfig
            {
                Label = "Ghost Acct", ApiToken = "tok", IsDemo = false, BrainKey = "Growth"
            });
            connection.Client.Endpoint = server.WsUrl;
            await hub.ConnectAllAsync();
            Assert.True(connection.IsConnected);

            connection.ApiVerifiedVirtual = null; // the API has not verified yet
            connection.VerifiedText = "unverified";

            hub.UnlockRealMoney(connection.Config.Id); // unlock alone is not enough
            Assert.Null(hub.StartGrowth(connection, GrowthPlan.Default,
                () => new AppSettings { AutonomyEnabled = true }, () => false));
            Assert.Empty(hub.Runners);
            Assert.Contains(refusals, e => e.Contains("not been verified", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RealMoneyGate_RefusalPath_IsIdempotent_UnderConcurrentStarts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            await using var server = RealBroker("CR900003", cts.Token);
            _ = server.RunAsync(cts.Token);

            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var connection = hub.AddAccount(new AccountConfig
            {
                Label = "Raced Acct", ApiToken = "tok", IsDemo = true, BrainKey = "Growth"
            });
            connection.Client.Endpoint = server.WsUrl;
            await hub.ConnectAllAsync();

            // Parallel starts on a locked real account: all refused, none
            // created a runner, and no exception escapes.
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                hub.StartGrowth(connection, GrowthPlan.Default,
                    () => new AppSettings { AutonomyEnabled = true }, () => false))).ToArray();
            await Task.WhenAll(tasks);
            Assert.All(tasks, t => Assert.Null(t.Result));
            Assert.Empty(hub.Runners);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>In-memory vault so the gate tests never touch %APPDATA%.</summary>
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
