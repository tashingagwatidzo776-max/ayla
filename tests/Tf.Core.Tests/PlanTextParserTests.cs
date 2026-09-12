using Tf.Core.Brain;

namespace Tf.Core.Tests;

/// <summary>
/// Tests for the headless Growth plan free-text parser (extracted from the
/// Growth tab view model / GrowthBrain).
/// </summary>
[Trait("Category", "Unit")]
public class PlanTextParserTests
{
    [Theory]
    [InlineData("150", 150)]
    [InlineData("$150", 150)]
    [InlineData(" $150 ", 150)]
    [InlineData("1,000", 1000)]
    [InlineData("$1,250.50", 1250.50)]
    [InlineData("2.5", 2.5)]
    public void TryCreateDrawdownCap_ValidText_ParsesValue(string text, decimal expected)
    {
        Assert.Equal(expected, PlanTextParser.TryCreateDrawdownCap(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("$0")]
    [InlineData("-5")]
    [InlineData("1.2.3")]
    public void TryCreateDrawdownCap_InvalidText_ReturnsNull(string? text)
    {
        Assert.Null(PlanTextParser.TryCreateDrawdownCap(text));
    }

    [Fact]
    public void GrowthPlan_TryCreateDrawdownCap_DelegatesToParser()
    {
        // The GrowthPlan facade must stay in sync with the headless parser.
        Assert.Equal(PlanTextParser.TryCreateDrawdownCap("$99"), GrowthPlan.TryCreateDrawdownCap("$99"));
    }
}
