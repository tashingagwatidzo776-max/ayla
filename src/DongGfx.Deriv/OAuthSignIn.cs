using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DongGfx.Deriv;

/// <summary>Tokens returned by Deriv's OAuth 2.0 token endpoint.</summary>
/// <param name="AccessToken">Bearer token ("ory_at_…") for REST and the OTP
/// flow — used exactly like a PAT in <see cref="NewPlatformAuth"/>.</param>
/// <param name="ExpiresInSeconds">Lifetime of the access token (docs: 3600).
/// Short-lived by design — callers must be ready to re-sign-in.</param>
/// <param name="RefreshToken">Present only when the server issues one; the
/// docs mark refresh as optional, so never assume it.</param>
public sealed record OAuthResult(
    string AccessToken,
    int ExpiresInSeconds,
    string? RefreshToken)
{
    public string TokenType => "Bearer";
}

/// <summary>
/// Deriv OAuth 2.0 sign-in (Authorization Code flow with PKCE) for desktop:
/// opens the system browser at Deriv's consent page, captures the redirect
/// on a loopback HTTP listener, and exchanges the authorization code for a
/// short-lived Bearer token. The user's Deriv password never touches this
/// app, and the result feeds the existing new-platform import/connect path.
///
/// Requirements (per developers.deriv.com): an OAuth-type app registration
/// (client_id) and a pre-registered redirect_uri. This service defaults the
/// redirect to <c>http://localhost:{port}/callback</c> — a local loopback
/// capture is the standard native-app pattern, but Deriv's docs advertise
/// HTTPS redirects only, so the registered callback MUST match this shape
/// exactly or Deriv rejects the request with redirect_uri mismatch.
///
/// State is verified on every callback (CSRF) and the code verifier is kept
/// in memory only. The authorization code is single-use and exchanged
/// immediately; it is never logged or stored.
/// </summary>
public class OAuthSignIn
{
    public const string DefaultAuthBaseUrl = "https://auth.deriv.com";

    private readonly HttpClient _http;

    public string AuthBaseUrl { get; }

    public OAuthSignIn(string? authBaseUrl = null, HttpClient? http = null)
    {
        AuthBaseUrl = (authBaseUrl ?? DefaultAuthBaseUrl).TrimEnd('/');
        _http = http ?? new HttpClient();
    }

    // ── PKCE (RFC 7636, method S256) ────────────────────────────────────

