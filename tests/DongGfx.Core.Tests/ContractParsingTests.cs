using System.Text.Json;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Contract-snapshot parsing must tolerate BOTH wire shapes: classic v3
/// (native JSON numbers/booleans, entry_tick_time/exit_tick_time) and the
/// new platform (numeric contract_id, string scalars "582.76"/"-1.00",
/// 0/1 integer flags, entry_spot_time/exit_spot_time/sell_time). The new
/// shape previously threw on GetBoolean()/GetDecimal() — the settlement
/// loop never returned, engines restart-looped, and no trade was ever
/// journaled (the Sep 20-21 incident).
/// </summary>
[Trait("Category", "Unit")]
public class ContractParsingTests
{
    private static ContractInfo Parse(string json) =>
        DerivClient.ParseContract(JsonDocument.Parse(json).RootElement, "fallback-id");

    [Fact]
    public void NewPlatformShape_NumericId_StringScalars_IntFlags()
    {
        var info = Parse("""
        {
            "contract_id": 13801651499,
            "status": "lost",
            "is_sold": 1,
            "is_expired": 1,
            "entry_spot": "582.51",
            "exit_spot": "582.76",
            "entry_spot_time": 1789968000,
            "exit_spot_time": 1789968684,
            "buy_price": "1.00",
            "sell_price": "0.00",
            "profit": "-1.00",
            "profit_percentage": -100,
            "currency": "USD"
        }
        """);

        Assert.Equal("13801651499", info.ContractId);
        Assert.Equal(ContractStatus.Lost, info.Status);
        Assert.True(info.IsSold);
        Assert.Equal(582.51, info.EntrySpot);
        Assert.Equal(582.76, info.ExitSpot);
        Assert.Equal(1789968000, info.EntryTime);
        Assert.Equal(1789968684, info.ExitTime);
        Assert.Equal(1.00m, info.BuyPrice);
        Assert.Equal(-1.00m, info.Profit);
    }

    [Fact]
    public void ClassicShape_Booleans_NativeNumbers_TickTimes()
    {
        var info = Parse("""
        {
            "contract_id": "classic-123",
            "status": "won",
            "is_sold": true,
            "entry_spot": 1.14816,
            "exit_spot": 1.14900,
            "entry_tick_time": 1700000000,
            "exit_tick_time": 1700000300,
            "buy_price": 1.0,
            "profit": 0.85,
            "currency": "USD"
        }
        """);

        Assert.Equal("classic-123", info.ContractId);
        Assert.Equal(ContractStatus.Won, info.Status);
        Assert.True(info.IsSold);
        Assert.Equal(1.14816, info.EntrySpot);
        Assert.Equal(0.85m, info.Profit);
        Assert.Equal(1700000000, info.EntryTime);
        Assert.Equal(1700000300, info.ExitTime);
    }

    [Fact]
    public void OpenContract_NotSold_MapsOpen()
    {
        var info = Parse("""
        {
            "contract_id": 42,
            "status": "open",
            "is_sold": 0,
            "entry_spot": "582.51"
        }
        """);

        Assert.Equal(ContractStatus.Open, info.Status);
        Assert.False(info.IsSold);
        Assert.Equal(0, info.ExitSpot);
        Assert.Equal(0, info.ExitTime);
    }

    [Fact]
    public void SoldWithoutStatus_FallsBackToSold()
    {
        // A sold snapshot whose status field is absent must still settle:
        // WaitForSettlement polls on IsSold.
        var info = Parse("""
        {
            "contract_id": 7,
            "is_sold": "1",
            "profit": "0.85"
        }
        """);

        Assert.True(info.IsSold);
        Assert.Equal(0.85m, info.Profit);
    }

    [Fact]
    public void MalformedScalars_NeverThrow_ParseAsZero()
    {
        var info = Parse("""
        {
            "contract_id": "x",
            "status": "open",
            "is_sold": false,
            "entry_spot": "not-a-number",
            "profit": null
        }
        """);

        Assert.Equal(0, info.EntrySpot);
        Assert.Equal(0m, info.Profit);
    }
}
