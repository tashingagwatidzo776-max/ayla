using Tf.App.Infrastructure;

namespace Tf.App.Tests;

/// <summary>
/// Headless tests for the first-run wizard's extracted logic (token parsing,
/// brain-key mapping, budget parsing). A WPF Window cannot be constructed in
/// a unit test, so the pure decision logic lives in FirstRunWizardLogic and
/// the code-behind merely wires controls to it.
/// </summary>
[Trait("Category", "Unit")]
public class FirstRunWizardLogicTests
{
    // ─── Brain key mapping ─────────────────────────────────

    [Theory]
    [InlineData(0, "Growth")]
    [InlineData(1, "TrendFollowing")]
    [InlineData(2, "Breakout")]
    [InlineData(3, "MeanReversion")]
    public void BrainKeyForIndex_KnownIndices_MapToRegistryKeys(int index, string expected)
    {
        Assert.Equal(expected, FirstRunWizardLogic.BrainKeyForIndex(index));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void BrainKeyForIndex_OutOfRange_FallsBackToGrowth(int index)
    {
        Assert.Equal("Growth", FirstRunWizardLogic.BrainKeyForIndex(index));
    }

    // ─── Budget parsing ────────────────────────────────────

    [Theory]
    [InlineData("10", 10.00)]
    [InlineData("7.50", 7.50)]
    [InlineData("  3.25  ", 3.25)] // whitespace-tolerant (decimal.TryParse trims)
    [InlineData("$5", 5.00)]       // currency symbol ignored by TryParse... actually fails → default
    public void ParseBudget_ValidText_ParsesValue(string text, decimal expected)
    {
        if (text == "$5")
        {
            // "$5" does not parse invariantly → the default applies.
            Assert.Equal(5.00m, FirstRunWizardLogic.ParseBudget(text));
        }
        else
        {
            Assert.Equal(expected, FirstRunWizardLogic.ParseBudget(text));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-2")]   // negative budgets make no sense → default
    [InlineData("0")]    // zero budget cannot trade → default
    public void ParseBudget_InvalidOrNonPositive_FallsBackToDefault(string? text)
    {
        Assert.Equal(5.00m, FirstRunWizardLogic.ParseBudget(text));
    }

    // ─── Token validation ──────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]        // too short
    [InlineData("1234567")]    // exactly under the 8-char floor
    public void IsTokenAcceptable_MissingOrTooShort_IsFalse(string? token)
    {
        Assert.False(FirstRunWizardLogic.IsTokenAcceptable(token));
    }

    [Theory]
    [InlineData("12345678")]                          // exactly 8 chars
    [InlineData("a1b2c3d4e5f6g7h8")]                  // realistic Deriv token shape
    [InlineData("  a1b2c3d4  ")]                      // surrounding whitespace is fine
    public void IsTokenAcceptable_LongEnough_IsTrue(string token)
    {
        Assert.True(FirstRunWizardLogic.IsTokenAcceptable(token));
    }
}
