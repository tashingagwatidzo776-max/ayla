using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DongGfx.App.Services;

/// <summary>One open MT5 position (bridge /positions).</summary>
public sealed record Mt5Position(
    long Ticket, string Symbol, string Side, double Volume,
    double PriceOpen, double PriceCurrent, double Profit);

/// <summary>One DOM level (bridge /book — empty on Deriv, kept for real books).</summary>
public sealed record Mt5BookLevel(string Side, double Price, double Volume);

/// <summary>One OHLC candle (bridge /candles).</summary>
public sealed record Mt5Candle(long Time, double Open, double High, double Low, double Close);

/// <summary>Result of an MT5 order sent through the bridge.</summary>
public sealed record Mt5OrderResult(
    bool Ok, int Retcode, string RetcodeName, long? Deal, long? Order,
    double? Price, double? Volume, string Comment);

/// <summary>Snapshot of the account behind the bridge.</summary>
public sealed record Mt5Account(
    long Login, string Server, string Currency,
    double Balance, double Equity, double MarginFree, int Leverage);

/// <summary>
/// Typed client for the loopback MT5 sidecar (bridge/mt5_sidecar.py).
/// Loopback URLs only — the sidecar is a local process, never a remote one.
/// Every method degrades to a clean failure so the terminal keeps working
/// when MT5 or the sidecar is off.
/// </summary>
public sealed class Mt5BridgeClient : IDisposable
{
    /// <summary>The loopback port the sidecar serves by default.</summary>
    public const int DefaultPort = 53190;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHandler;

