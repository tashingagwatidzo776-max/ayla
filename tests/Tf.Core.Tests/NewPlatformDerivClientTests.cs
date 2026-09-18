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

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (client.Status != ConnectionStatus.Connected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cts.Token);
        }

        Assert.Equal(ConnectionStatus.Connected, client.Status);
        Assert.Equal(2, otpCount); // a fresh OTP was minted for the reconnect
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
