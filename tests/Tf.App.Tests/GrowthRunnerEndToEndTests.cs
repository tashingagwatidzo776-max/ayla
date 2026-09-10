using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.Core;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.Tests;

/// <summary>
/// End-to-end tests that drive the real GrowthRunner stack — AccountConnection,
/// DerivClient, TradingBrain, GrowthBrain, RiskEngine, GrowthSessionEngine,
/// TradeStore and TradeJournal — against an in-process fake Deriv WebSocket
/// server. Covered scenarios:
///
///  1. One full growth cycle: connect → history backfill → RSI decision →
///     risk check → proposal → buy → settlement → tagged persistence →
///     bankroll update.
///  2. Kill switch: engaged → the scheduler exits with zero proposal/buy
///     requests; released and restarted → trading resumes cleanly.
///  3. Post-loss cooldown: a settled loss blocks the next cycle at the risk
///     engine — no second proposal or buy is ever sent.
///  4. Losing trade: persisted with the loss outcome, loss streak increments,
///     bankroll drops, and the recovery ladder doubles the next stake.
/// </summary>
[Trait("Category", "Integration")]
public class GrowthRunnerEndToEndTests
{
    private const int HistoryTickCount = 30;
    private const decimal ExpectedStake = 1.00m;    // GrowthPlan default: $5 budget × 20% risk
    private const decimal ExpectedProfit = 0.90m;   // $1 stake at the fake 1.90 payout
    private const decimal ExpectedBankrollAfterWin = 5.90m;
    private const decimal ExpectedBankrollAfterLoss = 4.00m;   // $5 budget − $1 stake lost
    private const string ContractId = "GROWTH-C-1";
    private const string KillContractId = "GROWTH-KS-1";
    private const string CooldownContractId = "GROWTH-CD-1";
    private const string LossContractId = "GROWTH-LOSS-1";

    [Fact]
    public async Task GrowthRunner_CompletesFullGrowthCycle_AgainstFakeDerivServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var proposalRequests = 0;
        var buyRequests = 0;
        var contractPolls = 0;
        decimal proposalAmount = 0;
        var liveTickIndex = 0;

