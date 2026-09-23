using DongGfx.App.Services;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The manual real-money gate is the shared session unlock for every
/// non-hub trade path: it starts locked, arms only explicitly, evaluates
/// through the shared fail-closed <see cref="RealMoneyGate"/>, and resets on
/// shutdown so unlocks are session-scoped by design.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class ManualRealMoneyGateTests
{
    [Fact]
    public void FreshGate_IsLocked()
    {
        var gate = new ManualRealMoneyGate();
        Assert.False(gate.IsUnlocked);
    }

    [Fact]
    public void Arm_ThenReset_LocksAgain()
    {
        var gate = new ManualRealMoneyGate();
        gate.Arm();
        Assert.True(gate.IsUnlocked);
        gate.Reset();
        Assert.False(gate.IsUnlocked);
    }

    [Fact]
    public void Arm_StampsArmedAtOnce_RepeatArmsKeepTheOriginalClock()
    {
        var gate = new ManualRealMoneyGate();
        Assert.Null(gate.ArmedAt);

        gate.Arm();
        var first = gate.ArmedAt;
        Assert.NotNull(first);

        gate.Arm(); // repeat unlock clicks must not reset the staleness clock
        Assert.Equal(first, gate.ArmedAt);

        gate.Reset();
        Assert.Null(gate.ArmedAt);
    }

    [Fact]
    public void DemoConfig_PassesThroughWithoutUnlock()
    {
        var gate = new ManualRealMoneyGate();
        var decision = gate.Evaluate(configIsDemo: true, apiVerifiedVirtual: null);
        Assert.Equal(RealMoneyDecision.DemoPassthrough, decision);
    }

    [Fact]
    public void RealConfigNotYetVerified_FailsClosedAsUnverified()
    {
        var gate = new ManualRealMoneyGate();
        var decision = gate.Evaluate(configIsDemo: false, apiVerifiedVirtual: null);
        Assert.Equal(RealMoneyDecision.BlockedUnverified, decision);
    }

    [Fact]
    public void RealConfigLocked_FailsClosed()
    {
        var gate = new ManualRealMoneyGate();
        var decision = gate.Evaluate(configIsDemo: false, apiVerifiedVirtual: false);
        Assert.Equal(RealMoneyDecision.BlockedLocked, decision);
    }

    [Fact]
    public void RealConfigArmed_WithVerifiedRealAccount_Allows()
    {
        var gate = new ManualRealMoneyGate();
        gate.Arm();
        // A real (non-virtual) verified account: apiVerifiedVirtual = false
        // while the config says real and the session is armed.
        var decision = gate.Evaluate(configIsDemo: false, apiVerifiedVirtual: false);
        Assert.Equal(RealMoneyDecision.Allowed, decision);
    }

    [Fact]
    public void ConfigSaysReal_ApiSaysVirtual_BlocksEvenWhenArmed()
    {
        var gate = new ManualRealMoneyGate();
        gate.Arm();
        var decision = gate.Evaluate(configIsDemo: false, apiVerifiedVirtual: true);
        Assert.Equal(RealMoneyDecision.BlockedAccountIsVirtual, decision);
    }

    [Fact]
    public void ConfigSaysDemo_ApiSaysReal_BlocksAsConfigMismatch()
    {
        var gate = new ManualRealMoneyGate();
        var decision = gate.Evaluate(configIsDemo: true, apiVerifiedVirtual: false);
        Assert.Equal(RealMoneyDecision.BlockedConfigMismatch, decision);
    }
}
