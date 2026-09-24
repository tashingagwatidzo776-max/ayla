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
    }
}
