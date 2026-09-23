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
    /// <summary>%APPDATA%\tf\data — shared by settings and trade data.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data");

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