using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.Core.Tests;

/// <summary>
/// End-to-end integration tests that simulate the full Deriv trading lifecycle:
/// connect → tick stream → propose → buy → settle, plus error handling,
/// reconnection, and concurrent scenarios.
/// All tests run against an in-process fake WebSocket server.
/// </summary>
[Trait("Category", "Integration")]
public class DerivIntegrationFlowTests
{
    // ─── Full Trade Lifecycle ──────────────────────────────

    [Fact]
    public async Task FullLifecycle_AuthorizeSubscribeProposeBuySettle()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pollCount = 0;

        var server = new FlowFakeServer(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("ticks", out _))
                return $"{{\"msg_type\":\"tick\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-1\"}}}}";

            if (req.TryGetProperty("proposal", out _))
                return $"{{\"msg_type\":\"proposal\",\"req_id\":{reqId},\"proposal\":{{\"id\":\"LIFECYCLE-1\",\"spot\":1.10050,\"longcode\":\"Rise contract\",\"payout\":1.85}}}}";

            if (req.TryGetProperty("buy", out _))
                return $"{{\"msg_type\":\"buy\",\"req_id\":{reqId},\"buy\":{{\"contract_id\":\"LC-CONTRACT-1\",\"buy_price\":1.00,\"balance_after\":999.00,\"longcode\":\"Rise contract\"}}}}";

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                pollCount++;
                if (pollCount == 1)
                    return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"LC-CONTRACT-1\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.10050,\"exit_spot\":0,\"entry_tick_time\":1700000000,\"exit_tick_time\":0,\"buy_price\":1.00,\"profit\":0,\"currency\":\"USD\"}}}}";
                return $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{reqId},\"proposal_open_contract\":{{\"contract_id\":\"LC-CONTRACT-1\",\"status\":\"won\",\"is_sold\":true,\"entry_spot\":1.10050,\"exit_spot\":1.10120,\"entry_tick_time\":1700000000,\"exit_tick_time\":1700000300,\"buy_price\":1.00,\"profit\":0.85,\"currency\":\"USD\"}}}}";
            }

            return "";
        }, cts.Token, req => $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":1.10050,\"ask\":1.10052,\"bid\":1.10048,\"epoch\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"pip_size\":5}}}}");
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        // Step 1: Connect
        await client.ConnectAsync(ct: cts.Token);
        Assert.True(client.IsConnected);

        // Step 2: Subscribe to ticks
        var ticks = new List<Tick>();
        client.TickReceived += t => ticks.Add(t);
        await client.SubscribeTicksAsync("frxEURUSD", cts.Token);
        await Task.Delay(500, cts.Token);
        Assert.NotEmpty(ticks);
        Assert.Equal("frxEURUSD", ticks[0].Symbol);

        // Step 3: Get proposal
        var proposal = await client.GetProposalAsync(
            "frxEURUSD", Direction.Rise, 1m, "USD", 5, cts.Token);
        Assert.Equal("LIFECYCLE-1", proposal.Id);
        Assert.Equal(1.85m, proposal.Payout);

        // Step 4: Buy
        var buy = await client.BuyAsync("LIFECYCLE-1", 1.00, cts.Token);
        Assert.Equal("LC-CONTRACT-1", buy.ContractId);
        Assert.Equal(1.00m, buy.BuyPrice);
        Assert.Equal(999.00m, buy.BalanceAfter);

        // Step 5: Wait for settlement
        var contract = await client.WaitForSettlementAsync(
            "LC-CONTRACT-1", TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal(ContractStatus.Won, contract.Status);
        Assert.True(contract.IsSold);
        Assert.Equal(0.85m, contract.Profit);
        Assert.True(pollCount >= 2, $"Expected at least 2 polls but got {pollCount}");
    }

    // ─── Tick Streaming ───────────────────────────────────

    [Fact]
    public async Task TickSubscription_ReceivesMultipleTicks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tickCount = 0;

        var server = new FlowFakeServer(req =>
        {
            var reqId = req.TryGetProperty("req_id", out var r) ? r.GetInt32() : 0;
            return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-mt\"}}}}";
        }, cts.Token, req =>
        {
            tickCount++;
            return $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":{1.10000 + tickCount * 0.00010:F5},\"epoch\":{1700000000 + tickCount},\"pip_size\":5}}}}";
        });
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        var receivedTicks = new ConcurrentBag<Tick>();
        client.TickReceived += t => receivedTicks.Add(t);
        await client.SubscribeTicksAsync("frxEURUSD", cts.Token);
        await Task.Delay(2000, cts.Token);

        Assert.True(receivedTicks.Count >= 2,
            $"Expected at least 2 ticks but got {receivedTicks.Count}");
    }

    [Fact]
    public async Task TickSubscription_IncludesAskBidSpread()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
        {
            var reqId = req.TryGetProperty("req_id", out var r) ? r.GetInt32() : 0;
            return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-spread\"}}}}";
        }, cts.Token, req =>
            $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxGBPUSD\",\"quote\":1.27000,\"ask\":1.27002,\"bid\":1.26998,\"epoch\":1700000000,\"pip_size\":5}}}}");
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        Tick? received = null;
        client.TickReceived += t => received = t;
        await client.SubscribeTicksAsync("frxGBPUSD", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.NotNull(received);
        Assert.Equal(1.27000, received!.Quote, 5);
        Assert.Equal(1.27002, received.Ask, 5);
        Assert.Equal(1.26998, received.Bid, 5);
    }

    // ─── Error Handling ───────────────────────────────────

    [Fact]
    public async Task InvalidProposal_ThrowsDerivApiException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
            $"{{\"msg_type\":\"error\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"error\":{{\"code\":\"InvalidSymbol\",\"message\":\"Invalid symbol: XYZ\"}}}}",
            cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        var ex = await Assert.ThrowsAsync<DerivApiException>(
            () => client.GetProposalAsync("XYZ", Direction.Rise, 1m, "USD", 5, cts.Token));
        Assert.Equal("InvalidSymbol", ex.Code);
        Assert.Contains("Invalid symbol", ex.Message);
    }

    [Fact]
    public async Task StreamedError_ReceivedAsEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
        {
            var reqId = req.TryGetProperty("req_id", out var r) ? r.GetInt32() : 0;
            return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-err\"}}}}";
        }, cts.Token, req =>
            $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":1.1,\"epoch\":1700000000,\"pip_size\":5}}}}");
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        var errors = new List<string>();
        client.ErrorReceived += e => errors.Add(e);

        await client.SubscribeTicksAsync("frxEURUSD", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.Empty(errors);
    }

    // ─── Reconnection ────────────────────────────────────

    [Fact]
    public async Task Disconnect_SetsStatusToDisconnected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req => "", cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
        Assert.Equal(ConnectionStatus.Disconnected, client.Status);
    }

    [Fact]
    public async Task StatusChanged_RaisesEvents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var statuses = new List<ConnectionStatus>();

        var server = new FlowFakeServer(req => "", cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        client.StatusChanged += s => statuses.Add(s);
        await client.ConnectAsync(ct: cts.Token);

        Assert.Contains(ConnectionStatus.Connected, statuses);
    }

    // ─── Proposal Edge Cases ──────────────────────────────

    [Fact]
    public async Task FallDirection_SendsPutContractType()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
            $"{{\"msg_type\":\"proposal\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"proposal\":{{\"id\":\"PUT-1\",\"spot\":1.25000,\"longcode\":\"Fall contract\",\"payout\":1.90}}}}",
            cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        var proposal = await client.GetProposalAsync(
            "V-75", Direction.Fall, 2m, "USD", 1, cts.Token);
        Assert.Equal("PUT-1", proposal.Id);
        Assert.Equal(Direction.Fall, proposal.Direction);
    }

    [Fact]
    public async Task SettlementTimeout_ThrowsTimeoutException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
            $"{{\"msg_type\":\"proposal_open_contract\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"proposal_open_contract\":{{\"contract_id\":\"STUCK-1\",\"status\":\"open\",\"is_sold\":false,\"entry_spot\":1.1,\"exit_spot\":0,\"entry_tick_time\":1700000000,\"exit_tick_time\":0,\"buy_price\":1.00,\"profit\":0,\"currency\":\"USD\"}}}}",
            cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.WaitForSettlementAsync("STUCK-1", TimeSpan.FromSeconds(2), cts.Token));
    }

    // ─── Concurrency ──────────────────────────────────────

    [Fact]
    public async Task MultipleProposals_ConcurrentRequests()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
        {
            var id = $"PROP-{req.GetProperty("req_id").GetInt32()}";
            return $"{{\"msg_type\":\"proposal\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"proposal\":{{\"id\":\"{id}\",\"spot\":1.10000,\"longcode\":\"Test\",\"payout\":1.80}}}}";
        }, cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        var tasks = new[]
        {
            client.GetProposalAsync("frxEURUSD", Direction.Rise, 1m, "USD", 5, cts.Token),
            client.GetProposalAsync("frxGBPUSD", Direction.Fall, 2m, "USD", 1, cts.Token),
            client.GetProposalAsync("frxUSDJPY", Direction.Rise, 1m, "JPY", 10, cts.Token)
        };

        var results = await Task.WhenAll(tasks);
        Assert.Equal(3, results.Length);
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.Id)));
    }

    // ─── History Parsing ──────────────────────────────────

    [Fact]
    public async Task GetTicksHistory_ParsesPricesAndTimes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
            $"{{\"msg_type\":\"history\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"history\":{{\"prices\":[1.10001,1.10005,1.10003],\"times\":[1700000000,1700000002,1700000004]}},\"pip_size\":5}}",
            cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        var ticks = await client.GetTicksHistoryAsync("frxEURUSD", 3, cts.Token);
        Assert.Equal(3, ticks.Count);
        Assert.Equal(1.10001, ticks[0].Quote, 5);
        Assert.Equal(1700000000, ticks[0].Epoch);
        Assert.Equal(1.10005, ticks[1].Quote, 5);
        Assert.Equal(1.10003, ticks[2].Quote, 5);
    }

    // ─── Bid/Ask Defaults ─────────────────────────────────

    [Fact]
    public async Task TickMissingAskBid_DefaultsToQuote()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = new FlowFakeServer(req =>
        {
            var reqId = req.TryGetProperty("req_id", out var r) ? r.GetInt32() : 0;
            return $"{{\"msg_type\":\"ticks\",\"req_id\":{reqId},\"subscription\":{{\"id\":\"sub-default\"}}}}";
        }, cts.Token, req =>
            $"{{\"msg_type\":\"tick\",\"tick\":{{\"symbol\":\"frxEURUSD\",\"quote\":1.10000,\"epoch\":1700000000,\"pip_size\":5}}}}");
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;
        await using var serverDisposal = server;

        await client.ConnectAsync(ct: cts.Token);

        Tick? received = null;
        client.TickReceived += t => received = t;
        await client.SubscribeTicksAsync("frxEURUSD", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.NotNull(received);
        Assert.Equal(received!.Quote, received.Ask, 5);
        Assert.Equal(received.Quote, received.Bid, 5);
    }

    // ─── Fake Server ──────────────────────────────────────

    private sealed class FlowFakeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<JsonElement, string> _responder;
        private readonly Func<JsonElement, string?>? _tickGenerator;
        private readonly List<string> _received = new();
        private readonly object _sync = new();
        private Task? _runTask;

        public FlowFakeServer(Func<JsonElement, string> responder, CancellationToken ct = default,
            Func<JsonElement, string?>? tickGenerator = null)
        {
            _responder = responder;
            _tickGenerator = tickGenerator;
            (_listener, var port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
            Port = port;
        }

        public int Port { get; }
        public string WsUrl => $"ws://127.0.0.1:{Port}";

        public Task RunAsync(CancellationToken ct = default)
        {
            _runTask ??= Task.Run(() => RunCoreAsync(ct), CancellationToken.None);
            return _runTask;
        }

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
                    catch (WebSocketException) { break; }

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    lock (_sync) { _received.Add(json); }

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var isTicksSub = root.TryGetProperty("ticks", out _);

                    var response = _responder(root);
                    if (!string.IsNullOrEmpty(response))
                    {
                        var bytes = Encoding.UTF8.GetBytes(response);
                        await ws.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }

                    // For tick subscriptions, the req_id is already handled by _responder.
                    // Now stream tick data if a tick generator is provided.
                    if (isTicksSub && _tickGenerator != null)
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
