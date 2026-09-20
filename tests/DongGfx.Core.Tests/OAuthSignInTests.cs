using System.Net;
using System.Net.Sockets;
using System.Text;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// OAuth 2.0 (Authorization Code + PKCE) sign-in tests: the PKCE challenge
/// derivation is pinned to the RFC 7636 test vector, the authorize URL
/// carries every required parameter, the token exchange parses the docs'
/// response shape (including the optional refresh token), and the full
/// desktop loopback flow — browser callback captured on localhost, state
/// verified, code exchanged — runs headlessly with a fake browser.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OAuthSignInTests
{
    [Fact]
    public void CodeChallenge_MatchesRfc7636TestVector()
    {
        // RFC 7636 appendix B: the canonical verifier and its S256 challenge.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(expected, OAuthSignIn.GenerateCodeChallenge(verifier));
    }

    [Fact]
    public void CodeVerifier_Is43Base64UrlChars()
    {
        var verifier = OAuthSignIn.GenerateCodeVerifier();

        Assert.Equal(43, verifier.Length);
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
    }

    [Fact]
    public void State_IsRandomPerCall()
    {
        Assert.NotEqual(OAuthSignIn.GenerateState(), OAuthSignIn.GenerateState());
    }

    [Fact]
    public void BuildAuthorizeUrl_CarriesAllRequiredParameters()
    {
        var oauth = new OAuthSignIn();
        var url = oauth.BuildAuthorizeUrl(
            "app12345", "http://localhost:4919/callback",
            scope: "trade account_manage",
            state: "st4te",
            codeChallenge: "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

        Assert.StartsWith("https://auth.deriv.com/oauth2/auth?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=app12345", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A4919%2Fcallback", url);
        Assert.Contains("scope=trade%20account_manage", url);
        Assert.Contains("state=st4te", url);
        Assert.Contains("code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.DoesNotContain("prompt=", url);
    }

    [Fact]
    public void BuildAuthorizeUrl_SignUpVariant_AddsRegistrationPrompt()
    {
        var oauth = new OAuthSignIn();
        var url = oauth.BuildAuthorizeUrl(
            "app12345", "http://localhost:4919/callback",
            state: "s", codeChallenge: "c", prompt: "registration");

        Assert.Contains("prompt=registration", url);
    }

    [Fact]
    public void BuildAuthorizeUrl_EmptyClientId_Throws()
    {
        var oauth = new OAuthSignIn();
        Assert.Throws<ArgumentException>(
            () => oauth.BuildAuthorizeUrl("", "http://localhost:1/callback"));
    }

    private sealed class FakeTokenHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Func<string?, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).Result;
            return Task.FromResult(Responder(LastBody));
        }
    }

    [Fact]
    public async Task ExchangeCodeAsync_Success_ParsesTokenResponse()
    {
        var handler = new FakeTokenHandler
        {
            Responder = body =>
            {
                Assert.NotNull(body);
                Assert.Contains("grant_type=authorization_code", body);
                Assert.Contains("client_id=app12345", body);
                Assert.Contains("code=THE_CODE", body);
                Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A4919%2Fcallback", body);
                // The verifier must be the one that produced the challenge —
                // shape-check here; exactness is pinned by the RFC vector test.
                Assert.Matches("code_verifier=[A-Za-z0-9_-]{43}", body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"ory_at_abc123","expires_in":3600,"token_type":"Bearer"}""",
                        Encoding.UTF8, "application/json")
                };
            }
        };
        var oauth = new OAuthSignIn(http: new HttpClient(handler));

        var result = await oauth.ExchangeCodeAsync(
            "THE_CODE", "app12345", OAuthSignIn.GenerateCodeVerifier(),
            "http://localhost:4919/callback");

        Assert.Equal("ory_at_abc123", result.AccessToken);
        Assert.Equal(3600, result.ExpiresInSeconds);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task ExchangeCodeAsync_InvalidGrant_ThrowsWithCode()
    {
        var handler = new FakeTokenHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""")
            }
        };
        var oauth = new OAuthSignIn(http: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<DerivApiException>(() =>
            oauth.ExchangeCodeAsync("c", "app", "v", "http://localhost:1/callback"));

        Assert.Equal("InvalidGrant", ex.Code);
    }

    /// <summary>Fakes the browser: fires the loopback callback with a
    /// query string derived from the authorization URL it was opened with.</summary>
    private sealed class FakeBrowserSignIn : OAuthSignIn
    {
        public string? OpenedUrl { get; private set; }
        private readonly string _callbackQuery;
        private readonly int _port;

        public FakeBrowserSignIn(string callbackQuery, int port, HttpMessageHandler handler)
            : base(http: new HttpClient(handler))
        {
            _callbackQuery = callbackQuery;
            _port = port;
        }

        protected override void OpenBrowser(string url)
        {
            OpenedUrl = url;
            // The redirect_uri is percent-encoded inside the auth URL, so the
            // port is passed explicitly; only "state=" appears literally.
            var state = Uri.UnescapeDataString(
                url.Split("state=")[1].Split('&')[0]);

            // Fire the callback from a background thread once the listener is up.
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                var req = WebRequest.Create($"http://localhost:{_port}/callback?{_callbackQuery.Replace("{STATE}", state)}");
                try { using var resp = req.GetResponse(); } catch { /* response body not needed */ }
            });
        }
    }

    [Fact]
    public async Task SignInAsync_LoopbackFlow_CapturesCallbackAndExchangesCode()
    {
        var handler = new FakeTokenHandler
        {
            Responder = body =>
            {
                Assert.NotNull(body);
                Assert.Contains("code=LOOPBACK_CODE", body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"ory_at_loop","expires_in":3600,"token_type":"Bearer"}""",
                        Encoding.UTF8, "application/json")
                };
            }
        };
        var port = OAuthSignIn.FindFreeLoopbackPort();
        var oauth = new FakeBrowserSignIn("code=LOOPBACK_CODE&state={STATE}", port, handler);

        var result = await oauth.SignInAsync("app12345", loopbackPort: port);

        Assert.NotNull(oauth.OpenedUrl);
        Assert.Contains("state=", oauth.OpenedUrl);
        Assert.Equal("ory_at_loop", result.AccessToken);
    }

    [Fact]
    public async Task SignInAsync_StateMismatch_AbortsAsCsrf()
    {
        var handler = new FakeTokenHandler
        {
            Responder = _ => throw new InvalidOperationException("token exchange must never run on state mismatch")
        };
        var port = OAuthSignIn.FindFreeLoopbackPort();
        var oauth = new FakeBrowserSignIn("code=EVIL&state=WRONG", port, handler);

        var ex = await Assert.ThrowsAsync<DerivApiException>(
            () => oauth.SignInAsync("app12345", loopbackPort: port));

        Assert.Equal("StateMismatch", ex.Code);
    }

    [Fact]
    public async Task SignInAsync_ErrorCallback_SurfacesDenial()
    {
        var handler = new FakeTokenHandler
        {
            Responder = _ => throw new InvalidOperationException("token exchange must never run on error callback")
        };
        var port = OAuthSignIn.FindFreeLoopbackPort();
        var oauth = new FakeBrowserSignIn("error=access_denied&error_description=User+cancelled", port, handler);

        var ex = await Assert.ThrowsAsync<DerivApiException>(
            () => oauth.SignInAsync("app12345", loopbackPort: port));

        Assert.Equal("AccessDenied", ex.Code);
    }
}
