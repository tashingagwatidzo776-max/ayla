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
/// End-to-end test that drives the real GrowthRunner stack — AccountConnection,
/// DerivClient, TradingBrain, GrowthBrain, RiskEngine, GrowthSessionEngine,
/// TradeStore and TradeJournal — against an in-process fake Deriv WebSocket
/// server. Verifies one full growth cycle completes: connect → history
/// backfill → RSI decision → risk check → proposal → buy → settlement →
/// tagged persistence → bankroll update.
/// </summary>
[Trait("Category", "Integration")]
public class GrowthRunnerEndToEndTests
{
    private const int HistoryTickCount = 30;
    private const decimal ExpectedStake = 1.00m;    // GrowthPlan default: $5 budget × 20% risk
    private const decimal ExpectedProfit = 0.90m;   // $1 stake at the fake 1.90 payout
    private const decimal ExpectedBankrollAfterWin = 5.90m;
    private const string ContractId = "GROWTH-C-1";

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
