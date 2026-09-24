using System.IO;
using System.Threading;
using System.Windows;
using DongGfx.App.Infrastructure;
using DongGfx.App.ViewModels;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// SettingsViewModel's live webhook validation, mode label, quiet-save path
/// and busy guard; DashboardViewModel's risk-rail alerting, governor
/// states, kill-switch toggle, and push APIs (ReportFxStatus, SetSymbol,
/// SetBalance, AddHistory) — all without WPF dialogs or a webhook endpoint.
/// </summary>
public class DashboardAndSettingsCoverageTests : IDisposable
{
    // SaveSettingsQuietAsync writes the REAL %APPDATA%\tf\data\settings.json;
    // back it up and restore it so tests never corrupt the running app's config.
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data", "settings.json");
    private readonly byte[]? _settingsBackup = File.Exists(SettingsFile) ? File.ReadAllBytes(SettingsFile) : null;

    public void Dispose()
    {
        try
        {
            if (_settingsBackup is not null) File.WriteAllBytes(SettingsFile, _settingsBackup);
            else File.Delete(SettingsFile);
        }
        catch { /* best effort */ }
    }

    // ── SettingsViewModel ─────────────────────────────────────────────

    [Fact]
    public void ModeLabel_Follows_The_Demo_Flag()
    {
        var vm = new SettingsViewModel(new SettingsService());
        Assert.Equal("Demo", vm.ModeLabel);

        vm.IsDemo = false;
        Assert.Equal("Real", vm.ModeLabel);
    }

    [Fact]
    public void Webhook_Validation_Tracks_Url_And_Platform()
    {
        var vm = new SettingsViewModel(new SettingsService());
        Assert.Equal(WebhookUrlSeverity.None, vm.WebhookUrlSeverity);   // empty = optional

        vm.WebhookUrl = "not-a-url";
        Assert.Equal(WebhookUrlSeverity.Error, vm.WebhookUrlSeverity);
        Assert.Contains("valid", vm.WebhookValidationMessage, StringComparison.OrdinalIgnoreCase);

        vm.IsDiscordWebhook = true;   // platform change revalidates
        Assert.Equal(WebhookUrlSeverity.Error, vm.WebhookUrlSeverity);

        vm.WebhookUrl = "https://example.com/hook";
        Assert.NotEqual(WebhookUrlSeverity.Error, vm.WebhookUrlSeverity);
    }

