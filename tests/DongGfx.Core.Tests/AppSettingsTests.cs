using System.Text.Json;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class AppSettingsTests
{
    [Fact]
    public void Defaults_AreDemoSafe()
    {
        var s = new AppSettings();

        Assert.True(s.IsDemo);
        Assert.Equal("Dark", s.Theme);   // modern theme is the default
        Assert.Equal("XAUUSD", s.FxSymbol);
        Assert.False(s.AutonomyEnabled);
        Assert.Equal(1.00m, s.Mt5MaxLots);
        Assert.Equal(25m, s.Mt5DailyLossCap);
        Assert.Equal(0m, s.Mt5EquityFloor);
        Assert.Equal("", s.Mt5TerminalPath);
        Assert.True(s.WebhookOnTrade);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var original = new AppSettings
        {
            IsDemo = false,
            AutonomyEnabled = true,
            FxSymbol = "XAUUSDmicro",
            FxSymbols = "XAUUSDmicro,EURUSD",
            Mt5TerminalPath = @"C:\Users\me\Desktop\tf\mt5_portable\terminal64.exe",
            Mt5MaxLots = 0.25m,
            Tp1PlanPctOverride = 30,
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.IsDemo);
        Assert.True(restored.AutonomyEnabled);
        Assert.Equal("XAUUSDmicro", restored.FxSymbol);
        Assert.Equal("XAUUSDmicro,EURUSD", restored.FxSymbols);
        Assert.Equal(@"C:\Users\me\Desktop\tf\mt5_portable\terminal64.exe", restored.Mt5TerminalPath);
        Assert.Equal(0.25m, restored.Mt5MaxLots);
        Assert.Equal(30, restored.Tp1PlanPctOverride);
    }

    [Fact]
    public void PlanPctOverride_DefaultsNull_AndDistinguishes_AbsentFromZero()
    {
        // null = the brain's own allocation plan; an armed override is a
        // standing operator decision that must survive a save/load. 0 is a
        // deliberate "do not take a partial", NOT the same as "unset" — so
        // the round-trip must preserve a genuine zero.
        Assert.Null(new AppSettings().Tp1PlanPctOverride);

        var json = JsonSerializer.Serialize(new AppSettings { Tp1PlanPctOverride = 0 });
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal(0, restored!.Tp1PlanPctOverride);   // present, and zero
    }

    [Fact]
    public void MinEntryConfidence_DefaultsOff_And_RoundTrips()
    {
        // 0 = the historical behavior (every qualifying signal dispatches);
        // a persisted floor is an operator decision and must survive a
        // save/load exactly (0.7 → 0.7, not defaulted away).
        Assert.Equal(0, new AppSettings().FxMinEntryConfidence);

        var json = JsonSerializer.Serialize(new AppSettings { FxMinEntryConfidence = 0.7 });
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal(0.7, restored!.FxMinEntryConfidence);
    }
}
