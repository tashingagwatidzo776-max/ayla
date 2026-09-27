using System.IO;
using System.Text.Json;
using DongGfx.Core.Models;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> under %APPDATA%\tf\data\.
/// There is no credential to protect any more: the Deriv API token left
/// with the binary-options integration, so settings are plain JSON.
/// </summary>
public sealed class SettingsService
{
    /// <summary>Environment variable redirecting the shared data dir.
    /// The test harness (scripts/ci-local.ps1) points this at a scratch
    /// directory so suite teardowns that DELETE the DataDir can never
    /// destroy the real session's journal and settings — the LiveSoakGuard
    /// only protects LIVE sessions (app running, journal fresh), so a
    /// closed-but-idle session's data would otherwise be fair game. The
    /// app itself never sets it; operators pointing it anywhere are
    /// deliberately relocating their data.</summary>
    public const string DataDirEnvVar = "TF_DATA_DIR";

    /// <summary>TF_DATA_DIR when set (the full data dir, trimmed; relative
    /// values stay relative and get resolved against the process cwd by the
    /// file APIs); otherwise %APPDATA%\tf\data. Pure so tests can pin the
    /// resolution without mutating process environment (xUnit runs suites
    /// in parallel — a global env flip would leak into them).</summary>
    internal static string ResolveDataDir(string? envOverride, string appDataBase) =>
        !string.IsNullOrWhiteSpace(envOverride) ? envOverride.Trim()
        : Path.Combine(appDataBase, "tf", "data");

    /// <summary>%APPDATA%\tf\data (or TF_DATA_DIR) — shared by settings
    /// and trade data.</summary>
    public static string DataDir => ResolveDataDir(
        Environment.GetEnvironmentVariable(DataDirEnvVar),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    private static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}