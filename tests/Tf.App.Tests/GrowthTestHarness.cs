using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tf.App.Infrastructure;
using Tf.App.Services;

namespace Tf.App.Tests;

/// <summary>
/// Shared harness for the growth end-to-end tests: an in-process fake Deriv
/// WebSocket server plus a <em>scripted</em> fake-broker responder, so each
/// scenario wires only the outcome script it cares about (which contract
/// wins or loses) instead of re-implementing the Deriv protocol boilerplate.
/// Scenarios needing non-standard behaviour (proposal errors, contract-id
/// asserts) keep their own custom responder — the server class is still shared.
/// </summary>
internal static class GrowthTestHarness
{
    public const int HistoryTickCount = 30;

    /// <summary>The monotonically falling tick history every fake broker quotes.</summary>
    public static (string Prices, string Times) History(int count = HistoryTickCount) =>
        (string.Join(",", Enumerable.Range(0, count)
            .Select(i => (1.2000 - 0.001 * i).ToString("F5", CultureInfo.InvariantCulture))),
         string.Join(",", Enumerable.Range(0, count)
            .Select(i => (1700000000L + i).ToString(CultureInfo.InvariantCulture))));

    /// <summary>Proposal/buy counters for one fake growth broker.</summary>
    public sealed class BrokerCounters
    {
        private int _proposals;
        private int _buys;

        public int Proposals => _proposals;
        public int Buys => _buys;

        /// <summary>Atomically consumes the next proposal number — the return
        /// value is unique per call even with concurrent connections.</summary>
        public int NextProposal() => Interlocked.Increment(ref _proposals);

        /// <summary>Atomically consumes the next buy number.</summary>
        public int NextBuy() => Interlocked.Increment(ref _buys);
    }

    /// <summary>
    /// One scripted settlement: the n-th bought contract (1-based) on this
    /// broker settles as a win (+90% of the stake) or a loss (−stake).
    /// </summary>
    public readonly record struct OutcomeStep(int ContractNumber, bool Win);

    /// <summary>Script: every contract wins (+90% of the stake).</summary>
    public static IReadOnlyList<OutcomeStep> AllWins { get; } = Array.Empty<OutcomeStep>();

    /// <summary>Script: every contract loses (−stake).</summary>
    public static IReadOnlyList<OutcomeStep> AllLosses { get; } = new OutcomeStep[] { new(1, Win: false) };

    /// <summary>
    /// Builds a scripted fake-broker responder: authorizes as
    /// <paramref name="loginId"/>, quotes the shared falling-tick history,
    /// buys contracts under the <paramref name="prefix"/> ids, and settles
    /// contract n according to <paramref name="outcomes"/>. Contracts beyond
    /// the last scripted step repeat the last scripted outcome (none → win).
    /// <paramref name="fallback"/> handles any message the script does not
    /// answer (defaults to ignoring it).
    /// </summary>
    public static Func<JsonElement, string> BrokerScript(
        string loginId, string prefix, BrokerCounters counters,
        IReadOnlyList<OutcomeStep> outcomes,
        Func<JsonElement, string>? fallback = null)
    {
        var (historyPrices, historyTimes) = History();
        var proposalStakeById = new ConcurrentDictionary<string, decimal>();
        var contractStakeById = new ConcurrentDictionary<string, decimal>();
        var openContracts = new ConcurrentDictionary<string, byte>();

        return req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();

            if (req.TryGetProperty("authorize", out _))
                return $@"{{""msg_type"":""authorize"",""req_id"":{reqId},""authorize"":{{""balance"":100.00,""currency"":""USD"",""loginid"":""{loginId}""}}}}";

            if (req.TryGetProperty("ticks_history", out _))
                return $@"{{""msg_type"":""history"",""req_id"":{reqId},""history"":{{""prices"":[{historyPrices}],""times"":[{historyTimes}]}},""pip_size"":5}}";

            if (req.TryGetProperty("ticks", out _))
                return $@"{{""msg_type"":""ticks"",""req_id"":{reqId},""subscription"":{{""id"":""sub-{prefix}""}}}}";

            if (req.TryGetProperty("proposal", out _))
            {
                var seq = counters.NextProposal();
                var id = $"PROP-{prefix}-{seq}";
                proposalStakeById[id] = req.GetProperty("amount").GetDecimal();
                return $@"{{""msg_type"":""proposal"",""req_id"":{reqId},""proposal"":{{""id"":""{id}"",""spot"":1.17000,""longcode"":""Rise contract"",""payout"":1.90}}}}";
            }

            if (req.TryGetProperty("buy", out _))
            {
                var seq = counters.NextBuy();
                var stake = proposalStakeById[req.GetProperty("buy").GetString() ?? ""];
                var cid = $"GROWTH-{prefix}-{seq}";
                contractStakeById[cid] = stake;
                return $@"{{""msg_type"":""buy"",""req_id"":{reqId},""buy"":{{""contract_id"":""{cid}"",""buy_price"":{stake.ToString(CultureInfo.InvariantCulture)},""balance_after"":99.00,""longcode"":""Rise contract""}}}}";
            }

            if (req.TryGetProperty("proposal_open_contract", out _))
            {
                var cid = req.GetProperty("contract_id").GetString() ?? "";
                var stake = contractStakeById[cid];
                var seq = int.Parse(cid.Replace($"GROWTH-{prefix}-", ""), CultureInfo.InvariantCulture);
                var win = outcomes.LastOrDefault(o => o.ContractNumber <= seq) is { ContractNumber: > 0 } step
                    ? step.Win
                    : true; // nothing scripted → repeat the (implicit) win
                var profit = win ? stake * 0.90m : -stake;
                var status = win ? "won" : "lost";
                var exitSpot = win ? "1.17800" : "1.16500";
                if (openContracts.TryAdd(cid, 0))
                    return $@"{{""msg_type"":""proposal_open_contract"",""req_id"":{reqId},""proposal_open_contract"":{{""contract_id"":""{cid}"",""status"":""open"",""is_sold"":false,""entry_spot"":1.17000,""exit_spot"":0,""entry_tick_time"":1700000300,""exit_tick_time"":0,""buy_price"":{stake.ToString(CultureInfo.InvariantCulture)},""profit"":0,""currency"":""USD""}}}}";
                return $@"{{""msg_type"":""proposal_open_contract"",""req_id"":{reqId},""proposal_open_contract"":{{""contract_id"":""{cid}"",""status"":""{status}"",""is_sold"":true,""entry_spot"":1.17000,""exit_spot"":{exitSpot},""entry_tick_time"":1700000300,""exit_tick_time"":1700000600,""buy_price"":{stake.ToString(CultureInfo.InvariantCulture)},""profit"":{profit.ToString(CultureInfo.InvariantCulture)},""currency"":""USD""}}}}";
            }

            return fallback?.Invoke(req) ?? "";
        };
    }
}

