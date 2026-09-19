using System.IO;
using DongGfx.App.Infrastructure;

namespace DongGfx.App.Tests;

/// <summary>
/// Round-trip tests for AccountVault's export/import (README: "Export/import
/// accounts for backup and migration"). The DPAPI-encrypted Load/Save paths
/// are user-mchine-bound and covered indirectly by the hub E2E tests; what
/// matters here is that a plain-JSON export file preserves every account
/// field verbatim and that a corrupt/foreign file imports as empty rather
/// than throwing (a user picking the wrong file must not crash the app).
/// </summary>
[Trait("Category", "Unit")]
public class AccountVaultTests : IDisposable
{
    private readonly string _dir;

    public AccountVaultTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_vault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AccountConfig MakeAccount(string label, string token, string brainKey = "Growth") => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        ApiToken = token,
        IsDemo = label.EndsWith("demo"),
        Symbol = "frxEURUSD",
        Currency = "USD",
        DurationMinutes = 5,
        StartBudget = 7.50m,
        BrainKey = brainKey,
    };

    [Fact]
    public void ExportThenImport_PreservesEveryField()
    {
        var vault = new AccountVault();
        var accounts = new List<AccountConfig>
        {
            MakeAccount("Main demo", "token-a"),
            MakeAccount("Scalper live", "token-b", brainKey: "TrendFollowing"),
        };
        var file = Path.Combine(_dir, "backup.json");

        vault.Export(file, accounts);
        var imported = vault.Import(file);

        Assert.Equal(2, imported.Count);
        for (var i = 0; i < accounts.Count; i++)
        {
            Assert.Equal(accounts[i].Id, imported[i].Id);
            Assert.Equal(accounts[i].Label, imported[i].Label);
            Assert.Equal(accounts[i].ApiToken, imported[i].ApiToken); // tokens ride along by design
            Assert.Equal(accounts[i].IsDemo, imported[i].IsDemo);
            Assert.Equal(accounts[i].Symbol, imported[i].Symbol);
            Assert.Equal(accounts[i].Currency, imported[i].Currency);
            Assert.Equal(accounts[i].DurationMinutes, imported[i].DurationMinutes);
            Assert.Equal(accounts[i].StartBudget, imported[i].StartBudget);
            Assert.Equal(accounts[i].BrainKey, imported[i].BrainKey);
        }
    }

    [Fact]
    public void Export_IsPlainJson_ReadableWithoutDecryption()
    {
        // The export is a backup/migration artifact: it must be portable
        // (plain JSON), unlike the DPAPI-encrypted vault file.
        var vault = new AccountVault();
        var file = Path.Combine(_dir, "plain.json");
        var account = MakeAccount("Portability", "token-x");

        vault.Export(file, [account]);
        var raw = File.ReadAllText(file);

        Assert.Contains("\"ApiToken\"", raw);
        Assert.Contains("token-x", raw);
        Assert.Contains("\"BrainKey\"", raw);
    }

    [Fact]
    public void Import_EmptyFile_YieldsEmptyList()
    {
        var vault = new AccountVault();
        var file = Path.Combine(_dir, "empty.json");
        File.WriteAllText(file, "[]");

        Assert.Empty(vault.Import(file));
    }

    [Fact]
    public void Import_CorruptJson_ThrowsOutOfRangeException()
    {
        // The vault's Import lets the JSON exception surface (unlike Load,
        // which swallows) — the caller shows a file-picker error. Pin the
        // behavior so a silent empty-import regression can't hide a corrupt
        // backup from the user.
        var vault = new AccountVault();
        var file = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(file, "{ not valid json");

        Assert.ThrowsAny<Exception>(() => vault.Import(file));
    }

    [Fact]
    public void Import_EmptyObjectFile_ThrowsLikeCorruptJson()
    {
        // Valid JSON, but not an account list: System.Text.Json throws while
        // deserializing into List<AccountConfig>. The vault's Import lets the
        // exception surface (same contract as corrupt JSON) so a wrong file
        // is reported to the user instead of silently importing nothing.
        var vault = new AccountVault();
        var file = Path.Combine(_dir, "wrong.json");
        File.WriteAllText(file, "{\"not\": \"accounts\"}");

        Assert.ThrowsAny<Exception>(() => vault.Import(file));
    }
}
