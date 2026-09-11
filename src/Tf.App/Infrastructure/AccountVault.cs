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
/// Storage surface for the account list. Abstracted so the multi-account hub
/// (and its tests) can run against an in-memory store instead of the
/// DPAPI-encrypted file vault.
/// </summary>
public interface IAccountVault
{
    IReadOnlyList<AccountConfig> Load();

    void Save(IReadOnlyList<AccountConfig> accounts);
}

/// <summary>
/// Persists the multi-account list under %APPDATA%\tf\data\accounts.bin.
/// The whole document — including every API token — is DPAPI-encrypted
/// (CurrentUser), so it only decrypts for this Windows user.
/// </summary>
public sealed class AccountVault : IAccountVault
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

    /// <summary>Export accounts as plain JSON to a user-chosen file (no encryption).</summary>
    public void Export(string filePath, IReadOnlyList<AccountConfig> accounts)
    {
        var json = JsonSerializer.Serialize(accounts, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>Import accounts from a plain JSON export file (tokens included).</summary>
    public IReadOnlyList<AccountConfig> Import(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<List<AccountConfig>>(json, JsonOptions)
               ?? new List<AccountConfig>();
    }
}