        // 30 monotonically falling ticks → RSI(14) = 0 → oversold → Rise signal,
        // regardless of how many live ticks arrive before the first cycle.
        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));

        var server = new GrowthFakeServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO1\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-growth\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                proposalAmount = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"PROP-GROWTH-1\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{ContractId}\",\"buy_price\":1.00,\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                contractPolls++;
                if (contractPolls == 1)
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{ContractId}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":1.00,\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{ContractId}\",\"status\":\"won\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.17800,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":1.00,\"profit\":{ExpectedProfit.ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var settled = new TaskCompletionSource<Trade>(TaskCreationOptions.RunContinuationsAsynchronously);
            store.TradeAdded += t => settled.TrySetResult(t);

            var config = new AccountConfig
            {
                Label = "E2E Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            // Real connection path: authorize → history backfill → tick subscription.
            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            await connection.ConnectAsync();

            Assert.True(connection.IsConnected, "AccountConnection should be connected to the fake server");
            Assert.Equal("VRTCDEMO1", connection.Client.LoginId);
            Assert.True(connection.Ticks.Count >= 21,
                $"expected ≥21 ticks after history backfill, got {connection.Ticks.Count}");

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,           // kill switch off
                journal);
            await using var runnerDisposal = runner;

            // Fire-and-forget scheduler; first cycle runs immediately.
            await runner.StartAsync(GrowthPlan.Default);
            Assert.True(runner.IsRunning, "runner should report running after StartAsync");

            // The cycle settles when the tagged trade lands in the store.
            var trade = await settled.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

            Assert.Equal(config.Id, trade.AccountId);
            Assert.Equal("E2E Demo", trade.AccountName);
            Assert.Equal(TradeSource.Growth, trade.Source);
            Assert.Equal("frxEURUSD", trade.Symbol);
            Assert.Equal(Direction.Rise, trade.Direction);
            Assert.Equal(ExpectedStake, trade.Stake);
            Assert.Equal(ContractId, trade.ContractId);
            Assert.Equal(ContractStatus.Won, trade.Outcome);
            Assert.True(trade.IsWin);
            Assert.Equal(ExpectedProfit, trade.Profit);

            // The session engine applied the settlement (fires right after Add).
            await WaitForAsync(() => runner.Engine?.Bankroll == ExpectedBankrollAfterWin,
                TimeSpan.FromSeconds(5), "bankroll update after settlement");
            Assert.Equal(ExpectedBankrollAfterWin, runner.Engine!.Bankroll);
            Assert.Equal(0, runner.Engine.LossStreak);
            Assert.False(runner.Engine.TargetHit, "a single $0.90 win must not reach the $10 daily target");
            Assert.True(runner.IsRunning);

            Assert.Contains("Rise", runner.LastActivity);
            Assert.Contains("Won", runner.LastActivity);
            Assert.Contains("PASSED risk", runner.LastActivity);

            // Exactly one clean round-trip against the fake server.
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);
            Assert.True(contractPolls >= 2, $"expected ≥2 contract polls, got {contractPolls}");
            Assert.Equal(ExpectedStake, proposalAmount);

            journal.Dispose(); // flush queued entries to disk

            var entries = journal.GetRecent(config.Id, 50);
            Assert.Contains(entries, e => e.Category == "GROWTH_STATE" && e.Details.Contains("started"));
            Assert.Contains(entries, e => e.Category == "BRAIN_DECISION" && e.Details.Contains("oversold"));
            Assert.Contains(entries, e => e.Category == "TRADE_SETTLEMENT" && e.Details.Contains("\"Won\":true"));

            var stored = Assert.Single(store.ForAccount(config.Id, TradeSource.Growth));
            Assert.Equal(ContractId, stored.ContractId);
            Assert.True(stored.IsWin);
            Assert.Equal(ExpectedStake, stored.Stake);
            Assert.Equal(ExpectedProfit, stored.Profit);

            runner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Kill switch: while engaged the scheduler exits at the top of every loop
    /// iteration, so the brain never runs and no proposal or buy request ever
    /// reaches the broker. After release, a restart produces a normal trading
    /// cycle again — proving the block was the kill switch and not a wedged
    /// runner.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_KillSwitchBlocksAllTrading_UntilReleased()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var proposalRequests = 0;
        var buyRequests = 0;
        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));
        var liveTickIndex = 0;

        var server = new GrowthFakeServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO2\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-killswitch\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"PROP-KS-1\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{KillContractId}\",\"buy_price\":1.00,\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{KillContractId}\",\"status\":\"won\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.17800,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":1.00,\"profit\":0.90,\"currency\":\"USD\"}}}}";

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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_ks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var killSwitch = false;

            var config = new AccountConfig
            {
                Label = "Kill Switch Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            await connection.ConnectAsync();

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => killSwitch,
                journal);
            await using var runnerDisposal = runner;

            // ── Phase 1: start with the kill switch ENGAGED. ──
            killSwitch = true;
            await runner.StartAsync(GrowthPlan.Default);

            // The scheduler's guard fires before any brain work, so within a
            // short window the runner must stop itself with zero trading.
            await WaitForAsync(() => !runner.IsRunning, TimeSpan.FromSeconds(5),
                "scheduler self-stop while the kill switch is engaged");
            Assert.False(runner.IsRunning, "kill switch must stop the scheduler immediately");

            // Give any (wrongly) started cycle ample time to hit the broker.
            await Task.Delay(1_000, cts.Token);
            Assert.Equal(0, proposalRequests);
            Assert.Equal(0, buyRequests);
            Assert.Empty(store.Trades);
            Assert.DoesNotContain("PASSED risk", runner.LastActivity);

            // ── Phase 2: release the switch and restart. ──
            killSwitch = false;
            await runner.StartAsync(GrowthPlan.Default);
            Assert.True(runner.IsRunning, "runner should restart after the kill switch is released");

            var trades = store.ForAccount(config.Id, TradeSource.Growth);
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count > 0,
                TimeSpan.FromSeconds(30), "growth trade after kill-switch release");

            trades = store.ForAccount(config.Id, TradeSource.Growth);
            var trade = Assert.Single(trades);
            Assert.Equal(TradeSource.Growth, trade.Source);
            Assert.True(trade.IsWin);
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);

            runner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Post-loss cooldown: after a settled loss, the risk engine blocks the
    /// next cycle until the cooldown window expires — the decision is logged,
    /// but no second proposal or buy request ever reaches the broker.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_PostLossCooldownBlocksSecondTrade_WithoutBrokerRequests()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var proposalRequests = 0;
        var buyRequests = 0;
        var contractPolls = 0;
        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));
        var liveTickIndex = 0;

        var server = new GrowthFakeServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO3\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-cooldown\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"PROP-CD-1\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{CooldownContractId}\",\"buy_price\":1.00,\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                contractPolls++;
                if (contractPolls == 1)
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{CooldownContractId}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":1.00,\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{CooldownContractId}\",\"status\":\"lost\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.16500,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":1.00,\"profit\":-1.00,\"currency\":\"USD\"}}}}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_cd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            var journal = new TradeJournal(Path.Combine(dataDir, "journal"));

            var config = new AccountConfig
            {
                Label = "Cooldown Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            await connection.ConnectAsync();

            // The very first settled loss must block the NEXT cycle: one tick
            // of cooldown would let the one-minute scheduler interval expire
            // before the rejection is observable, so use 10 minutes.
            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 10 };

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var runnerDisposal = runner;

            await runner.StartAsync(plan);

            // Wait for the loss to land in the store (cycle 1: proposal → buy
            // → settle as Lost).
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count > 0,
                TimeSpan.FromSeconds(30), "first (losing) trade");
            var loss = Assert.Single(store.ForAccount(config.Id, TradeSource.Growth));
            Assert.Equal(ContractStatus.Lost, loss.Outcome);
            Assert.False(loss.IsWin);
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);

            // Restart the runner so the next cycle fires immediately (rather
            // than waiting the one-minute scheduler interval). The session
            // engine replays today's settled trades, so the loss streak and
            // bankroll carry over; BuildRiskContext reads LastTradeOutcome
            // (Lost) and LastTradeAt from the store → cooldown rejection.
            runner.Stop();
            await runner.StartAsync(plan);

            // Give any (wrongly) placed second trade ample time to hit the
            // broker, then assert the cooldown blocked it.
            await Task.Delay(1_500, cts.Token);
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);
            Assert.Contains("blocked:", runner.LastActivity);
            Assert.Contains("cooldown after loss", runner.LastActivity);

            // The rejected decision is still journaled for audit.
            journal.Dispose();
            var entries = journal.GetRecent(config.Id, 50);
            Assert.Contains(entries, e => e.Category == "BRAIN_DECISION");

            runner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Losing trade lifecycle: the settled loss is persisted with outcome
    /// Lost and negative profit, the session engine increments the loss
    /// streak and drops the bankroll, and the recovery ladder doubles the
    /// suggested next stake (capped at half the remaining bankroll).
    /// </summary>
    [Fact]
    public async Task GrowthRunner_LosingTrade_PersistsLoss_IncrementsStreak_DoublesNextStake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var historyPrices = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture)));
        var historyTimes = string.Join(",", Enumerable.Range(0, HistoryTickCount)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture)));
        var liveTickIndex = 0;

        var server = new GrowthFakeServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO4\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-loss\"}}}}";

            if (req.TryGetProperty("proposal", out _))
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"PROP-LOSS-1\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";

            if (req.TryGetProperty("buy", out _))
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{LossContractId}\",\"buy_price\":1.00,\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{LossContractId}\",\"status\":\"lost\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.16500,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":1.00,\"profit\":-1.00,\"currency\":\"USD\"}}}}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_loss_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var settled = new TaskCompletionSource<Trade>(TaskCreationOptions.RunContinuationsAsynchronously);
            store.TradeAdded += t => settled.TrySetResult(t);

            var config = new AccountConfig
            {
                Label = "Loss Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            await connection.ConnectAsync();

            // Long cooldown so the settled loss cannot trigger a second cycle
            // while the assertions below run.
            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 10 };

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var runnerDisposal = runner;

            await runner.StartAsync(plan);
            var trade = await settled.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

            // ── The loss is persisted with the right shape. ──
            Assert.Equal(TradeSource.Growth, trade.Source);
            Assert.Equal(config.Id, trade.AccountId);
            Assert.Equal(Direction.Rise, trade.Direction);
            Assert.Equal(ContractStatus.Lost, trade.Outcome);
            Assert.False(trade.IsWin);
            Assert.Equal(ExpectedStake, trade.Stake);
            Assert.Equal(-ExpectedStake, trade.Profit); // stake is forfeited

            // ── The session engine reacted: streak up, bankroll down. ──
            await WaitForAsync(() => runner.Engine?.LossStreak == 1,
                TimeSpan.FromSeconds(5), "loss-streak increment after settlement");
            Assert.Equal(1, runner.Engine!.LossStreak);
            Assert.Equal(ExpectedBankrollAfterLoss, runner.Engine.Bankroll); // $5.00 − $1.00
            Assert.False(runner.Engine.TargetHit);
            Assert.False(runner.Engine.FloorHit, "$4 is above the $2 floor (40% of $5)");
            Assert.True(runner.Engine.CanTrade);
            Assert.True(runner.IsRunning, "a single loss must not stop the session");

            // ── The recovery ladder doubles the next stake. ──
            // Bankroll $4 × 20% risk × 2 (streak 1) = $1.60.
            Assert.Equal(1.60m, runner.Engine.SuggestStake());

            // ── The activity line reflects the loss and the bankroll. ──
            Assert.Contains("Lost", runner.LastActivity);
            Assert.Contains("PASSED risk", runner.LastActivity);
            Assert.Contains("$4", runner.LastActivity);

            // ── Restart replays today's loss: streak and bankroll resume. ──
            runner.Stop();
            await runner.StartAsync(plan);
            Assert.Equal(ExpectedBankrollAfterLoss, runner.Engine!.Bankroll);
            Assert.Equal(1, runner.Engine.LossStreak);

            runner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), $"Timed out waiting for {what}");
    }

    /// <summary>
    /// In-process fake Deriv WebSocket server (same approach as the
    /// DerivIntegrationFlowTests fake): accepts one connection, echoes req_id,
    /// and streams live ticks after a ticks subscription.
    /// </summary>
    private sealed class GrowthFakeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<JsonElement, string> _responder;
        private readonly Func<JsonElement, string?> _tickGenerator;
        private readonly List<string> _received = new();
        private readonly object _sync = new();

        public GrowthFakeServer(Func<JsonElement, string> responder, CancellationToken ct,
            Func<JsonElement, string?> tickGenerator)
        {
            _responder = responder;
            _tickGenerator = tickGenerator;

            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Port = port;
        }

        public int Port { get; }
        public string WsUrl => $"ws://127.0.0.1:{Port}";

        public Task RunAsync(CancellationToken ct) =>
            Task.Run(() => RunCoreAsync(ct), CancellationToken.None);

        private async Task RunCoreAsync(CancellationToken ct)
        {
            var context = await _listener.GetContextAsync().WaitAsync(ct);
            var ws = await context.AcceptWebSocketAsync(null).WaitAsync(ct);
            var buffer = new byte[65536];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    }
                    catch (WebSocketException)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    lock (_sync) { _received.Add(json); }

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var response = _responder(root);
                    if (!string.IsNullOrEmpty(response))
                    {
                        var bytes = Encoding.UTF8.GetBytes(response);
                        await ws.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }

                    // After a ticks subscription, stream a few live ticks.
                    if (root.TryGetProperty("ticks", out _))
                    {
                        for (var i = 0; i < 5 && !ct.IsCancellationRequested; i++)
                        {
                            await Task.Delay(100, ct);
                            var tickJson = _tickGenerator(root);
                            if (!string.IsNullOrEmpty(tickJson))
                            {
                                var tickBytes = Encoding.UTF8.GetBytes(tickJson);
                                await ws.WebSocket.SendAsync(tickBytes, WebSocketMessageType.Text, true, ct);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                try { ws.WebSocket.Dispose(); } catch { }
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { }
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