    [Fact]
    public async Task QuietSave_Persists_And_Reports_Success()
    {
        var vm = new SettingsViewModel(new SettingsService());
        vm.Load(new AppSettings { IsDemo = true, FxSymbol = "XAUUSDmicro" });

        await vm.SaveSettingsQuietCommand.ExecuteAsync(null);

        Assert.Equal("Settings updated from the Terminal.", vm.StatusMessage);
        Assert.True(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Save_Busy_Guard_Skips_And_Happy_Path_Saves()
    {
        // SaveAsync is XAML-bound (no RelayCommand): drive it by reflection.
        // IsDemo stays true so the real-money MessageBox is never shown.
        var vm = new SettingsViewModel(new SettingsService());
        vm.Load(new AppSettings { IsDemo = true });
        var saveAsync = vm.GetType().GetMethod("SaveAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        vm.IsBusy = true;
        await (Task)saveAsync.Invoke(vm, null)!;
        Assert.Equal("Settings loaded.", vm.StatusMessage);   // guard returned early

        vm.IsBusy = false;
        await (Task)saveAsync.Invoke(vm, null)!;
        Assert.Contains("saved", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── DashboardViewModel ────────────────────────────────────────────

    [Fact]
    public void ReportFxStatus_Drives_Governor_Rails_And_Pnl_Text()
    {
        var dashboard = new DashboardViewModel();

        dashboard.ReportFxStatus("cycle ok", halted: false, warned: false, dayPnl: 12.5m);
        Assert.Equal("cycle ok", dashboard.FxStatusText);
        Assert.False(dashboard.IsGovernorLatched);
        Assert.False(dashboard.IsGovernorWarned);
        Assert.Equal("+$12.50", dashboard.CombinedGrowthPnlText);
        Assert.Empty(dashboard.RiskRailAlerts);

        dashboard.ReportFxStatus("warning zone", halted: false, warned: true, dayPnl: -3m);
        Assert.True(dashboard.IsGovernorWarned);
        Assert.Contains(dashboard.RiskRailAlerts, a => a.Contains("drawdown warning"));

        dashboard.ReportFxStatus("loss stop", halted: true, warned: true, dayPnl: null);
        Assert.True(dashboard.IsGovernorLatched);
        Assert.False(dashboard.IsGovernorWarned);   // warned yields to latched
        Assert.Contains(dashboard.RiskRailAlerts, a => a.Contains("loss stop latched"));
        Assert.DoesNotContain(dashboard.RiskRailAlerts, a => a.Contains("drawdown"));
    }

    [Fact]
    public void KillSwitch_Toggle_Engages_And_Releases_With_Status_Lines()
    {
        var dashboard = new DashboardViewModel();

        dashboard.ToggleKillSwitchCommand.Execute(null);
        Assert.True(dashboard.IsKillSwitchEngaged);
        Assert.Contains("KILL SWITCH ENGAGED", dashboard.StatusText);
        Assert.Contains(dashboard.RiskRailAlerts, a => a.Contains("kill switch"));

        dashboard.ToggleKillSwitchCommand.Execute(null);
        Assert.False(dashboard.IsKillSwitchEngaged);
        Assert.Contains("re-armed", dashboard.StatusText);
        Assert.DoesNotContain(dashboard.RiskRailAlerts, a => a.Contains("kill switch"));
    }

    [Fact]
    public void PushApis_Update_Texts_And_History_Safely()
    {
        var dashboard = new DashboardViewModel();

        dashboard.SetSymbol("XAUUSDmicro");
        Assert.Equal("XAUUSDmicro", dashboard.SymbolText);

        dashboard.SetBalance("$2,729.34");
        Assert.Equal("$2,729.34", dashboard.BalanceText);

        // Chart is null in tests — AddHistory must stay a safe no-op there
        // while still updating the readouts.
        var tick = new Tick("XAUUSDmicro", 2650.55, 2650.65, 2650.45,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 2);
        dashboard.AddHistory(new[] { tick });
        Assert.Equal("2650.55000", dashboard.LastPriceText);
        Assert.Equal("1 ticks", dashboard.TickCountText);

        dashboard.AddHistory(Array.Empty<Tick>());
        Assert.Equal("2650.55000", dashboard.LastPriceText);   // unchanged
    }

    [Fact]
    public void ThemeToggle_RoundTrips_And_Persists_BuildSettings()
    {
        var svc = new SettingsService();
        var vm = new SettingsViewModel(svc);
        vm.Load(svc.Load());
        Assert.Equal("Dark", vm.Theme);   // default on any machine

        vm.Theme = "Classic";
        Assert.Equal("Classic", vm.BuildSettings().Theme);
        svc.Save(vm.BuildSettings());

        var vm2 = new SettingsViewModel(svc);
        vm2.Load(svc.Load());
        Assert.Equal("Classic", vm2.Theme);   // round-trips through disk

        // Restore the machine default so other tests/users are unaffected.
        vm.Theme = "Dark";
        svc.Save(vm.BuildSettings());
    }

    [Fact]
    public void ThemeManager_Normalizes_Unknown_To_Dark()
    {
        Assert.Equal("Dark", ThemeManager.Normalize("dark"));
        Assert.Equal("Classic", ThemeManager.Normalize("CLASSIC"));
        Assert.Equal("Dark", ThemeManager.Normalize(""));
        Assert.Equal("Dark", ThemeManager.Normalize(null));
        Assert.Equal("Dark", ThemeManager.Normalize("neon-pink"));
    }

    [Fact]
    public void ThemeDictionaries_Expose_The_Same_Resource_Keys()
    {
        // Both themes must declare every key the app binds — a missing key
        // is a runtime XAML crash on switch, so this is a contract test.
        // Keys are parsed straight from the theme source files (pack URIs
        // don't resolve inside the test host).
        var dir = AppContext.BaseDirectory;
        string? root = null;
        var probe = dir;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            if (File.Exists(Path.Combine(probe, "DongGfx.sln")))
            {
                root = probe;
                break;
            }

            probe = Path.GetDirectoryName(probe);
        }

        if (root is null)
        {
            return;   // not running from a checkout (published artifacts) — skip
        }

        static System.Collections.Generic.HashSet<string> Keys(string path) =>
            System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(path), "x:Key=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

        var dark = Keys(Path.Combine(root, "src", "DongGfx.App", "Theme", "Dark.xaml"));
        var classic = Keys(Path.Combine(root, "src", "DongGfx.App", "Theme", "Classic.xaml"));

        Assert.NotEmpty(dark);
        Assert.Equal(dark, classic);
    }
}
