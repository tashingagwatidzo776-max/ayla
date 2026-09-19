using System.IO;
using System.Net;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Tests;

/// <summary>
/// The unlock-arming audit trail: arming via the hub or the Growth tab's
/// unlock panel writes a REAL_MONEY_UNLOCK_ARMED journal entry (accounts,
/// scope, API-verified state), announces out-of-band on the webhook, and
/// feeds the metrics digest's arm-state leg. Refusals were already audited;
/// these tests prove the other direction — the moment real trading became
/// possible is recorded just as loudly.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class RealMoneyUnlockArmTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;

    // The manual gate is instance-scoped; this class's scope lives on its
    // default hub (and each test's local hub, below). No shared static state.
    private readonly ManualRealMoneyGate _gate;

    public RealMoneyUnlockArmTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_unlock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
        _gate = _hub.ManualGate;
    }

    public void Dispose()
    {
        _gate.Reset(); // keep the class's scope clean between tests
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private AccountConnection AddReal(string label, bool? verifiedVirtual, MultiAccountHub? hub = null)
    {
        var target = hub ?? _hub;
        var connection = target.AddAccount(new AccountConfig
        {
            Label = label, ApiToken = $"tok-{label}", IsDemo = false, BrainKey = "Growth"
        });
        if (verifiedVirtual is not null)
        {
            connection.ApiVerifiedVirtual = verifiedVirtual;
        }

        return connection;
    }

    [Fact]
    public void UnlockRealMoney_JournalsArmEntry_WithVerifiedState()
    {
        var real = AddReal("RealA", verifiedVirtual: false);

        _hub.UnlockRealMoney(real.Config.Id);
        _journal.Flush();

        var entry = Assert.Single(
            _journal.GetRecent(count: 50).Where(e => e.Category == "REAL_MONEY_UNLOCK_ARMED"));
        Assert.Equal(real.Config.Id, entry.AccountId);

        var doc = JsonDocument.Parse(entry.Details).RootElement;
        Assert.Equal(1, doc.GetProperty("AccountCount").GetInt32());
        Assert.Equal(1, doc.GetProperty("VerifiedReal").GetInt32()); // API-verified real
        Assert.False(doc.GetProperty("ManualSurfaces").GetBoolean());
        Assert.Contains("RealA", doc.GetProperty("Accounts").EnumerateArray().First().GetString());
    }

    [Fact]
    public void UnlockRealMoney_ReArm_IsNotReJournalled()
    {
        var real = AddReal("RealB", verifiedVirtual: false);

        _hub.UnlockRealMoney(real.Config.Id);
        _hub.UnlockRealMoney(real.Config.Id); // no-op the second time
        _journal.Flush();

        Assert.Single(
            _journal.GetRecent(count: 50).Where(e => e.Category == "REAL_MONEY_UNLOCK_ARMED"));
    }

    [Fact]
    public void TryArmUnlock_Batch_JournalsOneSummaryEntry()
    {
        // The panel's one-pass flow: arm N accounts, journal ONE summary
        // entry (the VM calls JournalUnlockArmed once for the batch).
        var a = AddReal("BatchA", verifiedVirtual: null); // unverified — fails closed but armable
        var b = AddReal("BatchB", verifiedVirtual: false);

        Assert.True(_hub.TryArmUnlock(a.Config.Id));
        Assert.True(_hub.TryArmUnlock(b.Config.Id));
        Assert.False(_hub.TryArmUnlock(a.Config.Id), "second arm is not a new event");

        _hub.JournalUnlockArmed(
            _hub.Accounts.Where(x => x.Config.Id == a.Config.Id || x.Config.Id == b.Config.Id).ToArray(),
            manualSurfaces: true);
        _journal.Flush();

        var entry = Assert.Single(
            _journal.GetRecent(count: 50).Where(e => e.Category == "REAL_MONEY_UNLOCK_ARMED"));
        Assert.Equal(a.Config.Id, entry.AccountId); // first armed account owns the entry

        var doc = JsonDocument.Parse(entry.Details).RootElement;
        Assert.Equal(2, doc.GetProperty("AccountCount").GetInt32());
        Assert.Equal(1, doc.GetProperty("VerifiedReal").GetInt32()); // only BatchB is verified
        Assert.True(doc.GetProperty("ManualSurfaces").GetBoolean());
    }

    [Fact]
    public void DescribeUnlockState_ReflectsArmedAccountsAndManualGate()
    {
        var real = AddReal("DescA", verifiedVirtual: false);

        Assert.Null(_hub.DescribeUnlockState()); // nothing armed → silence is healthy

        _hub.UnlockRealMoney(real.Config.Id);
        _gate.Arm();

        var state = _hub.DescribeUnlockState();
        Assert.NotNull(state);
        Assert.Contains("ARMED", state);
        Assert.Contains("DescA", state);
        Assert.Contains("manual surfaces", state);

        // The manual gate resetting (app shutdown path) drops that scope
        // but the account arming still shows — each state is independent.
        _gate.Reset();
        state = _hub.DescribeUnlockState();
        Assert.NotNull(state);
        Assert.Contains("DescA", state);
        Assert.DoesNotContain("manual surfaces", state);
    }

    [Fact]
    public async Task UnlockArmedAtUtc_TracksArmTimes_AndRemovesWithTheAccount()
    {
        var real = AddReal("TimeA", verifiedVirtual: false);
        Assert.Empty(_hub.UnlockArmedAtUtc);

        _hub.UnlockRealMoney(real.Config.Id);

        var armed = _hub.UnlockArmedAtUtc;
        Assert.Single(armed);
        Assert.True(armed[real.Config.Id] <= DateTimeOffset.UtcNow);

        await _hub.RemoveAccountAsync(real);

        Assert.Empty(_hub.UnlockArmedAtUtc); // removed accounts leave no stale arm state
    }

    [Fact]
    public async Task UnlockRealMoney_AnnouncesOnTheWebhook()
    {
        var (listener, port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        // The listener thread and the test thread meet here; the List is
        // written on one thread and polled on another, so the count must be
        // a volatile interlocked counter (a plain List read raced on slow
        // CI runners).
        var bodies = new List<string>();
        var bodyCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(cts.Token); }
                catch { return; }

                using var reader = new StreamReader(ctx.Request.InputStream);
                lock (bodies) { bodies.Add(reader.ReadToEnd()); }
                Interlocked.Increment(ref bodyCount);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
            }
        });

        try
        {
            using var webhook = new WebhookService
            {
                WebhookUrl = $"http://localhost:{port}/hook/",
                IsDiscord = true,
                MinInterval = TimeSpan.Zero
            };
            var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal, webhook: webhook);
            var real = hub.AddAccount(new AccountConfig
            {
                Label = "HookReal", ApiToken = "t", IsDemo = false, BrainKey = "Growth"
            });
            real.ApiVerifiedVirtual = false;

            hub.UnlockRealMoney(real.Config.Id);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Volatile.Read(ref bodyCount) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            List<string> snapshot;
            lock (bodies) { snapshot = new List<string>(bodies); }
            var body = Assert.Single(snapshot);
            using var doc = JsonDocument.Parse(body);
            var embed = doc.RootElement.GetProperty("embeds")[0];
            Assert.Contains("unlock armed", embed.GetProperty("title").GetString());
            var description = embed.GetProperty("description").GetString();
            Assert.Contains("HookReal", description);
            Assert.Contains("1/1 API-verified real", description);
        }
        finally
        {
            cts.Cancel();
            try { listener.Close(); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void StateChanged_RebuildsLockedBanners_WithFreshVerification()
    {
        // A connect lands after construction: discovery flips the account's
        // ApiVerifiedVirtual from null to a verdict, and the locked banner
        // must follow without waiting for an account-set change (the banner
        // previously stayed on 'type unverified' forever).
        var real = AddReal("FreshReal", verifiedVirtual: null);
        var vm = new GrowthViewModel(
            _hub, new GrowthPlanStore(), () => new AppSettings(),
            new DashboardViewModel(new DerivClient(), _hub), tracker: null);
        vm.TestRebuildLockedBanners();
        var banner = vm.LockedRealAccountBanners.Single(b => b.AccountId == real.Config.Id);
        Assert.Contains("unverified", banner.VerificationText);

        real.ApiVerifiedVirtual = false;
        real.RaiseStateChangedTest();

        banner = vm.LockedRealAccountBanners.Single(b => b.AccountId == real.Config.Id);
        Assert.Contains("verified REAL", banner.VerificationText);
    }

    [Fact]
    public void UnlockPanel_JournalsOneArmEntry_AndLogsActivity()
    {
        AddReal("PanelA", verifiedVirtual: false);
        AddReal("PanelB", verifiedVirtual: null); // unverified
        var vm = new GrowthViewModel(
            _hub, new GrowthPlanStore(), () => new AppSettings { AutonomyEnabled = true },
            new DashboardViewModel(new DerivClient(), _hub), tracker: null);

        vm.ShowUnlockPanelCommand.Execute(null);
        Assert.Equal(2, vm.UnlockableAccounts.Count);
        vm.UnlockPhrase = RealMoneyGate.ConfirmationPhrase;
        vm.ConfirmUnlockCommand.Execute(null);

        _journal.Flush();
        var entry = Assert.Single(
            _journal.GetRecent(count: 50).Where(e => e.Category == "REAL_MONEY_UNLOCK_ARMED"));
        var doc = JsonDocument.Parse(entry.Details).RootElement;
        Assert.Equal(2, doc.GetProperty("AccountCount").GetInt32());
        Assert.True(doc.GetProperty("ManualSurfaces").GetBoolean());
        Assert.True(_gate.IsUnlocked);

        // The activity log line names exactly what the journal entry covers.
        Assert.Contains("real-money unlock armed", vm.ActivityLog[0]);
        Assert.Contains("2 hub account(s)", vm.ActivityLog[0]);
        Assert.Contains("manual surfaces", vm.ActivityLog[0]);
    }

    [Fact]
    public void Digest_CarriesHubArmState()
    {
        var real = AddReal("DigestA", verifiedVirtual: false);
        var digest = new MetricsDigestService(new MetricsCollector(), new WebhookService())
        {
            UnlockStateProvider = _hub.DescribeUnlockState
        };

        // Locked → silence (nothing is armed, nothing is news).
        Assert.Null(digest.ComposeDigest());

        _hub.UnlockRealMoney(real.Config.Id);

        var text = digest.ComposeDigest();
        Assert.NotNull(text);
        Assert.Contains("ARMED", text);
        Assert.Contains("DigestA", text);
    }

    // ─── Arm-staleness alerting (virtual clock) ───────────

    [Fact]
    public void StaleUnlock_FiresOnTheVirtualClock_WithToastWebhookAndJournal()
    {
        var clock = new TestVirtualClock();
        var stale = new List<(string Name, TimeSpan Age, int Cycles)>();
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal,
            timeProvider: clock);
        _gate.Reset(); // the VM built below resolves its gate from this hub
        hub.UnlockStale += s => stale.Add((s.Name, s.Age, s.Cycles));
        hub.ArmStalenessThreshold = TimeSpan.FromHours(4);

        var real = AddReal("StaleA", verifiedVirtual: false, hub: hub);
        hub.UnlockRealMoney(real.Config.Id);

        // 3h59m: nothing yet.
        clock.Advance((long)TimeSpan.FromHours(3.98).TotalMilliseconds);
        Assert.Empty(stale);
        Assert.DoesNotContain(_journal.GetRecent(count: 50),
            e => e.Category == "REAL_MONEY_UNLOCK_STALE");

        // 4h: the one-shot fires — journal + toast/webhook (null rails here;
        // the E2E webhook leg is covered by UnlockRealMoney_AnnouncesOnTheWebhook)
        // + the UnlockStale event with the exact age and threshold cycle.
        clock.Advance((long)TimeSpan.FromMinutes(5).TotalMilliseconds);
        var flag = Assert.Single(stale);
        Assert.Equal("StaleA", flag.Name);
        Assert.Equal(1, flag.Cycles);
        Assert.True(flag.Age >= TimeSpan.FromHours(4));
        _journal.Flush();
        Assert.Contains(_journal.GetRecent(count: 50),
            e => e.Category == "REAL_MONEY_UNLOCK_STALE" && e.Details.Contains("StaleA"));

        // 8h: still armed → the next threshold re-flags it.
        clock.Advance((long)TimeSpan.FromHours(4).TotalMilliseconds);
        Assert.Equal(2, stale.Count);
        Assert.Equal(2, stale[1].Cycles);
    }

    [Fact]
    public async Task StaleUnlock_IsSilentAfterAccountRemoval()
    {
        var clock = new TestVirtualClock();
        var stale = 0;
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal,
            timeProvider: clock);
        hub.UnlockStale += _ => stale++;
        hub.ArmStalenessThreshold = TimeSpan.FromHours(1);

        var real = AddReal("StaleB", verifiedVirtual: false, hub: hub);
        hub.UnlockRealMoney(real.Config.Id);
        await hub.RemoveAccountAsync(real);

        clock.Advance((long)TimeSpan.FromHours(5).TotalMilliseconds);

        Assert.Equal(0, stale); // a removed account never flags
    }

    [Fact]
    public async Task StaleUnlock_RepeatUnlockCalls_CannotResetTheStalenessClock()
    {
        var clock = new TestVirtualClock();
        var stale = 0;
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal,
            timeProvider: clock);
        hub.UnlockStale += _ => stale++;
        hub.ArmStalenessThreshold = TimeSpan.FromHours(1);

        var real = AddReal("StaleC", verifiedVirtual: false, hub: hub);
        hub.UnlockRealMoney(real.Config.Id);

        clock.Advance((long)TimeSpan.FromMinutes(50).TotalMilliseconds);

        // A repeat unlock of the same account is a no-op (not a new arm
        // event) — so it must NOT push the staleness clock out either:
        // clicking unlock again cannot dodge the stale flag.
        hub.UnlockRealMoney(real.Config.Id);
        clock.Advance((long)TimeSpan.FromMinutes(15).TotalMilliseconds); // 1h05m since the FIRST arm
        Assert.Equal(1, stale);

        await hub.RemoveAccountAsync(real); // cancels the follow-up watch
    }

    // ─── Configurable staleness threshold ────────────────

    [Fact]
    public void ArmStalenessThreshold_ReadsFromTheConfiguredSource()
    {
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal);

        Assert.Equal(TimeSpan.FromHours(4), hub.ArmStalenessThreshold); // default

        hub.SetThresholdSource(() => 12);
        Assert.Equal(TimeSpan.FromHours(12), hub.ArmStalenessThreshold);

        hub.SetThresholdSource(() => 0); // 0 disables the alert
        Assert.Null(hub.ArmStalenessThreshold);

        hub.SetThresholdSource(() => 999); // clamped to 72h
        Assert.Equal(TimeSpan.FromHours(72), hub.ArmStalenessThreshold);
    }

    [Fact]
    public async Task LoweredThreshold_CatchesAnArmAlreadyPastIt_OnTheNextTick()
    {
        var clock = new TestVirtualClock();
        var stale = 0;
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal,
            timeProvider: clock);
        hub.UnlockStale += _ => stale++;
        hub.SetThresholdSource(() => 10); // generous window

        var real = AddReal("ThreshA", verifiedVirtual: false, hub: hub);
        hub.UnlockRealMoney(real.Config.Id);

        clock.Advance((long)TimeSpan.FromHours(5).TotalMilliseconds);
        Assert.Equal(0, stale);

        // The user lowers the threshold to 1h — the arm is already 5h in.
        // SetThresholdSource refreshes the watches, so the next tick fires.
        hub.SetThresholdSource(() => 1);
        Assert.Equal(TimeSpan.FromHours(1), hub.ArmStalenessThreshold);

        clock.Advance(0); // the rescheduled watch is due immediately
        Assert.Equal(1, stale);

        await hub.RemoveAccountAsync(real);
    }

    // ─── One-click config-flag fix (API says virtual, config says real) ───

    [Fact]
    public void FixAccountFlagToDemo_RelabelsPersistsAndJournals()
    {
        var vault = new MemoryVault();
        var hub = new MultiAccountHub(vault, new TradeStore(_dir), _journal);
        var mismatched = hub.AddAccount(new AccountConfig
        {
            Label = "Mismatch", ApiToken = "t", IsDemo = false, BrainKey = "Growth"
        });
        mismatched.ApiVerifiedVirtual = true; // API says virtual, config claims real

        Assert.True(hub.FixAccountFlagToDemo(mismatched.Config.Id));
        Assert.True(mismatched.Config.IsDemo); // relabelled
        Assert.Single(vault.Items); // persisted through the vault
        Assert.True(vault.Items[0].IsDemo);

        _journal.Flush();
        Assert.Contains(_journal.GetRecent(count: 50), e =>
            e.Category == "ACCOUNT_EVENT" && e.Details.Contains("re-labelled to demo"));

        // The gate now passes it through as a demo account — the mismatch
        // refusal is gone.
        Assert.Equal(RealMoneyDecision.DemoPassthrough,
            RealMoneyGate.Evaluate(mismatched.Config.IsDemo, mismatched.ApiVerifiedVirtual,
                unlockArmed: false));

        // The fixed account drops out of the unlock panel list.
        var vm = new GrowthViewModel(hub, new GrowthPlanStore(),
            () => new AppSettings { AutonomyEnabled = true },
            new DashboardViewModel(new DerivClient(), hub), tracker: null);
        vm.ShowUnlockPanelCommand.Execute(null);
        Assert.Empty(vm.UnlockableAccounts);
    }

    [Fact]
    public void FixAccountFlagToDemo_RefusesEverythingButTheExactMismatch()
    {
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal);

        // Unknown id.
        Assert.False(hub.FixAccountFlagToDemo(Guid.NewGuid()));

        // Genuinely real (API-verified real) — must stay a human decision.
        var real = AddReal("RealStay", verifiedVirtual: false, hub: hub);
        Assert.False(hub.FixAccountFlagToDemo(real.Config.Id));

        // Already demo — nothing to fix.
        var demo = hub.AddAccount(new AccountConfig
        {
            Label = "DemoStay", ApiToken = "d", IsDemo = true, BrainKey = "Growth"
        });
        demo.ApiVerifiedVirtual = true;
        Assert.False(hub.FixAccountFlagToDemo(demo.Config.Id));

        // Unverified (never authorized) — fixing it would be a guess.
        var unverified = hub.AddAccount(new AccountConfig
        {
            Label = "Unverif", ApiToken = "u", IsDemo = false, BrainKey = "Growth"
        });
        unverified.ApiVerifiedVirtual = null;
        Assert.False(hub.FixAccountFlagToDemo(unverified.Config.Id));
    }

    // ─── Stale-unlock banner surface (Growth tab) ─────────

    [Fact]
    public async Task StaleBanners_MirrorTheHubArmState()
    {
        var clock = new TestVirtualClock();
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal,
            timeProvider: clock);
        hub.ArmStalenessThreshold = TimeSpan.FromHours(4);
        var real = AddReal("StaleUi", verifiedVirtual: false, hub: hub);
        var vm = new GrowthViewModel(hub, new GrowthPlanStore(),
            () => new AppSettings { AutonomyEnabled = true },
            new DashboardViewModel(new DerivClient(), hub), tracker: null);

        // Nothing armed → no stale banner.
        Assert.Empty(vm.StaleUnlockBanners);

        // Fresh arm → still no stale banner (the lock banner disappears too).
        hub.UnlockRealMoney(real.Config.Id);
        vm.TestRebuildStaleBanners();
        Assert.Empty(vm.StaleUnlockBanners);

        // Past the threshold the staleness timer fires the hub's UnlockStale
        // event — the VM rebuilds on it, so the banner appears without any
        // manual refresh (this is the exact production path).
        clock.Advance((long)TimeSpan.FromHours(4).TotalMilliseconds + 1);
        var banner = Assert.Single(vm.StaleUnlockBanners);
        Assert.Equal("StaleUi", banner.AccountName);
        Assert.Equal("4h", banner.ThresholdText);
        Assert.Contains("h", banner.ArmedForText);

        // Removing the account rebuilds the surface — the banner leaves.
        await hub.RemoveAccountAsync(real);
        Assert.Empty(vm.StaleUnlockBanners);
    }

    [Fact]
    public void StaleBanners_DisappearWhenAlertingIsDisabled()
    {
        var clock = new TestVirtualClock();
        var hub = new MultiAccountHub(new MemoryVault(), new TradeStore(_dir), _journal,
            timeProvider: clock);
        hub.SetThresholdSource(() => 0); // alerting off — no stale surface either
        var real = AddReal("StaleOff", verifiedVirtual: false, hub: hub);
        var vm = new GrowthViewModel(hub, new GrowthPlanStore(),
            () => new AppSettings { AutonomyEnabled = true },
            new DashboardViewModel(new DerivClient(), hub), tracker: null);

        hub.UnlockRealMoney(real.Config.Id);
        vm.TestRebuildStaleBanners();

        Assert.Empty(vm.StaleUnlockBanners); // disabled alert ⇒ disabled banner
    }

    // ─── Unlock-window trade counting (digest arm leg) ─────

    [Fact]
    public void SettlementsInsideTheWindow_CountTowardTheDigestArmLine()
    {
        var real = AddReal("WinA", verifiedVirtual: false);
        _hub.UnlockRealMoney(real.Config.Id);

        // No growth trade yet: the arm line says so.
        var before = _hub.DescribeUnlockState();
        Assert.NotNull(before);
        Assert.Contains("no growth trades", before);

        // Two settlements inside the window: the digest leg counts them and
        // sums the net (+2.00 −1.00 = +1.00) — real mode was armed AND used.
        var runner = new GrowthRunner(real, _store,
            () => new AppSettings { AutonomyEnabled = true }, () => false, _journal);
        _hub.ObserveRunner(runner);
        _hub.TestRaiseSettled(runner, GrowthTrade(+2.00m, "WinA", real.Config.Id));
        _hub.TestRaiseSettled(runner, GrowthTrade(-1.00m, "WinA", real.Config.Id));

        var state = _hub.DescribeUnlockState();
        Assert.NotNull(state);
        Assert.Contains("2 growth trades", state);
        Assert.Contains("net +1", state);
    }

    [Fact]
    public async Task RemovingTheAccount_ClosesTheWindow()
    {
        var real = AddReal("Gone", verifiedVirtual: false);
        _hub.UnlockRealMoney(real.Config.Id);
        var runner = new GrowthRunner(real, _store,
            () => new AppSettings { AutonomyEnabled = true }, () => false, _journal);
        _hub.ObserveRunner(runner);
        _hub.TestRaiseSettled(runner, GrowthTrade(0.50m, "Gone", real.Config.Id));
        Assert.Contains("1 growth trade", _hub.DescribeUnlockState());

        await _hub.RemoveAccountAsync(real);

        // The window closed with the account — the digest leg is silent again.
        Assert.Null(_hub.DescribeUnlockState());
    }

    private static Trade GrowthTrade(decimal profit, string accountName, Guid accountId) => new(
        Guid.NewGuid(), "frxEURUSD",
        profit >= 0 ? Direction.Rise : Direction.Fall,
        1.00m, "USD", 1.17, 1700000300, $"C-{Guid.NewGuid():N}",
        profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
        profit, 1.165, 1700000600, DateTimeOffset.UtcNow,
        accountId, accountName, TradeSource.Growth);

    private sealed class MemoryVault : IAccountVault
    {
        public List<AccountConfig> Items { get; } = new();

        public IReadOnlyList<AccountConfig> Load() => Items.ToArray();

        public void Save(IReadOnlyList<AccountConfig> accounts)
        {
            Items.Clear();
            Items.AddRange(accounts);
        }
    }
}
