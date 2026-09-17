using System.Globalization;
using System.IO;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.App.ViewModels;
using Tf.Core;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;
using Tf.Deriv;
using static Tf.App.Tests.GrowthTestHarness;

namespace Tf.App.Tests;

/// <summary>
/// The manual trading surfaces (Trades tab and Brain tab) must obey the same
/// real-money gate as the growth engines: a real account — or one the API has
/// not verified — stays locked until the session unlock is armed, and the
/// unlock fails closed when omitted (headless tests never arm it).
/// </summary>
[Trait("Category", "Unit")]
[Collection("ManualRealMoneyGate")]
public class ManualRealMoneyGateTests : IDisposable
{
    private readonly string _dir;

    public ManualRealMoneyGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_manual_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ManualRealMoneyGate.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AppSettings RealSettings() => new() { IsDemo = false, ApiToken = "tok" };
    private static AppSettings DemoSettings() => new() { IsDemo = true, ApiToken = "tok" };

    /// <summary>Fake broker authorizing a REAL account and rejecting every
    /// proposal with an API error — past-the-gate flows fail fast at the
    /// broker instead of trading.</summary>
    private static FakeDerivServer RealBrokerRejectingProposals(CancellationToken ct) =>
        new(req =>
        {
            var reqId = req.GetProperty("req_id").GetInt32();
            if (req.TryGetProperty("authorize", out _))
            {
                return $@"{{""msg_type"":""authorize"",""req_id"":{reqId},""authorize"":{{""balance"":250.00,""currency"":""USD"",""loginid"":""CR900100"",""is_virtual"":0}}}}";
            }
            if (req.TryGetProperty("ticks_history", out _))
            {
                var (prices, times) = History();
                return $@"{{""msg_type"":""history"",""req_id"":{reqId},""history"":{{""prices"":[{prices}],""times"":[{times}]}},""pip_size"":5}}";
            }
            if (req.TryGetProperty("ticks", out _))
            {
                return $@"{{""msg_type"":""ticks"",""req_id"":{reqId},""subscription"":{{""id"":""sub-manual""}}}}";
            }
            return $@"{{""error"":{{""code"":""GateProbe"",""message"":""proposal reached the broker (gate passed)""}},""req_id"":{reqId}}}";
        }, ct);

    private TradesViewModel NewTradesVm(DerivClient client, AppSettings settings) => new(
        client,
        new TradeStore(_dir),
        () => settings,
        new DashboardViewModel(client, new MultiAccountHub(
            new EmptyVault(), new TradeStore(_dir),
            new TradeJournal(Path.Combine(_dir, "journal")))),
        isRealMoneyUnlocked: () => ManualRealMoneyGate.IsUnlocked);

    [Fact]
    public async Task TradesTab_RealAccount_LockedRefuses_UnlockReachesBroker()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = RealBrokerRejectingProposals(cts.Token);
        _ = server.RunAsync(cts.Token);

        var client = new DerivClient { Endpoint = server.WsUrl };
        var vm = NewTradesVm(client, RealSettings());
        await client.ConnectAsync("tok");
        Assert.False(client.Balance.IsVirtual, "the fake broker authorized a real account");

        // ── Locked: the gate refuses before anything else is consulted. ──
        ManualRealMoneyGate.Reset();
        Assert.True(vm.IsRealModeBlocked);
        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);
        Assert.Contains("REFUSED", vm.StatusMessage);
        Assert.Contains("locked", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // ── Unlocked: the gate passes, the flow reaches the broker (whose
        // proposal error proves the request was actually sent). ──
        ManualRealMoneyGate.Arm();
        Assert.False(vm.IsRealModeBlocked);
        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);
        Assert.DoesNotContain("REFUSED", vm.StatusMessage);
        Assert.Contains("GateProbe", vm.StatusMessage);
    }

    [Fact]
    public async Task TradesTab_RealAccount_Unverified_FailsClosed()
    {
        var client = new DerivClient(); // never authorized → no API verdict
        var vm = NewTradesVm(client, RealSettings());
        ManualRealMoneyGate.Arm(); // even the unlock cannot fix "unverified"

        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);
        Assert.Contains("REFUSED", vm.StatusMessage);
        Assert.Contains("not been verified", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TradesTab_DemoConfig_NeverBlocked()
    {
        var client = new DerivClient();
        var vm = NewTradesVm(client, DemoSettings());
        ManualRealMoneyGate.Reset();

        await vm.PlaceDemoTradeCommand.ExecuteAsync(null);
        Assert.DoesNotContain("REFUSED", vm.StatusMessage);
        Assert.False(vm.IsRealModeBlocked);
    }

    [Fact]
    public void Evaluate_MirrorsSharedGate()
    {
        ManualRealMoneyGate.Reset();

        // Config real + API real, locked → BlockedLocked; after Arm → Allowed.
        Assert.Equal(RealMoneyDecision.BlockedLocked, ManualRealMoneyGate.Evaluate(false, false));
        ManualRealMoneyGate.Arm();
        Assert.Equal(RealMoneyDecision.Allowed, ManualRealMoneyGate.Evaluate(false, false));

        // Unverified stays refused even when unlocked.
        Assert.Equal(RealMoneyDecision.BlockedUnverified, ManualRealMoneyGate.Evaluate(false, null));
        // Demo passthrough is untouched by the unlock state.
        Assert.Equal(RealMoneyDecision.DemoPassthrough, ManualRealMoneyGate.Evaluate(true, true));
        // Config mismatch refuses regardless.
        Assert.Equal(RealMoneyDecision.BlockedConfigMismatch, ManualRealMoneyGate.Evaluate(true, false));
    }

    [Fact]
    public async Task BrainTab_GateRefusal_BlocksManualCycle()
    {
        // A BrainViewModel with a gate source that refuses (locked real
        // account): RunCycle must surface the refusal and never report a
        // placed trade. The command is async - it must be awaited, or the
        // assertions race the status update (lost once in CI).
        ManualRealMoneyGate.Reset();
        var client = new DerivClient();
        var hub = new MultiAccountHub(new EmptyVault(), new TradeStore(_dir),
            new TradeJournal(Path.Combine(_dir, "journal")));
        var vm = new BrainViewModel(
            client,
            new TradeStore(_dir),
            new DashboardViewModel(client, hub),
            () => new AppSettings { IsDemo = false, AutonomyEnabled = true },
            () => new[] { new Tick("frxEURUSD", 1.1, 1.1, 1.1, 1700000000, 5) },
            () => new RiskContext(false, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>(),
            realMoneyDecision: () => ManualRealMoneyGate.Evaluate(false, apiVerifiedVirtual: false));

        await vm.RunCycleCommand.ExecuteAsync(null);
        Assert.Contains("REFUSED", vm.StatusText);
        Assert.Contains("locked", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        // Autonomy start is guarded the same way.
        vm.StartAutonomy();
        Assert.False(vm.IsAutonomyRunning);
    }

    private sealed class EmptyVault : IAccountVault
    {
        public IReadOnlyList<AccountConfig> Load() => [];
        public void Save(IReadOnlyList<AccountConfig> accounts) { }
    }
}
