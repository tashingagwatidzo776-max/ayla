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
///  5. Daily loss cap: once today's net profit crosses −cap the risk engine
///     rejects every decision; a new day (yesterday-stamped settlements)
///     resets the session to the full budget.
///  6. Multi-account hub: two accounts trade growth sessions through one
///     MultiAccountHub — bankrolls, loss streaks, trades and session engines
///     stay strictly isolated per account.
///  7. Broker proposal error: an error response makes the scheduler back off
///     (≥5s between attempts), the runner survives and the trade log stays
///     clean until a later attempt succeeds.
///  8. Floor hit: repeated losses drive the bankroll below the session
///     floor — the engine self-stops (CanTrade=false, stake 0) and no
///     further proposals are ever sent.
///  9. Target hit: repeated wins drive the bankroll to the daily target —
///     the engine self-stops and a fresh cycle Holds instead of proposing.
/// 10. Kill switch engaged mid-cycle: an in-flight trade still settles and
///     persists, and the next cycle never reaches the broker.
/// 11. Paused account: StartAsync refuses while paused, cycles skip while
///     the account is paused mid-session, and resume trades again.
/// 12. Account disconnect mid-session: the hub stops runners, the clients
///     reconnect by themselves, and restarted sessions resume from replayed
///     state.
/// 13. Bounded auto-restart: repeated broker failures trigger journaled
///     automatic restarts until the budget is exhausted, then the hub gives
///     up until a manual start resets the counter.
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

    // The fake server settles every contract on the second proposal_open_contract
    // poll, and DerivClient polls every 2 s in production — a flat 2 s sleep per
    // settled trade. Test clients shorten the interval so the suites stay
    // wait-based without paying that sleep; poll-count asserts are lower bounds
    // and stay valid.
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(50);

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

        var server = new FakeDerivServer(req =>
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
            connection.Client.SettlementPollInterval = FastPollInterval;
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

        var server = new FakeDerivServer(req =>
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
            connection.Client.SettlementPollInterval = FastPollInterval;
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
            await WaitForAsync(() => !runner.IsRunning, TimeSpan.FromSeconds(15),
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

        var server = new FakeDerivServer(req =>
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
            connection.Client.SettlementPollInterval = FastPollInterval;
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

        var server = new FakeDerivServer(req =>
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
            connection.Client.SettlementPollInterval = FastPollInterval;
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

    // ─────────────────────────────────────────────────────────────
    //  Daily loss cap
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Daily loss cap: with a $2.50 cap and one seeded loss (−$1.00), the
    /// first live loss (−$1.60) pushes today's net to −$2.60 — past the cap —
    /// and the risk engine then rejects every decision: the broker never sees
    /// another proposal or buy. Re-stamping the settlements to yesterday (the
    /// test stand-in for midnight) resets the day: a restart starts a fresh
    /// $5 session and trades again.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_DailyLossCapBlocksTrading_AndANewDayResetsIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var proposalRequests = 0;
        var buyRequests = 0;
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
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO5\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-dailycap\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                var id = $"PROP-DL-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-DL-{buyRequests}";
                contractStakeById[cid] = stake;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{cid}\",\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = contractStakeById[cid];
                if (openContracts.Add(cid))
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"lost\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.16500,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":{(-stake).ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));

            var config = new AccountConfig
            {
                Label = "Daily Cap Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await connection.ConnectAsync();

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true, DailyLossCap = 2.50m },
                () => false,
                journal);
            await using var runnerDisposal = runner;

            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0 };

            // ── Seed one settled loss "today": engine replays bankroll $4, streak 1. ──
            store.Add(MakeGrowthTrade(config.Id, "Daily Cap Demo", "GROWTH-SEED-1",
                1.00m, -1.00m, DateTimeOffset.UtcNow.AddMinutes(-30)));

            await runner.StartAsync(plan);
            Assert.Equal(4.00m, runner.Engine!.Bankroll);
            Assert.Equal(1, runner.Engine.LossStreak);

            // First live cycle: net −$1.00 is above the cap → trades (recovery
            // stake $1.60) and loses → today's net hits −$2.60 ≤ −$2.50.
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "the first live loss");
            await WaitForAsync(() => runner.Engine!.Bankroll == 2.40m,
                TimeSpan.FromSeconds(5), "bankroll after the live loss");
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);

            // ── Next cycle: the daily-loss-cap rejection blocks trading. ──
            runner.Stop();
            await runner.StartAsync(plan);
            await WaitForAsync(() => runner.LastActivity.Contains("daily loss cap"),
                TimeSpan.FromSeconds(15), "daily-loss-cap rejection");
            Assert.Contains("blocked:", runner.LastActivity);

            // Give any (wrongly) placed trade ample time to hit the broker.
            await Task.Delay(1_500, cts.Token);
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);
            Assert.Equal(2, store.ForAccount(config.Id, TradeSource.Growth).Count);

            // ── Midnight: re-stamp the settlements to yesterday, then restart ──
            // ── on a fresh store+runner (the runner binds its store at ──
            // ── construction, exactly like the app does on launch). ──
            runner.Stop();
            ShiftTradeLogToPreviousDay(store.FilePath);
            var freshStore = new TradeStore(dataDir);
            var freshRunner = new GrowthRunner(
                connection, freshStore,
                () => new AppSettings { AutonomyEnabled = true, DailyLossCap = 2.50m },
                () => false,
                journal);

            await freshRunner.StartAsync(plan);

            // A new day starts from the full budget: yesterday's losses no
            // longer count toward today's cap.
            Assert.Equal(5.00m, freshRunner.Engine!.Bankroll);
            Assert.Equal(0, freshRunner.Engine.LossStreak);

            await WaitForAsync(() => freshStore.ForAccount(config.Id, TradeSource.Growth).Count == 3,
                TimeSpan.FromSeconds(30), "a fresh trade on the new day");
            var fresh = freshStore.ForAccount(config.Id, TradeSource.Growth)[2];
            Assert.Equal(1.00m, fresh.Stake);
            Assert.Equal(ContractStatus.Lost, fresh.Outcome);
            Assert.Equal(-1.00m, fresh.Profit);

            // The engine applies the settlement a beat after the store write;
            // wait for it rather than reading the bankroll mid-apply.
            await WaitForAsync(() => freshRunner.Engine!.Bankroll == 4.00m,
                TimeSpan.FromSeconds(5), "bankroll after the fresh-day loss");
            Assert.Equal(1, freshRunner.Engine.LossStreak);
            Assert.Equal(2, buyRequests);
            Assert.Contains("PASSED risk", freshRunner.LastActivity);

            freshRunner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Broker proposal error
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Broker failure: the fake Deriv server answers the first two proposal
    /// requests with API errors. The scheduler must back off (≥5s between
    /// attempts), keep running, and leave the trade log untouched; when the
    /// broker recovers, the next cycle trades and settles cleanly, and a
    /// restart replays the persisted state exactly.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_ProposalError_BacksOff_SurvivesAndKeepsStateClean()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        const int failedProposals = 2;
        var proposalRequests = 0;
        var buyRequests = 0;
        var proposalTimes = new ConcurrentQueue<long>();
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
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO6\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-properr\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                proposalTimes.Enqueue(Environment.TickCount64);
                if (proposalRequests <= failedProposals)
                    return $"{{\"msg_type\":\"error\",\"req_id\":{reqId},\"error\":{{\"code\":\"MarketIsClosed\",\"message\":\"synthetic proposal failure\"}}}}";

                var id = $"PROP-ERR-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-ERR-{buyRequests}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_err_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));

            var config = new AccountConfig
            {
                Label = "Proposal Error Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await connection.ConnectAsync();

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var runnerDisposal = runner;

            // FailureBackoffSeconds = 3 overrides the scheduler's 5s default —
            // proving the plan value (not the hard-coded default) governs the
            // backoff after failed cycles.
            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, FailureBackoffSeconds = 3.0 };
            await runner.StartAsync(plan);

            // Two failures, each followed by the configured 3s backoff, then
            // the third attempt succeeds and the trade goes through.
            // Waiting on the buy (not the proposal) proves the recovered
            // attempt actually traded — the buy follows its successful
            // proposal, so the order keeps this race-free at any poll speed.
            await WaitForAsync(() => buyRequests >= 1,
                TimeSpan.FromSeconds(45), "broker recovery after two failures");

            var times = proposalTimes.ToArray();
            Assert.True(times.Length >= failedProposals + 1);
            var backoffMs = (int)(plan.FailureBackoffSeconds * 1000) - 500; // 500ms clock jitter
            for (var i = 1; i < times.Length; i++)
            {
                var gap = times[i] - times[i - 1];
                Assert.True(gap >= backoffMs,
                    $"expected the ≥{plan.FailureBackoffSeconds:0.#}s configured backoff between proposal " +
                    $"attempts {i} and {i + 1}, got {gap}ms (floor {backoffMs}ms)");
            }

            // The recovered cycle trades and settles cleanly.
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "the trade after broker recovery");
            var trade = Assert.Single(store.ForAccount(config.Id, TradeSource.Growth));
            Assert.True(trade.IsWin);
            Assert.Equal(1.00m, trade.Stake);
            await WaitForAsync(() => runner.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "bankroll after recovery");
            Assert.Equal(0, runner.Engine!.LossStreak);
            Assert.True(runner.IsRunning, "the runner must survive broker errors");

            // No corrupted state: the log holds exactly one replayable trade.
            var persisted = JsonSerializer.Deserialize<List<Trade>>(
                File.ReadAllText(store.FilePath), TradeLogOptions);
            var persistedTrade = Assert.Single(persisted!);
            Assert.Equal(trade.Id, persistedTrade.Id);
            Assert.Equal(ContractStatus.Won, persistedTrade.Outcome);

            // A restart replays the settlement — state resumes exactly.
            runner.Stop();
            await runner.StartAsync(plan);
            Assert.Equal(5.90m, runner.Engine!.Bankroll);
            Assert.Equal(0, runner.Engine.LossStreak);

            runner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Session floor
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Floor hit: with the floor at 60% of the $5 budget ($3.00), two losses
    /// ($1.00 then the $1.60 recovery stake) drive the bankroll to $2.40 —
    /// below the floor. The engine self-stops (FloorHit, CanTrade=false,
    /// stake 0) and a fresh cycle Holds with the floor reason instead of
    /// sending any further proposal.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_FloorHit_StopsSession_AndSendsNoFurtherProposals()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var proposalRequests = 0;
        var buyRequests = 0;
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
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO7\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-floor\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                var id = $"PROP-FL-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-FL-{buyRequests}";
                contractStakeById[cid] = stake;
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"{cid}\",\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"balance_after\":99.00,\"longcode\":\"Rise contract\"}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = contractStakeById[cid];
                if (openContracts.Add(cid))
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.17000,\"exit_spot\":0,\"entry_tick_time\":1700000300,\"exit_tick_time\":0,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"{cid}\",\"status\":\"lost\",\"is_sold\":true,\"entry_spot\":1.17000,\"exit_spot\":1.16500,\"entry_tick_time\":1700000300,\"exit_tick_time\":1700000600,\"buy_price\":{stake.ToString(CultureInfo.InvariantCulture)},\"profit\":{(-stake).ToString(CultureInfo.InvariantCulture)},\"currency\":\"USD\"}}}}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_floor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));

            var config = new AccountConfig
            {
                Label = "Floor Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await connection.ConnectAsync();

            // Floor at 60% of $5 = $3.00; the recovery ladder (streak 2 < 3
            // steps) is NOT exhausted, so the block must be the floor itself.
            var plan = GrowthPlan.Default with
            {
                FloorFraction = 0.60,
                CooldownMinutesAfterLoss = 0,
                IntervalMinutes = 60
            };

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var runnerDisposal = runner;

            // ── Loss 1: $1.00 → bankroll $4.00, above the floor. ──
            await runner.StartAsync(plan);
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "the first loss");
            await WaitForAsync(() => runner.Engine!.Bankroll == 4.00m,
                TimeSpan.FromSeconds(5), "bankroll after the first loss");
            Assert.Equal(1, runner.Engine!.LossStreak);
            Assert.False(runner.Engine.FloorHit);
            Assert.True(runner.Engine.CanTrade);
            Assert.Equal(1, proposalRequests);

            // ── Loss 2: the $1.60 recovery stake → $2.40, below the $3.00 floor. ──
            runner.Stop();
            await runner.StartAsync(plan);
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "the second loss");
            await WaitForAsync(() => runner.Engine!.Bankroll == 2.40m,
                TimeSpan.FromSeconds(5), "bankroll after the second loss");

            Assert.Equal(2, runner.Engine.LossStreak);
            Assert.True(runner.Engine.FloorHit, "$2.40 must breach the $3.00 floor");
            Assert.False(runner.Engine.CanTrade);
            Assert.Equal(0m, runner.Engine.SuggestStake());
            Assert.Contains("floor reached", runner.Engine.BlockReason);
            Assert.Equal(2, proposalRequests);
            await WaitForAsync(() => runner.SessionStateText == "floor hit — stopped",
                TimeSpan.FromSeconds(5), "session state after the floor");

            // ── A fresh cycle must HOLD on the floor, never propose again. ──
            runner.Stop();
            await runner.StartAsync(plan);
            Assert.Equal(2.40m, runner.Engine!.Bankroll);
            Assert.Equal(2, runner.Engine.LossStreak);
            Assert.True(runner.Engine.FloorHit);

            await WaitForAsync(() => runner.LastActivity.Contains("session floor reached"),
                TimeSpan.FromSeconds(15), "the floor HOLD decision");
            Assert.Contains("HOLD", runner.LastActivity);

            await Task.Delay(1_500, cts.Token);
            Assert.Equal(2, proposalRequests);
            Assert.Equal(2, buyRequests);

            runner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Daily target
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Target hit: with the default 100% target ($10 on a $5 budget), five
    /// consecutive wins (each 90% of an escalating stake) drive the bankroll
    /// past $10. The engine self-stops (TargetHit, CanTrade=false, stake 0)
    /// and a fresh cycle Holds with the target reason instead of sending any
    /// further proposal.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_TargetHit_StopsSession_AndSendsNoFurtherProposals()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var proposalRequests = 0;
        var buyRequests = 0;
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
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO8\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-target\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                var id = $"PROP-TG-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-TG-{buyRequests}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_target_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));

            var config = new AccountConfig
            {
                Label = "Target Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await connection.ConnectAsync();

            // IntervalMinutes = 60 keeps each start to exactly one immediate
            // cycle; restarting between wins fires the next one right away.
            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, IntervalMinutes = 60 };

            // Expected stake ladder: 20% of the compounding bankroll, floored
            // to the cent, always ≥ the $1 minimum.
            var expectedStakes = new[] { 1.00m, 1.18m, 1.39m, 1.64m, 1.93m };

            for (var cycle = 0; cycle < expectedStakes.Length; cycle++)
            {
                var runner = new GrowthRunner(
                    connection, store,
                    () => new AppSettings { AutonomyEnabled = true },
                    () => false,
                    journal);
                await using var runnerDisposal = runner;

                await runner.StartAsync(plan);

                // Every settled win lands in the store with the ladder stake.
                await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == cycle + 1,
                    TimeSpan.FromSeconds(30), $"win #{cycle + 1}");
                var trade = store.ForAccount(config.Id, TradeSource.Growth)[cycle];
                Assert.True(trade.IsWin);
                Assert.Equal(expectedStakes[cycle], trade.Stake);

                if (cycle < expectedStakes.Length - 1)
                {
                    // Interim bankrolls: 5.90, 6.962, 8.213, 9.689 — still below target.
                    var interimBankrolls = new[] { 5.90m, 6.962m, 8.213m, 9.689m };
                    await WaitForAsync(() => runner.Engine!.Bankroll == interimBankrolls[cycle],
                        TimeSpan.FromSeconds(5), $"bankroll after win #{cycle + 1}");
                    Assert.False(runner.Engine!.TargetHit, $"win #{cycle + 1} must not reach the target");
                    Assert.True(runner.Engine.CanTrade);
                }
                else
                {
                    // The fifth win crosses $10: the session stops itself.
                    Assert.True(runner.Engine!.TargetHit, "$11.43 must reach the $10.00 target");
                    Assert.False(runner.Engine.FloorHit);
                    Assert.False(runner.Engine.CanTrade);
                    Assert.Equal(0m, runner.Engine.SuggestStake());
                    Assert.True(runner.Engine.Bankroll >= runner.Engine.Target);
                    await WaitForAsync(() => runner.SessionStateText == "target reached 🎯",
                        TimeSpan.FromSeconds(5), "session state after the target");
                }
            }

            Assert.Equal(5, proposalRequests);
            Assert.Equal(5, buyRequests);

            // ── A fresh cycle must HOLD on the target, never propose again. ──
            var finalRunner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var finalRunnerDisposal = finalRunner;
            await finalRunner.StartAsync(plan);

            Assert.True(finalRunner.Engine!.TargetHit);
            Assert.Equal(0m, finalRunner.Engine.SuggestStake());

            await WaitForAsync(() => finalRunner.LastActivity.Contains("daily target reached"),
                TimeSpan.FromSeconds(15), "the target HOLD decision");
            Assert.Contains("HOLD", finalRunner.LastActivity);

            await Task.Delay(1_500, cts.Token);
            Assert.Equal(5, proposalRequests);
            Assert.Equal(5, buyRequests);

            finalRunner.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Kill switch mid-cycle
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Kill switch engaged mid-cycle (after the proposal, before settlement):
    /// the in-flight trade still buys and settles — the switch cannot cancel
    /// work already risk-approved — the settlement is persisted, and the loop
    /// then exits without another cycle ever reaching the broker.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_KillSwitchMidCycle_LetsInFlightTradeSettle_ThenStops()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var proposalRequests = 0;
        var buyRequests = 0;
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
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMO9\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-ksmid\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                var id = $"PROP-KM-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-KM-{buyRequests}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_ksmid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));
            var killSwitch = false;

            var config = new AccountConfig
            {
                Label = "KS Mid-Cycle Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await connection.ConnectAsync();

            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, IntervalMinutes = 60 };

            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => killSwitch,
                journal);
            await using var runnerDisposal = runner;

            await runner.StartAsync(plan);

            // Engage the switch mid-cycle: the proposal already passed the risk
            // check, the buy/settle chain is now in flight.
            await WaitForAsync(() => proposalRequests == 1,
                TimeSpan.FromSeconds(30), "the mid-cycle proposal");
            killSwitch = true;

            // The in-flight trade still buys and settles — the switch must not
            // cancel already-approved work.
            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "the in-flight settlement");
            Assert.Equal(1, buyRequests);
            var trade = Assert.Single(store.ForAccount(config.Id, TradeSource.Growth));
            Assert.True(trade.IsWin);
            await WaitForAsync(() => runner.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "bankroll after the in-flight settlement");

            // Once the cycle completes, the scheduler exits on its own and
            // nothing else ever reaches the broker.
            await WaitForAsync(() => !runner.IsRunning,
                TimeSpan.FromSeconds(10), "scheduler self-stop after the kill switch");
            await Task.Delay(1_000, cts.Token);
            Assert.Equal(1, proposalRequests);
            Assert.Equal(1, buyRequests);

            // Release and restart: the session resumes from the replayed state.
            killSwitch = false;
            var runner2 = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => killSwitch,
                journal);
            await using var runner2Disposal = runner2;
            await runner2.StartAsync(plan);

            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "the post-release trade");
            var second = store.ForAccount(config.Id, TradeSource.Growth)[1];
            Assert.True(second.IsWin);
            Assert.Equal(1.18m, second.Stake); // 20% of the replayed $5.90
            Assert.Equal(2, proposalRequests);

            runner2.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Paused account
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Paused account: StartAsync refuses while the account is paused (no
    /// cycles, no broker traffic), unpausing lets the session trade, pausing
    /// again blocks a restart, and resuming continues from the replayed
    /// bankroll — the pause is per-account and fully reversible.
    /// </summary>
    [Fact]
    public async Task GrowthRunner_PausedAccount_StartsRefused_CyclesSkip_AndResumesAfterResume()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var proposalRequests = 0;
        var buyRequests = 0;
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
                return $"{{\"msg_type\":\"authorize\",\"req_id\":{reqId},\"authorize\":{{\"balance\":100.00,\"currency\":\"USD\",\"loginid\":\"VRTCDEMOA\"}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $"{{\"msg_type\":\"history\",\"req_id\":{reqId},\"history\":{{\"prices\":[{historyPrices}],\"times\":[{historyTimes}]}},\"pip_size\":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-paused\"}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                proposalRequests++;
                var id = $"PROP-PA-{proposalRequests}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.17000,\"longcode\":\"Rise contract\",\"payout\":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                buyRequests++;
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-PA-{buyRequests}";
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

        var dataDir = Path.Combine(Path.GetTempPath(), $"tf_growth_paused_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var store = new TradeStore(dataDir);
            using var journal = new TradeJournal(Path.Combine(dataDir, "journal"));

            var config = new AccountConfig
            {
                Label = "Paused Demo",
                ApiToken = "fake-demo-token",
                IsDemo = true,
                BrainKey = "Growth"
            };

            await using var connection = new AccountConnection(config);
            connection.Client.Endpoint = server.WsUrl;
            connection.Client.SettlementPollInterval = FastPollInterval;
            await connection.ConnectAsync();

            var plan = GrowthPlan.Default with { CooldownMinutesAfterLoss = 0, IntervalMinutes = 60 };

            // ── Phase 1: paused → StartAsync refuses, nothing reaches the broker. ──
            connection.IsPaused = true;
            var runner = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var runnerDisposal = runner;

            await runner.StartAsync(plan);
            Assert.False(runner.IsRunning, "a paused account must not start a scheduler");
            Assert.Contains("paused", runner.LastActivity);

            await Task.Delay(1_500, cts.Token);
            Assert.Equal(0, proposalRequests);
            Assert.Equal(0, buyRequests);
            Assert.Empty(store.Trades);

            // ── Phase 2: resume → the session trades and settles. ──
            connection.IsPaused = false;
            await runner.StartAsync(plan);
            Assert.True(runner.IsRunning);

            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 1,
                TimeSpan.FromSeconds(30), "the trade after resume");
            Assert.True(store.ForAccount(config.Id, TradeSource.Growth)[0].IsWin);
            await WaitForAsync(() => runner.Engine!.Bankroll == 5.90m,
                TimeSpan.FromSeconds(5), "bankroll after the resumed trade");
            Assert.Contains("PASSED risk", runner.LastActivity);
            Assert.Equal(1, proposalRequests);

            // ── Phase 3: pause again → a restart is refused once more. ──
            connection.IsPaused = true;
            runner.Stop();
            await runner.StartAsync(plan);
            Assert.False(runner.IsRunning);
            Assert.Contains("paused", runner.LastActivity);
            await Task.Delay(1_000, cts.Token);
            Assert.Equal(1, proposalRequests);

            // ── Phase 4: resume → the session continues from the replayed state. ──
            connection.IsPaused = false;
            var runner2 = new GrowthRunner(
                connection, store,
                () => new AppSettings { AutonomyEnabled = true },
                () => false,
                journal);
            await using var runner2Disposal = runner2;
            await runner2.StartAsync(plan);

            Assert.Equal(5.90m, runner2.Engine!.Bankroll); // replayed, not reset

            await WaitForAsync(() => store.ForAccount(config.Id, TradeSource.Growth).Count == 2,
                TimeSpan.FromSeconds(30), "the trade after the second resume");
            var second = store.ForAccount(config.Id, TradeSource.Growth)[1];
            Assert.True(second.IsWin);
            Assert.Equal(1.18m, second.Stake); // 20% of the replayed $5.90

            // The engine applies the settlement a beat after the store write;
            // wait for it rather than reading the bankroll mid-apply (the gap
            // widens under parallel-run CPU contention).
            await WaitForAsync(() => runner2.Engine!.Bankroll == 6.962m,
                TimeSpan.FromSeconds(5), "bankroll after the second resumed win");
            Assert.Equal(2, proposalRequests);

            runner2.Stop();
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Webhook delivery of restart / give-up events
    // ─────────────────────────────────────────────────────────────


    /// <summary>
    /// Builds a settled growth trade with the given outcome for seeding the
    /// shared trade log directly (bypassing the broker).
    /// </summary>
    private static Trade MakeGrowthTrade(Guid accountId, string accountName, string contractId,
        decimal stake, decimal profit, DateTimeOffset settledAt) => new(
            Guid.NewGuid(), "frxEURUSD",
            profit >= 0 ? Direction.Rise : Direction.Fall,
            stake, "USD", 1.17, 1700000300, contractId,
            profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
            profit, 1.165, 1700000600, settledAt,
            accountId, accountName, TradeSource.Growth);

    private static readonly JsonSerializerOptions TradeLogOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Rewrites every persisted settlement's timestamp to yesterday — the
    /// test-only stand-in for the clock passing midnight — directly in the
    /// trade log file, so both the risk context and the session replay see a
    /// clean new day after the store is reloaded.
    /// </summary>
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

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout
    /// expires. Transient throws (socket races, enumerating a collection while
    /// another thread mutates it) are treated as "not yet" so the failure is
    /// always the caller's real condition, not harness noise. The timeout
    /// message carries the poll count and the condition's recurring exception,
    /// if any, to make CI failures diagnosable.
    /// </summary>
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
}
