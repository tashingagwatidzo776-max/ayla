using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.Core.Tests;

/// <summary>
/// New-platform (PAT) plumbing tests: OTP-driven connect, the absence of a
/// classic authorize call, the underlying_symbol rename, and the demo/real
/// verdict flowing from a discovery record. All against in-process fake
/// servers (WebSocket + REST), so no token or network is needed. The wire
/// shapes asserted here were captured live from api.derivws.com on
/// 2026-09-18 (see the new-platform probes in the session log).
/// </summary>
[Trait("Category", "Unit")]
public class NewPlatformDerivClientTests
{
    /// <summary>Standard balance payload the OTP socket returns for the
    /// seeded balance probe (same shape as classic, minus is_virtual).</summary>
    private const string BalanceBody =
        @"{""balance"":9814.97,""currency"":""USD"",""loginid"":""DOT92951338"",""id"":""sub-1""}";

    private static string Respond(JsonElement req, string msgType, string body) =>
        $@"{{""msg_type"":""{msgType}"",""req_id"":{req.GetProperty("req_id").GetInt32()},""{msgType}"":{body}}}";

    [Fact]
    public async Task ListAccountsAsync_ParsesStringBalance_LiveShape()
    {
        // Live-verified 2026-09-18: discovery returns balance as a STRING
        // ("9814.97"). GetDecimal() on it throws, which failed every hub
        // Connect with 'requires an element of type Number'. The parser must
        // accept both shapes.
        const string liveBody = @"{""data"":[{""account_id"":""DOT1"",""account_type"":""demo"",""balance"":""9814.97"",""currency"":""USD"",""status"":""active""}]}";
        var http = new HttpClient(new FakeHttpHandler(liveBody));
        var auth = new NewPlatformAuth("test-app-id", "http://localhost:1", http);

        var accounts = await auth.ListAccountsAsync("pat-token");

        var account = Assert.Single(accounts);
        Assert.Equal("DOT1", account.AccountId);
        Assert.Equal(9814.97m, account.Balance);
        Assert.True(account.IsDemoAccount);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FakeHttpHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task ConnectOtp_SkipsAuthorize_SeedsBalance_AndWorks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new DerivTradePlumbingTests.FakeDerivServer(req =>
        {
            if (req.TryGetProperty("balance", out _))
            {
                return Respond(req, "balance", BalanceBody);
            }
            return "";
        });
        _ = server.RunAsync(cts.Token);        var auth = new FakeAuth(() => server.WsUrl + "?otp=one");
        await using var client = new DerivClient
        {
            NewPlatform = auth,
            NewPlatformToken = "pat-token",
            NewPlatformAccountId = "DOT92951338"
        };

        var balanceTcs = new TaskCompletionSource<AccountBalance>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.BalanceUpdated += b => balanceTcs.TrySetResult(b);

        await client.ConnectAsync(ct: cts.Token);

        Assert.Equal(ConnectionStatus.Connected, client.Status);
        Assert.Equal("DOT92951338", client.LoginId);

        // The seeded balance probe answered without any authorize call.
        var balance = await balanceTcs.Task.WaitAsync(cts.Token);
        Assert.Equal(9814.97m, balance.Balance);

        // No classic authorize was ever sent on the OTP socket.
        Assert.DoesNotContain(server.Received, m => m.Contains("\"authorize\""));

        // And the auth module was asked for exactly the configured account.
        Assert.Equal("DOT92951338", auth.RequestedOtpAccountId);
    }

    [Fact]
    public async Task GetProposalAsync_NewPlatform_UsesUnderlyingSymbol()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string? seenKey = null;
        var server = new DerivTradePlumbingTests.FakeDerivServer(req =>
        {
            if (req.TryGetProperty("balance", out _))
            {
                return Respond(req, "balance", BalanceBody);
            }
            if (req.TryGetProperty("proposal", out _))
            {
                seenKey = req.TryGetProperty("underlying_symbol", out var us)
                    ? us.GetString()
                    : (req.TryGetProperty("symbol", out var s) ? s.GetString() : null);
                return Respond(req, "proposal",
                    @"{""id"":""PROP-1"",""spot"":599.5,""longcode"":""x"",""payout"":1.9}");
            }
            return "";
        });
        _ = server.RunAsync(cts.Token);

