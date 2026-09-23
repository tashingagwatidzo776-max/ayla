using System.Net;
using System.Net.Http;
using System.Text;
using DongGfx.App.Services;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The bridge client must degrade cleanly on every failure (the terminal
/// keeps working when MT5 or the sidecar is off) and parse every endpoint
/// the UI and FX brain consume. All traffic goes through an injected
/// handler — no sockets.
/// </summary>
[Trait("Category", "Unit")]
public class Mt5BridgeClientTests
{
    private sealed class FakeBridge : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json)> GetRoutes { get; } = new();
        public Dictionary<string, (HttpStatusCode Status, string Json)> PostRoutes { get; } = new();
        public List<string> Requests { get; } = new();
        public string? LastBody { get; private set; }
        public bool ThrowOnNext { get; set; }

        public void Route(string path, string json) =>
            GetRoutes[path] = (HttpStatusCode.OK, json);

        public void RoutePost(string path, HttpStatusCode status, string json) =>
            PostRoutes[path] = (status, json);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var verb = request.Method == HttpMethod.Post ? "POST " : "GET ";
            Requests.Add(verb + request.RequestUri!.PathAndQuery.TrimStart('/'));
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            if (ThrowOnNext)
            {
                throw new HttpRequestException("connection refused");
            }

            var table = request.Method == HttpMethod.Post ? PostRoutes : GetRoutes;
            if (table.TryGetValue(path, out var r))
            {
                return new HttpResponseMessage(r.Status)
                {
                    Content = new StringContent(r.Json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (Mt5BridgeClient Client, FakeBridge Fake) NewClient()
    {
        var fake = new FakeBridge();
        return (new Mt5BridgeClient(fake, new Uri("http://127.0.0.1:53190/")), fake);
    }

    private static string Json(params string[] fields) => "{" + string.Join(",", fields) + "}";

    [Fact]
    public void Constructor_Refuses_Non_Loopback_But_Accepts_Loopback()
    {
        var fake = new FakeBridge();
        var ex = Assert.Throws<ArgumentException>(
            () => new Mt5BridgeClient(fake, new Uri("http://example.com/")));
        Assert.Contains("loopback", ex.Message);

        using var local = new Mt5BridgeClient(fake, new Uri("http://localhost:53190/"));
        Assert.True(local.IsLoopback);
    }

    [Fact]
    public async Task Health_Parses_Attached_Account_And_Degrades_To_Null()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("health", Json("\"ok\": true", "\"login\": 201587365", "\"server\": \"Deriv-Demo\""));
            var up = await client.HealthAsync();
            Assert.NotNull(up);
            Assert.True(up!.Value.Ok);
            Assert.Equal(201587365, up.Value.Login);
            Assert.Equal("Deriv-Demo", up.Value.Server);

            fake.Route("health", Json("\"ok\": true")); // no login/server keys
            var minimal = await client.HealthAsync();
            Assert.True(minimal!.Value.Ok);
            Assert.Null(minimal.Value.Login);

            fake.GetRoutes.Remove("health"); // 404 -> sidecar down
            Assert.Null(await client.HealthAsync());
        }
    }

    [Fact]
    public async Task Account_Parses_Fields_And_Verdict_Through_Every_Mode()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("account", Json(
                "\"login\": 201587365", "\"server\": \"Deriv-Demo\"", "\"currency\": \"USD\"",
                "\"balance\": 2729.34", "\"equity\": 2729.34", "\"margin_free\": 5000.0",
                "\"leverage\": 1000", "\"trade_mode\": 0"));
            var demo = await client.GetAccountAsync();
            Assert.NotNull(demo);
            Assert.Equal(201587365, demo!.Login);
            Assert.Equal("USD", demo.Currency);
            Assert.Equal(2729.34, demo.Equity, 6);
            Assert.Equal(1000, demo.Leverage);
            Assert.True(demo.TradeModeVerifiedVirtual);
            Assert.True(demo.GateVerifiedVirtual);

            // Venue says real -> the verdict is verified-real, never virtual.
            fake.Route("account", Json(
                "\"login\": 7", "\"server\": \"Live-Server\"", "\"currency\": \"USD\"",
                "\"balance\": 1.0", "\"equity\": 1.0", "\"margin_free\": 1.0",
                "\"leverage\": 100", "\"trade_mode\": 2"));
            var real = await client.GetAccountAsync();
            Assert.False(real!.TradeModeVerifiedVirtual);
            Assert.False(real.GateVerifiedVirtual);

            // Older sidecar without trade_mode: heuristic may verify demo
            // (server name) but never real -> gate stays fail-closed.
            fake.Route("account", Json(
                "\"login\": 7", "\"server\": \"Deriv-Demo\"", "\"currency\": \"USD\"",
                "\"balance\": 1.0", "\"equity\": 1.0", "\"margin_free\": 1.0",
                "\"leverage\": 100"));
            var noVerdict = await client.GetAccountAsync();
            Assert.Null(noVerdict!.TradeMode);
            Assert.Null(noVerdict.TradeModeVerifiedVirtual);
            Assert.True(noVerdict.GateVerifiedVirtual); // demo by server name

            fake.Route("account", Json(
                "\"login\": 7", "\"server\": \"Live-Server\"", "\"currency\": \"USD\"",
                "\"balance\": 1.0", "\"equity\": 1.0", "\"margin_free\": 1.0",
                "\"leverage\": 100"));
            var liveNoVerdict = await client.GetAccountAsync();
            Assert.Null(liveNoVerdict!.GateVerifiedVirtual); // refuses to verify

            fake.GetRoutes.Remove("account");
            Assert.Null(await client.GetAccountAsync());
        }
    }

    [Fact]
    public async Task Symbols_Parse_Volume_Geometry_And_Defaults()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("symbols", "{\"symbols\": [" +
                "{\"symbol\": \"XAUUSDmicro\", \"description\": \"Gold micro\", \"bid\": 4345.19, \"ask\": 4345.48, " +
                "\"spread_points\": 27, \"digits\": 2, \"trade_mode\": 4, \"volume_min\": 0.1, " +
                "\"volume_step\": 0.1, \"volume_max\": 100.0, \"contract_size\": 1.0}," +
                "{\"symbol\": \"EURUSD\", \"bid\": null, \"ask\": 1.1, \"digits\": 5}" +
                "]}");
            var symbols = await client.GetSymbolsAsync();

            Assert.Equal(2, symbols.Count);
            var gold = symbols[0];
            Assert.Equal("XAUUSDmicro", gold.Symbol);
            Assert.Equal(4345.19, gold.Bid!.Value, 6);
            Assert.Equal(0.1, gold.VolumeStep, 6);
            Assert.Equal(1.0, gold.ContractSize, 6);

            var sparse = symbols[1];
            Assert.Null(sparse.Bid);           // explicit null is not a number
            Assert.Equal(1.1, sparse.Ask!.Value, 6);
            Assert.Equal(5, sparse.Digits);
            Assert.Equal(0, sparse.SpreadPoints);   // absent -> default
            Assert.Equal(0, sparse.VolumeStep);     // absent -> unknown
            Assert.Equal("", sparse.Description);

            fake.GetRoutes.Remove("symbols");
            Assert.Empty(await client.GetSymbolsAsync());
        }
    }

    [Fact]
    public async Task Tick_Parses_Quote_And_Refuses_Without_Bid()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("ticks/XAUUSDmicro",
                Json("\"bid\": 4345.19", "\"ask\": 4345.48", "\"time\": 1790003496"));
            var tick = await client.GetTickAsync("XAUUSDmicro");
            Assert.NotNull(tick);
            Assert.Equal(4345.19, tick!.Value.Bid, 6);
            Assert.Equal(1790003496, tick.Value.Time);

            fake.Route("ticks/XAUUSDmicro", Json("\"bid\": null", "\"ask\": 1.0"));
            Assert.Null(await client.GetTickAsync("XAUUSDmicro"));

            fake.GetRoutes.Remove("ticks/XAUUSDmicro");
            Assert.Null(await client.GetTickAsync("XAUUSDmicro"));
        }
    }

    [Fact]
    public async Task Book_Parses_Levels_And_Degrades_To_Empty()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("book/XAUUSDmicro", "{\"levels\": [" +
                "{\"side\": \"bid\", \"price\": 4345.19, \"volume\": 3.0}," +
                "{\"side\": \"ask\", \"price\": 4345.48, \"volume\": 1.5}" +
                "]}");
            var levels = await client.GetBookAsync("XAUUSDmicro");
            Assert.Equal(2, levels.Count);
            Assert.Equal(4345.19, levels[0].Price, 6);
            Assert.Equal("ask", levels[1].Side);

            fake.GetRoutes.Remove("book/XAUUSDmicro");
            Assert.Empty(await client.GetBookAsync("XAUUSDmicro"));
        }
    }

    [Fact]
    public async Task Candles_Parse_Ohlc_And_Degrade_To_Empty()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("candles/EURUSD", "{\"candles\": [" +
                "{\"time\": 1790000000, \"open\": 1.1, \"high\": 1.2, \"low\": 1.0, \"close\": 1.15}," +
                "{\"time\": 1790000060, \"open\": 1.15, \"high\": 1.3, \"low\": 1.1, \"close\": 1.25}" +
                "]}");
            var candles = await client.GetCandlesAsync("EURUSD", "M5", 2);
            Assert.Equal(2, candles.Count);
            Assert.Equal(1790000060, candles[1].Time);
            Assert.Equal(1.3, candles[1].High, 6);
            Assert.Contains("tf=M5", fake.Requests[^1]);
            Assert.Contains("n=2", fake.Requests[^1]);

            fake.GetRoutes.Remove("candles/EURUSD");
            Assert.Empty(await client.GetCandlesAsync("EURUSD"));
        }
    }

    [Fact]
    public async Task PlaceOrder_Parses_Full_And_Minimal_Success()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.RoutePost("order", HttpStatusCode.OK, Json(
                "\"ok\": true", "\"retcode\": 10009", "\"retcode_name\": \"TRADE_DONE\"",
                "\"deal\": 555", "\"order\": 444", "\"price\": 4345.5", "\"volume\": 0.1",
                "\"comment\": \"filled\""));
            var done = await client.PlaceOrderAsync(
                "XAUUSDmicro", "buy", "market", 0.1, price: 4345.5, sl: 4340.0, tp: 4350.0);
            Assert.True(done.Ok);
            Assert.Equal(10009, done.Retcode);
            Assert.Equal(555, done.Deal);
            Assert.Equal(4345.5, done.Price!.Value, 6);
            Assert.Equal("filled", done.Comment);
            Assert.NotNull(fake.LastBody);
            Assert.Contains("\"lots\"", fake.LastBody);
            Assert.Contains("\"sl\"", fake.LastBody);
            Assert.DoesNotContain("\"stopprice\"", fake.LastBody); // not passed

            // Minimal body: every optional field takes its absent branch.
            fake.RoutePost("order", HttpStatusCode.OK, Json(
                "\"ok\": false", "\"retcode\": 10013", "\"retcode_name\": \"TRADE_REJECTED\""));
            var rejected = await client.PlaceOrderAsync("XAUUSDmicro", "buy", "market", 0.1);
            Assert.False(rejected.Ok);
            Assert.Null(rejected.Deal);
            Assert.Null(rejected.Price);
            Assert.Null(rejected.Volume);
            Assert.Equal("", rejected.Comment);
        }
    }

    [Fact]
    public async Task PlaceOrder_Throws_Bridge_Exception_With_The_Refusal_Reason()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.RoutePost("order", HttpStatusCode.BadRequest,
                Json("\"error\": \"volume step 0.18 is off-grid\""));
            var ex = await Assert.ThrowsAsync<Mt5BridgeException>(
                () => client.PlaceOrderAsync("XAUUSDmicro", "buy", "market", 0.18));
            Assert.Contains("off-grid", ex.Message);

            // Refusal without an error key falls back to the raw body.
            fake.RoutePost("order", HttpStatusCode.BadRequest, "{\"detail\": \"boom\"}");
            var raw = await Assert.ThrowsAsync<Mt5BridgeException>(
                () => client.PlaceOrderAsync("XAUUSDmicro", "buy", "market", 0.1));
            Assert.Contains("boom", raw.Message);

            // Non-JSON refusal (HTML error page from a squatter on the port)
            // must still surface as Mt5BridgeException, never JsonException.
            fake.RoutePost("order", HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>");
            var html = await Assert.ThrowsAsync<Mt5BridgeException>(
                () => client.PlaceOrderAsync("XAUUSDmicro", "buy", "market", 0.1));
            Assert.Contains("502", html.Message);
        }
    }

    [Fact]
    public async Task Positions_Parse_And_Degrade_To_Empty()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("positions", "{\"positions\": [" +
                "{\"ticket\": 111, \"symbol\": \"XAUUSDmicro\", \"side\": \"buy\", \"volume\": 0.1, " +
                "\"price_open\": 4340.0, \"price_current\": 4345.0, \"profit\": 0.5}" +
                "]}");
            var positions = await client.GetPositionsAsync();
            Assert.Single(positions);
            Assert.Equal(111, positions[0].Ticket);
            Assert.Equal(0.5, positions[0].Profit, 6);

            fake.GetRoutes.Remove("positions");
            Assert.Empty(await client.GetPositionsAsync());
        }
    }

    [Fact]
    public async Task ClosePosition_Parses_Success_And_Propagates_Refusals()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.RoutePost("close/77", HttpStatusCode.OK, Json(
                "\"ok\": true", "\"retcode\": 10009", "\"retcode_name\": \"TRADE_DONE\""));
            var closed = await client.ClosePositionAsync(77);
            Assert.True(closed.Ok);
            Assert.Equal(77, closed.Order); // the requested ticket rides along
            Assert.Contains("POST close/77", fake.Requests);

            fake.RoutePost("close/77", HttpStatusCode.BadRequest, Json("\"error\": \"position gone\""));
            var withError = await Assert.ThrowsAsync<Mt5BridgeException>(
                () => client.ClosePositionAsync(77));
            Assert.Contains("position gone", withError.Message);

            fake.RoutePost("close/77", HttpStatusCode.BadRequest, Json("\"retcode\": 10006"));
            var noErrorField = await Assert.ThrowsAsync<Mt5BridgeException>(
                () => client.ClosePositionAsync(77));
            Assert.Contains("close refused", noErrorField.Message);
        }
    }

    [Fact]
    public async Task Deals_Parse_Fully_And_Degrade_Safely()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.Route("deals", "{\"deals\": [" +
                "{\"ticket\": 101, \"order\": 9, \"symbol\": \"XAUUSDmicro\", \"side\": \"buy\", " +
                "\"volume\": 0.5, \"price\": 2400.0, \"profit\": 12.5, \"commission\": -2.0, " +
                "\"swap\": -0.5, \"time\": 1790000000}," +
                "{\"ticket\": 102, \"symbol\": \"XAUUSDmicro\"}" +
                "]}");
            var deals = await client.GetDealsAsync(days: 7);
            Assert.Equal(2, deals.Count);
            Assert.Equal(101, deals[0].Ticket);
            Assert.Equal(-2.0, deals[0].Commission, 6);
            Assert.Equal(1790000000, deals[0].Time);
            Assert.Contains("days=7", fake.Requests[^1]);

            var sparse = deals[1];
            Assert.Equal(0, sparse.Order);      // absent -> defaults
            Assert.Equal("", sparse.Side);
            Assert.Equal(0, sparse.Volume, 6);
            Assert.Equal(0, sparse.Time);

            fake.Route("deals", Json("\"not_deals\": []")); // wrong shape
            Assert.Empty(await client.GetDealsAsync());

            fake.GetRoutes.Remove("deals");
            Assert.Empty(await client.GetDealsAsync());
        }
    }

    [Fact]
    public async Task Connection_Failure_Never_Throws_It_Returns_Null()
    {
        var (client, fake) = NewClient();
        using (client)
        {
            fake.ThrowOnNext = true;
            Assert.Null(await client.HealthAsync());
            Assert.Null(await client.GetAccountAsync());
            Assert.Empty(await client.GetSymbolsAsync());
        }
    }

    [Fact]
    public void Dispose_Owned_Client_But_Never_An_Injected_Handler()
    {
        var fake = new FakeBridge();

        // Injected handler: client disposal must leave the handler usable.
        var injected = new Mt5BridgeClient(fake, new Uri("http://127.0.0.1:53190/"));
        injected.Dispose();
        var second = new Mt5BridgeClient(fake, new Uri("http://127.0.0.1:53190/"));
        second.Dispose();

        // Owned client (production ctor): dispose without any request is safe.
        using var owned = new Mt5BridgeClient(port: 53191);
        Assert.True(owned.IsLoopback);
    }
}
