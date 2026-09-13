using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.Core;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;
using static Tf.App.Tests.GrowthTestHarness;

namespace Tf.App.Tests;

/// <summary>
/// Multi-account hub end-to-end scenarios: portfolio drawdown governor
/// (trip, latch, re-arm, pre-trip warning), bounded auto-restart and
/// give-up, webhook announcements, mid-session disconnect/resume, and
/// per-account isolation across two or three concurrent sessions — all
/// against fake Deriv brokers, one server per account. Split from
/// GrowthRunnerEndToEndTests so the hub and runner suites form two xunit
/// collections and can run in parallel.
/// </summary>
[Trait("Category", "Integration")]
public class MultiAccountHubEndToEndTests
{
    private const int HistoryTickCount = 30;

    // The fake server settles every contract on the second proposal_open_contract
    // poll, and DerivClient polls every 2 s in production — a flat 2 s sleep per
    // settled trade. Test clients shorten the interval so the suites stay
    // wait-based without paying that sleep; poll-count asserts are lower bounds
    // and stay valid.
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions TradeLogOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        Exception? lastError = null;
        var deadline = DateTime.UtcNow + timeout;
        var polls = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex; // transient — treat as "not yet"
            }

            polls++;
            await Task.Delay(50);
        }

        bool finalResult;
        try
        {
            finalResult = condition();
        }
        catch (Exception ex)
        {
            finalResult = false;
            lastError ??= ex;
        }

        var suffix = lastError is null
            ? ""
            : $"\nThe condition kept throwing {lastError.GetType().Name}: {lastError.Message}";
        Assert.True(finalResult,
            $"Timed out after {timeout.TotalSeconds:0.#}s ({polls} polls) waiting for {what}.{suffix}");
    }

    private static Trade MakeGrowthTrade(Guid accountId, string accountName, string contractId,
        decimal stake, decimal profit, DateTimeOffset settledAt) => new(
            Guid.NewGuid(), "frxEURUSD",
            profit >= 0 ? Direction.Rise : Direction.Fall,
            stake, "USD", 1.17, 1700000300, contractId,
            profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
            profit, 1.165, 1700000600, settledAt,
            accountId, accountName, TradeSource.Growth);

    private static void ShiftTradeLogToPreviousDay(string tradeLogPath)
    {
        var trades = JsonSerializer.Deserialize<List<Trade>>(File.ReadAllText(tradeLogPath), TradeLogOptions)
            ?? throw new InvalidOperationException("trade log did not deserialize");
        for (var i = 0; i < trades.Count; i++)
        {
            trades[i] = trades[i] with { SettledAt = trades[i].SettledAt.AddDays(-1) };
        }
        File.WriteAllText(tradeLogPath, JsonSerializer.Serialize(trades, TradeLogOptions));
    }

    /// <summary>In-memory vault so the hub tests never touch %APPDATA%.</summary>
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

    /// <summary>
    /// Two accounts run growth sessions through one MultiAccountHub against
    /// two independent fake brokers. Alpha's broker always settles wins; Beta's
    /// always settles losses. After both first settlements — and a hub-level
    /// stop/restart of Beta that takes a second loss — every surface (the
    /// shared TradeStore, per-account risk context, session replay, and the
    /// session engines) must stay strictly isolated per account.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_TwoGrowthSessions_KeepBankrollsAndTradesIsolated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));
        var liveTickIndex = 0;

        Func<JsonElement, string?> tickGenerator = _ =>
        {
            // Two servers share this closure with concurrent clients — the
            // index must be atomic or quotes can duplicate under load.
            var index = Interlocked.Increment(ref liveTickIndex);
            var quote = (1.2000 - 0.001 * (HistoryTickCount + index))
                .ToString("F5", CultureInfo.InvariantCulture);
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":{quote},\"ask\":{quote},\"bid\":{quote},\"epoch\":{epoch},\"pip_size\":5}}}}";
        };

        // Alpha's broker: every contract settles WON (+90% of the stake).
        var proposalA = 0;
        var buyA = 0;
        var proposalStakeA = new Dictionary<string, decimal>();
        var contractStakeA = new Dictionary<string, decimal>();
        var openA = new HashSet<string>();
        var serverA = new FakeDerivServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCHUBA\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-hub-a\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalA++;
                var id = $"PROP-A-{proposalA}";
                proposalStakeA[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyA++;
                var stake = proposalStakeA[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-A-{buyA}";
                contractStakeA[cid] = stake;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{cid}\",\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = contractStakeA[cid];
                if (openA.Add(cid))
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"won\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.17800,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":{(stake * 0.90m).ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
            }

            return "";
        }, cts.Token, tickGenerator);

        // Beta's broker: every contract settles LOST (−the whole stake).
        var proposalB = 0;
        var buyB = 0;
        var proposalStakeB = new Dictionary<string, decimal>();
        var contractStakeB = new Dictionary<string, decimal>();
        var openB = new HashSet<string>();
        var serverB = new FakeDerivServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCHUBB\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-hub-b\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalB++;
                var id = $"PROP-B-{proposalB}";
                proposalStakeB[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyB++;
                var stake = proposalStakeB[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-B-{buyB}";
                contractStakeB[cid] = stake;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{cid}\",\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = contractStakeB[cid];
                if (openB.Add(cid))
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"lost\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.16500,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":{(-stake).ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
            }

            return "";
        }, cts.Token, tickGenerator);

        _ = serverA.RunAsync(cts.Token);
        _ = serverB.RunAsync(cts.Token);
        await using var serverADisposal = serverA;
        await using var serverBDisposal = serverB;

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_hub_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var configA = new AccountConfig
            {
                Label = "Hub Alpha",
                ApiToken = "token-alpha",
                IsDemo = true,
                BrainKey = "Growth"
            };
            var configB = new AccountConfig
            {
                Label = "Hub Beta",
                ApiToken = "token-beta",
                IsDemo = true,
                BrainKey = "Growth"
            };

            var connectionA = hub.AddAccount(configA);
            connectionA.Client.Endpoint = serverA.WsUrl;
            connectionA.Client.SettlementPollInterval = FastPollInterval;
            var connectionB = hub.AddAccount(configB);
            connectionB.Client.Endpoint = serverB.WsUrl;
            connectionB.Client.SettlementPollInterval = FastPollInterval;
            Assert.Equal(2, hub.Accounts.Count);

            // One account per API token — duplicates are refused.
            Assert.Throws<InvalidOperationException>(
                () => hub.AddAccount(new AccountConfig { Label = "Dup", ApiToken = "token-alpha" }));
            Assert.Equal(2, hub.Accounts.Count);

            await hub.ConnectAllAsync();
            Assert.True(connectionA.IsConnected, "alpha should connect to its fake broker");
            Assert.True(connectionB.IsConnected, "beta should connect to its fake broker");

            // IntervalMinutes = 60 keeps each session to exactly one immediate
            // cycle per start — deterministic, and no surprise second trades.
            var planA = GrowthPlan.Default with { CooldownMinutesAfterLoss = 10, IntervalMinutes = 60 };
            var planB = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, IntervalMinutes = 60 };
            var settings = () => new AppSettings { AutonomyEnabled = true };

            var runnerA = hub.StartGrowth(connectionA, planA, settings, () => false);
            var runnerB = hub.StartGrowth(connectionB, planB, settings, () => false);
            Assert.NotNull(runnerA);
            Assert.NotNull(runnerB);
            Assert.Same(runnerA, hub.Runners[configA.Id]);
            Assert.Same(runnerB, hub.Runners[configB.Id]);
            Assert.NotSame(runnerA!.Engine, runnerB!.Engine);

            // ── Both sessions trade their immediate first cycle in parallel: ──
            // ── alpha wins against its broker, beta loses against its own. ──
            await WaitForAsync(() => store.ForAccount(configA.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "alpha's winning trade");
            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "beta's losing trade");
            await WaitForAsync(() => runnerA.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "alpha bankroll after its win");
            await WaitForAsync(() => runnerB.Engine!.Bankroll == 4.00m,
                TimeSpan.FromSeconds(5), "beta bankroll after its loss");

            // Each partition holds exactly one trade, correctly tagged.
            Assert.Equal(1, proposalA);
            Assert.Equal(1, buyA);
            var alphaTrade = Assert.Single(store.ForAccount(configA.Id, TradeSource.Growth));
            Assert.Equal(configA.Id, alphaTrade.AccountId);
            Assert.Equal("Hub Alpha", alphaTrade.AccountName);
            Assert.True(alphaTrade.IsWin);
            Assert.Equal(1.00m, alphaTrade.Stake);

            var betaTrade = Assert.Single(store.ForAccount(configB.Id, TradeSource.Growth));
            Assert.Equal(configB.Id, betaTrade.AccountId);
            Assert.Equal("Hub Beta", betaTrade.AccountName);
            Assert.False(betaTrade.IsWin);
            Assert.Equal(1.00m, betaTrade.Stake);

            // Session engines moved in opposite directions — proof that each
            // runner applied only its own account's settlement.
            Assert.Equal(0, runnerA.Engine!.LossStreak);
            Assert.Equal(1, runnerB.Engine!.LossStreak);

            // ── Restart Beta through the hub: its session replays ONLY its own ──
            // ── trades (streak 1, $4.00) and takes a second loss ($1.60 → $2.40). ──
            hub.StopGrowth(configB.Id);
            Assert.False(hub.Runners.ContainsKey(configB.Id));
            Assert.True(hub.Runners.ContainsKey(configA.Id), "stopping beta must not stop alpha");

            var runnerB2 = hub.StartGrowth(connectionB, planB, settings, () => false);
            Assert.NotNull(runnerB2);
            Assert.NotSame(runnerB, runnerB2);
            await WaitForAsync(() => runnerB2!.Engine!.Bankroll == 4.00m && runnerB2.Engine.LossStreak == 1,
                TimeSpan.FromSeconds(10), "beta replaying only its own session state");

            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "beta's second loss");
            await WaitForAsync(() => runnerB2!.Engine!.Bankroll == 2.40m,
                TimeSpan.FromSeconds(5), "beta bankroll after its second loss");
            Assert.Equal(2, runnerB2!.Engine!.LossStreak);
            Assert.Equal(1.60m, store.ForAccount(configB.Id, TradeSource.Growth)[1].Stake);

            // ── Final isolation sweep: two clean per-account partitions. ──
            var alphaTrades = store.ForAccount(configA.Id, TradeSource.Growth);
            var betaTrades = store.ForAccount(configB.Id, TradeSource.Growth);
            Assert.Single(alphaTrades);
            Assert.Equal(2, betaTrades.Count);
            Assert.All(alphaTrades.Concat(betaTrades), t =>
                Assert.True(t.AccountId == configA.Id || t.AccountId == configB.Id));
            Assert.Equal(5.90m, runnerA.Engine!.Bankroll);
            Assert.Equal(0, runnerA.Engine.LossStreak);
            Assert.True(runnerA.IsRunning);

            await hub.StopGrowthAllAsync();
            await hub.RemoveAccountAsync(connectionA);
            await hub.RemoveAccountAsync(connectionB);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Three accounts trade growth sessions concurrently through one
    /// MultiAccountHub and one shared TradeStore (two always-win brokers,
    /// one always-lose broker). The shared trade log, the journal entries,
    /// and the per-account daily summaries must all stay strictly partitioned
    /// by account id — no entry ever leaks across accounts.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_ThreeConcurrentSessions_PartitionTradesJournalAndSummaries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var countersA = new BrokerCounters();
        var countersB = new BrokerCounters();
        var countersC = new BrokerCounters();

        var serverA = new FakeDerivServer(BrokerScript(
            "VRTCHUB3A", "H3A", countersA, AllWins), cts.Token);
        var serverB = new FakeDerivServer(BrokerScript(
            "VRTCHUB3B", "H3B", countersB, AllLosses), cts.Token);
        var serverC = new FakeDerivServer(BrokerScript(
            "VRTCHUB3C", "H3C", countersC, AllWins), cts.Token);

        _ = serverA.RunAsync(cts.Token);
        _ = serverB.RunAsync(cts.Token);
        _ = serverC.RunAsync(cts.Token);
        await using var serverADisposal = serverA;
        await using var serverBDisposal = serverB;
        await using var serverCDisposal = serverC;

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_hub3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var configA = new AccountConfig { Label = "Hub3 Alpha", ApiToken = "token-a", IsDemo = true, BrainKey = "Growth" };
            var configB = new AccountConfig { Label = "Hub3 Beta", ApiToken = "token-b", IsDemo = true, BrainKey = "Growth" };
            var configC = new AccountConfig { Label = "Hub3 Gamma", ApiToken = "token-c", IsDemo = true, BrainKey = "Growth" };

            var connectionA = hub.AddAccount(configA);
            connectionA.Client.Endpoint = serverA.WsUrl;
            connectionA.Client.SettlementPollInterval = FastPollInterval;
            var connectionB = hub.AddAccount(configB);
            connectionB.Client.Endpoint = serverB.WsUrl;
            connectionB.Client.SettlementPollInterval = FastPollInterval;
            var connectionC = hub.AddAccount(configC);
            connectionC.Client.Endpoint = serverC.WsUrl;
            connectionC.Client.SettlementPollInterval = FastPollInterval;

            await hub.ConnectAllAsync();
            Assert.True(connectionA.IsConnected);
            Assert.True(connectionB.IsConnected);
            Assert.True(connectionC.IsConnected);

            // IntervalMinutes = 60 → exactly one immediate cycle per start.
            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, IntervalMinutes = 60 };
            var settings = () => new AppSettings { AutonomyEnabled = true };

            var runnerA = hub.StartGrowth(connectionA, plan, settings, () => false);
            var runnerB = hub.StartGrowth(connectionB, plan, settings, () => false);
            var runnerC = hub.StartGrowth(connectionC, plan, settings, () => false);
            Assert.NotNull(runnerA);
            Assert.NotNull(runnerB);
            Assert.NotNull(runnerC);
            Assert.NotSame(runnerA!.Engine, runnerB!.Engine);
            Assert.NotSame(runnerB.Engine, runnerC!.Engine);

            // ── First wave: all three sessions settle their immediate cycle. ──
            await WaitForAsync(() => store.ForAccount(configA.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "alpha's win");
            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "beta's loss");
            await WaitForAsync(() => store.ForAccount(configC.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "gamma's win");
            await WaitForAsync(() => runnerA.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "alpha bankroll");
            await WaitForAsync(() => runnerB.Engine!.Bankroll == 4.00m,
                TimeSpan.FromSeconds(5), "beta bankroll");
            await WaitForAsync(() => runnerC.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "gamma bankroll");

            // ── Beta restarts through the hub and takes a second (bigger) loss. ──
            hub.StopGrowth(configB.Id);
            var runnerB2 = hub.StartGrowth(connectionB, plan, settings, () => false);
            Assert.NotNull(runnerB2);
            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "beta's second loss");
            await WaitForAsync(() => runnerB2!.Engine!.Bankroll == 2.40m,
                TimeSpan.FromSeconds(5), "beta bankroll after its second loss");

            // ── The shared trade log is strictly partitioned per account. ──
            var tradesA = store.ForAccount(configA.Id, TradeSource.Growth);
            var tradesB = store.ForAccount(configB.Id, TradeSource.Growth);
            var tradesC = store.ForAccount(configC.Id, TradeSource.Growth);
            Assert.Single(tradesA);
            Assert.Equal(2, tradesB.Count);
            Assert.Single(tradesC);
            Assert.All(tradesA, t => Assert.Equal(configA.Id, t.AccountId));
            Assert.All(tradesB, t => Assert.Equal(configB.Id, t.AccountId));
            Assert.All(tradesC, t => Assert.Equal(configC.Id, t.AccountId));
            Assert.True(tradesA[0].IsWin);
            Assert.All(tradesB, t => Assert.False(t.IsWin));
            Assert.Equal(1.00m, tradesB[0].Stake);
            Assert.Equal(1.60m, tradesB[1].Stake); // recovery ladder, beta-only
            Assert.True(tradesC[0].IsWin);
            Assert.Equal(1, countersA.Buys);
            Assert.Equal(2, countersB.Buys);
            Assert.Equal(1, countersC.Buys);

            // ── Per-account daily summaries are partitioned too. ──
            var today = DateTimeOffset.UtcNow;
            var summaryA = store.SummaryFor(today, configA.Id, TradeSource.Growth);
            var summaryB = store.SummaryFor(today, configB.Id, TradeSource.Growth);
            var summaryC = store.SummaryFor(today, configC.Id, TradeSource.Growth);
            Assert.Equal((1, 1, 0, 0.90m), (summaryA.Count, summaryA.Wins, summaryA.Losses, summaryA.NetProfit));
            Assert.Equal((2, 0, 2, -2.60m), (summaryB.Count, summaryB.Wins, summaryB.Losses, summaryB.NetProfit));
            Assert.Equal((1, 1, 0, 0.90m), (summaryC.Count, summaryC.Wins, summaryC.Losses, summaryC.NetProfit));

            // ── Journal entries are partitioned: each account's settlements ──
            // ── carry its own id and outcome, with no cross-contamination. ──
            journal.Dispose(); // flush queued entries to disk
            var entries = journal.GetRecent(null, 500);

            var settlementsA = entries.Where(e => e.AccountId == configA.Id && e.Category == "TRADE_SETTLEMENT").ToList();
            var settlementsB = entries.Where(e => e.AccountId == configB.Id && e.Category == "TRADE_SETTLEMENT").ToList();
            var settlementsC = entries.Where(e => e.AccountId == configC.Id && e.Category == "TRADE_SETTLEMENT").ToList();
            Assert.Single(settlementsA);
            Assert.Equal(2, settlementsB.Count);
            Assert.Single(settlementsC);
            Assert.All(settlementsA, e => Assert.Contains("\"Won\":true", e.Details));
            Assert.All(settlementsB, e => Assert.Contains("\"Won\":false", e.Details));
            Assert.All(settlementsC, e => Assert.Contains("\"Won\":true", e.Details));

            // No account's journal entry references another account's broker.
            Assert.DoesNotContain(entries.Where(e => e.AccountId == configA.Id), e => e.Details.Contains("H3B") || e.Details.Contains("H3C"));
            Assert.DoesNotContain(entries.Where(e => e.AccountId == configB.Id), e => e.Details.Contains("H3A") || e.Details.Contains("H3C"));
            Assert.DoesNotContain(entries.Where(e => e.AccountId == configC.Id), e => e.Details.Contains("H3A") || e.Details.Contains("H3B"));

            await hub.StopGrowthAllAsync();
            await hub.RemoveAccountAsync(connectionA);
            await hub.RemoveAccountAsync(connectionB);
            await hub.RemoveAccountAsync(connectionC);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Account disconnect mid-session: the fake broker aborts the socket, the
    /// hub stops each affected runner, the DerivClients reconnect and
    /// re-authorize by themselves, and sessions restarted through the hub
    /// resume trading from the replayed bankrolls.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_DisconnectMidSession_StopsRunner_AndResumesAfterReconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var counters = new BrokerCounters();
        var server = new FakeDerivServer(BrokerScript(
            "VRTCHUBDC", "DC", counters, AllWins), cts.Token);
        _ = server.RunAsync(cts.Token);
        await using var serverDisposal = server;

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_dc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var configA = new AccountConfig { Label = "Disc Alpha", ApiToken = "token-dc-a", IsDemo = true, BrainKey = "Growth" };
            var configB = new AccountConfig { Label = "Disc Beta", ApiToken = "token-dc-b", IsDemo = true, BrainKey = "Growth" };

            var connectionA = hub.AddAccount(configA);
            connectionA.Client.Endpoint = server.WsUrl;
            connectionA.Client.SettlementPollInterval = FastPollInterval;
            var connectionB = hub.AddAccount(configB);
            connectionB.Client.Endpoint = server.WsUrl;
            connectionB.Client.SettlementPollInterval = FastPollInterval;

            await hub.ConnectAllAsync();
            Assert.True(connectionA.IsConnected);
            Assert.True(connectionB.IsConnected);

            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, IntervalMinutes = 60 };
            var settings = () => new AppSettings { AutonomyEnabled = true };
            var runnerA = hub.StartGrowth(connectionA, plan, settings, () => false);
            var runnerB = hub.StartGrowth(connectionB, plan, settings, () => false);
            Assert.NotNull(runnerA);
            Assert.NotNull(runnerB);

            // Both sessions settle their immediate cycle (wins against the broker).
            await WaitForAsync(() => store.ForAccount(configA.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "alpha's pre-drop win");
            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "beta's pre-drop win");
            await WaitForAsync(() => runnerA!.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "alpha bankroll pre-drop");
            await WaitForAsync(() => runnerB!.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "beta bankroll pre-drop");

            // ── The fake broker aborts every socket: a network cut. ──
            server.DropConnection();
            await WaitForAsync(() => server.ConnectionCount == 0,
                TimeSpan.FromSeconds(5), "the socket drop");

            // The hub stops each runner whose connection went down. The hub
            // removes the runner from Runners before it journals the stop, so
            // both the removal and the journal line are awaited — asserting
            // immediately after removal races the hub's bookkeeping under
            // runner load.
            await WaitForAsync(
                () => !hub.Runners.ContainsKey(configA.Id) && !hub.Runners.ContainsKey(configB.Id),
                TimeSpan.FromSeconds(10), "hub stopping runners after the disconnect");
            await WaitForAsync(() => runnerA!.LastActivity.Contains("Stopped — account disconnected."),
                TimeSpan.FromSeconds(10), "alpha journal notes the disconnect");
            await WaitForAsync(() => runnerB!.LastActivity.Contains("Stopped — account disconnected."),
                TimeSpan.FromSeconds(10), "beta journal notes the disconnect");

            // The DerivClients reconnect and re-authorize by themselves.
            await WaitForAsync(() => connectionA.IsConnected && connectionB.IsConnected,
                TimeSpan.FromSeconds(20), "automatic reconnect");
            Assert.Equal("VRTCHUBDC", connectionA.Client.LoginId);

            // ── Restarted sessions replay their bankrolls and trade again. ──
            GrowthRunner? runnerA2 = null;
            GrowthRunner? runnerB2 = null;
            await WaitForAsync(
                () =>
                {
                    runnerA2 ??= hub.StartGrowth(connectionA, plan, settings, () => false);
                    runnerB2 ??= hub.StartGrowth(connectionB, plan, settings, () => false);
                    return runnerA2 is not null && runnerB2 is not null;
                },
                TimeSpan.FromSeconds(20), "restarting both sessions after the reconnect");

            Assert.Equal(5.90m, runnerA2!.Engine!.Bankroll); // replayed, not reset
            Assert.Equal(5.90m, runnerB2!.Engine!.Bankroll);

            await WaitForAsync(() => store.ForAccount(configA.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "alpha's post-resume win");
            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "beta's post-resume win");
            await WaitForAsync(() => runnerA2.Engine.Bankroll == 6.962m,
                TimeSpan.FromSeconds(5), "alpha bankroll post-resume");
            Assert.Equal(1.18m, store.ForAccount(configA.Id, TradeSource.Growth)[1].Stake);
            Assert.Equal(4, counters.Buys); // two buys per account × two accounts

            await hub.StopGrowthAllAsync();
            await hub.RemoveAccountAsync(connectionA);
            await hub.RemoveAccountAsync(connectionB);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Repeated broker failures: the scheduler exits after its consecutive-
    /// failure cap, the hub automatically restarts the session on growing
    /// exponential delays (journaled per restart, announced via toast/webhook
    /// hooks), and a successful cycle recovers trading. A second wave of
    /// persistent failures then exhausts the bounded budget: after
    /// MaxAutoRestarts the hub gives up and sends nothing further until a
    /// manual start resets the counter.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_RepeatedBrokerFailures_AutoRestartsBounded_AndGivesUp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));

        var proposalRequests = 0;
        var buyRequests = 0;
        var failFirstProposals = 3;   // wave 1: the first three proposals fail
        var failAllProposals = false; // wave 2: every proposal fails
        var lastHubActivity = "";
        var proposalStakeById = new Dictionary<string, decimal>();
        var contractStakeById = new Dictionary<string, decimal>();
        var openContracts = new HashSet<string>();
        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));
        var liveTickIndex = 0;

        var server = new FakeDerivServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCAUTO1\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-autorestart\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                if (failAllProposals || proposalRequests <= failFirstProposals)
                    return $"{{\"msg_type\":\"error\",\"req_id\":{reqId},\"error\":{{\"code\":\"MarketIsClosed\",\"message\":\"synthetic proposal failure\"}}}}";

                var id = $"PROP-AR-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-AR-{buyRequests}";
                contractStakeById[cid] = stake;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{cid}\",\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = contractStakeById[cid];
                if (openContracts.Add(cid))
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"won\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.17800,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":{(stake * 0.90m).ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
            }

            return "";
        }, cts.Token, _ =>
        {
            liveTickIndex++;
            var quote = (1.2000 - 0.001 * (HistoryTickCount + liveTickIndex))
                .ToString("F5", CultureInfo.InvariantCulture);
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":{quote},\"ask\":{quote},\"bid\":{quote},\"epoch\":{epoch},\"pip_size\":5}}}}";
        });
        _ = server.RunAsync(cts.Token);
        await using var serverDisposal = server;

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_auto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            // Announcement timestamps per attempt, for the growing-gap asserts.
            var restartAnnouncedAt = new Dictionary<int, long>();
            var gaveUpSeen = false;
            hub.GrowthActivity += (_, line) =>
            {
                lastHubActivity = line;
                var match = System.Text.RegularExpressions.Regex.Match(line, "attempt (\\d)/3");
                if (match.Success)
                {
                    restartAnnouncedAt[int.Parse(match.Groups[1].Value)] = Environment.TickCount64;
                }
            };
            hub.RestartStateChanged += (_, _, gaveUp) => gaveUpSeen = gaveUp;

            var config = new AccountConfig { Label = "Auto Demo", ApiToken = "token-auto", IsDemo = true, BrainKey = "Growth" };
            var connection = hub.AddAccount(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await hub.ConnectAllAsync();
            Assert.True(connection.IsConnected);

            // 200 ms cycle backoff and a 0.4 s restart base keep the
            // exponential ladder (0.4 / 1.2 / 3.6 s) observable but fast; the
            // budget and factor are injected explicitly to prove the plan
            // drives the policy.
            var plan = GrowthPlan.Default with
            {
                CooldownMinutesAfterLoss = 0,
                FailureBackoffSeconds = 0.2,
                MaxAutoRestarts = 3,
                RestartBaseDelaySeconds = 0.4,
                RestartBackoffFactor = 3.0
            };
            var settings = () => new AppSettings { AutonomyEnabled = true };

            // ── Wave 1: three failures → self-stop → auto-restart recovers. ──
            var runner = hub.StartGrowth(connection, plan, settings, () => false);
            Assert.NotNull(runner);

            await WaitForAsync(() => lastHubActivity.Contains("attempt 1/3"),
                TimeSpan.FromSeconds(20), "the first auto-restart");

            // The restarted session's first proposal succeeds: one clean win.
            // (No separate "zero buys so far" assert here — the restart line is
            // raised before the new runner starts, so its winning buy may land
            // first; the exact final counters below prove no trade happened
            // during the failed waves.)
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "the post-restart win");
            Assert.True(store.ForAccount(config.Id, TradeSource.Growth)[0].IsWin);
            Assert.Equal(1, buyRequests);
            Assert.Equal(4, proposalRequests); // 3 failed + 1 successful

            // ── Wave 2: manual restart (resets the counter), then persistent ──
            // ── failure until the hub gives up. ──
            failAllProposals = true;
            hub.StopGrowth(config.Id);
            runner = hub.StartGrowth(connection, plan, settings, () => false);
            Assert.NotNull(runner);

            // 3 fails × 4 starts (initial + 3 auto-restarts) = 12 more failed
            // proposals, then the hub stops trying. Wait for the give-up line.
            await WaitForAsync(() => lastHubActivity.Contains("3 automatic restarts used"),
                TimeSpan.FromSeconds(60), "the bounded give-up");

            // ── The restart delays grow exponentially: the gap between the ──
            // ── announcements 2→3 must cover attempt 2's 1.2 s delay plus the ──
            // ── three failed cycles (~0.6 s) that precede announcement 3. ──
            Assert.True(restartAnnouncedAt.ContainsKey(2), "restart 2 should have been announced");
            Assert.True(restartAnnouncedAt.ContainsKey(3), "restart 3 should have been announced");
            var gap2to3 = restartAnnouncedAt[3] - restartAnnouncedAt[2];
            Assert.True(gap2to3 >= 1_100,
                $"gap between attempt 2 and 3 ({gap2to3} ms) must cover the 1.2 s exponential delay");

            Assert.True(gaveUpSeen, "the hub must raise RestartStateChanged(gaveUp: true)");
            Assert.True(hub.IsGivenUp(config.Id), "the hub must report the exhausted budget");
            Assert.Equal(3, hub.RestartAttempts[config.Id]);

            await Task.Delay(1_500, cts.Token);
            Assert.Equal(16, proposalRequests); // stable: nothing further is sent
            Assert.Equal(1, buyRequests);
            Assert.False(hub.Runners.ContainsKey(config.Id), "the hub must give up, not keep restarting");

            // Every restart (and the give-up) is journaled for audit, with the
            // exponential delay each attempt waited out: 0.4 → 1.2 → 3.6 s.
            journal.Dispose();
            var entries = journal.GetRecent(config.Id, 200);
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" && e.Details.Contains("restart 1/3") && e.Details.Contains("(delay 0.4 s)"));
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" && e.Details.Contains("restart 2/3") && e.Details.Contains("(delay 1.2 s)"));
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" && e.Details.Contains("restart 3/3") && e.Details.Contains("(delay 3.6 s)"));
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" && e.Details.Contains("gave up after 3 restarts"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Two accounts both lose $1.00 through one hub with a $1.50 portfolio
    /// drawdown cap: the second settlement drives the combined net to −$2.00,
    /// breaching the cap regardless of settlement order. The governor stops
    /// the hub's session, journals the trip, and refuses starts until an
    /// explicit re-arm re-baselines the drawdown; the post-re-arm win must
    /// not re-trip it.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_PortfolioDrawdownCap_TripsStopsEverything_AndRearms()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));

        var lastHubActivity = "";
        var governorNet = decimal.MinValue;
        var warningCount = 0;
        var warningUsed = decimal.MinValue;
        var countersA = new BrokerCounters();
        var countersB = new BrokerCounters();

        // Alpha's broker: every contract settles as a loss (Alpha trades once
        // before the governor stops everything). Beta's broker: contract 1
        // loses (completing the cap breach), any later contract wins (the
        // post-re-arm recovery). One server per account, so concurrent
        // connections never share responder state.
        var serverA = new FakeDerivServer(BrokerScript(
            "VRTCGOVA", "GA", countersA, AllLosses), cts.Token);
        var serverB = new FakeDerivServer(BrokerScript(
            "VRTCGOVB", "GB", countersB, new[] { new OutcomeStep(1, Win: false), new OutcomeStep(2, Win: true) }), cts.Token);

        _ = serverA.RunAsync(cts.Token);
        _ = serverB.RunAsync(cts.Token);
        await using var serverADisposal = serverA;
        await using var serverBDisposal = serverB;

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_gov_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);
            var governorLineSeen = false;
            hub.GrowthActivity += (_, line) =>
            {
                lastHubActivity = line;
                if (line.Contains("Portfolio drawdown cap breached"))
                {
                    governorLineSeen = true;
                }
            };
            hub.PortfolioGovernorTripped += net => governorNet = net;
            hub.PortfolioGovernorWarning += used => { warningCount++; warningUsed = used; };

            var configA = new AccountConfig { Label = "Gov Alpha", ApiToken = "token-gov-a", IsDemo = true, BrainKey = "Growth" };
            var configB = new AccountConfig { Label = "Gov Beta", ApiToken = "token-gov-b", IsDemo = true, BrainKey = "Growth" };
            var connectionA = hub.AddAccount(configA);
            connectionA.Client.Endpoint = serverA.WsUrl;
            connectionA.Client.SettlementPollInterval = FastPollInterval;
            var connectionB = hub.AddAccount(configB);
            connectionB.Client.Endpoint = serverB.WsUrl;
            connectionB.Client.SettlementPollInterval = FastPollInterval;
            await hub.ConnectAllAsync();
            Assert.True(connectionA.IsConnected);
            Assert.True(connectionB.IsConnected);

            var plan = GrowthPlan.Default with
            {
                CooldownMinutesAfterLoss = 0,
                IntervalMinutes = 60,
                MaxAutoRestarts = 3,
                RestartBaseDelaySeconds = 0.4,
                RestartBackoffFactor = 3.0,
                PortfolioDailyDrawdownCap = 1.50m
            };
            var settings = () => new AppSettings { AutonomyEnabled = true };

            Assert.False(hub.IsGovernorTripped);

            // Alpha is hub-managed; Beta is externally managed but observed,
            // so the governor sees both accounts' settlements.
            var runnerA = hub.StartGrowth(connectionA, plan, settings, () => false);
            Assert.NotNull(runnerA);
            var runnerB = new GrowthRunner(connectionB, store, settings, () => false, journal);
            hub.ObserveRunner(runnerB, plan);
            await runnerB.StartAsync(plan);

            // Both sessions lose $1.00 — the second settlement takes the
            // combined net to −$2.00, past the −$1.50 cap, whichever settles
            // first. The governor must trip there.
            await WaitForAsync(() => hub.IsGovernorTripped,
                TimeSpan.FromSeconds(60), "the portfolio governor trip");
            Assert.Equal(-2.00m, hub.CombinedGrowthNetPnl());

            // The trip event fires after the latch, stop-all, and webhook
            // work — under parallel load that window widens, so wait for the
            // handler instead of asserting it synchronously.
            await WaitForAsync(() => governorNet == -2.00m,
                TimeSpan.FromSeconds(10), "the PortfolioGovernorTripped event");

            // The warning must fire exactly once before the trip, with the
            // drawdown at or past the 80% band edge (−$1.20 of the −$1.50
            // cap) and no further than the worst-case combined loss. Where
            // it lands depends on which settlement the settling thread sees
            // first, so assert the range rather than an exact value.
            Assert.Equal(1, warningCount);
            Assert.InRange(warningUsed, 1.20m, 2.00m);

            // The hub's session was stopped; the latched governor refuses
            // new starts and nothing else trades.
            await WaitForAsync(() => hub.Runners.Count == 0,
                TimeSpan.FromSeconds(10), "the hub stopping its session");
            Assert.True(governorLineSeen, "the governor must announce the stop");
            Assert.Contains("Stopped — portfolio drawdown cap breached", runnerA!.LastActivity);
            await Task.Delay(1_500, cts.Token);
            Assert.Equal(2, store.Trades.Count(t => t.Source == TradeSource.Growth));
            Assert.Equal(2, countersA.Buys + countersB.Buys);
            Assert.True(hub.IsGovernorTripped, "the governor must stay latched");

            // The trip is journaled for audit (flush first: entries are
            // buffered and written to disk on a timer).
            journal.Flush();
            var entries = journal.GetRecent(configA.Id, 200)
                .Concat(journal.GetRecent(configB.Id, 200));
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" && e.Details.Contains("portfolio-governor") && e.Details.Contains("drew down"));

            // A manual start is refused while the governor is tripped...
            Assert.Null(hub.StartGrowth(connectionA, plan, settings, () => false));

            // ...and an explicit re-arm re-baselines the drawdown (now −$2.00)
            // so Beta's post-re-arm win must not re-trip it.
            hub.RearmGovernor();
            Assert.False(hub.IsGovernorTripped, "the re-arm must clear the latch");

            var runnerB2 = new GrowthRunner(connectionB, store, settings, () => false, journal);
            hub.ObserveRunner(runnerB2, plan);
            await runnerB2.StartAsync(plan);

            await WaitForAsync(() => store.ForAccount(configB.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "beta's post-rearm win");
            await Task.Delay(1_500, cts.Token);
            Assert.Equal(1, countersA.Buys);
            Assert.Equal(2, countersB.Buys);
            Assert.Equal(3, countersA.Proposals + countersB.Proposals);
            Assert.False(hub.IsGovernorTripped);
            // Post-re-arm win: Beta replays bankroll $4.00, the recovery
            // ladder doubles the stake to $1.60, and the win returns +$1.44.
            Assert.Equal(-0.56m, hub.CombinedGrowthNetPnl()); // −2.00 + 1.44

            await hub.StopGrowthAllAsync();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Trips the portfolio governor with two accounts, then simulates an app
    /// restart: fresh TradeStore, TradeJournal, and MultiAccountHub instances
    /// over the same on-disk data directory. The latched trip must be restored
    /// from the journal (banner net included), refuse to run either account,
    /// and only clear on an explicit re-arm — after which a real settlement
    /// breach must trip the restored governor again.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_GovernorLatch_SurvivesRestartFromDisk()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));

        var countersA = new BrokerCounters();
        var countersB = new BrokerCounters();

        // Both brokers settle every contract as a loss: the second settlement
        // takes the combined net to −$2.00, past the −$1.50 cap, whichever
        // account settles first.
        var serverA = new FakeDerivServer(BrokerScript("VRTCGOVR", "GR", countersA, AllLosses), cts.Token);
        var serverB = new FakeDerivServer(BrokerScript("VRTCGOVS", "GS", countersB, AllLosses), cts.Token);
        _ = serverA.RunAsync(cts.Token);
        _ = serverB.RunAsync(cts.Token);
        await using var serverADisposal = serverA;
        await using var serverBDisposal = serverB;

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_govre_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            // ── Session 1: two accounts breach the cap; the governor trips ──
            var store = new TradeStore(dataDir);
            var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub = new MultiAccountHub(new MemoryVault(), store, journal);

            var configA = new AccountConfig { Label = "Restart Alpha", ApiToken = "token-govr-a", IsDemo = true, BrainKey = "Growth" };
            var configB = new AccountConfig { Label = "Restart Beta", ApiToken = "token-govr-b", IsDemo = true, BrainKey = "Growth" };
            var connectionA = hub.AddAccount(configA);
            connectionA.Client.Endpoint = serverA.WsUrl;
            connectionA.Client.SettlementPollInterval = FastPollInterval;
            var connectionB = hub.AddAccount(configB);
            connectionB.Client.Endpoint = serverB.WsUrl;
            connectionB.Client.SettlementPollInterval = FastPollInterval;
            await hub.ConnectAllAsync();
            Assert.True(connectionA.IsConnected);
            Assert.True(connectionB.IsConnected);

            var plan = GrowthPlan.Default with
            {
                CooldownMinutesAfterLoss = 0,
                IntervalMinutes = 60,
                PortfolioDailyDrawdownCap = 1.50m
            };
            var settings = () => new AppSettings { AutonomyEnabled = true };

            Assert.False(hub.IsGovernorTripped);
            Assert.NotNull(hub.StartGrowth(connectionA, plan, settings, () => false));
            Assert.NotNull(hub.StartGrowth(connectionB, plan, settings, () => false));

            await WaitForAsync(() => hub.IsGovernorTripped,
                TimeSpan.FromSeconds(60), "the portfolio governor trip");
            Assert.Equal(-2.00m, hub.CombinedGrowthNetPnl());
            await WaitForAsync(() => hub.Runners.Count == 0,
                TimeSpan.FromSeconds(10), "the hub stopping its sessions");

            // Persist exactly what a real exit would: the journal buffers, so
            // flush it; trades.json is already rewritten on every settlement.
            Assert.Equal(2, store.Trades.Count(t => t.Source == TradeSource.Growth));
            journal.Flush();
            journal.Dispose();
            await hub.DisconnectAllAsync();

            // ── Session 2: the app restarts over the same on-disk state ──
            var store2 = new TradeStore(dataDir);
            using var journal2 = new TradeJournal(Path.Combine(dataDir, "journal"));
            var hub2 = new MultiAccountHub(new MemoryVault(), store2, journal2);

            // The constructor restores the latched trip from the journal —
            // including the breaching net for the UI banner.
            Assert.True(hub2.IsGovernorTripped, "the latched trip must survive the restart");
            Assert.Equal(-2.00m, hub2.GovernorTrippedNet);

            // Accounts come back from persisted config (fresh ids, as a real
            // restart produces); restoring them must not clear the latch.
            var configA2 = new AccountConfig { Label = "Restart Alpha", ApiToken = "token-govr-a", IsDemo = true, BrainKey = "Growth" };
            var configB2 = new AccountConfig { Label = "Restart Beta", ApiToken = "token-govr-b", IsDemo = true, BrainKey = "Growth" };
            var connectionA2 = hub2.AddAccount(configA2);
            connectionA2.Client.Endpoint = serverA.WsUrl;
            connectionA2.Client.SettlementPollInterval = FastPollInterval;
            var connectionB2 = hub2.AddAccount(configB2);
            connectionB2.Client.Endpoint = serverB.WsUrl;
            connectionB2.Client.SettlementPollInterval = FastPollInterval;
            await hub2.ConnectAllAsync();
            Assert.True(hub2.IsGovernorTripped, "restoring the accounts must not clear the latch");

            // The latched governor refuses every start after the restart...
            Assert.Null(hub2.StartGrowth(connectionA2, plan, settings, () => false));
            Assert.Null(hub2.StartGrowth(connectionB2, plan, settings, () => false));
            Assert.Equal(2, countersA.Buys + countersB.Buys); // nothing new traded

            // ...and the portfolio net is rebuilt from the persisted trades.
            Assert.Equal(-2.00m, hub2.CombinedGrowthNetPnl());

            // Re-arm clears the restored latch (re-baselining to −$2.00).
            hub2.RearmGovernor();
            Assert.False(hub2.IsGovernorTripped);
            Assert.Null(hub2.GovernorTrippedNet);

            // The hub runs again; both brokers lose once more, driving the
            // combined net to −$4.00 — a −$2.00 drawdown past the re-armed
            // baseline — so the governor must trip again on the real path.
            Assert.NotNull(hub2.StartGrowth(connectionA2, plan, settings, () => false));
            Assert.NotNull(hub2.StartGrowth(connectionB2, plan, settings, () => false));

            await WaitForAsync(() => hub2.IsGovernorTripped,
                TimeSpan.FromSeconds(60), "the post-re-arm re-trip");
            Assert.Equal(-4.00m, hub2.CombinedGrowthNetPnl());
            Assert.Equal(4, countersA.Buys + countersB.Buys);

            // The second trip is journaled for audit (flush first).
            journal2.Flush();
            var entries = journal2.GetRecent(count: 200);
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE"
                && e.Details.Contains("portfolio-governor") && e.Details.Contains("drew down"));

            await hub2.StopGrowthAllAsync();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Runs a local HTTP listener as the user's webhook endpoint and drives a
    /// session into an auto-restart and (on a second failure wave) into the
    /// bounded give-up. Every hub alert must arrive as a well-formed POST:
    /// JSON body, Discord embed format, with the restart and circuit-breaker
    /// payloads actually delivered over the wire.
    /// </summary>
    [Fact]
    public async Task MultiAccountHub_RestartAndGiveUp_PostWebhookPayloads()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var proposalRequests = 0;
        var buyRequests = 0;
        var failAllProposals = false;
        var proposalStakeById = new Dictionary<string, decimal>();
        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));
        var liveTickIndex = 0;

        var server = new FakeDerivServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCHOOK1\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-hook\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                if (failAllProposals)
                    return $"{{\"msg_type\":\"error\",\"req_id\":{reqId},\"error\":{{\"code\":\"MarketIsClosed\",\"message\":\"synthetic proposal failure\"}}}}";

                var id = $"PROP-HOOK-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"HOOK-{buyRequests}";
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{cid}\",\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = proposalStakeById.Values.FirstOrDefault();
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"won\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.17800,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":{(stake * 0.90m).ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
            }

            return "";
        }, cts.Token, _ =>
        {
            liveTickIndex++;
            var quote = (1.2000 - 0.001 * (HistoryTickCount + liveTickIndex))
                .ToString("F5", CultureInfo.InvariantCulture);
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":{quote},\"ask\":{quote},\"bid\":{quote},\"epoch\":{epoch},\"pip_size\":5}}}}";
        });
        _ = server.RunAsync(cts.Token);
        await using var serverDisposal = server;

        // ── The webhook endpoint: a loopback HTTP listener. ──
        var port = 0;
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/hook/");
        listener.Start();

        var bodies = new ConcurrentQueue<string>();
        var contentTypes = new ConcurrentQueue<string>();
        var listenerTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch
                {
                    return; // listener stopped
                }

                using var reader = new StreamReader(ctx.Request.InputStream);
                bodies.Enqueue(reader.ReadToEnd());
                contentTypes.Enqueue(ctx.Request.ContentType ?? "");
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
            }
        });

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_hook_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var webhook = new WebhookService
            {
                WebhookUrl = $"http://localhost:{port}/hook/",
                IsDiscord = true,
                MinInterval = TimeSpan.Zero // the test cares about delivery, not throttling
            };
            var hub = new MultiAccountHub(new MemoryVault(), store, journal,
                webhook: webhook);

            var config = new AccountConfig { Label = "Hook Demo", ApiToken = "token-hook", IsDemo = true, BrainKey = "Growth" };
            var connection = hub.AddAccount(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await hub.ConnectAllAsync();
            Assert.True(connection.IsConnected);

            // 0.3 s restart base → the ladder 0.3 / 0.9 / 2.7 s.
            var plan = GrowthPlan.Default with
            {
                CooldownMinutesAfterLoss = 0,
                FailureBackoffSeconds = 0.2,
                MaxAutoRestarts = 3,
                RestartBaseDelaySeconds = 0.3,
                RestartBackoffFactor = 3.0
            };
            var settings = () => new AppSettings { AutonomyEnabled = true };

            // ── Wave 1: three failures → auto-restart alert → recovery win. ──
            var runner = hub.StartGrowth(connection, plan, settings, () => false);
            Assert.NotNull(runner);
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "the post-restart win");

            // ── Wave 2: persistent failure → restarts 1-3 → give-up alert. ──
            failAllProposals = true;
            hub.StopGrowth(config.Id);
            runner = hub.StartGrowth(connection, plan, settings, () => false);
            Assert.NotNull(runner);
            await WaitForAsync(() => hub.IsGivenUp(config.Id),
                TimeSpan.FromSeconds(60), "the bounded give-up");

            // Everything the hub announced must have been delivered.
            await WaitForAsync(() => bodies.Count >= 5,
                TimeSpan.FromSeconds(15), "the webhook deliveries (win + 3 restarts + give-up)");
            await Task.Delay(500, cts.Token);

            Assert.All(bodies, body =>
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body); // well-formed JSON
                var embed = doc.RootElement.GetProperty("embeds")[0];
                Assert.False(string.IsNullOrWhiteSpace(embed.GetProperty("title").GetString()));
            });
            Assert.All(contentTypes, ct => Assert.StartsWith("application/json", ct));

            var titles = bodies.Select(b =>
                System.Text.Json.JsonDocument.Parse(b).RootElement
                    .GetProperty("embeds")[0].GetProperty("title").GetString()!).ToList();
            var descriptions = bodies.Select(b =>
                System.Text.Json.JsonDocument.Parse(b).RootElement
                    .GetProperty("embeds")[0].GetProperty("description").GetString()!).ToList();

            Assert.Contains(titles, t => t.Contains("Trade Settled"));            // the settlement alert
            Assert.Contains(titles, t => t.Contains("Growth engine restarting"));  // restart alerts
            Assert.Contains(descriptions, d => d.Contains("restart 1/3") && d.Contains("0.3 s"));
            Assert.Contains(descriptions, d => d.Contains("restart 3/3"));
            Assert.Contains(titles, t => t.Contains("Circuit Breaker Tripped"));   // the give-up alert
            Assert.Contains(descriptions, d => d.Contains("3 consecutive failures"));

            listener.Stop();
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