    public Mt5BridgeClient(int port = DefaultPort)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(8) },
               new Uri($"http://127.0.0.1:{port}/"), ownsHandler: true)
    {
    }

    /// <summary>Test constructor: inject a handler (no network at all) and
    /// any base address. Production base addresses must be loopback.</summary>
    public Mt5BridgeClient(HttpMessageHandler handler, Uri baseAddress)
    {
        if (baseAddress.Host != "127.0.0.1" && baseAddress.Host != "localhost")
        {
            throw new ArgumentException(
                "the MT5 bridge is loopback-only: refusing " + baseAddress.Host);
        }

        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(8) };
        _ownsHandler = false;
    }

    private Mt5BridgeClient(HttpClient http, Uri baseAddress, bool ownsHandler)
    {
        _http = http;
        _http.BaseAddress = baseAddress;
        _ownsHandler = ownsHandler;
    }

    public bool IsLoopback => _http.BaseAddress?.Host is "127.0.0.1" or "localhost";

    /// <summary>Liveness + attached-account snapshot. Null = sidecar down.</summary>
    public async Task<(bool Ok, long? Login, string? Server)?> HealthAsync(CancellationToken ct = default)
    {
        using var doc = await GetJson("health", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        return (doc.RootElement.GetProperty("ok").GetBoolean(),
                doc.RootElement.TryGetProperty("login", out var login) ? login.GetInt64() : null,
                doc.RootElement.TryGetProperty("server", out var server) ? server.GetString() : null);
    }

    public async Task<Mt5Account?> GetAccountAsync(CancellationToken ct = default)
    {
        using var doc = await GetJson("account", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var r = doc.RootElement;
        return new Mt5Account(
            r.GetProperty("login").GetInt64(),
            r.GetProperty("server").GetString() ?? "",
            r.GetProperty("currency").GetString() ?? "",
            r.GetProperty("balance").GetDouble(),
            r.GetProperty("equity").GetDouble(),
            r.GetProperty("margin_free").GetDouble(),
            r.GetProperty("leverage").GetInt32());
    }

    /// <summary>Live bid/ask for a symbol, or null when unavailable.</summary>
    public async Task<(double Bid, double Ask, long Time)?> GetTickAsync(string symbol, CancellationToken ct = default)
    {
        using var doc = await GetJson($"ticks/{Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var r = doc.RootElement;
        if (r.TryGetProperty("bid", out var bid) && bid.ValueKind == JsonValueKind.Number)
        {
            return (bid.GetDouble(), r.GetProperty("ask").GetDouble(),
                    r.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
                        ? t.GetInt64() : 0);
        }

        return null;
    }

    /// <summary>DOM levels for a symbol (empty on Deriv — the caller builds a
    /// synthetic ladder from bid/ask instead).</summary>
    public async Task<IReadOnlyList<Mt5BookLevel>> GetBookAsync(string symbol, CancellationToken ct = default)
    {
        using var doc = await GetJson($"book/{Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return Array.Empty<Mt5BookLevel>();
        }

        var levels = new List<Mt5BookLevel>();
        foreach (var l in doc.RootElement.GetProperty("levels").EnumerateArray())
        {
            levels.Add(new Mt5BookLevel(
                l.GetProperty("side").GetString() ?? "other",
                l.GetProperty("price").GetDouble(),
                l.GetProperty("volume").GetDouble()));
        }

        return levels;
    }

    public async Task<IReadOnlyList<Mt5Candle>> GetCandlesAsync(string symbol, string tf = "M1", int n = 120, CancellationToken ct = default)
    {
        using var doc = await GetJson(
            $"candles/{Uri.EscapeDataString(symbol)}?tf={Uri.EscapeDataString(tf)}&n={n}", ct)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Array.Empty<Mt5Candle>();
        }

        var candles = new List<Mt5Candle>();
        foreach (var c in doc.RootElement.GetProperty("candles").EnumerateArray())
        {
            candles.Add(new Mt5Candle(
                c.GetProperty("time").GetInt64(),
                c.GetProperty("open").GetDouble(),
                c.GetProperty("high").GetDouble(),
                c.GetProperty("low").GetDouble(),
                c.GetProperty("close").GetDouble()));
        }

        return candles;
    }

    /// <summary>Places an order through the bridge. Throws Mt5BridgeException
    /// with the retcode when the terminal refuses (validation errors reach
    /// the caller as Mt5BridgeException too, so the UI shows one message).</summary>
    public async Task<Mt5OrderResult> PlaceOrderAsync(
        string symbol, string action, string type, double lots,
        double? price = null, double? stopPrice = null, double? sl = null, double? tp = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["action"] = action,
            ["type"] = type,
            ["lots"] = lots,
        };
        if (price.HasValue) { body["price"] = price.Value; }
        if (stopPrice.HasValue) { body["stopprice"] = stopPrice.Value; }
        if (sl.HasValue) { body["sl"] = sl.Value; }
        if (tp.HasValue) { body["tp"] = tp.Value; }

        using var content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("order", content, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;
        if (!resp.IsSuccessStatusCode)
        {
            var error = r.TryGetProperty("error", out var e) ? e.GetString() : raw;
            throw new Mt5BridgeException(error ?? "order refused");
        }

        return new Mt5OrderResult(
            r.GetProperty("ok").GetBoolean(),
            r.GetProperty("retcode").GetInt32(),
            r.GetProperty("retcode_name").GetString() ?? "",
            r.TryGetProperty("deal", out var deal) && deal.ValueKind == JsonValueKind.Number ? deal.GetInt64() : null,
            r.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Number ? order.GetInt64() : null,
            r.TryGetProperty("price", out var price2) && price2.ValueKind == JsonValueKind.Number ? price2.GetDouble() : null,
            r.TryGetProperty("volume", out var vol) && vol.ValueKind == JsonValueKind.Number ? vol.GetDouble() : null,
            r.TryGetProperty("comment", out var com) ? com.GetString() ?? "" : "");
    }

    public async Task<IReadOnlyList<Mt5Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJson("positions", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return Array.Empty<Mt5Position>();
        }

        var positions = new List<Mt5Position>();
        foreach (var p in doc.RootElement.GetProperty("positions").EnumerateArray())
        {
            positions.Add(new Mt5Position(
                p.GetProperty("ticket").GetInt64(),
                p.GetProperty("symbol").GetString() ?? "",
                p.GetProperty("side").GetString() ?? "",
                p.GetProperty("volume").GetDouble(),
                p.GetProperty("price_open").GetDouble(),
                p.GetProperty("price_current").GetDouble(),
                p.GetProperty("profit").GetDouble()));
        }

        return positions;
    }

    public async Task<Mt5OrderResult> ClosePositionAsync(long ticket, CancellationToken ct = default)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"close/{ticket}", content, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;
        if (!resp.IsSuccessStatusCode)
        {
            throw new Mt5BridgeException(
                r.TryGetProperty("error", out var e) ? e.GetString() ?? "close refused" : "close refused");
        }

        return new Mt5OrderResult(
            r.GetProperty("ok").GetBoolean(), r.GetProperty("retcode").GetInt32(),
            r.GetProperty("retcode_name").GetString() ?? "", null, ticket, null, null, "");
    }

    private async Task<JsonDocument?> GetJson(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(raw);
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            // Sidecar down / timeout / connection refused → the caller's
            // "bridge unavailable" path. Never let this crash the UI.
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHandler)
        {
            _http.Dispose();
        }
    }
}

/// <summary>The bridge or the terminal refused a request.</summary>
public sealed class Mt5BridgeException : Exception
{
    public Mt5BridgeException(string message) : base(message) { }
}
