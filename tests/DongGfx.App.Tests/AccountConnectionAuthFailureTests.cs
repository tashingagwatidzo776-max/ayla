using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Terminal auth failures (expired/revoked bearer — the OAuth 1-hour token
/// case, or a revoked PAT) must NOT churn the circuit breaker: discovery
/// returns 401 before any socket is opened, so retrying can never succeed.
/// The row surfaces an actionable "token expired" state instead; transient
/// failures keep the existing breaker semantics.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccountConnectionAuthFailureTests
{
    private sealed class FakeAuth : NewPlatformAuth
    {
        private readonly Func<Task<IReadOnlyList<NewPlatformAccount>>> _behavior;

        public FakeAuth(Func<Task<IReadOnlyList<NewPlatformAccount>>> behavior)
            : base("app123")
        {
            _behavior = behavior;
        }

        public override Task<IReadOnlyList<NewPlatformAccount>> ListAccountsAsync(
            string bearerToken, CancellationToken ct = default) => _behavior();
    }

    private static AccountConnection MakeConnection(NewPlatformAuth auth) =>
        new(new AccountConfig
        {
            Label = "Demo",
            ApiToken = "tok-1234567890",
            NewPlatform = true,
            DerivAppId = "app123"
        }, tickCache: null, heartbeat: null, newPlatformAuth: auth);

    private static readonly NewPlatformAccount[] Accounts =
    {
        new("DOT111", "demo", 10000m, "USD", "active")
    };

    [Fact]
    public async Task UnauthorizedDiscovery_MarksAuthFailureWithoutBreaker()
    {
        var conn = MakeConnection(new FakeAuth(
            () => throw new DerivApiException("Unauthorized", "The new platform rejected the token")));

        await conn.ConnectAsync(); // deliberate: no throw on terminal auth failure

        Assert.True(conn.AuthFailure);
        Assert.Contains("expired", conn.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sign in", conn.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(conn.IsDegraded);
        Assert.Equal(string.Empty, conn.CircuitStatus);
        Assert.Equal("Unauthorized", conn.LastError.Contains("Unauthorized", StringComparison.Ordinal)
            ? "Unauthorized" : conn.LastError);
    }

    [Fact]
    public async Task TransientDiscoveryFailure_KeepsCircuitBreakerSemantics()
    {
        var conn = MakeConnection(new FakeAuth(
            () => throw new DerivApiException("AccountsListFailed", "HTTP 503 listing accounts: unavailable")));

        await Assert.ThrowsAsync<DerivApiException>(() => conn.ConnectAsync());

        Assert.False(conn.AuthFailure);
        Assert.Contains("Connect failed (1/5)", conn.StatusText);
    }

    [Theory]
    [InlineData("Unauthorized", "rejected", true)]
    [InlineData("OtpFailed", "HTTP 401 requesting OTP", true)]
    [InlineData("AccountsListFailed", "HTTP 503 listing accounts", false)]
    [InlineData("OtpFailed", "HTTP 502 requesting OTP: bad gateway", false)]
    public void IsTerminalAuthFailure_ClassifiesExactCodes(string code, string message, bool expected)
    {
        Assert.Equal(expected, AccountConnection.IsTerminalAuthFailure(new DerivApiException(code, message)));
    }

    [Fact]
    public async Task Disconnect_ClearsAuthFailure()
    {
        var conn = MakeConnection(new FakeAuth(
            () => throw new DerivApiException("Unauthorized", "rejected")));
        await conn.ConnectAsync();
        Assert.True(conn.AuthFailure);

        await conn.DisconnectAsync();

        Assert.False(conn.AuthFailure);
        Assert.False(conn.HasAuthFailure);
    }
}
