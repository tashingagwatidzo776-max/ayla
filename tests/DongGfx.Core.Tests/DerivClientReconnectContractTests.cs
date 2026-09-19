using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.Core.Tests;

/// <summary>
/// Locks the client's reconnect contract in isolation (the multi-account hub
/// E2E covers it through the hub; these tests pin the client itself):
///
///   1. An explicit <see cref="DerivClient.DisconnectAsync"/> means "the
///      session is over" — no reconnect may fire afterwards, even when the
///      receive loop's exit races the disconnect (an outage scheduling a
///      reconnect just before the caller disconnects must self-cancel).
///   2. The next explicit <see cref="DerivClient.ConnectAsync"/> re-arms
///      auto-reconnect: a genuine outage afterwards reconnects on its own.
///
/// Both tests run against an in-process fake WebSocket server that accepts
/// any number of sequential connections, so an unsuppressed reconnect attempt
/// actually SUCCEEDS — the strongest possible failure signal.
/// </summary>
[Trait("Category", "Integration")]
public class DerivClientReconnectContractTests
{
    [Fact]
    public async Task ExplicitDisconnect_SuppressesAutoReconnect_UntilNextExplicitConnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server1 = new MultiAcceptFakeServer();
        var client = new DerivClient { AppId = "1089", Endpoint = server1.WsUrl };
        await using var clientDisposal = client;

        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += statuses.Enqueue;

        await client.ConnectAsync(ct: cts.Token);
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();
        Assert.Equal(ConnectionStatus.Disconnected, client.Status);

        // Kill the server outright: any unsuppressed reconnect attempt would
        // surface as Reconnecting/Connecting within the 2s first-attempt
        // delay (and keep retrying on its own backoff).
        await server1.DisposeAsync();
        await Task.Delay(4000, cts.Token);

        Assert.Equal(ConnectionStatus.Disconnected, client.Status);
        Assert.DoesNotContain(ConnectionStatus.Reconnecting, statuses);
    }

    [Fact]
    public async Task ExplicitDisconnect_RacingAnOutage_NeverLetsThePendingReconnectFire()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new MultiAcceptFakeServer();
        await using var serverDisposal = server;
        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await using var clientDisposal = client;

        await client.ConnectAsync(ct: cts.Token);
        Assert.True(client.IsConnected);

        // Two things race: the abort kills the receive loop (whose exit would
        // schedule a reconnect) and the explicit disconnect sets the
        // suppression flag. Whichever wins, the pending attempt must never
        // connect — the server stays up so an unsuppressed attempt would
        // succeed and flip the client back to Connected.
        await server.Accepted.WaitAsync(cts.Token); // server-side accept has run
        server.DropCurrent();
        await client.DisconnectAsync();

        await Task.Delay(4000, cts.Token);
        Assert.Equal(ConnectionStatus.Disconnected, client.Status);
    }

    [Fact]
    public async Task NextExplicitConnect_ReArmsAutoReconnect_AfterAPreviousExplicitDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server1 = new MultiAcceptFakeServer();
        var client = new DerivClient { AppId = "1089", Endpoint = server1.WsUrl };
        await using var clientDisposal = client;

        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += statuses.Enqueue;
        var errors = new ConcurrentQueue<string>();
        client.ErrorReceived += errors.Enqueue;

        // Explicit disconnect (suppression armed), then move to a fresh server.
        await client.ConnectAsync(ct: cts.Token);
        await client.DisconnectAsync();
        await server1.DisposeAsync();

        var server2 = new MultiAcceptFakeServer();
        await using var server2Disposal = server2;
        client.Endpoint = server2.WsUrl;
        await client.ConnectAsync(ct: cts.Token);
        Assert.True(client.IsConnected);

        // A genuine outage afterwards must reconnect by itself: the client
        // re-establishes the session on the still-running server without any
        // further explicit call.
        await server2.Accepted.WaitAsync(cts.Token); // server-side accept has run
        var statusesBeforeOutage = statuses.Count;
        server2.DropCurrent();

        // The drop tears the TCP connection down asynchronously: the client's
        // status stays Connected for a few milliseconds after DropCurrent.
        // Wait for the outage to be OBSERVED first (otherwise the recovery
        // wait below returns instantly and the assert races the events),
        // then wait for the autonomous reconnection.
        await WaitUntilAsync(
            () => client.Status != ConnectionStatus.Connected,
            TimeSpan.FromSeconds(10), cts.Token);
        await WaitUntilAsync(
            () => client.Status == ConnectionStatus.Connected,
            TimeSpan.FromSeconds(20), cts.Token);
        var observed = string.Join(", ", statuses.Skip(statusesBeforeOutage));
        var errText = string.Join(" | ", errors);
        Assert.True(
            statuses.Skip(statusesBeforeOutage).Any(s => s == ConnectionStatus.Reconnecting),
            $"auto-reconnect did not re-arm; statuses after the outage: [{observed}], errors: [{errText}]");
    }

    /// <summary>Polls until the condition holds (the fake-server suite's 5s
    /// default is too short for a reconnect cycle: attempt 1 fires at 2s and
    /// the reconnection itself adds connection time under CI load).</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, ct);
        }

        Assert.True(condition(), $"condition not met within {timeout.TotalSeconds:0}s");
    }

    /// <summary>A fake WebSocket server that accepts any number of sequential
    /// connections and can abort the current one — exactly what the reconnect
    /// contract tests need (an unsuppressed reconnect must actually succeed).</summary>
    private sealed class MultiAcceptFakeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private WebSocket? _current;
        private HttpListenerContext? _currentCtx;

        // Completes when a WebSocket handshake has been accepted server-side:
        // the client's ConnectAsync can return before the server's accept
        // runs, so an abort issued immediately afterwards could otherwise
        // race the accept and drop nothing.
        private readonly TaskCompletionSource _accepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MultiAcceptFakeServer()
        {
            (_listener, var port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
            Port = port;
            _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        }

        public int Port { get; }

        public string WsUrl => $"ws://127.0.0.1:{Port}";

        /// <summary>Completes once a connection has been accepted.</summary>
        public Task Accepted => _accepted.Task;

        /// <summary>Aborts the current connection the way a dead network
        /// would — the client's receive loop sees a WebSocketException. The
        /// response context is aborted as well: the accepted wrapper's own
        /// abort can leave the underlying keep-alive connection open until
        /// the listener reaps the context, which would make the drop
        /// invisible to the client (racy, not "dead network").</summary>
        public void DropCurrent()
        {
            _current?.Abort();
            try { _currentCtx?.Response.Abort(); } catch { /* already gone */ }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch
                {
                    break; // listener stopped or cancelled
                }

                _ = Task.Run(() => HandleAsync(ctx, ct), CancellationToken.None);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var ws = await ctx.AcceptWebSocketAsync(null);
            _current = ws.WebSocket;
            _currentCtx = ctx;
            _accepted.TrySetResult();
            var buffer = new byte[16384];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    }
                    catch
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    // These tests send no requests; idle-serve the connection.
                }
            }
            finally
            {
                try { ws.WebSocket.Dispose(); } catch { /* best effort */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best effort */ }
            _listener.Close();
            try { await _loop; } catch { /* cancellation */ }
            _cts.Dispose();
        }
    }
}
