using DongGfx.Core;
using DongGfx.Core.Brain;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class IndicatorTests
{
    [Fact]
    public void Sma_ComputesAverageOfLastPeriod()
    {
        var closes = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        Assert.Equal(3.0, Indicators.Sma(closes, 5), 10);
        Assert.Equal(4.5, Indicators.Sma(closes, 2), 10);
    }

    [Fact]
    public void Sma_ReturnsNaN_WhenNotEnoughData()
    {
        Assert.True(double.IsNaN(Indicators.Sma(new[] { 1.0, 2.0 }, 5)));
    }

    [Fact]
    public void Rsi_IsHighInUptrend_LowInDowntrend()
    {
        var uptrend = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        var downtrend = Enumerable.Range(1, 20).Select(i => (double)(21 - i)).ToArray();

        var upRsi = Indicators.Rsi(uptrend);
        var downRsi = Indicators.Rsi(downtrend);

        Assert.True(upRsi > 70, $"expected overbought-ish, got {upRsi}");
        Assert.True(downRsi < 30, $"expected oversold-ish, got {downRsi}");
    }

    [Fact]
    public void PercentChange_MeasuresWindowMove()
    {
        Assert.Equal(10.0, Indicators.PercentChange(new[] { 100.0, 110.0 }), 10);
        Assert.Equal(-50.0, Indicators.PercentChange(new[] { 100.0, 50.0 }), 10);
        Assert.Equal(0.0, Indicators.PercentChange(new[] { 42.0 }));
    }
}

[Trait("Category", "Unit")]
public class DecisionParserTests
{
    [Fact]
    public void Parse_ValidJson_ReturnsDecision()
    {
        var d = DecisionParser.Parse(
            "{\"direction\":\"RISE\",\"confidence\":0.8,\"stake\":2.5,\"reasoning\":\"trend up\"}");

        Assert.Equal(BrainDirection.Rise, d.Direction);
        Assert.Equal(0.8, d.Confidence, 5);
        Assert.Equal(2.5m, d.Stake);
        Assert.Equal("trend up", d.Reasoning);
    }

    [Fact]
    public void Parse_AcceptCaseInsensitiveDirections()
    {
        Assert.Equal(BrainDirection.Fall, DecisionParser.Parse(
            "{\"direction\":\"fall\",\"confidence\":0.5,\"stake\":1}").Direction);
        Assert.Equal(BrainDirection.Rise, DecisionParser.Parse(
            "{\"direction\":\"CALL\",\"confidence\":0.5,\"stake\":1}").Direction);
    }

    [Fact]
    public void Parse_ClampsConfidenceAndStake()
    {
        var d = DecisionParser.Parse(
            "{\"direction\":\"RISE\",\"confidence\":7,\"stake\":-3,\"reasoning\":\"\"}");
        Assert.Equal(1.0, d.Confidence, 5);
        Assert.Equal(0m, d.Stake);
    }

    [Fact]
    public void Parse_HandlesMarkdownFences()
    {
        var d = DecisionParser.Parse(
            "```json\n{\"direction\":\"HOLD\",\"confidence\":0.2,\"stake\":0,\"reasoning\":\"no edge\"}\n```");
        Assert.Equal(BrainDirection.Hold, d.Direction);
    }

    [Fact]
    public void Parse_Garbage_DegradesToHold()
    {
        var d = DecisionParser.Parse("I think the market will go up!");
        Assert.Equal(BrainDirection.Hold, d.Direction);
        Assert.Contains("Unparseable", d.Reasoning);
    }

    [Fact]
    public void Parse_MissingFields_DefaultsToHold()
    {
        var d = DecisionParser.Parse("{\"direction\":\"SIDEWAYS\"}");
        Assert.Equal(BrainDirection.Hold, d.Direction);
        Assert.Equal(0, d.Confidence, 5);
    }
}

[Trait("Category", "Unit")]
public class RiskEngineTests
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

    private static LlmDecision Trade(double confidence = 0.8, decimal stake = 2m) =>
        new(BrainDirection.Rise, confidence, stake, "test");

    [Fact]
    public void Allows_ValidTrade()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(Trade(), new RiskContext(
            false, 0, 1000m, 0m, 0, null, null));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Rejects_KillSwitch()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(Trade(), new RiskContext(
            true, 0, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
        Assert.Contains("kill switch", verdict.Reason);
    }

    [Fact]
    public void Rejects_Hold()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(new LlmDecision(BrainDirection.Hold, 0.9, 2m, "wait"),
            new RiskContext(false, 0, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Rejects_LowConfidence()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(Trade(confidence: 0.4), new RiskContext(
            false, 0, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
        Assert.Contains("confidence", verdict.Reason);
    }

    [Fact]
    public void Rejects_StakeAboveMax()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(Trade(stake: 50m), new RiskContext(
            false, 0, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
        Assert.Contains("max", verdict.Reason);
    }

    [Fact]
    public void Rejects_AtMaxConcurrent()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(Trade(), new RiskContext(
            false, 1, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
        Assert.Contains("concurrent", verdict.Reason);
    }

    [Fact]
    public void Rejects_DailyLossCap()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(Trade(), new RiskContext(
            false, 0, 1000m, -52m, 5, null, null));
        Assert.False(verdict.Allowed);
        Assert.Contains("loss cap", verdict.Reason);
    }

    [Fact]
    public void Rejects_CooldownAfterLoss()
    {
        var engine = new RiskEngine(Settings());
        var recentLoss = new RiskContext(
            false, 0, 1000m, -2m, 1,
            DateTimeOffset.UtcNow.AddMinutes(-2), ContractStatus.Lost);
        var verdict = engine.Evaluate(Trade(), recentLoss);
        Assert.False(verdict.Allowed);
        Assert.Contains("cooldown", verdict.Reason);
    }

    [Fact]
    public void Allows_AfterCooldownElapsed()
    {
        var engine = new RiskEngine(Settings());
        var oldLoss = new RiskContext(
            false, 0, 1000m, -2m, 1,
            DateTimeOffset.UtcNow.AddMinutes(-30), ContractStatus.Lost);
        Assert.True(engine.Evaluate(Trade(), oldLoss).Allowed);
    }
}