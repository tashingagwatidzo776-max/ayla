using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DongGfx.Core.Models;

namespace DongGfx.Deriv;

/// <summary>One entry of the public active-symbols listing.</summary>
/// <param name="Symbol">Deriv symbol code (e.g. "R_100").</param>
/// <param name="DisplayName">Human name when the feed carries one.</param>
/// <param name="ExchangeIsOpen">The feed's open verdict for the symbol's
/// exchange — null when the feed did not say (tolerant parse).</param>
public sealed record PublicSymbolInfo(string Symbol, string? DisplayName, bool? ExchangeIsOpen);

/// <summary>
/// No-auth market-data feed on Deriv's public WebSocket
/// (<c>wss://api.derivws.com/trading/v1/options/ws/public</c>): active
/// symbols, their open/closed status, and live ticks — none of it needs a
/// token. Monitoring that rides this feed (market-closed probes, tick
/// watchers) therefore survives access-token expiry and re-auth churn by
/// construction: there is nothing to re-authenticate.
///
/// Transport conventions mirror <see cref="DerivClient"/> (req_id
/// correlation, 30 s keepalive ping, supervised reconnect with capped
/// backoff) but the client is strictly read-only market data: it never
/// authorizes, never trades, and has no session state to invalidate.
/// </summary>
public sealed class PublicMarketDataClient : IAsyncDisposable
{
    public const string DefaultEndpoint = "wss://api.derivws.com/trading/v1/options/ws/public";

    private const int PingIntervalSeconds = 30;

    private readonly string _endpoint;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly HashSet<string> _subscriptions = new();
    private readonly object _sync = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _pingTask;
    private int _nextReqId = 1;
    private int _reconnectAttempt;
    private bool _disposed;
    private volatile bool _userClosed;

    /// <summary>Raised for every live tick on a subscribed symbol.</summary>
    public event Action<Tick>? TickReceived;

    /// <summary>Raised whenever the transport state changes (text label).</summary>
    public event Action<string>? StatusChanged;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public PublicMarketDataClient(string? endpoint = null)
    {
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    // ── Transport ────────────────────────────────────────────────────

    /// <summary>Opens the public socket and starts the supervised receive
    /// loop. Safe to call when already connected (no-op) — probes call it
    /// via <see cref="EnsureConnectedAsync"/> on demand.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PublicMarketDataClient));
        }

        if (IsConnected)
        {
            return;
        }

