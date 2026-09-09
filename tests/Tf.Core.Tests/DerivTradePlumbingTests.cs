using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.Core.Tests;

/// <summary>
/// Milestone-2 plumbing tests: the exact JSON sent to Deriv and the parsing
/// of proposal/buy/contract responses. Runs against an in-process fake
/// WebSocket server, so no token or network is needed.
/// </summary>
[Trait("Category", "Unit")]
public class DerivTradePlumbingTests
{
    private static async Task<(FakeDerivServer Server, DerivClient Client)> SetupAsync(
        Func<JsonElement, string> responder, CancellationToken ct = default)
    {
        var server = new FakeDerivServer(responder);
        _ = server.RunAsync(ct);

        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await client.ConnectAsync(ct: ct);
        return (server, client);
    }

    /// <summary>Wraps a payload in a response that echoes the request's req_id.</summary>
    private static string Respond(JsonElement req, string msgType, string body) =>
        $"{{\"msg_type\":\"{msgType}\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"{msgType}\":{body}}}";

    private static string ProposalBody(string id, double spot, decimal payout) =>
        $"{{\"id\":\"{id}\",\"spot\":{spot},\"longcode\":\"{id}\",\"payout\":{payout}}}";

    private static string ContractBody(string id, string status, bool isSold, double entry,
        double exit, long entryTime, long exitTime, decimal buyPrice, decimal profit) =>
        $"{{\"contract_id\":\"{id}\",\"status\":\"{status}\",\"is_sold\":{(isSold ? "true" : "false")}," +
        $"\"entry_spot\":{entry},\"exit_spot\":{exit},\"entry_tick_time\":{entryTime}," +
        $"\"exit_tick_time\":{exitTime},\"buy_price\":{buyPrice},\"profit\":{profit},\"currency\":\"USD\"}}";

    [Fact]
    public async Task GetProposalAsync_SendsCorrectPayload_ForRise()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = await SetupAsync(req =>
            Respond(req, "proposal", ProposalBody("PROP-RISE", 1.12345, 1.9m)), cts.Token);
        await using var _ = client;
        await using var __ = server;

        var proposal = await client.GetProposalAsync("frxEURUSD", Direction.Rise, 1m, "USD", 5, cts.Token);

        Assert.Equal("PROP-RISE", proposal.Id);
        Assert.Equal("frxEURUSD", proposal.Symbol);
        Assert.Equal(1.12345, proposal.Spot, 5);
        Assert.Equal(1.9m, proposal.Payout);
        Assert.Equal("CALL", proposal.Direction.ToContractType());

        var sent = JsonDocument.Parse(server.Received.Single()).RootElement;
        Assert.Equal(1, sent.GetProperty("proposal").GetInt32());
        Assert.Equal(1m, sent.GetProperty("amount").GetDecimal());
        Assert.Equal("stake", sent.GetProperty("basis").GetString());
        Assert.Equal("CALL", sent.GetProperty("contract_type").GetString());
        Assert.Equal("USD", sent.GetProperty("currency").GetString());
        Assert.Equal(5, sent.GetProperty("duration").GetInt32());
        Assert.Equal("m", sent.GetProperty("duration_unit").GetString());
        Assert.Equal("frxEURUSD", sent.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task GetProposalAsync_Fall_SendsPut()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = await SetupAsync(req =>
            Respond(req, "proposal", ProposalBody("PROP-FALL", 2.5, 1.9m)), cts.Token);
        await using var _ = client;
        await using var __ = server;

        await client.GetProposalAsync("V-100", Direction.Fall, 2m, "USD", 1, cts.Token);

        var sent = JsonDocument.Parse(server.Received.Single()).RootElement;
        Assert.Equal("PUT", sent.GetProperty("contract_type").GetString());
        Assert.Equal(2m, sent.GetProperty("amount").GetDecimal());
        Assert.Equal(1, sent.GetProperty("duration").GetInt32());
    }

    [Fact]
    public async Task BuyAsync_ParsesBuyResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = await SetupAsync(req =>
            Respond(req, "buy",
                "{\"contract_id\":\"CONTRACT-77\",\"buy_price\":1.05,\"balance_after\":999.95,\"longcode\":\"Rise\"}"),
            cts.Token);
        await using var _ = client;
        await using var __ = server;