/// <summary>
/// In-process fake Deriv WebSocket server (same approach as the
/// DerivIntegrationFlowTests fake): accepts connections sequentially so a
/// DerivClient reconnect takes over cleanly, echoes req_id, streams a few
/// live ticks after a ticks subscription, and can drop every socket to
/// simulate a network failure.
/// </summary>
internal sealed class FakeDerivServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<JsonElement, string> _responder;
    private readonly Func<JsonElement, string?> _tickGenerator;
    private readonly List<string> _received = new();
    private readonly List<WebSocket> _connections = new();
    private readonly object _sync = new();

    public FakeDerivServer(Func<JsonElement, string> responder, CancellationToken ct,
        Func<JsonElement, string?>? tickGenerator = null)
    {
        _responder = responder;
        _tickGenerator = tickGenerator ?? DefaultTickGenerator;

        // Bind inside the helper's lock-and-retry window: the naive
        // probe-then-bind here races the port away under parallel test
        // hosts and intermittently fails setup with HttpListenerException.
        (_listener, var port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        Port = port;
    }

    public int Port { get; }
    public string WsUrl => $"ws://127.0.0.1:{Port}";

    /// <summary>Live WebSocket connections (0 right after a socket drop).</summary>
    public int ConnectionCount
    {
        get { lock (_sync) { return _connections.Count; } }
    }

    /// <summary>Simulates a network failure: aborts every live socket.</summary>
    public void DropConnection()
    {
        WebSocket[] sockets;
        lock (_sync)
        {
            sockets = _connections.ToArray();
        }

        foreach (var socket in sockets)
        {
            try { socket.Abort(); } catch { /* best effort */ }
        }
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.Run(() => RunCoreAsync(ct), CancellationToken.None);

    private async Task RunCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break; // listener disposed while shutting down
            }

            WebSocket ws;
            try
            {
                ws = (await context.AcceptWebSocketAsync(null).WaitAsync(ct)).WebSocket;
            }
            catch (Exception)
            {
                continue;
            }

            // Accept connections sequentially: one DerivClient at a time,
            // so a reconnect cleanly replaces a dropped socket.
            lock (_sync) { _connections.Add(ws); }
            _ = Task.Run(() => HandleConnectionAsync(ws, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                }
                catch (WebSocketException)
                {
                    break;
                }
                catch (OperationCanceledException)
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
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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
                            await ws.SendAsync(tickBytes, WebSocketMessageType.Text, true, ct);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_sync) { _connections.Remove(ws); }
            try { ws.Dispose(); } catch { }
        }
    }

    /// <summary>A gently falling live tick following the quoted history.</summary>
    private static int _defaultTickIndex;

    private static string DefaultTickGenerator(JsonElement _)
    {
        var index = Interlocked.Increment(ref _defaultTickIndex);
        var quote = (1.2000 - 0.001 * (GrowthTestHarness.HistoryTickCount + index))
            .ToString("F5", CultureInfo.InvariantCulture);
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $@"{{""msg_type"":""tick"",""tick"":{{""symbol"":""frxEURUSD"",""quote"":{quote},""ask"":{quote},""bid"":{quote},""epoch"":{epoch},""pip_size"":5}}}}";
    }

    public ValueTask DisposeAsync()
    {
        DropConnection();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}
