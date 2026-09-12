using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tf.Core.Models;

namespace Tf.Deriv;

/// <summary>
/// Minimal Deriv API v3 WebSocket client.
///
/// Speaks directly to <c>wss://ws.derivws.com/websockets/v3?app_id=...</c>.
/// Requests are correlated by <c>req_id</c>; ticks and subscription updates
/// arrive as events. Reconnects automatically with exponential backoff,
/// re-authorizing and re-subscribing so the feed resumes by itself.
/// </summary>
public sealed class DerivClient : IAsyncDisposable
{
    private const string DefaultEndpoint = "wss://ws.derivws.com/websockets/v3";

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly object _sync = new();
    private readonly List<string> _subscriptions = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _connectCts;
    private Task? _receiveTask;
    private int _nextReqId = 1;
    private bool _disposed;
    private int _reconnectAttempt;
    private string? _authorizedToken;

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event Action<ConnectionStatus>? StatusChanged;

    /// <summary>Raised for every live tick on a subscribed symbol.</summary>
    public event Action<Tick>? TickReceived;

    /// <summary>Raised when a balance is reported (authorize or balance request).</summary>
    public event Action<AccountBalance>? BalanceUpdated;

    /// <summary>Raised for API-level errors that are not tied to a request.</summary>
    public event Action<string>? ErrorReceived;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// How often <see cref="WaitForSettlementAsync"/> re-queries an open
    /// contract. Production keeps the 2 s default; fake-server tests shorten
    /// it so each settled trade does not pay a flat poll sleep.
    /// </summary>
    public TimeSpan SettlementPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Application id used in the handshake (public market data needs no token).</summary>
    public string AppId { get; set; } = AppSettings.DefaultAppId;

    /// <summary>Symbol currently subscribed (null when not subscribed).</summary>
    public string? SubscribedSymbol { get; private set; }

    /// <summary>Account login id once authorized (null otherwise).</summary>
    public string? LoginId { get; private set; }

    /// <summary>Last reported account balance.</summary>
    public AccountBalance Balance { get; private set; } = AccountBalance.Empty;

    public bool IsConnected => Status == ConnectionStatus.Connected;

    // ─────────────────────────────────────────────────────────────
    //  Connection lifecycle
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the WebSocket endpoint and (optionally) authorizes.
    /// Safe to call repeatedly — reconnects are idempotent.
    /// </summary>
    public async Task ConnectAsync(string? apiToken = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (Status is ConnectionStatus.Connected or ConnectionStatus.Connecting)
            {
                if (apiToken is not null && LoginId is null)
                {
                    // Already connected but not authorized: authorize now.
                    _ = AuthorizeAsync(apiToken, ct);
                }
                return;
            }
        }

        SetStatus(ConnectionStatus.Connecting);
        _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = new CancellationTokenSource();
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        var uri = new UriBuilder(Endpoint) { Query = $"app_id={Uri.EscapeDataString(AppId)}" }.Uri;
        await socket.ConnectAsync(uri, _connectCts.Token).ConfigureAwait(false);

        lock (_sync)
        {
            _socket = socket;
        }

        _reconnectAttempt = 0;
        SetStatus(ConnectionStatus.Connected);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        if (apiToken is not null)
        {
            await AuthorizeAsync(apiToken, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Authorizes the session and captures the account balance.</summary>
    public async Task<AccountBalance> AuthorizeAsync(string apiToken, CancellationToken ct = default)
    {
        var response = await SendAsync(new { authorize = apiToken }, ct).ConfigureAwait(false);

        var balance = response.RootElement.GetProperty("authorize");
        var result = ParseBalance(balance);
        LoginId = result.LoginId;
        Balance = result;
        _authorizedToken = apiToken;
        BalanceUpdated?.Invoke(result);
        return result;
    }

    /// <summary>Closes the socket and stops reconnecting.</summary>
    public async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        ClientWebSocket? socket;
        lock (_sync)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is not null)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
            socket.Dispose();
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
            _receiveTask = null;
        }