        var buy = await client.BuyAsync("PROP-RISE", 1.05, cts.Token);

        Assert.Equal("CONTRACT-77", buy.ContractId);
        Assert.Equal(1.05m, buy.BuyPrice);
        Assert.Equal(999.95m, buy.BalanceAfter);

        var sent = JsonDocument.Parse(server.Received.Single()).RootElement;
        Assert.Equal("PROP-RISE", sent.GetProperty("buy").GetString());
        Assert.Equal(1.05, sent.GetProperty("price").GetDouble());
    }

    [Fact]
    public async Task GetContractAsync_ParsesWonContract()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = await SetupAsync(req =>
            Respond(req, "proposal_open_contract",
                ContractBody("CONTRACT-77", "won", true, 1.10, 1.11, 1700000000, 1700000300, 1.05m, 0.95m)),
            cts.Token);
        await using var _ = client;
        await using var __ = server;

        var contract = await client.GetContractAsync("CONTRACT-77", cts.Token);

        Assert.Equal(ContractStatus.Won, contract.Status);
        Assert.True(contract.IsSold);
        Assert.Equal(1.10, contract.EntrySpot);
        Assert.Equal(0.95m, contract.Profit);
        Assert.Equal("USD", contract.Currency);
    }

    [Fact]
    public async Task WaitForSettlementAsync_PollsUntilSold()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var polls = 0;
        var (server, client) = await SetupAsync(req =>
        {
            polls++;
            return polls == 1
                ? Respond(req, "proposal_open_contract",
                    ContractBody("CONTRACT-77", "open", false, 1.10, 0, 1700000000, 0, 1.05m, 0m))
                : Respond(req, "proposal_open_contract",
                    ContractBody("CONTRACT-77", "lost", true, 1.10, 1.09, 1700000000, 1700000300, 1.05m, -1.05m));
        }, cts.Token);
        await using var _ = client;
        await using var __ = server;

        var final = await client.WaitForSettlementAsync(
            "CONTRACT-77", timeout: TimeSpan.FromSeconds(5), ct: cts.Token);

        Assert.Equal(ContractStatus.Lost, final.Status);
        Assert.True(final.IsSold);
        Assert.Equal(-1.05m, final.Profit);
        Assert.True(polls >= 2, "settlement should require more than one poll");
    }

    [Fact]
    public async Task ApiError_ThrowsDerivApiException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = await SetupAsync(req =>
            $"{{\"msg_type\":\"error\",\"req_id\":{req.GetProperty("req_id").GetInt32()}," +
            "\"error\":{\"code\":\"AuthorizationRequired\",\"message\":\"Please authenticate.\"}}",
            cts.Token);
        await using var _ = client;
        await using var __ = server;

        var ex = await Assert.ThrowsAsync<DerivApiException>(
            () => client.GetProposalAsync("frxEURUSD", Direction.Rise, 1m, "USD", 5, cts.Token));
        Assert.Equal("AuthorizationRequired", ex.Code);
    }

    /// <summary>
    /// Tiny in-process Deriv API stand-in: accepts one WebSocket connection,
    /// records every request, and answers from a responder function.
    /// </summary>
    private sealed class FakeDerivServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<JsonElement, string> _responder;
        private readonly List<string> _received = new();
        private readonly object _sync = new();
        private Task? _runTask;

        public FakeDerivServer(Func<JsonElement, string> responder)
        {
            _responder = responder;

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
        public IReadOnlyList<string> Received
        {
            get { lock (_sync) { return _received.ToArray(); } }
        }

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
                    var result = await ws.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    lock (_sync)
                    {
                        _received.Add(json);
                    }

                    using var doc = JsonDocument.Parse(json);
                    var response = _responder(doc.RootElement);
                    if (!string.IsNullOrEmpty(response))
                    {
                        var bytes = Encoding.UTF8.GetBytes(response);
                        await ws.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // test finished
            }
            catch (WebSocketException)
            {
                // client went away
            }
            finally
            {
                try { ws.WebSocket.Dispose(); } catch { /* best effort */ }
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { /* best effort */ }
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}