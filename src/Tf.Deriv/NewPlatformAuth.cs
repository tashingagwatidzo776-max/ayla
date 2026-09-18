using System.Net.Http;
using System.Text.Json;

namespace Tf.Deriv;

/// <summary>
/// Client for Deriv's new trading platform (developers.deriv.com "PAT"
/// apps). PATs do not work on the classic <c>authorize</c> WebSocket
/// endpoint; they authenticate REST calls with
/// <c>Authorization: Bearer</c> plus a <c>Deriv-App-ID</c> header, and open
/// trading sockets through a one-time-password URL that is valid for 120
/// seconds and single use — the socket arrives pre-authorized.
///
/// Live-verified 2026-09-18 against <c>api.derivws.com</c>: account
/// discovery via <c>GET /trading/v1/options/accounts</c>, OTP exchange via
/// <c>POST /trading/v1/options/accounts/{id}/otp</c>.
/// </summary>
public class NewPlatformAuth
{
    public const string DefaultBaseUrl = "https://api.derivws.com";

    private readonly HttpClient _http;

    public string BaseUrl { get; }
    public string AppId { get; }

    public NewPlatformAuth(string appId, string? baseUrl = null, HttpClient? http = null)
    {
        AppId = appId;
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = http ?? new HttpClient();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string bearerToken)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Add("Deriv-App-ID", AppId);
        req.Headers.Add("Authorization", "Bearer " + bearerToken);
        return req;
    }

    /// <summary>Lists the accounts the bearer token can access. Each entry
    /// carries the account id, its type ("demo"/"real" — the new platform's
    /// equivalent of the classic is_virtual flag), balance, currency and
    /// status.</summary>
    public virtual async Task<IReadOnlyList<NewPlatformAccount>> ListAccountsAsync(
        string bearerToken, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, "/trading/v1/options/accounts", bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if ((int)resp.StatusCode == 401)
        {
            throw new DerivApiException("Unauthorized",
                "The new platform rejected the token (check the PAT and the Deriv-App-ID).");
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new DerivApiException("AccountsListFailed",
                $"HTTP {(int)resp.StatusCode} listing accounts: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var accounts = new List<NewPlatformAccount>();
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            accounts.Add(new NewPlatformAccount(
                el.TryGetProperty("account_id", out var id) ? id.GetString() ?? "" : "",
                el.TryGetProperty("account_type", out var t) ? t.GetString() ?? "" : "",
                el.TryGetProperty("balance", out var b) && b.TryGetDecimal(out var bd) ? bd : 0m,
                el.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD",
                el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ""));
        }

        return accounts;
    }

    /// <summary>Exchanges the PAT for a one-time authenticated WebSocket URL
    /// for the given account. The URL embeds the OTP: connect immediately —
    /// it is valid for 120 seconds and single use, and the resulting socket
    /// is already authorized (no classic <c>authorize</c> call is sent or
    /// accepted).</summary>
    public virtual async Task<string> GetOtpWebSocketUrlAsync(
        string bearerToken, string accountId, CancellationToken ct = default)
    {
        using var req = Request(
            HttpMethod.Post,
            $"/trading/v1/options/accounts/{Uri.EscapeDataString(accountId)}/otp",
            bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode == 401 ? "Unauthorized" : "OtpFailed";
            throw new DerivApiException(code,
                $"HTTP {(int)resp.StatusCode} requesting OTP for {accountId}: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").GetProperty("url").GetString()
            ?? throw new DerivApiException("OtpFailed", "OTP response carried no url.");
    }

    private static string Trim(string body) =>
        body.Length <= 200 ? body : body[..200];
}

/// <summary>One account reported by the new platform's discovery endpoint.
/// <see cref="AccountType"/> is "demo" or "real" — the authoritative
/// demo/real verdict for this platform (no separate is_virtual field). A
/// missing/unknown type is treated as real by callers: fail closed.</summary>
public sealed record NewPlatformAccount(
    string AccountId,
    string AccountType,
    decimal Balance,
    string Currency,
    string Status)
{
    public bool IsDemoAccount =>
        string.Equals(AccountType, "demo", StringComparison.OrdinalIgnoreCase);
}