        // Cancel all pending request/response pairs so callers don't hang
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetCanceled();
        }
        _pending.Clear();

        LoginId = null;
        SubscribedSymbol = null;
        _subscriptions.Clear();
        SetStatus(ConnectionStatus.Disconnected);
    }

    // ─────────────────────────────────────────────────────────────
    //  API calls
    // ─────────────────────────────────────────────────────────────

    /// <summary>Requests the current account balance.</summary>
    public async Task<AccountBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(new { balance = 1 }, ct).ConfigureAwait(false);
        var result = ParseBalance(response.RootElement.GetProperty("balance"));
        Balance = result;
        BalanceUpdated?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Subscribes to live ticks for <paramref name="symbol"/> (e.g. frxEURUSD).
    /// Ticks are delivered on <see cref="TickReceived"/>. Re-subscription
    /// happens automatically after a reconnect.
    /// </summary>
    public async Task SubscribeTicksAsync(string symbol, CancellationToken ct = default)
    {
        EnsureConnected();
        SubscribedSymbol = symbol;
        lock (_sync)
        {
            if (!_subscriptions.Contains(symbol))
            {
                _subscriptions.Add(symbol);
            }
        }

        await SendAsync(new { ticks = symbol, subscribe = 1 }, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches recent tick history for chart backfill (no subscription).</summary>
    public async Task<IReadOnlyList<Tick>> GetTicksHistoryAsync(
        string symbol, int count = 200, CancellationToken ct = default)
    {
        EnsureConnected();
        var response = await SendAsync(new
        {
            ticks_history = symbol,
            adjust_start_time = 1,
            count,
            end = "latest",
            start = 1,
            style = "ticks"
        }, ct).ConfigureAwait(false);

        var history = response.RootElement.GetProperty("history");
        var prices = history.GetProperty("prices").EnumerateArray()
            .Select(p => p.GetDouble()).ToArray();
        var times = history.GetProperty("times").EnumerateArray()
            .Select(t => t.GetInt64()).ToArray();

        var ticks = new List<Tick>(prices.Length);
        for (var i = 0; i < prices.Length; i++)
        {
            var price = prices[i];
            ticks.Add(new Tick(symbol, price, price, price, times[i], 0));
        }

        return ticks;
    }

    // ─────────────────────────────────────────────────────────────
    //  Trading (milestone 2)
    // ─────────────────────────────────────────────────────────────

    /// <summary>Requests a binary-contract quote (proposal) for a symbol.</summary>
    public async Task<Proposal> GetProposalAsync(
        string symbol, Direction direction, decimal amount, string currency,
        int durationMinutes, CancellationToken ct = default)
    {
        EnsureConnected();
        using var response = await SendAsync(new
        {
            proposal = 1,
            amount,
            basis = "stake",
            contract_type = direction.ToContractType(),
            currency,
            duration = durationMinutes,
            duration_unit = "m",
            symbol
        }, ct).ConfigureAwait(false);

        var p = response.RootElement.GetProperty("proposal");
        var id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            throw new DerivApiException("NoProposalId", "Proposal response did not include an id.");
        }

        var spot = p.TryGetProperty("spot", out var spotEl) ? spotEl.GetDouble() : 0;
        var longcode = p.TryGetProperty("longcode", out var lcEl) ? lcEl.GetString() ?? "" : "";
        var payout = p.TryGetProperty("payout", out var poEl) ? poEl.GetDecimal() : 0m;
        return new Proposal(id, symbol, direction, amount, currency, durationMinutes, spot, longcode, payout);
    }

    /// <summary>Buys a proposal at (usually) its current spot price.</summary>
    public async Task<BuyResult> BuyAsync(string proposalId, double price, CancellationToken ct = default)
    {
        EnsureConnected();
        using var response = await SendAsync(new { buy = proposalId, price }, ct).ConfigureAwait(false);

        var b = response.RootElement.GetProperty("buy");
        var contractId = b.TryGetProperty("contract_id", out var cid) ? cid.GetString() ?? "" : "";
        var buyPrice = b.TryGetProperty("buy_price", out var bp) ? bp.GetDecimal() : (decimal)price;
        var balanceAfter = b.TryGetProperty("balance_after", out var ba) ? ba.GetDecimal() : 0m;
        var longcode = b.TryGetProperty("longcode", out var lc) ? lc.GetString() ?? "" : "";
        return new BuyResult(contractId, buyPrice, balanceAfter, longcode);
    }

    /// <summary>Fetches a single contract snapshot by id.</summary>
    public async Task<ContractInfo> GetContractAsync(string contractId, CancellationToken ct = default)
    {
        EnsureConnected();
        using var response = await SendAsync(
            new { proposal_open_contract = 1, contract_id = contractId }, ct).ConfigureAwait(false);
        return ParseContract(response.RootElement.GetProperty("proposal_open_contract"), contractId);
    }

    /// <summary>
    /// Polls a contract until it settles (is_sold). Timeout defaults to 15
    /// minutes — long enough for any supported contract duration.
    /// </summary>
    public async Task<ContractInfo> WaitForSettlementAsync(
        string contractId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(15));
        while (true)
        {
            var info = await GetContractAsync(contractId, ct).ConfigureAwait(false);
            if (info.IsSold)
            {
                return info;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Contract {contractId} did not settle within the timeout.");
            }

            await Task.Delay(SettlementPollInterval, ct).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Internals
    // ─────────────────────────────────────────────────────────────

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }
    }

    private async Task<JsonDocument> SendAsync(object payload, CancellationToken ct)
    {
        var reqId = Interlocked.Increment(ref _nextReqId);

        // Merge the payload properties with req_id at the TOP level — the
        // Deriv protocol does not accept a nested payload object.
        using var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = new JsonObject();
        foreach (var prop in payloadDoc.RootElement.EnumerateObject())
        {
            root[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        root["req_id"] = reqId;
        var json = root.ToJsonString();

        var tcs = new TaskCompletionSource<JsonDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;

        try
        {
            await SendRawAsync(json, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _pending.TryRemove(reqId, out _);
            throw;
        }
    }

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        ClientWebSocket? socket;
        lock (_sync)
        {
            socket = _socket;
        }

        if (socket is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            var buffer = new byte[65536];
            var message = new List<byte>();

            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? socket;
                lock (_sync)
                {
                    socket = _socket;
                }

                if (socket is null)
                {
                    break;
                }

                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.AddRange(buffer.AsSpan(0, result.Count).ToArray());

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.ToArray());
                message.Clear();

                try
                {
                    Dispatch(json);
                }
                catch (Exception ex)
                {
                    ErrorReceived?.Invoke($"Message dispatch failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke($"Receive loop error: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                ScheduleReconnect();
            }
        }
    }

    private void Dispatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("req_id", out var reqIdProp) && reqIdProp.TryGetInt32(out var reqId))
        {
            if (_pending.TryRemove(reqId, out var tcs))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
                    var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                    tcs.SetException(new DerivApiException(code ?? "Unknown", msg ?? "Unknown error"));
                }
                else
                {
                    // doc is disposed when this method returns, so hand the
                    // caller its own document.
                    tcs.SetResult(JsonDocument.Parse(json));
                }
            }
            return;
        }

        // Subscription / streamed messages carry no req_id.
        if (root.TryGetProperty("msg_type", out var msgType))
        {
            switch (msgType.GetString())
            {
                case "tick" when root.TryGetProperty("tick", out var tickEl):
                    EmitTick(tickEl);
                    break;
                case "tick" when root.TryGetProperty("subscription", out _):
                    // subscription confirmation with no payload
                    break;
                case "error":
                    EmitError(root.GetProperty("error"));
                    break;
            }
        }
        else if (root.TryGetProperty("error", out var streamError))
        {
            EmitError(streamError);
        }
    }

    private void EmitTick(JsonElement tickEl)
    {
        var symbol = tickEl.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : SubscribedSymbol ?? "";
        var quote = tickEl.GetProperty("quote").GetDouble();
        var ask = tickEl.TryGetProperty("ask", out var a) ? a.GetDouble() : quote;
        var bid = tickEl.TryGetProperty("bid", out var b) ? b.GetDouble() : quote;
        var epoch = tickEl.GetProperty("epoch").GetInt64();
        var pipSize = tickEl.TryGetProperty("pip_size", out var p) ? p.GetInt32() : 0;

        TickReceived?.Invoke(new Tick(symbol, quote, ask, bid, epoch, pipSize));
    }

    private void EmitError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
        var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
        ErrorReceived?.Invoke($"[{code}] {msg}");
    }

    private static ContractInfo ParseContract(JsonElement c, string fallbackId)
    {
        var contractId = c.TryGetProperty("contract_id", out var ci) ? ci.GetString() ?? fallbackId : fallbackId;
        var status = c.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var isSold = c.TryGetProperty("is_sold", out var sold) && sold.GetBoolean();
        var entrySpot = c.TryGetProperty("entry_spot", out var es) ? es.GetDouble() : 0;
        var exitSpot = c.TryGetProperty("exit_spot", out var xs) ? xs.GetDouble() : 0;
        var entryTime = c.TryGetProperty("entry_tick_time", out var et) ? et.GetInt64() : 0;
        var exitTime = c.TryGetProperty("exit_tick_time", out var xt) ? xt.GetInt64() : 0;
        var buyPrice = c.TryGetProperty("buy_price", out var bp) ? bp.GetDecimal() : 0m;
        var profit = c.TryGetProperty("profit", out var pf) ? pf.GetDecimal() : 0m;
        var currency = c.TryGetProperty("currency", out var cu) ? cu.GetString() ?? "USD" : "USD";
        return new ContractInfo(contractId, MapStatus(status), entrySpot, exitSpot,
            entryTime, exitTime, buyPrice, profit, currency, isSold);
    }

    private static ContractStatus MapStatus(string status) => status switch
    {
        "open" => ContractStatus.Open,
        "won" => ContractStatus.Won,
        "lost" => ContractStatus.Lost,
        "sold" => ContractStatus.Sold,
        "cancelled" => ContractStatus.Cancelled,
        _ => ContractStatus.Unknown
    };

    private static AccountBalance ParseBalance(JsonElement balance)
    {
        var value = balance.TryGetProperty("balance", out var b) ? b.GetDecimal() : 0m;
        var currency = balance.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD";
        var loginId = balance.TryGetProperty("loginid", out var l) ? l.GetString() ?? "" : "";
        return new AccountBalance(value, currency, loginId);
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void ScheduleReconnect()
    {
        if (_disposed)
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _reconnectAttempt);
        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
        SetStatus(ConnectionStatus.Reconnecting);
        ErrorReceived?.Invoke($"Connection lost — reconnecting in {delay.TotalSeconds:0}s (attempt {attempt})");

        var ct = _connectCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (_disposed || ct.IsCancellationRequested)
                {
                    return;
                }

                await ConnectCoreAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"Reconnect failed: {ex.Message}");
                ScheduleReconnect();
            }
        });
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Refresh the socket handle (the old one is dead).
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        SetStatus(ConnectionStatus.Connecting);
        var uri = new UriBuilder(Endpoint) { Query = $"app_id={Uri.EscapeDataString(AppId)}" }.Uri;
        await socket.ConnectAsync(uri, ct).ConfigureAwait(false);

        lock (_sync)
        {
            _socket = socket;
        }

        _reconnectAttempt = 0;
        SetStatus(ConnectionStatus.Connected);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts!.Token));

        // Resume the previous session: re-authorize, then re-subscribe.
        if (_authorizedToken is not null)
        {
            await AuthorizeAsync(_authorizedToken, ct).ConfigureAwait(false);
        }

        string[] symbols;
        lock (_sync)
        {
            symbols = _subscriptions.ToArray();
        }

        foreach (var symbol in symbols)
        {
            await SendAsync(new { ticks = symbol, subscribe = 1 }, ct).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);

        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetCanceled();
        }

        _pending.Clear();
    }
}