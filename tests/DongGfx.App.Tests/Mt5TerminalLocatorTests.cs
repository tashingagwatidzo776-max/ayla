using System;
using System.IO;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The MT5 terminal locator: with two MT5 installs on one machine the
/// order-routing bridge must target the SAME terminal the app uses — the
/// pinned Settings path wins when the file exists, the known install dirs
/// are probed in order otherwise, and an absent install yields null rather
/// than a wrong guess. The published data/mt5-bridge.json is what the
/// watchdog reads, so its shape is part of the contract.
/// </summary>
[Trait("Category", "Unit")]
public class Mt5TerminalLocatorTests
{
    [Fact]
    public void Find_HonorsPinnedPath_WhenTheFileExists()
    {
        var pin = Path.Combine(Path.GetTempPath(), "terminal64-pin-" + Guid.NewGuid() + ".exe");
        File.WriteAllText(pin, "stub");
        try
        {
            Assert.Equal(pin, Mt5TerminalLocator.Find(pin));
        }
        finally
        {
            File.Delete(pin);
        }
    }

    [Fact]
    public void Find_FallsBackToKnownInstall_AndNeverReturnsAMissingFile()
    {
        var found = Mt5TerminalLocator.Find(
            Path.Combine(Path.GetTempPath(), "no-such-terminal64-" + Guid.NewGuid() + ".exe"));

        if (found is not null)
        {
            // env-agnostic: when something is discovered it must really exist
            Assert.True(File.Exists(found));
            Assert.EndsWith("terminal64.exe", found, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Find_WithoutPin_IsEnvironmentHonest()
    {
        var found = Mt5TerminalLocator.Find(null);
        Assert.True(found is null || File.Exists(found));
    }

    [Fact]
    public void WriteConfig_PublishesTerminalPathAndPort_AsReadableJson()
    {
        var cfg = Path.Combine(Path.GetTempPath(), "mt5-bridge-" + Guid.NewGuid() + ".json");
        try
        {
            Mt5TerminalLocator.WriteConfig(@"C:\mt5	erminal64.exe", 53199, cfg);

            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            Assert.Equal(@"C:\mt5	erminal64.exe",
                doc.RootElement.GetProperty("terminalPath").GetString());
            Assert.Equal(53199, doc.RootElement.GetProperty("port").GetInt32());
        }
        finally
        {
            File.Delete(cfg);
        }
    }
}
