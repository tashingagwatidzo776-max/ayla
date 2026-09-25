using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.ViewModels;
using DongGfx.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Coverage sprint over the largest remaining gaps: the settings store's
/// load branches (missing file, corrupt JSON, happy round-trip), the
/// composition root's property routing and shutdown ordering, and the
/// Terminal row records' UI-facing computed properties. The real settings
/// file is backed up and restored around every test — the deployed app is
/// running and owns that file in production.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
// Serialized against the other DataDir-touching suites (see
// SharedDataDirCollection.cs): these tests delete and rewrite the real
// settings file under %APPDATA% and must not race the DI-graph suites.
[Collection("Shared-DataDir-Directory")]
public class CoverageSprintTests : IDisposable
{

    private static readonly string SettingsPath = Path.Combine(
        SettingsService.DataDir, "settings.json");

    private readonly string? _backup;
    private readonly bool _hadFile;
    private ServiceProvider? _provider;

    public CoverageSprintTests()
    {
        if (File.Exists(SettingsPath))
        {
            _hadFile = true;
            _backup = SettingsPath + ".covsprint.bak";
            File.Copy(SettingsPath, _backup, overwrite: true);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_hadFile && _backup is not null)
            {
                File.Copy(_backup, SettingsPath, overwrite: true);
                File.Delete(_backup);
            }
            else if (File.Exists(SettingsPath))
            {
                File.Delete(SettingsPath);
            }
        }
        catch (IOException) { /* best-effort restore */ }

        _provider?.Dispose();
    }

    // ── SettingsService: load branches + save round-trip ──────────────

    [Fact]
    public void Settings_Load_MissingFile_ReturnsDefaults()
    {
        if (File.Exists(SettingsPath))
        {
            File.Delete(SettingsPath);
        }

        var settings = new SettingsService().Load();

        Assert.True(settings.IsDemo);
        Assert.Equal("XAUUSD", settings.FxSymbol);
        Assert.Equal(1.00m, settings.Mt5MaxLots);
    }

    [Fact]
    public void Settings_Load_CorruptJson_ReturnsDefaults_NotThrow()
    {
        Directory.CreateDirectory(SettingsService.DataDir);
        File.WriteAllText(SettingsPath, "{ this is not json !!!");

        var settings = new SettingsService().Load();

        // Corrupt file must fall back to safe defaults (demo, capped lots),
        // never throw and never half-populate.
        Assert.True(settings.IsDemo);
        Assert.Equal(1.00m, settings.Mt5MaxLots);
    }

    [Fact]
    public void Settings_SaveThenLoad_RoundTripsAllRiskFields()
    {
        var service = new SettingsService();
        var original = new AppSettings
        {
            IsDemo = false,
            Mt5MaxLots = 0.42m,
            Mt5DailyLossCap = 33.5m,
            Mt5EquityFloor = 150m,
            FxSymbol = "EURUSD",
            FxSymbols = "EURUSD,GBPUSD",
            FxPortfolioMaxLots = 0.25m,
            NewsBlackoutMinutes = 42,
            WebhookOnTrade = false,
            ArmStalenessHours = 9,
            LogLevel = 2,
        };

        service.Save(original);
        var loaded = service.Load();

        Assert.Equal(original.IsDemo, loaded.IsDemo);
        Assert.Equal(original.Mt5MaxLots, loaded.Mt5MaxLots);
        Assert.Equal(original.Mt5DailyLossCap, loaded.Mt5DailyLossCap);
        Assert.Equal(original.Mt5EquityFloor, loaded.Mt5EquityFloor);
        Assert.Equal(original.FxSymbol, loaded.FxSymbol);
        Assert.Equal(original.FxSymbols, loaded.FxSymbols);
        Assert.Equal(original.FxPortfolioMaxLots, loaded.FxPortfolioMaxLots);
        Assert.Equal(original.NewsBlackoutMinutes, loaded.NewsBlackoutMinutes);
        Assert.Equal(original.WebhookOnTrade, loaded.WebhookOnTrade);
        Assert.Equal(original.ArmStalenessHours, loaded.ArmStalenessHours);
        Assert.Equal(original.LogLevel, loaded.LogLevel);
    }

    // ── MainViewModel: composition root routing + shutdown ────────────

    /// <summary>Resolves the real composition root from the real DI graph
    /// (same wiring the app runs) — a hand-built graph would drift from
    /// production. The graph is disposed with the test class.</summary>
    private MainViewModel BuildMain(AppSettings settings)
    {
        _provider = AppStartupWiringTests.BuildGraph(settings);
        return _provider.GetRequiredService<MainViewModel>();
    }

    [Fact]
    public void MainViewModel_Properties_RouteTheirViewModels()
    {
        var settings = new AppSettings { FxSymbol = "XAUUSDmicro" };
        var main = BuildMain(settings);

        Assert.NotNull(main.Dashboard);
        Assert.NotNull(main.SettingsVm);
        Assert.NotNull(main.JournalVm);
        Assert.NotNull(main.UpdateVm);
        Assert.NotNull(main.PerformanceVm);
        Assert.NotNull(main.TerminalVm);
    }

    [Fact]
    public void MainViewModel_Shutdown_StopsTheFxBrain_WithoutThrowingWhenIdle()
    {
        var main = BuildMain(new AppSettings());

        // Shutdown on a never-started VM must be a clean no-op (window-close
        // teardown runs on every exit, started or not) and leave no host behind.
        main.Shutdown();

        Assert.Null(main.TerminalVm.FxHost);
    }

    // ── Terminal row records: computed UI properties ──────────────────

    [Fact]
    public void TerminalTradeRow_CloseButton_VisibilityFollowsMt5Ticket()
    {
        var mt5 = new TerminalTradeRow("1", "XAUUSDmicro", "BUY", 0.1,
            2650.0, 2651.0, 1.0, "MT5", Mt5Ticket: 123456);
        var internal_ = new TerminalTradeRow("2", "XAUUSDmicro", "SELL", 0.1,
            2650.0, 2649.0, 1.0, "FxBrain");

        Assert.Equal(System.Windows.Visibility.Visible, mt5.Mt5CloseVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, internal_.Mt5CloseVisibility);
    }

    [Fact]
    public void TerminalExposureRow_CarriesNetVolumeAndOpenProfit()
    {
        var row = new TerminalExposureRow("XAUUSDmicro", 0.3, -12.5);

        Assert.Equal("XAUUSDmicro", row.Symbol);
        Assert.Equal(0.3, row.NetVolume);
        Assert.Equal(-12.5, row.OpenProfit);
    }
}
