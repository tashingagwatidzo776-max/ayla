using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core;
using DongGfx.Core.Logging;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.App.Tests;

[Trait("Category", "Unit")]
public sealed class PatSplitWizardTests
{
    private static readonly Guid DemoId = Guid.NewGuid();
    private static readonly Guid RealId = Guid.NewGuid();
    private const string Shared = "shared-token-abcdef123456";
    private const string Fresh = "fresh-demo-token-654321";

    private static MultiAccountHub MakeHub(params AccountConfig[] configs)
    {
        var vault = new MemoryVault(configs);
        var store = new TradeStore(Path.Combine(Path.GetTempPath(), "pat-split-tests", Guid.NewGuid().ToString("N")));
        var journal = new TradeJournal(Path.Combine(Path.GetTempPath(), "pat-split-tests", Guid.NewGuid().ToString("N")));
        return new MultiAccountHub(vault, store, journal);
    }

    [Fact]
    public void DetectSharedTokens_GroupsRowsSharingOneToken()
    {
        var hub = MakeHub(
            new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = Shared },
            new AccountConfig { Id = RealId, Label = "Real", ApiToken = Shared });

        var wizard = new PatSplitWizard(hub, _ => throw new InvalidOperationException("verify must not run during detect"));

        var groups = wizard.DetectSharedTokens();

        var group = Assert.Single(groups);
        Assert.Equal(Shared, group.Token);
        Assert.Equal(2, group.AccountIds.Count);
        Assert.Contains(DemoId, group.AccountIds);
        Assert.Contains(RealId, group.AccountIds);
        Assert.True(wizard.IsShared(Shared));
        Assert.False(wizard.IsShared(Fresh));
    }

    [Fact]
    public void DetectSharedTokens_EmptyWhenEveryAccountHasItsOwn()
    {
        var hub = MakeHub(
            new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = "demo-token-111111" },
            new AccountConfig { Id = RealId, Label = "Real", ApiToken = "real-token-222222" });

        var wizard = new PatSplitWizard(hub, _ => throw new InvalidOperationException("verify must not run"));

        Assert.Empty(wizard.DetectSharedTokens());
    }

    [Fact]
    public async Task VerifyNewTokenAsync_RefusesTokenAlreadyAssignedToAnotherRow()
    {
        var hub = MakeHub(
            new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = Shared },
            new AccountConfig { Id = RealId, Label = "Real", ApiToken = Shared });

        var wizard = new PatSplitWizard(hub, _ => throw new InvalidOperationException("verify must not run for a duplicate"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => wizard.VerifyNewTokenAsync(Shared));

        Assert.Contains("already assigned", ex.Message);
    }

    [Fact]
    public async Task VerifyNewTokenAsync_CallsDiscoveryVerifier()
    {
        var hub = MakeHub(
            new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = Shared },
            new AccountConfig { Id = RealId, Label = "Real", ApiToken = Shared });

        var seen = new List<string>();
        var wizard = new PatSplitWizard(hub, token =>
        {
            seen.Add(token);
            return Task.FromResult<IReadOnlyList<NewPlatformAccount>>(new[]
            {
                new NewPlatformAccount("DOT92951338", "demo", 9814.97m, "USD", "active"),
            });
        });

        var accounts = await wizard.VerifyNewTokenAsync(Fresh);

        Assert.Equal(new[] { Fresh }, seen);
        var account = Assert.Single(accounts);
        Assert.True(account.IsDemoAccount);
        Assert.Equal(9814.97m, account.Balance);
    }

    [Fact]
    public void ApplyToken_SwapsTokenInPlace_PreservingIdAndPersisting()
    {
        var hub = MakeHub(
            new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = Shared },
            new AccountConfig { Id = RealId, Label = "Real", ApiToken = Shared });

        var wizard = new PatSplitWizard(hub, _ => throw new InvalidOperationException("apply must not verify"));

        wizard.ApplyToken(DemoId, Fresh);

        var demo = hub.Accounts.First(a => a.Config.Id == DemoId);
        var real = hub.Accounts.First(a => a.Config.Id == RealId);
        Assert.Equal(Fresh, demo.Config.ApiToken);
        Assert.Equal(Shared, real.Config.ApiToken);
        Assert.Same(hub.Accounts[0], demo); // in place — no row recreation
        Assert.False(wizard.IsShared(Shared)); // now only Real references it
    }

    [Fact]
    public void ApplyToken_RejectsShortTokens()
    {
        var hub = MakeHub(new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = Shared });

        var wizard = new PatSplitWizard(hub, _ => throw new InvalidOperationException("apply must not verify"));

        Assert.Throws<InvalidOperationException>(() => wizard.ApplyToken(DemoId, "short"));
        Assert.Equal(Shared, hub.Accounts[0].Config.ApiToken);
    }

    [Fact]
    public void ApplyToken_UnknownAccountThrows()
    {
        var hub = MakeHub(new AccountConfig { Id = DemoId, Label = "Demo", ApiToken = Shared });

        var wizard = new PatSplitWizard(hub, _ => throw new InvalidOperationException("apply must not verify"));

        Assert.Throws<InvalidOperationException>(() => wizard.ApplyToken(Guid.NewGuid(), Fresh));
    }

    private sealed class MemoryVault : IAccountVault
    {
        private List<AccountConfig> _accounts;

        public MemoryVault(params AccountConfig[] initial)
        {
            _accounts = initial.ToList();
        }

        public IReadOnlyList<AccountConfig> Load() => _accounts;

        public void Save(IReadOnlyList<AccountConfig> accounts)
        {
            Saved++;
            _accounts = accounts.ToList();
        }

        public void Export(string filePath, IReadOnlyList<AccountConfig> accounts) { }

        public IReadOnlyList<AccountConfig> Import(string filePath) => _accounts;

        public int Saved { get; private set; }
    }
}
