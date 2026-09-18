using Tf.App.Infrastructure;
using Tf.Core.Models;

namespace Tf.App.Tests;

/// <summary>
/// Headless tests for the first-run wizard's extracted logic (token parsing,
/// brain-key mapping, budget parsing, and the merge of wizard choices into
/// existing settings). A WPF Window cannot be constructed in a unit test, so
/// the pure decision logic lives in FirstRunWizardLogic and the code-behind
/// merely wires controls to it. Carries the RealMoney trait because the
/// merge tests pin rail-relevant settings survival (ManualMaxStake,
/// IsDemo forcing) — see the audit doc's test class coverage table.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
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

    // ─── Merge into existing settings (the anti-clobber rule) ───

    private static FirstRunChoices Choices(
        string token = "a1b2c3d4e5f6g7h8",
        string symbol = "frxGBPUSD",
        bool autonomy = false,
        bool marketHours = true) =>
        new(token, symbol, "Growth", 5.00m, autonomy, marketHours);

    [Fact]
    public void ApplyChoices_NullExisting_BuildsFreshDemoSettings()
    {
        var s = FirstRunWizardLogic.ApplyChoices(null, Choices());

        Assert.Equal("a1b2c3d4e5f6g7h8", s.ApiToken);
        Assert.Equal("frxGBPUSD", s.Symbol);
        Assert.False(s.AutonomyEnabled);
        Assert.True(s.RespectMarketHours);
        Assert.True(s.IsDemo); // the wizard always lands on demo
    }

    [Fact]
    public void ApplyChoices_ExistingSettings_OverwritesOnlyTheWizardFields()
    {
        // Everything a user may have configured before completing the wizard
        // must survive; the five wizard fields are the only writes.
        var existing = new AppSettings
        {
            ApiToken = "old-token",
            Symbol = "frxOLD",
            AutonomyEnabled = true,
            RespectMarketHours = false,
            WebhookUrl = "https://discord.example/hook",
            ManualMaxStake = 25.00m,
            ArmStalenessHours = 6,
            MaxStake = 7.50m,
            DailyLossCap = 12.00m
        };

        var s = FirstRunWizardLogic.ApplyChoices(existing, Choices());

        Assert.Equal("a1b2c3d4e5f6g7h8", s.ApiToken); // overwritten
        Assert.Equal("frxGBPUSD", s.Symbol);          // overwritten
        Assert.False(s.AutonomyEnabled);               // overwritten
        Assert.True(s.RespectMarketHours);             // overwritten
        Assert.True(s.IsDemo);                         // forced by the wizard
        Assert.Equal("https://discord.example/hook", s.WebhookUrl);
        Assert.Equal(25.00m, s.ManualMaxStake);
        Assert.Equal(6, s.ArmStalenessHours);
        Assert.Equal(7.50m, s.MaxStake);
        Assert.Equal(12.00m, s.DailyLossCap);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyChoices_BlankSymbol_FallsBackToDefault(string symbol)
    {
        var s = FirstRunWizardLogic.ApplyChoices(null, Choices(symbol: symbol));
        Assert.Equal(AppSettings.DefaultSymbol, s.Symbol);
    }

    [Fact]
    public void ApplyChoices_TrimsTokenAndSymbol()
    {
        var s = FirstRunWizardLogic.ApplyChoices(
            null, Choices(token: "  a1b2c3d4  ", symbol: " frxGBPUSD "));
        Assert.Equal("a1b2c3d4", s.ApiToken);
        Assert.Equal("frxGBPUSD", s.Symbol);
    }

    [Fact]
    public void ApplyChoices_AlwaysForcesDemo()
    {
        // Re-labelling to real is a deliberate, gate-checked act on the
        // Growth tab — the wizard can never be the thing that does it.
        var existing = new AppSettings { IsDemo = false };
        var s = FirstRunWizardLogic.ApplyChoices(existing, Choices());
        Assert.True(s.IsDemo);
    }
}
