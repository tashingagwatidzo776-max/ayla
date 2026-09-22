using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class TickTests
{
    [Fact]
    public void FromJson_PopulatesAllFields()
    {
        var tick = Tick.FromJson("frxEURUSD", 1.23525, 1.2354, 1.2351, 1_700_000_000_000, 5);

        Assert.Equal("frxEURUSD", tick.Symbol);
        Assert.Equal(1.23525, tick.Quote);
        Assert.Equal(1.2354, tick.Ask);
        Assert.Equal(1.2351, tick.Bid);
        Assert.Equal(1_700_000_000_000, tick.Epoch);
        Assert.Equal(5, tick.PipSize);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            tick.Time);
    }

    [Fact]
    public void Ticks_AreValueEqual()
    {
        var a = Tick.FromJson("frxEURUSD", 1.23, 1.24, 1.22, 1, 5);
        var b = Tick.FromJson("frxEURUSD", 1.23, 1.24, 1.22, 1, 5);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}