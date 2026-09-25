using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.ViewModels;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The login dialog's prefill round-trip: the last successful account
/// switch persists its account id and server through the settings store
/// so the next dialog opens prefilled. The password is deliberately not
/// part of any of this — it must never be persisted anywhere.
/// </summary>
[Trait("Category", "Unit")]
public class LoginPrefillSettingsTests
{
    private static (SettingsViewModel Vm, SettingsService Service) NewVm()
    {
        var service = new SettingsService();
        return (new SettingsViewModel(service), service);
    }

    [Fact]
    public void Load_Then_BuildSettings_Preserves_Last_Login_And_Server()
    {
        var (vm, service) = NewVm();
        try
        {
            var settings = new AppSettings
            {
                Mt5LastLogin = "201587365",
                Mt5LastServer = "Deriv-Demo",
            };

            vm.Load(settings);
            var built = vm.BuildSettings();

            Assert.Equal("201587365", built.Mt5LastLogin);
            Assert.Equal("Deriv-Demo", built.Mt5LastServer);
        }
        finally
        {
            try { Directory.Delete(SettingsService.DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Editor_Whitespace_Is_Trimmed_By_BuildSettings()
    {
        var (vm, _) = NewVm();
        try
        {
            vm.Load(new AppSettings());
            vm.Mt5LastLogin = " 201587365 ";
            vm.Mt5LastServer = " Deriv-Demo ";

            var built = vm.BuildSettings();

            Assert.Equal("201587365", built.Mt5LastLogin);
            Assert.Equal("Deriv-Demo", built.Mt5LastServer);
        }
        finally
        {
            try { Directory.Delete(SettingsService.DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Defaults_Are_Empty_Both_In_Settings_And_The_Built_Object()
    {
        var (vm, _) = NewVm();
        try
        {
            Assert.Equal("", new AppSettings().Mt5LastLogin);
            Assert.Equal("", new AppSettings().Mt5LastServer);

            vm.Load(new AppSettings());
            var built = vm.BuildSettings();

            Assert.Equal("", built.Mt5LastLogin);
            Assert.Equal("", built.Mt5LastServer);
        }
        finally
        {
            try { Directory.Delete(SettingsService.DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── RecordMt5Login: the two-slot Recent history ────────────────────

    [Fact]
    public void Record_Login_Shifts_Last_Into_Prev_And_Keeps_Both()
    {
        var (vm, _) = NewVm();
        try
        {
            vm.Load(new AppSettings());
            vm.RecordMt5Login("111", "Deriv-Demo");
            vm.RecordMt5Login("222", "Deriv-Real");

            var built = vm.BuildSettings();
            Assert.Equal("222", built.Mt5LastLogin);
            Assert.Equal("Deriv-Real", built.Mt5LastServer);
            Assert.Equal("111", built.Mt5PrevLogin);        // shifted, not forgotten
            Assert.Equal("Deriv-Demo", built.Mt5PrevServer);
        }
        finally
        {
            try { Directory.Delete(SettingsService.DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Record_Same_Account_Again_Does_Not_Advance_The_History()
    {
        var (vm, _) = NewVm();
        try
        {
            vm.Load(new AppSettings());
            vm.RecordMt5Login("111", "Deriv-Demo");
            vm.RecordMt5Login("222", "Deriv-Real");
            vm.RecordMt5Login("222", "Deriv-Real");   // repeat click: no-op
            vm.RecordMt5Login("222", "Deriv-Real");

            var built = vm.BuildSettings();
            Assert.Equal("111", built.Mt5PrevLogin);   // A→B→A→A must not forget A
        }
        finally
        {
            try { Directory.Delete(SettingsService.DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Record_Ignores_Blank_Accounts_And_Servers()
    {
        var (vm, _) = NewVm();
        try
        {
            vm.Load(new AppSettings());
            vm.RecordMt5Login("111", "Deriv-Demo");
            vm.RecordMt5Login("   ", "Deriv-Real");
            vm.RecordMt5Login("222", "");

            var built = vm.BuildSettings();
            Assert.Equal("111", built.Mt5LastLogin);   // untouched by blanks
            Assert.Equal("", built.Mt5PrevLogin);
        }
        finally
        {
            try { Directory.Delete(SettingsService.DataDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
