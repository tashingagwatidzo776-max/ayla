using DongGfx.App.Services;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The venue-verdict mapping the real-money gate consumes: the bridge's
/// <c>account_info().trade_mode</c> is authoritative (0 = demo, 2 = real),
/// the server-name/login heuristic may verify an account as demo but never
/// as real, and anything unknown fails closed to unverified. These tests
/// are the contract between the bridge payload and the gate.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public sealed class Mt5AccountGateVerdictTests
{
    [Theory]
    [InlineData(0, true)]    // ACCOUNT_TRADE_MODE_DEMO
    [InlineData(2, false)]   // ACCOUNT_TRADE_MODE_REAL
    public void TradeMode_MapsVenueVerdict(int tradeMode, bool expected)
    {
        var account = new Mt5Account(1, "Any-Server", "USD", 0, 0, 0, 100, tradeMode);
        Assert.Equal(expected, account.TradeModeVerifiedVirtual);
    }

    [Theory]
    [InlineData(1)]    // contest
    [InlineData(3)]   // unknown future value
    [InlineData(-1)]
    public void TradeMode_UnknownValuesFailClosed(int tradeMode)
    {
        var account = new Mt5Account(1, "Any-Server", "USD", 0, 0, 0, 100, tradeMode);
        Assert.Null(account.TradeModeVerifiedVirtual);
    }

    [Fact]
    public void TradeMode_AbsentFailsClosed()
    {
        var account = new Mt5Account(1, "Any-Server", "USD", 0, 0, 0, 100);
        Assert.Null(account.TradeModeVerifiedVirtual);
    }

    [Fact]
    public void GateVerdict_VenueDemo_IsVerifiedVirtual()
    {
        var account = new Mt5Account(201587365, "Deriv-Demo", "USD", 0, 0, 0, 100, TradeMode: 0);
        Assert.True(account.GateVerifiedVirtual);
    }

    [Fact]
    public void GateVerdict_VenueReal_IsVerifiedReal()
    {
        var account = new Mt5Account(12345, "SomeBroker-Live", "USD", 0, 0, 0, 100, TradeMode: 2);
        Assert.False(account.GateVerifiedVirtual);
    }

    [Fact]
    public void GateVerdict_HeuristicDemoServer_VerifiesDemo()
    {
        // Legacy sidecar (no trade_mode): a demo server name still verifies
        // the account as virtual — conservative direction.
        var account = new Mt5Account(201587365, "Deriv-Demo", "USD", 0, 0, 0, 100);
        Assert.True(account.GateVerifiedVirtual);
    }

    [Fact]
    public void GateVerdict_HeuristicRealAccount_NeverVerifies()
    {
        // The heuristic must never verify an account as real: fail closed.
        var account = new Mt5Account(12345, "SomeBroker-Live", "USD", 0, 0, 0, 100);
        Assert.Null(account.GateVerifiedVirtual);
    }

    [Fact]
    public void GateVerdict_VenueRealOverridesHeuristicServerName()
    {
        // A broker naming scheme that happens to contain "demo" must not
        // override the venue's own real verdict.
        var account = new Mt5Account(12345, "RealDemo-Live02", "USD", 0, 0, 0, 100, TradeMode: 2);
        Assert.False(account.GateVerifiedVirtual);
    }

    [Theory]
    [InlineData(true, RealMoneyDecision.DemoPassthrough)]   // venue says demo
    [InlineData(false, RealMoneyDecision.Allowed)]          // venue says real, unlocked
    public void Gate_FlowsVenueVerdictThrough(bool virtualFlag, RealMoneyDecision expected)
    {
        var decision = RealMoneyGate.Evaluate(
            configIsDemo: virtualFlag,
            apiVerifiedVirtual: virtualFlag,
            unlockArmed: true);
        Assert.Equal(expected, decision);
    }

    [Fact]
    public void Gate_RealWithoutVenueVerdict_FailsClosedUnverified()
    {
        var decision = RealMoneyGate.Evaluate(configIsDemo: false, apiVerifiedVirtual: null, unlockArmed: true);
        Assert.Equal(RealMoneyDecision.BlockedUnverified, decision);
    }
}