    /// <summary>Cryptographically random code_verifier (43–128 chars,
    /// base64url alphabet). 32 random bytes → 43 chars — the minimum.</summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    /// <summary>code_challenge = BASE64URL(SHA256(code_verifier)).
    /// Verified against the RFC 7636 appendix-B test vector in tests.</summary>
    public static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url(hash);
    }

    /// <summary>Random state for CSRF protection — fresh for every request.</summary>
    public static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Authorize URL ───────────────────────────────────────────────────

    /// <summary>Builds the authorization URL to open in the browser.
    /// Scope is space-separated per the docs ("trade account_manage" in the
    /// examples); it is percent-encoded as %20 here. Pass
    /// <paramref name="prompt"/> = "registration" for the sign-up variant.</summary>
    public string BuildAuthorizeUrl(
        string clientId,
        string redirectUri,
        string scope = "trade",
        string? state = null,
        string? codeChallenge = null,
        string? prompt = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("OAuth client_id is required (register an OAuth app at developers.deriv.com).", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            throw new ArgumentException("A redirect_uri registered with Deriv is required.", nameof(redirectUri));
        }

        var challenge = codeChallenge ?? GenerateCodeChallenge(GenerateCodeVerifier());
        var st = state ?? GenerateState();

        var sb = new StringBuilder(AuthBaseUrl)
            .Append("/oauth2/auth")
            .Append("?response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&scope=").Append(Uri.EscapeDataString(scope))
            .Append("&state=").Append(Uri.EscapeDataString(st))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&code_challenge_method=S256");

        if (!string.IsNullOrEmpty(prompt))
        {
            sb.Append("&prompt=").Append(Uri.EscapeDataString(prompt));
        }

        return sb.ToString();
    }

    // ── Token exchange ──────────────────────────────────────────────────

    /// <summary>Exchanges the authorization code for tokens via the token
    /// endpoint (form-encoded POST, per docs). Call this immediately after
    /// the callback — the code is single-use and expires quickly.</summary>
    public virtual async Task<OAuthResult> ExchangeCodeAsync(
        string code,
        string clientId,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        });

        using var resp = await _http.PostAsync(AuthBaseUrl + "/oauth2/token", form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new DerivApiException(
                (int)resp.StatusCode == 400 ? "InvalidGrant" : "TokenExchangeFailed",
                $"OAuth token exchange failed: HTTP {(int)resp.StatusCode} — {Trim(body)} " +
                "(code already used/expired, or the code_verifier doesn't match the challenge).");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new DerivApiException("TokenExchangeFailed", "Token response carried no access_token.");
        }

        int expires = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e) ? e : 3600;
        string? refresh = root.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : null;

        return new OAuthResult(access, expires, refresh);
    }

    // ── Refresh grant ───────────────────────────────────────────────────

    /// <summary>Exchanges a refresh token for a fresh access token
    /// (grant_type=refresh_token, per RFC 6749 §6). Deriv issues the
    /// refresh token alongside the access token at sign-in; the access
    /// token expires (~3600s) but the refresh token lives far longer, so
    /// long sessions renew instead of re-running the browser consent.
    ///
    /// The server MAY rotate the refresh token on every use — when the
    /// response carries a new one it replaces the old, when it omits one
    /// the incoming token stays valid (the returned
    /// <see cref="OAuthResult.RefreshToken"/> is always the one to store).
    /// A revoked/expired refresh token fails with HTTP 400/401 →
    /// <see cref="DerivApiException"/> code "InvalidGrant": the only
    /// remedy is a fresh browser sign-in.</summary>
    public virtual async Task<OAuthResult> RefreshAsync(
        string refreshToken,
        string clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("A refresh token is required.", nameof(refreshToken));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("OAuth client_id is required.", nameof(clientId));
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });

        using var resp = await _http.PostAsync(AuthBaseUrl + "/oauth2/token", form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new DerivApiException(
                (int)resp.StatusCode == 400 || (int)resp.StatusCode == 401
                    ? "InvalidGrant" : "RefreshFailed",
                $"OAuth refresh failed: HTTP {(int)resp.StatusCode} — {Trim(body)} " +
                "(the refresh token was revoked or expired — a fresh browser sign-in is required).");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new DerivApiException("RefreshFailed", "Refresh response carried no access_token.");
        }

        int expires = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e) ? e : 3600;

        // Rotation-tolerant: keep the incoming refresh token when the server
        // does not issue a new one.
        string refresh = root.TryGetProperty("refresh_token", out var rf) &&
                         !string.IsNullOrEmpty(rf.GetString())
            ? rf.GetString()!
            : refreshToken;

        return new OAuthResult(access, expires, refresh);
    }

    // ── Full desktop sign-in ────────────────────────────────────────────

    /// <summary>The localhost redirect the app captures. Deriv requires the
    /// redirect_uri to be registered EXACTLY — expose this to the user so
    /// they register the right string (docs ask for HTTPS; loopback http is
    /// the native-app convention and must be validated with Deriv first).</summary>
    public static string BuildLoopbackRedirectUri(int port) => $"http://localhost:{port}/callback";

    /// <summary>Picks a free loopback port for the one-shot listener.</summary>
    public static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Runs the whole desktop sign-in: starts the loopback capture,
    /// opens the browser at Deriv's consent page, waits for the redirect,
    /// verifies state, exchanges the code, then shuts the listener down.
    /// <paramref name="redirectUri"/> must be the exact string registered
    /// with Deriv; when null, a free-port loopback redirect is used (the
    /// user must have registered that shape — see the doc caveat).</summary>
    public virtual async Task<OAuthResult> SignInAsync(
        string clientId,
        string? redirectUri = null,
        int loopbackPort = 0,
        Action<string>? onWaiting = null,
        CancellationToken ct = default)
    {
        bool loopback = string.IsNullOrWhiteSpace(redirectUri);
        int port = loopback
            ? (loopbackPort > 0 ? loopbackPort : FindFreeLoopbackPort())
            : 0;
        redirectUri ??= BuildLoopbackRedirectUri(port);

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = GenerateState();
        var authUrl = BuildAuthorizeUrl(clientId, redirectUri, state: state, codeChallenge: challenge);

        HttpListener? listener = null;
        try
        {
            if (loopback)
            {
                listener = new HttpListener();
                // Root prefix of the loopback port: matches /callback?code=…
                // (a /callback prefix would need a trailing slash and would
                // NOT match the bare path the browser redirects to). Explicit
                // 'localhost' hosts need no admin URLACL, unlike '+' wildcards.
                // Non-callback paths are rejected with 404 in the wait loop.
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
            }

            OpenBrowser(authUrl);
            onWaiting?.Invoke("Complete the sign-in in your browser — waiting for Deriv to redirect…");

            (string code, string returnedState) = loopback
                ? await WaitForCallbackAsync(listener!, ct).ConfigureAwait(false)
                : throw new InvalidOperationException(
                    "Only the loopback redirect can be captured by the app. Register it with Deriv or use the built-in localhost redirect.");

            if (!string.Equals(returnedState, state, StringComparison.Ordinal))
            {
                throw new DerivApiException("StateMismatch",
                    "OAuth state mismatch on the callback — possible CSRF; sign-in aborted.");
            }

            return await ExchangeCodeAsync(code, clientId, verifier, redirectUri, ct).ConfigureAwait(false);
        }
        finally
        {
            try { listener?.Close(); } catch { /* listener may never have started */ }
        }
    }

    private static async Task<(string Code, string State)> WaitForCallbackAsync(
        HttpListener listener, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            HttpListenerContext ctx = await listener.GetContextAsync().ConfigureAwait(false);

            // Only the callback path carries the code; anything else gets a 404
            // and the listener keeps waiting.
            if (!ctx.Request.Url!.AbsolutePath.EndsWith("/callback", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var query = ctx.Request.Url.Query;
            var parsed = System.Web.HttpUtility.ParseQueryString(query);

            if (parsed["error"] is not null)
            {
                ctx.Response.StatusCode = 200;
                WriteResponse(ctx.Response,
                    "Sign-in cancelled. You can close this tab and return to the app.");
                var err = parsed["error"];
                var errDesc = parsed["error_description"];
                throw new DerivApiException("AccessDenied",
                    $"Deriv sign-in was not completed: {err} — {errDesc}");
            }

            var code = parsed["code"];
            var state = parsed["state"];
            if (string.IsNullOrEmpty(code))
            {
                ctx.Response.StatusCode = 200;
                WriteResponse(ctx.Response,
                    "The redirect carried no authorization code. Return to the app and try again.");
                throw new DerivApiException("NoAuthorizationCode", "OAuth callback carried no code parameter.");
            }

            ctx.Response.StatusCode = 200;
            WriteResponse(ctx.Response,
                "<h2>Sign-in complete ✔</h2>You can close this tab and return to DON G FX.");
            return (code, state ?? "");
        }
    }

    private static void WriteResponse(HttpListenerResponse response, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:sans-serif;background:#101418;color:#e8e6e3;text-align:center;padding-top:4em'>"
            + html + "</body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    /// <summary>Opens the system browser. Virtual so tests can run the
    /// full loopback flow headlessly (they fire the callback themselves).</summary>
    protected virtual void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200];
}
