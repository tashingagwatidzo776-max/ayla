using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tf.Core.Models;

namespace Tf.App.Infrastructure;

/// <summary>One Deriv account the app can connect and trade.</summary>
public sealed class AccountConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public bool IsDemo { get; set; } = true;
    public string Symbol { get; set; } = AppSettings.DefaultSymbol;
    public string Currency { get; set; } = AppSettings.DefaultCurrency;
    public int DurationMinutes { get; set; } = 5;
    public decimal StartBudget { get; set; } = 5.00m;

    /// <summary>Which brain type to use for this account (Growth, TrendFollowing, etc.).</summary>
    public string BrainKey { get; set; } = "Growth";
}

/// <summary>
/// Persists the multi-account list under %APPDATA%\tf\data\accounts.bin.
/// The whole document — including every API token — is DPAPI-encrypted
/// (CurrentUser), so it only decrypts for this Windows user.
/// </summary>
public sealed class AccountVault
{
    private static readonly string AccountsPath =
        Path.Combine(SettingsService.DataDir, "accounts.bin");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<AccountConfig> Load()
    {
        try
        {
            if (!File.Exists(AccountsPath))
            {
                return Array.Empty<AccountConfig>();
            }

            var protectedBytes = File.ReadAllBytes(AccountsPath);
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<List<AccountConfig>>(json, JsonOptions)
                   ?? new List<AccountConfig>();
        }
        catch
        {
            // Undecryptable or corrupt vault (e.g. restored profile) — start empty.
            return Array.Empty<AccountConfig>();
        }
    }

    public void Save(IReadOnlyList<AccountConfig> accounts)
    {
        Directory.CreateDirectory(SettingsService.DataDir);
        var json = JsonSerializer.Serialize(accounts, JsonOptions);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AccountsPath, protectedBytes);
    }
}
