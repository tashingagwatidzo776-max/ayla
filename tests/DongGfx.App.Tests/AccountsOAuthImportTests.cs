using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The Accounts tab's OAuth 2.0 sign-in path: a successful browser round-trip
/// feeds the SAME import path as a pasted PAT (one new-platform row per
/// discovered account, duplicate tokens skipped), the sign-in is refused
/// without a registered client id, and the status surfaces the short token
/// lifetime. PAT remains the default path — these tests pin that an OAuth
/// result lands as an ordinary new-platform account row.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccountsOAuthImportTests
{
    private sealed class MemoryVault : IAccountVault
    {
        public List<AccountConfig> Rows { get; set; } = new();

        public MemoryVault(params AccountConfig[] rows)
        {
            Rows = rows.ToList();
        }

        public IReadOnlyList<AccountConfig> Load() => Rows;
        public void Save(IReadOnlyList<AccountConfig> accounts) => Rows = accounts.ToList();
    }

    private static (MultiAccountHub Hub, AccountsViewModel Vm) MakeVm()
    {
        var vault = new MemoryVault();
        var store = new TradeStore(Path.Combine(Path.GetTempPath(), "oauth-vm-tests", Guid.NewGuid().ToString("N")));
        var journal = new TradeJournal(Path.Combine(Path.GetTempPath(), "oauth-vm-tests", Guid.NewGuid().ToString("N")));
        var hub = new MultiAccountHub(vault, store, journal);
        var settings = new AppSettings();
        var vm = new AccountsViewModel(hub, () => settings);
        return (hub, vm);
    }

    private sealed class FakeOAuth : OAuthSignIn
    {
        private readonly OAuthResult _result;

        public FakeOAuth(OAuthResult result)
        {
            _result = result;
        }

        public override Task<OAuthResult> SignInAsync(string clientId, string? redirectUri = null,
            int loopbackPort = 0, Action<string>? onWaiting = null, CancellationToken ct = default)
        {
            onWaiting?.Invoke("Complete the sign-in in your browser — waiting for Deriv to redirect…");
            return Task.FromResult(_result);
        }
    }

    private static void StubDiscovery(AccountsViewModel vm, params NewPlatformAccount[] accounts) =>
        vm.DiscoveryOverrideForTests = _ => Task.FromResult<IReadOnlyList<NewPlatformAccount>>(accounts);

    [Fact]
    public async Task SignInWithoutClientId_IsRefusedWithGuidance()
    {
        var (_, vm) = MakeVm();

        await vm.SignInWithDerivCommand.ExecuteAsync(null);

        Assert.Contains("developers.deriv.com", vm.OAuthStatus);
        Assert.DoesNotContain("failed", vm.OAuthStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulSignIn_ImportsDemoRowForTheToken()
    {
        var (hub, vm) = MakeVm();
        vm.OAuthClientId = "app12345";
        StubDiscovery(vm,
            new NewPlatformAccount("DOT111", "demo", 10000m, "USD", "active"),
            new NewPlatformAccount("ROT222", "real", 55.25m, "USD", "active"));
        vm.SetOAuthForTests(new FakeOAuth(new OAuthResult("ory_at_x", 3600, null)));

        await vm.SignInWithDerivCommand.ExecuteAsync(null);

        // One row per token (rows sharing a token churn each other's
        // sessions), demo preferred when the consent covers several.
        var row = Assert.Single(hub.Accounts);
        Assert.True(row.Config.NewPlatform);
        Assert.Equal("ory_at_x", row.Config.ApiToken);
        Assert.Equal("DOT111", row.Config.DerivAccountId);
        Assert.True(row.Config.IsDemo);
        Assert.Contains("OAuth session", row.Config.Label);

        // The lifetime warning is part of the outcome — users must know the
        // token is short-lived and PATs remain the default.
        Assert.Contains("expires", vm.OAuthStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReSignInWithSameToken_DoesNotDuplicateRows()
    {
        var (hub, vm) = MakeVm();
        vm.OAuthClientId = "app12345";
        StubDiscovery(vm, new NewPlatformAccount("DOT111", "demo", 10000m, "USD", "active"));
        vm.SetOAuthForTests(new FakeOAuth(new OAuthResult("ory_at_x", 3600, null)));

        await vm.SignInWithDerivCommand.ExecuteAsync(null);
        await vm.SignInWithDerivCommand.ExecuteAsync(null);

        Assert.Single(hub.Accounts);
    }

    [Fact]
    public async Task DiscoveryFailure_SurfacesAsFailedSignIn()
    {
        var (_, vm) = MakeVm();
        vm.OAuthClientId = "app12345";
        vm.DiscoveryOverrideForTests = _ =>
            Task.FromException<IReadOnlyList<NewPlatformAccount>>(
                new DerivApiException("Unauthorized", "rejected"));

        await vm.SignInWithDerivCommand.ExecuteAsync(null);

        Assert.Contains("failed", vm.OAuthStatus, StringComparison.OrdinalIgnoreCase);
    }
}
