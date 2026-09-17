using System.Text.Json;
using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.Core.Tests;

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

/// <summary>
/// <see cref="AccountBalance.ParseIsVirtual"/> must fail CLOSED: any missing
/// or malformed is_virtual flag parses as virtual (demo), never as real.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class AccountBalanceIsVirtualTests
{
    private static bool Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AccountBalance.ParseIsVirtual(doc.RootElement);
    }

    [Fact]
    public void RealAccount_NumericFlag_IsNotVirtual()
    {
        Assert.False(Parse("""{"is_virtual": 0}"""));
        Assert.True(Parse("""{"is_virtual": 1}"""));
    }

    [Fact]
    public void BooleanFlag_Parses()
    {
        Assert.True(Parse("""{"is_virtual": true}"""));
        Assert.False(Parse("""{"is_virtual": false}"""));
    }

    [Fact]
    public void StringFlag_Parses()
    {
        Assert.True(Parse("""{"is_virtual": "true"}"""));
        Assert.False(Parse("""{"is_virtual": "false"}"""));
        Assert.True(Parse("""{"is_virtual": "1"}"""));
        Assert.False(Parse("""{"is_virtual": "0"}"""));
    }

    [Fact]
    public void MissingFlag_FailsClosed_AsVirtual()
    {
        Assert.True(Parse("""{"balance": 100.00, "currency": "USD"}"""));
    }

    [Theory]
    [InlineData("\"maybe\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public void MalformedFlag_FailsClosed_AsVirtual(string raw)
    {
        Assert.True(Parse($$"""{"is_virtual": {{raw}}}"""));
    }

    [Fact]
    public void EmptyRecord_DefaultsToVirtual()
    {
        Assert.True(AccountBalance.Empty.IsVirtual);
    }
}

/// <summary>
/// The risk engine must honor the real-money verdict carried on the risk
/// context: a refusal blocks every decision regardless of anything else,
/// while the demo passthrough and an explicit Allow never do. This is the
/// defence-in-depth layer on top of the gate at engine-start time.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class RiskEngineRealMoneyTests
{
    private static AppSettings Settings() => new()
    {
        MaxStake = 10m,
        MaxConcurrentContracts = 1,
        DailyLossCap = 50m,
        MinConfidence = 0.6,
        CooldownMinutesAfterLoss = 15,
        Currency = "USD"
    };

    private static LlmDecision Trade() => new(BrainDirection.Rise, 0.8, 2m, "test");

    private static RiskContext Context(RealMoneyDecision realMoney) => new(
        false, 0, 1000m, 0m, 0, null, null, realMoney);

    [Fact]
    public void BlockedVerdict_RejectsTheTrade()
    {
        foreach (var decision in new[]
        {
            RealMoneyDecision.BlockedAccountIsVirtual,
            RealMoneyDecision.BlockedUnverified,
            RealMoneyDecision.BlockedLocked,
            RealMoneyDecision.BlockedConfigMismatch,
        })
        {
            var verdict = new RiskEngine(Settings()).Evaluate(Trade(), Context(decision));
            Assert.False(verdict.Allowed, $"{decision} must block the trade");
            Assert.Contains("real-money gate", verdict.Reason);
        }
    }

    [Fact]
    public void DemoPassthrough_Default_DoesNotBlock()
    {
        var verdict = new RiskEngine(Settings()).Evaluate(Trade(), Context(RealMoneyDecision.DemoPassthrough));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Allowed_UnlockedReal_DoesNotBlock()
    {
        var verdict = new RiskEngine(Settings()).Evaluate(Trade(), Context(RealMoneyDecision.Allowed));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void GateCheck_ComesAfterKillSwitch()
    {
        var context = new RiskContext(
            true, 0, 1000m, 0m, 0, null, null, RealMoneyDecision.BlockedLocked);
        var verdict = new RiskEngine(Settings()).Evaluate(Trade(), context);
        Assert.False(verdict.Allowed);
        Assert.Contains("kill switch", verdict.Reason);
    }
}
