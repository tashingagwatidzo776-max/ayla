using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DongGfx.App.Services;
using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The trade feed turns the bridge's deal history into Performance/Journal
/// rows: closing legs with a realised outcome are recorded once (ticket
/// dedup), flat legs are remembered but not recorded, a dead bridge falls
/// back to the injected provider, and the account identity resolves from
/// /account when it answers. Read-only — the feed can never place an order.
/// </summary>
[Trait("Category", "Unit")]
public class FxTradeFeedTests
{
    private sealed class FakeBridge : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json)> Routes { get; } = new();

        public void Route(string path, string json) =>
            Routes[path] = (HttpStatusCode.OK, json);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (Routes.TryGetValue(path, out var r))
            {
                return Task.FromResult(new HttpResponseMessage(r.Status)
                {
                    Content = new StringContent(r.Json, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Deal(long ticket, double profit, double commission, double swap, long time) =>
        $"{{\"ticket\": {ticket}, \"order\": {ticket - 1}, \"symbol\": \"XAUUSDmicro\", \"side\": \"sell\", " +
        $"\"volume\": 0.5, \"price\": 2400.0, \"profit\": {profit}, \"commission\": {commission}, " +
        $"\"swap\": {swap}, \"time\": {time}}}";

    private static string AccountJson(long login = 201587365, string server = "Deriv-Demo") =>
        $"{{\"login\": {login}, \"server\": \"{server}\", \"currency\": \"USD\", \"balance\": 2729.34, " +
        "\"equity\": 2729.34, \"margin_free\": 5000.0, \"leverage\": 1000, \"trade_mode\": 0}";

    private static (FxTradeFeed Feed, FakeBridge Fake, PerformanceTracker Tracker, TradeJournal Journal) NewFeed(
        Func<IReadOnlyList<Mt5Deal>>? provider = null)
    {
        var fake = new FakeBridge();
        var client = new Mt5BridgeClient(fake, new Uri("http://127.0.0.1:53190/"));
        var root = Path.Combine(Path.GetTempPath(), "donggfx-tests", Guid.NewGuid().ToString("N"));
        var tracker = new PerformanceTracker(Path.Combine(root, "perf"));
        var journal = new TradeJournal(Path.Combine(root, "journal"));
        var feed = new FxTradeFeed(client, tracker, journal, provider);
        return (feed, fake, tracker, journal);
    }

    [Fact]
    public async Task Refresh_Records_Settled_Deals_Once_And_Skips_Flat_Legs()
    {
        var (feed, fake, _, journal) = NewFeed();
        fake.Route("deals", "{\"deals\": [" +
            Deal(101, 12.5, -2.0, -0.5, 1790000000) + "," +   // win: net +10
            Deal(202, -20.0, 0.0, -1.0, 1790003600) + "," +   // loss: net -21
            Deal(303, 0.0, 0.0, 0.0, 1790007200) +            // flat entry leg: never recorded
            "]}");

        var added = await feed.RefreshAsync();

        Assert.Equal(2, added);
        var trades = feed.Trades;
        Assert.Equal(2, trades.Count);
        Assert.Equal(-21m, trades[0].Profit);   // newest first
        Assert.Equal(10m, trades[1].Profit);
        Assert.Equal("XAUUSDmicro", trades[0].Symbol);
        Assert.True(trades[0].IsWin == false);
        Assert.Equal(1200m, trades[1].Stake);   // volume 0.5 x price 2400
        Assert.Equal(TradeSource.Fx, trades[0].Source);
        Assert.Null(trades[0].AccountName);     // /account never answered

        // Polling again is idempotent: every ticket (flat included) is seen.
        Assert.Equal(0, await feed.RefreshAsync());

        journal.Flush();
        var settlements = journal.GetRecent(count: 50)
            .Where(e => e.Category == "TRADE_SETTLEMENT")
            .ToList();
        Assert.Equal(2, settlements.Count);
        Assert.All(settlements, e => Assert.Equal(Guid.Empty, e.AccountId));
    }

    [Fact]
    public async Task Refresh_Falls_Back_To_The_Injected_Provider_When_Bridge_Is_Down()
    {
        var providerDeals = new List<Mt5Deal>
        {
            new(505, 504, "EURUSD", "sell", 0.2, 1.1, 7.5, 0, 0, 1790000000),
        };
        var (feed, fake, _, _) = NewFeed(() => providerDeals);
        // No /deals route -> 404 -> client degrades to empty -> provider wins.
        fake.Route("account", AccountJson(77777, "Bridgeless-Demo"));

        var added = await feed.RefreshAsync();

        Assert.Equal(1, added);
        Assert.Single(feed.Trades);
        Assert.Equal("MT5 77777 (Bridgeless-Demo)", feed.Trades[0].AccountName);
        Assert.NotNull(feed.Trades[0].AccountId);
    }

    [Fact]
    public async Task Refresh_Returns_Zero_When_Bridge_And_Provider_Are_Both_Empty()
    {
        var (feed, _, _, _) = NewFeed();   // provider defaults to empty

        Assert.Equal(0, await feed.RefreshAsync());
        Assert.Empty(feed.Trades);
    }

    [Fact]
    public async Task Refresh_Resolves_Account_Identity_From_The_Bridge()
    {
        var (feed, fake, _, journal) = NewFeed();
        fake.Route("deals", "{\"deals\": [" + Deal(900, 5.0, 0.0, 0.0, 1790000000) + "]}");
        fake.Route("account", AccountJson(201587365, "Deriv-Demo"));

        var added = await feed.RefreshAsync();

        Assert.Equal(1, added);
        var trade = feed.Trades[0];
        Assert.Equal("MT5 201587365 (Deriv-Demo)", trade.AccountName);
        Assert.NotNull(trade.AccountId);
        Assert.NotEqual(Guid.Empty, trade.AccountId.Value);

        journal.Flush();
        var settlement = journal.GetRecent(count: 50)
            .Single(e => e.Category == "TRADE_SETTLEMENT");
        Assert.Equal(trade.AccountId.Value, settlement.AccountId);
    }

    [Fact]
    public void Start_And_Dispose_Are_Safe_With_No_Cycle_Due_Yet()
    {
        var (feed, _, _, _) = NewFeed();

        // First callback is due after 15 s — well past this test's lifetime —
        // so this only proves the timer wiring and dispose path.
        feed.Start(TimeSpan.FromHours(1));
        feed.Dispose();
    }
}
