using System.Text.Json;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

/// <summary>
/// The real-money gate is the last line of defense before an engine trades
/// real funds: every condition (config, API-verified account type, session
/// unlock) must pass, and unknown states must fail CLOSED — an unverified
/// account is treated as demo, never as real.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class RealMoneyGateTests
{
    [Fact]
    public void DemoConfig_PassesThrough_WhenApiAgrees()
    {
        var decision = RealMoneyGate.Evaluate(configIsDemo: true, apiVerifiedVirtual: true, unlockArmed: false);
        Assert.Equal(RealMoneyDecision.DemoPassthrough, decision);
    }

    [Fact]
    public void DemoConfig_PassesThrough_WhenUnverified()
    {
        var decision = RealMoneyGate.Evaluate(configIsDemo: true, apiVerifiedVirtual: null, unlockArmed: false);
        Assert.Equal(RealMoneyDecision.DemoPassthrough, decision);
    }

    [Fact]
    public void DemoConfig_ButApiSaysReal_IsConfigMismatch()
    {
        // A demo-flagged account that the API verifies as real is a config
        // mistake worth surfacing loudly, not silently trading.
        var decision = RealMoneyGate.Evaluate(configIsDemo: true, apiVerifiedVirtual: false, unlockArmed: false);
        Assert.Equal(RealMoneyDecision.BlockedConfigMismatch, decision);

        // Even the unlock must not wave it through — the config must be fixed.
        var armed = RealMoneyGate.Evaluate(configIsDemo: true, apiVerifiedVirtual: false, unlockArmed: true);
        Assert.Equal(RealMoneyDecision.BlockedConfigMismatch, armed);
    }

    [Fact]
    public void RealConfig_Unverified_IsRefused()
    {
        var decision = RealMoneyGate.Evaluate(configIsDemo: false, apiVerifiedVirtual: null, unlockArmed: true);
        Assert.Equal(RealMoneyDecision.BlockedUnverified, decision);
    }

    [Fact]
    public void RealConfig_ApiSaysVirtual_IsRefused()
    {
        // Config claims real but the API only ever saw a demo account.
        var decision = RealMoneyGate.Evaluate(configIsDemo: false, apiVerifiedVirtual: true, unlockArmed: true);
        Assert.Equal(RealMoneyDecision.BlockedAccountIsVirtual, decision);
    }

    [Fact]
    public void RealConfig_VerifiedReal_ButLocked_IsRefused()
    {
        var decision = RealMoneyGate.Evaluate(configIsDemo: false, apiVerifiedVirtual: false, unlockArmed: false);
        Assert.Equal(RealMoneyDecision.BlockedLocked, decision);
    }

    [Fact]
    public void RealConfig_VerifiedReal_AndUnlocked_IsAllowed()
    {
        var decision = RealMoneyGate.Evaluate(configIsDemo: false, apiVerifiedVirtual: false, unlockArmed: true);
        Assert.Equal(RealMoneyDecision.Allowed, decision);
    }

    [Fact]
    public void Explain_CoversEveryDecision()
    {
        foreach (var decision in Enum.GetValues<RealMoneyDecision>())
        {
            var text = RealMoneyGate.Explain(decision);

            // DemoPassthrough stays silent (nothing to explain on the demo
            // path); every refusal must explain itself.
            if (decision == RealMoneyDecision.DemoPassthrough)
            {
                Assert.Equal("", text);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{decision} must explain itself");
            }
        }

        Assert.Contains(RealMoneyGate.ConfirmationPhrase,
            RealMoneyGate.Explain(RealMoneyDecision.BlockedLocked));
    }
}

