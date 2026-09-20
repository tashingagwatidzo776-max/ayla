using System.Text.Json;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The public market-data feed's pure parsing layer (unit-tested with no
/// transport): active-symbol listings parse tolerantly (direct or nested
/// array, number or boolean open flags, junk tolerated as null verdicts)
/// and tick messages parse in both nested and flat shapes with epoch
/// seconds converted to the app's epoch-milliseconds convention. The
/// endpoint constant is pinned so a silent upstream move cannot strand the
/// market-closed probe unnoticed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PublicMarketDataParsingTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Endpoint_IsThePublicNoAuthHost()
    {
        // The whole point of this client: a feed that needs no token. Pin
        // the documented endpoint so a rename can't silently break the
        // market-closed probe.
        Assert.Equal("wss://api.derivws.com/trading/v1/options/ws/public",
            PublicMarketDataClient.DefaultEndpoint);
    }

    [Fact]
    public void ParseActiveSymbols_DirectArray_ParsesOpenFlags()
    {
        var root = Parse("""
            {"active_symbols":[
                {"symbol":"R_100","display_name":"Random 100 Index","exchange_is_open":1},
                {"symbol":"FRXUSD","display_name":"USD Index","exchange_is_open":0}
            ]}
            """);

        var list = PublicMarketDataClient.ParseActiveSymbols(root);

        Assert.Equal(2, list.Count);
        Assert.Equal("R_100", list[0].Symbol);
        Assert.Equal("Random 100 Index", list[0].DisplayName);
        Assert.True(list[0].ExchangeIsOpen);
        Assert.Equal("FRXUSD", list[1].Symbol);
        Assert.False(list[1].ExchangeIsOpen);
    }

    [Fact]
    public void ParseActiveSymbols_NestedUnderData_Parses()
    {
        var root = Parse("""
            {"data":{"active_symbols":[
                {"symbol":"R_100","exchange_is_open":true}
            ]}}
            """);

        var list = PublicMarketDataClient.ParseActiveSymbols(root);

        var item = Assert.Single(list);
        Assert.Equal("R_100", item.Symbol);
        Assert.True(item.ExchangeIsOpen);
    }

    [Fact]
    public void ParseActiveSymbols_MissingOpenFlag_IsNullVerdict()
    {
        var root = Parse("""
            {"active_symbols":[{"symbol":"R_100","display_name":"n"}]}
            """);

        var list = PublicMarketDataClient.ParseActiveSymbols(root);

        var item = Assert.Single(list);
        Assert.Null(item.ExchangeIsOpen);
    }

    [Theory]
    [InlineData("""{"error":"no symbols"}""")]
    [InlineData("""{"active_symbols":[]}""")]
    [InlineData("""{"active_symbols":"brief"}""")]
    public void ParseActiveSymbols_NonArrayResponses_ReturnEmpty(string json)
    {
        Assert.Empty(PublicMarketDataClient.ParseActiveSymbols(Parse(json)));
    }

    [Fact]
    public void ParseActiveSymbols_JunkEntries_AreSkipped()
    {
        var root = Parse("""
            {"active_symbols":[
                {"display_name":"no symbol code"},
                {"symbol":"R_100","exchange_is_open":1},
                "not an object"
            ]}
            """);

        var list = PublicMarketDataClient.ParseActiveSymbols(root);

        var item = Assert.Single(list);
        Assert.Equal("R_100", item.Symbol);
    }

    [Fact]
    public void ParseTick_NestedShape_ParsesAndConvertsEpochSeconds()
    {
        // 1_700_000_000 epoch-seconds (≈1.7e9 → below the ms threshold).
        var root = Parse("""
            {"tick":{"symbol":"R_100","quote":1234.56,"epoch":1700000000,"pip_size":2}}
            """);

        var tick = PublicMarketDataClient.ParseTick(root);

        Assert.NotNull(tick);
        Assert.Equal("R_100", tick!.Symbol);
        Assert.Equal(1234.56, tick.Quote, 2);
        Assert.Equal(1_700_000_000_000L, tick.Epoch); // seconds → milliseconds
        Assert.Equal(2, tick.PipSize);
    }

    [Fact]
    public void ParseTick_FlatShape_AlreadyMilliseconds_Kept()
    {
        var root = Parse("""
            {"symbol":"R_100","quote":100.5,"epoch":1700000000000}
            """);

        var tick = PublicMarketDataClient.ParseTick(root);

        Assert.NotNull(tick);
        Assert.Equal(1_700_000_000_000L, tick!.Epoch); // ms values pass through
    }

    [Fact]
    public void ParseTick_StringQuote_Parses()
    {
        var root = Parse("""
            {"tick":{"symbol":"R_100","quote":"42.25","epoch":1700000000}}
            """);

        var tick = PublicMarketDataClient.ParseTick(root);

        Assert.NotNull(tick);
        Assert.Equal(42.25, tick!.Quote, 2);
    }

    [Theory]
    [InlineData("""{"error":"MarketIsClosed"}""")]
    [InlineData("""{"tick":{"symbol":"","quote":1,"epoch":1}}""")]
    [InlineData("""{"tick":{"symbol":"R_100","quote":0,"epoch":1}}""")]
    [InlineData("""{"ping":1}""")]
    public void ParseTick_NonTickMessages_ReturnNull(string json)
    {
        Assert.Null(PublicMarketDataClient.ParseTick(Parse(json)));
    }
}
