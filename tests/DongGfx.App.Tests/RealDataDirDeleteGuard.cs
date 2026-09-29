using System;
using System.IO;
using DongGfx.App.Infrastructure;

namespace DongGfx.App.Tests;

/// <summary>
/// Hard guard for the DataDir-deleting test suites: refuses to
/// <see cref="DeleteDataDir"/> when the resolved data dir is the REAL
/// %APPDATA%\tf\data — i.e. when no TF_DATA_DIR redirect is in effect and
/// the operator has not set TF_TESTS_ALLOW_LIVE_DATADIR=1 deliberately.
///
/// Why this exists: the LiveSoakGuard protects LIVE sessions (app running
/// or journal fresh), but a closed session's history is still real evidence
/// — and a bare `dotnet test` invocation (no ci-local.ps1 wrapper, so no
/// TF_DATA_DIR) with a stale journal sailed past the liveness guard and
/// deleted the whole demo data dir (second wipe, 2026-09-27 ~04:5x). The
/// rule now: tests may only delete a directory that is either redirected
/// or explicitly surrendered.
/// </summary>
public static class RealDataDirDeleteGuard
{
    /// <summary>Deletes <see cref="SettingsService.DataDir"/> recursively —
    /// but only when it is NOT the real data dir (TF_DATA_DIR redirect in
    /// effect) or the operator explicitly allowed touching the real one.
    /// Any refusal throws, so the test fails loudly instead of silently
    /// destroying operator data.</summary>
    public static void DeleteDataDir()
    {
        var real = SettingsService.ResolveDataDir(null,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var target = SettingsService.DataDir;
        var redirected = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SettingsService.DataDirEnvVar));

        if (string.Equals(
                Path.GetFullPath(target), Path.GetFullPath(real),
                StringComparison.OrdinalIgnoreCase)
            && !redirected
            && Environment.GetEnvironmentVariable("TF_TESTS_ALLOW_LIVE_DATADIR") != "1")
        {
            throw new InvalidOperationException(
                "Refusing to delete the REAL data dir (" + target + "): no TF_DATA_DIR " +
                "redirect is in effect and TF_TESTS_ALLOW_LIVE_DATADIR != 1. Run the gate " +
                "via scripts/ci-local.ps1 (which sets TF_DATA_DIR), or set the override " +
                "deliberately. This guard exists because a bare dotnet test wiped the " +
                "demo session's history (2026-09-27).");
        }

        try { Directory.Delete(target, recursive: true); } catch (DirectoryNotFoundException) { }
        catch (IOException) { }   // locked mid-write: best effort, same as before
        catch (UnauthorizedAccessException) { }
    }
}
