using System;
using System.IO;
using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// TF_DATA_DIR redirection: the test harness (scripts/ci-local.ps1) sets it
/// to a scratch directory so the DataDir-touching suites — whose teardowns
/// DELETE SettingsService.DataDir — can never destroy the real session's
/// journal and settings. The resolver is pure, so the resolution rules are
/// pinned here WITHOUT mutating process environment (xUnit runs suites in
/// parallel; a global env flip would leak into them).
/// </summary>
[Trait("Category", "Unit")]
public class SettingsDataDirOverrideTests
{
    [Fact]
    public void EnvVar_Name_Is_The_Documented_One() =>
        Assert.Equal("TF_DATA_DIR", SettingsService.DataDirEnvVar);

    [Fact]
    public void No_Override_Falls_Back_To_AppData_Tf_Data()
    {
        var appDataBase = Path.Combine("C:", "Users", "anyone", "AppData", "Roaming");
        Assert.Equal(Path.Combine(appDataBase, "tf", "data"),
            SettingsService.ResolveDataDir(null, appDataBase));

        // Blank strings of any kind are not an override.
        Assert.Equal(Path.Combine("C:", "base", "tf", "data"),
            SettingsService.ResolveDataDir("", "C:\\base"));
        Assert.Equal(Path.Combine("C:", "base", "tf", "data"),
            SettingsService.ResolveDataDir("   ", "C:\\base"));
    }

    [Theory]
    [InlineData("D:\\tf-scratch", "D:\\tf-scratch")]
    [InlineData("  D:\\tf-scratch  ", "D:\\tf-scratch")]   // trimmed
    [InlineData("relative-scratch", "relative-scratch")]  // verbatim, resolved by callers
    public void Override_Wins_As_The_Full_Data_Dir(string raw, string expected) =>
        Assert.Equal(expected, SettingsService.ResolveDataDir(raw, "C:\\base"));
}
