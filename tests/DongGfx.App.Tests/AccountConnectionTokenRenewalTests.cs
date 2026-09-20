using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// OAuth access-token renewal on the connection path: a due (or expired)
/// token is renewed via the refresh grant BEFORE discovery is called, so a
/// weekend-idle → Monday-open session re-authenticates in place and the
/// dead token never reaches the API. The residual-skew case (recorded
/// expiry says valid, the API disagrees) gets one refresh-and-retry, a
/// refused grant falls through to the actionable terminal state, and PAT
/// accounts never enter the renewal path.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccountConnectionTokenRenewalTests
{
    private sealed class FakeAuth : NewPlatformAuth
    {
        private readonly Func<string, Task<IReadOnlyList<NewPlatformAccount>>> _behavior;

        /// <summary>The bearer tokens each ListAccountsAsync call received.</summary>
        public List<string> SeenTokens { get; } = new();

        public FakeAuth(Func<string, Task<IReadOnlyList<NewPlatformAccount>>> behavior)
            : base("app123")
        {
            _behavior = behavior;
        }

        public override Task<IReadOnlyList<NewPlatformAccount>> ListAccountsAsync(
            string bearerToken, CancellationToken ct = default)
        {
            SeenTokens.Add(bearerToken);
            return _behavior(bearerToken);
        }
    }

    private sealed class StopBeforeSocket : InvalidOperationException
    {
        public StopBeforeSocket() : base("abort: renewal verified")
        {
        }
    }

    private static readonly NewPlatformAccount[] Accounts =
    {
        new("DOT111", "demo", 10000m, "USD", "active")
    };

    private static AccountConfig MakeOAuthConfig(bool expired) => new()
    {
        Label = "Demo (OAuth)",
        ApiToken = "expired-access-token",
        NewPlatform = true,
        DerivAppId = "app123",
        OAuthRefreshToken = "refresh-1",
        OAuthClientId = "app12345",
        TokenExpiresAtUtc = expired
            ? DateTimeOffset.UtcNow - TimeSpan.FromHours(2)
            : DateTimeOffset.UtcNow + TimeSpan.FromHours(2)
    };

    [Fact]
    public async Task DueToken_RenewsBeforeDiscoverySeesIt()
    {
        var config = MakeOAuthConfig(expired: true);
        var persistCount = 0;
        var fake = new FakeAuth(_ => throw new StopBeforeSocket());
        var conn = new AccountConnection(
            config, tickCache: null, heartbeat: null,
            newPlatformAuth: fake,
            persist: () => persistCount++)
        {
            RefreshOverrideForTests = (refresh, clientId) =>
            {
                Assert.Equal("refresh-1", refresh);
                Assert.Equal("app12345", clientId);
                return Task.FromResult(new OAuthResult("fresh-token", 3600, "refresh-2"));
            }
        };

        await Assert.ThrowsAsync<StopBeforeSocket>(() => conn.ConnectAsync());

        // The renewed token — not the dead one — reached discovery.
        Assert.Equal(["fresh-token"], fake.SeenTokens);

        // Config, live client and vault write all reflect the renewal.
        Assert.Equal("fresh-token", config.ApiToken);
        Assert.Equal("refresh-2", config.OAuthRefreshToken);
        Assert.NotNull(config.TokenExpiresAtUtc);
        Assert.True(config.TokenExpiresAtUtc!.Value - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(55));
        Assert.NotNull(conn.TokenRenewedAtUtc);
        Assert.Equal(1, persistCount);
        Assert.False(conn.AuthFailure);
    }

    [Fact]
    public async Task FreshToken_SkipsRenewal()
    {
        var config = MakeOAuthConfig(expired: false);
        var persistCount = 0;
        var fake = new FakeAuth(_ => throw new StopBeforeSocket());
        var conn = new AccountConnection(
            config, tickCache: null, heartbeat: null,
            newPlatformAuth: fake,
            persist: () => persistCount++)
        {
            RefreshOverrideForTests = (_, _) =>
                throw new InvalidOperationException("renewal must not run for a fresh token")
        };

        await Assert.ThrowsAsync<StopBeforeSocket>(() => conn.ConnectAsync());

        Assert.Equal("expired-access-token", config.ApiToken); // untouched
        Assert.Equal("expired-access-token", fake.SeenTokens.Single());
        Assert.Null(conn.TokenRenewedAtUtc);
        Assert.Equal(0, persistCount);
    }

    [Fact]
    public async Task DiscoveryRejectsRecordedFreshToken_RefreshesAndRetries()
    {
        var config = MakeOAuthConfig(expired: false); // skew: expiry says valid
        var calls = 0;
        var fake = new FakeAuth(token =>
        {
            calls++;
            return calls == 1
                ? throw new DerivApiException("Unauthorized", "The new platform rejected the token")
                : throw new StopBeforeSocket();
        });
        var conn = new AccountConnection(
            config, tickCache: null, heartbeat: null,
            newPlatformAuth: fake)
        {
            RefreshOverrideForTests = (_, _) =>
                Task.FromResult(new OAuthResult("fresh-token", 3600, "refresh-2"))
        };

        await Assert.ThrowsAsync<StopBeforeSocket>(() => conn.ConnectAsync());

        // First attempt used the dead token; the retry used the renewed one.
        Assert.Equal(["expired-access-token", "fresh-token"], fake.SeenTokens);
        Assert.Equal("fresh-token", config.ApiToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RefusedRefresh_FallsThroughToTerminalAuthState()
    {
        var config = MakeOAuthConfig(expired: true);
        var conn = new AccountConnection(
            config, tickCache: null, heartbeat: null,
            newPlatformAuth: new FakeAuth(_ =>
                throw new DerivApiException("Unauthorized", "The new platform rejected the token")))
        {
            RefreshOverrideForTests = (_, _) =>
                throw new DerivApiException("Unauthorized", "refresh grant refused")
        };

        await conn.ConnectAsync(); // terminal auth failure: surfaces, never throws

        // The actionable state — "sign in again", not breaker churn.
        Assert.True(conn.AuthFailure);
        Assert.False(conn.IsDegraded);
        Assert.Equal("expired-access-token", config.ApiToken); // no half-renewal
        Assert.Null(conn.TokenRenewedAtUtc);
    }

    [Fact]
    public async Task PatAccount_NeverEntersRenewal()
    {
        var config = new AccountConfig
        {
            Label = "Demo (PAT)",
            ApiToken = "pat-token-123456",
            NewPlatform = true,
            DerivAppId = "app123",
            OAuthRefreshToken = "", // PAT: no refresh credential
            OAuthClientId = "",
            TokenExpiresAtUtc = null // unknown = "due" for OAuth accounts
        };
        var fake = new FakeAuth(_ => throw new StopBeforeSocket());
        var conn = new AccountConnection(
            config, tickCache: null, heartbeat: null,
            newPlatformAuth: fake)
        {
            RefreshOverrideForTests = (_, _) =>
                throw new InvalidOperationException("a PAT must never attempt renewal")
        };

        await Assert.ThrowsAsync<StopBeforeSocket>(() => conn.ConnectAsync());

        Assert.False(conn.CanRenewToken);
        Assert.Equal("pat-token-123456", fake.SeenTokens.Single());
        Assert.Null(conn.TokenRenewedAtUtc);
    }
}
