using System.Collections.Concurrent;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.Core.Tests;

/// <summary>
/// Milestone-1 verification against the live Deriv API.
///
/// Since late-2025 Deriv requires an authorized session for LIVE tick
/// subscriptions, and anonymous WebSocket handshakes are additionally
/// rejected from datacenter IPs (observed as HTTP 401 during the upgrade
/// from GitHub Actions runners), the live smoke test below is opt-in:
/// set <c>TF_DERIV_SMOKE=1</c> to run it. Set <c>TF_DERIV_TOKEN</c> (a
/// Deriv API token, demo is fine) to exercise the live-tick path. Set
/// <c>TF_DERIV_APP_ID</c>/<c>TF_DERIV_SYMBOL</c> to override the defaults.
/// </summary>
[Trait("Category", "Integration")]
public class DerivClientIntegrationTests
{
    private static readonly string AppId =
        Environment.GetEnvironmentVariable("TF_DERIV_APP_ID") ?? AppSettings.DefaultAppId;

    private static readonly string Symbol =
        Environment.GetEnvironmentVariable("TF_DERIV_SYMBOL") ?? AppSettings.DefaultSymbol;

    private static readonly string Token =
        Environment.GetEnvironmentVariable("TF_DERIV_TOKEN") ?? "";

    [Fact]
    public async Task Connects_And_BackfillsHistory_WithoutToken()
    {
        if (Environment.GetEnvironmentVariable("TF_DERIV_SMOKE") != "1")
        {
            Console.WriteLine(
                "TF_DERIV_SMOKE not set — skipping live anonymous-connect smoke test. " +
                "Deriv rejects anonymous handshakes from CI/datacenter IPs, so this " +
                "test only runs when explicitly requested.");
            return;
        }

        await using var client = new DerivClient { AppId = AppId };

        await client.ConnectAsync();
        Assert.True(client.IsConnected, "client should be connected");

        var history = await client.GetTicksHistoryAsync(Symbol, 50);
        Assert.NotNull(history);
        Assert.True(history.Count > 0, $"expected tick history for {Symbol}");
    }

    [Fact]
    public async Task Authorizes_And_StreamsLiveTicks_WhenTokenProvided()
    {
        if (string.IsNullOrEmpty(Token))
        {
            Console.WriteLine(
                "TF_DERIV_TOKEN not set — skipping live-tick assertion. " +
                "Set it to a Deriv (demo) token to verify the M1 live feed.");
            return;
        }

        await using var client = new DerivClient { AppId = AppId };
        var ticks = new ConcurrentQueue<Tick>();

        client.TickReceived += t => ticks.Enqueue(t);
        client.ErrorReceived += Console.WriteLine;

        await client.ConnectAsync(Token);
        Assert.True(client.IsConnected, "client should be connected");
        Assert.NotEqual(0m, client.Balance.Balance);
        Assert.False(string.IsNullOrEmpty(client.Balance.Currency));

        await client.SubscribeTicksAsync(Symbol);

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (ticks.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        Assert.False(ticks.IsEmpty, $"expected at least one live tick for {Symbol}");
        var tick = ticks.First();
        Assert.Equal(Symbol, tick.Symbol);
        Assert.True(tick.Quote > 0, "tick quote should be a positive price");
    }
}