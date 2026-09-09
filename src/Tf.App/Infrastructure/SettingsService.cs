using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tf.Core.Models;

namespace Tf.App.Infrastructure;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> under %APPDATA%\tf\data\.
/// The API token is written separately, encrypted with DPAPI
/// (CurrentUser scope) so it only decrypts for this Windows user.
/// </summary>
public sealed class SettingsService
{
    /// <summary>%APPDATA%\tf\data — shared by settings, token and trade log.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tf", "data");

    private static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    private static readonly string TokenPath = Path.Combine(DataDir, "token.bin");

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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            if (File.Exists(TokenPath))
            {
                try
                {
                    var protectedBytes = File.ReadAllBytes(TokenPath);
                    settings.ApiToken = Encoding.UTF8.GetString(
                        ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException)
                {
                    // Token is undecryptable (e.g. profile restored from backup) — start fresh.
                    settings.ApiToken = "";
                }
            }

            return settings;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDir);

        var token = settings.ApiToken;
        settings.ApiToken = "";
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);

            if (string.IsNullOrEmpty(token))
            {
                if (File.Exists(TokenPath))
                {
                    File.Delete(TokenPath);
                }
            }
            else
            {
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(TokenPath, protectedBytes);
            }
        }
        finally
        {
            settings.ApiToken = token;
        }
    }
}