        await using var client = new DerivClient
        {
            NewPlatform = new FakeAuth(),
            NewPlatformToken = "pat-token",
            NewPlatformAccountId = "DOT92951338",
            NewPlatformUrlOverride = _ => Task.FromResult(server.WsUrl + "?otp=one")
        };
        await client.ConnectAsync(ct: cts.Token);

        var proposal = await client.GetProposalAsync(
            "frxEURUSD", Direction.Rise, 1m, "USD", 5, cts.Token);

        Assert.Equal("PROP-1", proposal.Id);
        Assert.Equal("frxEURUSD", seenKey); // via underlying_symbol, not symbol
    }

    [Fact]
    public async Task Reconnect_MintsFreshOtp_AndResumesSubscriptions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var otpCount = 0;
        var server = new DerivTradePlumbingTests.FakeDerivServer(req =>
        {
            if (req.TryGetProperty("balance", out _))
            {
                return Respond(req, "balance", BalanceBody);
            }
            if (req.TryGetProperty("ticks", out _))
            {
                // subscription confirmation, no payload
                return $@"{{""msg_type"":""tick"",""req_id"":{req.GetProperty("req_id").GetInt32()},""subscription"":{{""id"":""s""}}}}";
            }
            return "";
        });
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient
        {
            NewPlatform = new FakeAuth(),
            NewPlatformToken = "pat-token",
            NewPlatformAccountId = "DOT92951338",
            NewPlatformUrlOverride = _ =>
            {
                Interlocked.Increment(ref otpCount);
                return Task.FromResult(server.WsUrl + "?otp=" + otpCount);
            }
        };
        await using var clientLease = client;

        await client.ConnectAsync(ct: cts.Token);
        await client.SubscribeTicksAsync("R_100", cts.Token);

        // Simulate a dropped socket: the receive loop exits, the reconnect
        // ladder fires, and the client must mint a SECOND OTP (the first
        // URL was single-use) rather than replaying it.
        server.KillSocket();

        // Wait on the observable fact (a second OTP minted = the ladder
        // reconnected), not on Status transitions, which race the socket
        // teardown on slow runners.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (Volatile.Read(ref otpCount) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cts.Token);
        }
        Assert.True(Volatile.Read(ref otpCount) >= 2, "reconnect did not mint a second OTP in time");

        var connectedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (client.Status != ConnectionStatus.Connected && DateTime.UtcNow < connectedDeadline)
        {
            await Task.Delay(100, cts.Token);
        }

        Assert.Equal(ConnectionStatus.Connected, client.Status);

        var tickDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!server.Received.Any(m => m.Contains("\"ticks\"")) && DateTime.UtcNow < tickDeadline)
        {
            await Task.Delay(100, cts.Token);
        }
        Assert.Contains(server.Received, m => m.Contains("\"ticks\"")); // resubscribed
    }

    private sealed class FakeAuth : NewPlatformAuth
    {
        private readonly Func<string> _urlFactory;

        public FakeAuth(Func<string>? urlFactory = null)
            : base("test-app-id", "http://localhost:1", new HttpClient())
        {
            _urlFactory = urlFactory ?? (() => "ws://invalid-placeholder?otp=none");
        }

        public string? RequestedOtpAccountId { get; private set; }

        public override Task<string> GetOtpWebSocketUrlAsync(
            string bearerToken, string accountId, CancellationToken ct = default)
        {
            RequestedOtpAccountId = accountId;
            return Task.FromResult(_urlFactory());
        }
    }
}
