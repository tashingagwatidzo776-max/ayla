using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DongGfx.App.Services;

/// <summary>One tradable symbol with a live quote (bridge /symbols).</summary>
/// <summary>One tradable symbol from the bridge. The volume/contract
/// fields are the venue's sizing ground truth (e.g. XAUUSDmicro:
/// contract_size=1, volume step 0.1 — not the standard-gold contract).
/// Defaults keep older sidecars parseable; contract_size=0 means unknown.
/// </summary>
public sealed record Mt5Symbol(
    string Symbol, string Description, double? Bid, double? Ask,
    int SpreadPoints, int Digits, int TradeMode,
    double VolumeMin = 0, double VolumeStep = 0, double VolumeMax = 0,
    double ContractSize = 0);

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

/// <summary>One closed/open deal from the bridge history (/deals).</summary>
public sealed record Mt5Deal(
    long Ticket, long Order, string Symbol, string Side, double Volume,
    double Price, double Profit, double Commission, double Swap, long Time);

/// <summary>Snapshot of the account behind the bridge.</summary>
public sealed record Mt5Account(
    long Login, string Server, string Currency,
    double Balance, double Equity, double MarginFree, int Leverage,
    int? TradeMode = null)
{
    /// <summary>The venue's own demo/real verdict from
    /// <c>account_info().trade_mode</c>: true = verified virtual (demo),
    /// false = verified real, null = absent or unknown value (old sidecar /
    /// contest mode) — the real-money gate fails closed on null.</summary>
    public bool? TradeModeVerifiedVirtual => TradeMode switch
    {
        0 => true,   // ACCOUNT_TRADE_MODE_DEMO
        2 => false,  // ACCOUNT_TRADE_MODE_REAL
        _ => null,   // 1 = contest, or field missing — refuse to verify
    };

    /// <summary>The demo/real verdict the real-money gate consumes: the
    /// venue's <c>trade_mode</c> when the sidecar publishes it, else the
    /// server-name/login heuristic — which may verify an account as demo
    /// but NEVER as real. A real account without a venue verdict stays
    /// unverified, so the gate fails closed.</summary>
    public bool? GateVerifiedVirtual =>
        TradeMode is not null
            ? TradeModeVerifiedVirtual
            : Server.Contains("demo", StringComparison.OrdinalIgnoreCase)
              || Login > 500_000_000
                ? true
                : null;
}

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
            r.GetProperty("leverage").GetInt32(),
            // Optional field: absent on an older sidecar → null (fails closed).
            r.TryGetProperty("trade_mode", out var tm) && tm.ValueKind == JsonValueKind.Number
                ? tm.GetInt32()
                : null);
    }

    /// <summary>Live bid/ask for a symbol, or null when unavailable.</summary>
    public async Task<IReadOnlyList<Mt5Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJson("symbols", ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("symbols", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Mt5Symbol>();
        }

        var list = new List<Mt5Symbol>();
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new Mt5Symbol(
                e.GetProperty("symbol").GetString() ?? "",
                e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                e.TryGetProperty("bid", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : null,
                e.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null,
                e.TryGetProperty("spread_points", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0,
                e.TryGetProperty("digits", out var dg) && dg.ValueKind == JsonValueKind.Number ? dg.GetInt32() : 5,
                e.TryGetProperty("trade_mode", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : 0,
                e.TryGetProperty("volume_min", out var vmin) && vmin.ValueKind == JsonValueKind.Number ? vmin.GetDouble() : 0,
                e.TryGetProperty("volume_step", out var vstep) && vstep.ValueKind == JsonValueKind.Number ? vstep.GetDouble() : 0,
                e.TryGetProperty("volume_max", out var vmx) && vmx.ValueKind == JsonValueKind.Number ? vmx.GetDouble() : 0,
                e.TryGetProperty("contract_size", out var csz) && csz.ValueKind == JsonValueKind.Number ? csz.GetDouble() : 0));
        }
        return list;
    }

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
        if (!resp.IsSuccessStatusCode)
        {
            // Refusals must surface as Mt5BridgeException even when the body
            // is malformed (e.g. an HTML error page from something squatting
            // on the port) — parse defensively, never let JSON plumbing
            // failures replace the refusal itself.
            throw new Mt5BridgeException(
                TryErrorText(raw) ?? (string.IsNullOrWhiteSpace(raw) ? "order refused" : raw));
        }

        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;

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
        if (!resp.IsSuccessStatusCode)
        {
            // Same defensive rule as PlaceOrderAsync: a malformed refusal
            // body still becomes Mt5BridgeException, never JsonException.
            throw new Mt5BridgeException(TryErrorText(raw) ?? "close refused");
        }

        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;

        return new Mt5OrderResult(
            r.GetProperty("ok").GetBoolean(), r.GetProperty("retcode").GetInt32(),
            r.GetProperty("retcode_name").GetString() ?? "", null, ticket, null, null, "");
    }

    /// <summary>Recent deal history (bridge /deals?days=N): the FX side's
    /// realised P/L, used to feed the performance tracker and journal.</summary>
    public async Task<IReadOnlyList<Mt5Deal>> GetDealsAsync(int days = 7, CancellationToken ct = default)
    {
        using var doc = await GetJson($"deals?days={days}", ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("deals", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Mt5Deal>();
        }

        var deals = new List<Mt5Deal>();
        foreach (var d in arr.EnumerateArray())
        {
            deals.Add(new Mt5Deal(
                d.GetProperty("ticket").GetInt64(),
                d.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0,
                d.GetProperty("symbol").GetString() ?? "",
                d.TryGetProperty("side", out var s) ? s.GetString() ?? "" : "",
                d.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0,
                d.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0,
                d.TryGetProperty("profit", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDouble() : 0,
                d.TryGetProperty("commission", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0,
                d.TryGetProperty("swap", out var sw) && sw.ValueKind == JsonValueKind.Number ? sw.GetDouble() : 0,
                d.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0));
        }

        return deals;
    }

    /// <summary>The sidecar's "error" text from a refusal body, or null
    /// when the body is not JSON, has no error key, or carries a non-string
    /// value. Never throws — a malformed refusal body must not replace the
    /// refusal itself as the exception.</summary>
    private static string? TryErrorText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var e) &&
                e.ValueKind == JsonValueKind.String)
            {
                return e.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON body: the caller decides what message fits.
        }
        return null;
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
