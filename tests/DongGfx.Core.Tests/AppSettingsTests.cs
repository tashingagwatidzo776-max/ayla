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
        Assert.Equal("frxEURUSD", s.Symbol);
        Assert.False(s.AutonomyEnabled);
        Assert.Equal(10.00m, s.MaxStake);
        Assert.Equal(1, s.MaxConcurrentContracts);
        Assert.Equal(50.00m, s.DailyLossCap);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var original = new AppSettings
        {
            ApiToken = "secret",
            AppId = "12345",
            Symbol = "frxGBPUSD",
            AutonomyEnabled = true,
            Stake = 2.50m,
            DurationMinutes = 10
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal("12345", restored!.AppId);
        Assert.Equal("frxGBPUSD", restored.Symbol);
        Assert.True(restored.AutonomyEnabled);
        Assert.Equal(2.50m, restored.Stake);
        Assert.Equal(10, restored.DurationMinutes);
    }
}