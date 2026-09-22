using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Auto-reconnect: a live account whose socket drops comes back on its own —
/// the loop stops when a manual disconnect or dispose happens, exhausts
/// after the attempt ladder, and never starts from a manual connect's own
/// Connecting… transition. Discovery is injected to fail transiently (no
/// network), delays are overridden, so tests run in milliseconds.
/// </summary>
[Trait("Category", "Unit")]
public class AutoReconnectTests
{
    private sealed class FailingAuth : NewPlatformAuth
    {
        public FailingAuth() : base("app123") { }
        public override Task<IReadOnlyList<NewPlatformAccount>> ListAccountsAsync(
            string bearerToken, CancellationToken ct = default) =>
            throw new DerivApiException("HttpTransient", "HTTP 503 test-fail (not terminal)");
    }

    private static AccountConnection MakeConnection() =>
        new(new AccountConfig
        {
            Label = "Demo (reconnect)",
            ApiToken = "tok-1234567890",
            IsDemo = true,
            NewPlatform = true,
            DerivAppId = "app123"
        }, tickCache: null, heartbeat: null, newPlatformAuth: new FailingAuth());

    private static void SetConnected(AccountConnection c, bool connected)
    {
        typeof(AccountConnection).GetProperty("IsConnected")!
            .SetValue(c, connected);
    }

    private static void RaiseStatus(AccountConnection c, ConnectionStatus status)
    {
        typeof(AccountConnection).GetMethod("OnStatusChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(c, new object[] { status });
    }

    /// <summary>Raises the client status event the way a server drop does:
    /// Connected first (arming _userWantsConnection), then the drop.</summary>
    private static void Drop(AccountConnection c)
    {
        SetConnected(c, true);
        RaiseStatus(c, ConnectionStatus.Connected);
        RaiseStatus(c, ConnectionStatus.Disconnected);
    }

    [Fact]
    public void Backoff_Ladder_Is_Exponential_Capped()
    {
        var c = MakeConnection();
        Assert.Equal(TimeSpan.FromSeconds(1), c.AutoReconnectDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), c.AutoReconnectDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), c.AutoReconnectDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), c.AutoReconnectDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(16), c.AutoReconnectDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(32), c.AutoReconnectDelay(6));
        Assert.Equal(TimeSpan.FromSeconds(60), c.AutoReconnectDelay(7));
        Assert.Equal(TimeSpan.FromSeconds(60), c.AutoReconnectDelay(20));
    }

    [Fact]
    public void Drop_Triggers_Reconnect_Attempt()
    {
        var c = MakeConnection();
        c.AutoReconnectDelayOverride = _ => TimeSpan.FromMilliseconds(30);
        Drop(c);

        // The loop must arm: poll briefly for the (failing) attempt to run.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && c.AutoReconnectAttemptsForTests < 1)
        {
            Thread.Sleep(50);
        }
        Assert.True(c.AutoReconnectAttemptsForTests >= 1,
            $"expected at least one attempt, saw {c.AutoReconnectAttemptsForTests}; status: {c.StatusText}");
    }

    [Fact]
    public void Manual_Disconnect_Stops_The_Loop()
    {
        var c = MakeConnection();
        c.AutoReconnectDelayOverride = _ => TimeSpan.FromMilliseconds(30);
        Drop(c);
        Thread.Sleep(80);

        c.DisconnectAsync().GetAwaiter().GetResult();
        var after = c.AutoReconnectAttemptsForTests;
        Thread.Sleep(400);
        Assert.Equal(after, c.AutoReconnectAttemptsForTests); // no further attempts
    }

    [Fact]
    public void Loop_Exhausts_After_Max_Attempts()
    {
        var c = MakeConnection();
        c.AutoReconnectDelayOverride = _ => TimeSpan.FromMilliseconds(20);
        Drop(c);

        // Poll until exhaustion instead of a fixed sleep: on a loaded CI
        // runner the loop needs longer than 5 x 20 ms of wall clock, and a
        // fixed window turned that into "expected exhaustion at 5, saw 3".
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               c.AutoReconnectAttemptsForTests < AccountConnection.AutoReconnectMaxAttempts)
        {
            Thread.Sleep(50);
        }

        Assert.True(c.AutoReconnectAttemptsForTests >= AccountConnection.AutoReconnectMaxAttempts,
            $"expected exhaustion at {AccountConnection.AutoReconnectMaxAttempts}, saw {c.AutoReconnectAttemptsForTests}");
    }

    [Fact]
    public void Connecting_Transition_Never_Starts_The_Loop()
    {
        var c = MakeConnection();
        c.AutoReconnectDelayOverride = _ => TimeSpan.FromMilliseconds(30);
        // A manual connect sets Connecting… from a non-connected previous state.
        RaiseStatus(c, ConnectionStatus.Connecting);

        Thread.Sleep(250);
        Assert.Equal(0, c.AutoReconnectAttemptsForTests);
    }
}