        _userClosed = false;
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_endpoint), ct).ConfigureAwait(false);
        _socket = socket;
        _reconnectAttempt = 0;
        StatusChanged?.Invoke("Connected (public, no auth)");

        _cts ??= new CancellationTokenSource();
        _receiveTask ??= Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _pingTask ??= Task.Run(() => PingLoopAsync(_cts.Token));
    }

    /// <summary>Closes the socket and stops the supervised loops. The
    /// client can be reconnected afterwards.</summary>
    public async Task DisconnectAsync()
    {
        _userClosed = true;
        _reconnectAttempt = 0;
        await CloseSocketAsync().ConfigureAwait(false);
        StatusChanged?.Invoke("Not connected (public)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _userClosed = true;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // Cancel is best-effort during teardown.
        }

        await CloseSocketAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task CloseSocketAsync()
    {
        var socket = _socket;
        _socket = null;
        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client close", timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // A dying socket is disposed either way; nothing to salvage.
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>Connects when necessary. The public feed authenticates
    /// nothing, so a fresh socket is always a valid session.</summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!IsConnected)
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Requests ─────────────────────────────────────────────────────

    /// <summary>Lists the feed's active symbols with their open verdicts.
    /// Tolerant parse: the response shape on the public endpoint is
    /// <c>{"active_symbols":[…]}</c> (possibly nested under "data").</summary>
    public async Task<IReadOnlyList<PublicSymbolInfo>> GetActiveSymbolsAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        using var resp = await SendForResponseAsync(
            new JsonObject { ["active_symbols"] = "brief" }, ct).ConfigureAwait(false);
        return ParseActiveSymbols(resp.RootElement);
    }

    /// <summary>Asks the public feed whether one symbol's exchange is
    /// currently open — the market-closed monitor's probe. Unknown symbol
    /// or missing verdict → false (stay idle; the engine's own proposal
    /// error remains the authority for resumption).</summary>
    public async Task<bool> IsSymbolOpenAsync(string symbol, CancellationToken ct = default)
    {
        var symbols = await GetActiveSymbolsAsync(ct).ConfigureAwait(false);
        return symbols.FirstOrDefault(s =>
                string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            ?.ExchangeIsOpen == true;
    }

    /// <summary>Subscribes to the public tick stream for a symbol; ticks
    /// arrive on <see cref="TickReceived"/>. Remembered so a supervised
    /// reconnect re-subscribes automatically.</summary>
    public async Task SubscribeTicksAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            _subscriptions.Add(symbol);
        }

        await SendForResponseAsync(
            new JsonObject { ["ticks"] = symbol, ["subscribe"] = 1 }, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendForResponseAsync(JsonObject payload, CancellationToken ct)
    {
        var socket = _socket
            ?? throw new InvalidOperationException("The public socket is not connected.");

        int reqId;
        lock (_sync)
        {
            reqId = _nextReqId++;
        }

        payload["req_id"] = reqId;
        var json = payload.ToJsonString();

        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct))
                .ConfigureAwait(false);
            if (done != tcs.Task)
            {
                throw new TimeoutException($"The public feed did not answer req {reqId} in 15 s.");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(reqId, out _);
        }
    }

    // ── Loops ────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && !_disposed)
        {
            ClientWebSocket? socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                await ReconnectAfterDropAsync(ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new WebSocketException("The public feed closed the socket.");
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                if (_userClosed || _disposed)
                {
                    return;
                }

                StatusChanged?.Invoke("Public feed dropped — reconnecting…");
                await CloseSocketAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReconnectAfterDropAsync(CancellationToken ct)
    {
        if (_userClosed || _disposed)
        {
            return;
        }

        // Capped exponential backoff — the feed is a convenience, never let
        // its outage turn into a hot loop.
        int attempt = Interlocked.Increment(ref _reconnectAttempt);
        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            await ConnectAsync(ct).ConfigureAwait(false);

            string[] subs;
            lock (_sync)
            {
                subs = _subscriptions.ToArray();
            }

            foreach (var symbol in subs)
            {
                try
                {
                    await SendForResponseAsync(
                        new JsonObject { ["ticks"] = symbol, ["subscribe"] = 1 }, ct).ConfigureAwait(false);
                }
                catch
                {
                    // One failed re-subscription must not kill the others;
                    // the next drop retries the whole set.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Backoff loop handles the next attempt.
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds), ct).ConfigureAwait(false);
                if (IsConnected)
                {
                    await SendForResponseAsync(new JsonObject { ["ping"] = 1 }, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // The receive loop's supervised reconnect owns transport
                // recovery; a failed ping is just a skipped beat.
            }
        }
    }

    private void HandleMessage(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return; // Not JSON — ignore; the feed may send keepalive noise.
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("req_id", out var reqIdProp) && reqIdProp.TryGetInt32(out var reqId)
                && _pending.TryRemove(reqId, out var pending))
            {
                pending.TrySetResult(JsonDocument.Parse(json));
                return;
            }

            // Unrequested messages: ticks (tolerant shape — nested "tick"
            // object or flat fields).
            var tick = ParseTick(root);
            if (tick is not null)
            {
                TickReceived?.Invoke(tick);
            }
        }
    }

    // ── Pure parsing (unit-tested directly, no transport) ───────────

    /// <summary>Tolerant active-symbols parse: array under "active_symbols"
    /// (possibly inside "data"), entries as objects with "symbol"; the open
    /// verdict may be a number (1/0) or a boolean.</summary>
    internal static IReadOnlyList<PublicSymbolInfo> ParseActiveSymbols(JsonElement root)
    {
        JsonElement array = default;
        bool found = false;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("active_symbols", out var inner)
                && inner.ValueKind == JsonValueKind.Array)
            {
                array = inner;
                found = true;
            }
            else if (root.TryGetProperty("active_symbols", out var direct)
                     && direct.ValueKind == JsonValueKind.Array)
            {
                array = direct;
                found = true;
            }
        }

        if (!found)
        {
            return Array.Empty<PublicSymbolInfo>();
        }

        var list = new List<PublicSymbolInfo>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("symbol", out var sym) || sym.GetString() is not { Length: > 0 } symbol)
            {
                continue;
            }

            list.Add(new PublicSymbolInfo(
                symbol,
                el.TryGetProperty("display_name", out var dn) ? dn.GetString() : null,
                ParseOpenFlag(el)));
        }

        return list;
    }

    private static bool? ParseOpenFlag(JsonElement el)
    {
        if (el.TryGetProperty("exchange_is_open", out var open))
        {
            if (open.ValueKind == JsonValueKind.Number && open.TryGetInt32(out var n))
            {
                return n != 0;
            }

            if (open.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (open.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>Tolerant tick parse: {"tick":{"symbol","quote","epoch"}} or
    /// the same fields flat. Epoch is Deriv's epoch-SECONDS on the classic
    /// feed and epoch-ms on the new one — disambiguated by magnitude.
    /// Anything else → null.</summary>
    internal static Tick? ParseTick(JsonElement root)
    {
        var src = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tick", out var nested)
            && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;

        if (src.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? symbol = src.TryGetProperty("symbol", out var s) ? s.GetString() : null;
        double quote = 0;
        if (src.TryGetProperty("quote", out var q))
        {
            if (q.ValueKind == JsonValueKind.Number && q.TryGetDouble(out var d))
            {
                quote = d;
            }
            else if (q.ValueKind == JsonValueKind.String
                     && double.TryParse(q.GetString(),
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var ds))
            {
                quote = ds;
            }
        }

        long epoch = src.TryGetProperty("epoch", out var e) && e.TryGetInt64(out var ep) ? ep : 0;
        // Feed carries epoch-seconds; Tick wants epoch-ms. A seconds value
        // (≈1.7e9) is three orders below a ms value (≈1.7e12).
        long epochMs = epoch > 0 && epoch < 100_000_000_000L ? epoch * 1000 : epoch;
        if (string.IsNullOrEmpty(symbol) || quote <= 0)
        {
            return null;
        }

        return new Tick(symbol, quote, quote, quote, epochMs,
            src.TryGetProperty("pip_size", out var p) && p.TryGetInt32(out var pip) ? pip : 0);
    }
}
