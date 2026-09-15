using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>Live state the risk engine needs to evaluate a decision.</summary>
public sealed record RiskContext(
    bool KillSwitchEngaged,
    int OpenContracts,
    decimal Balance,
    decimal DailyNetProfit,
    int TradesToday,
    DateTimeOffset? LastTradeAt,
    ContractStatus? LastTradeOutcome);

public sealed record RiskVerdict(bool Allowed, string Reason)
{
    public static RiskVerdict Allow() => new(true, "ok");
    public static RiskVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// Guardrails that must ALL pass before a trade is placed: kill switch,
/// confidence floor, stake bounds, concurrent-contract limit, daily loss
/// cap, and a post-loss cooldown. Every rejection carries its reason.
/// </summary>
public sealed class RiskEngine
{
    private readonly AppSettings _settings;
    private readonly TimeProvider _timeProvider;

    public RiskEngine(AppSettings settings, TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RiskVerdict Evaluate(LlmDecision decision, RiskContext context)
    {
        if (context.KillSwitchEngaged)
        {
            return RiskVerdict.Reject("master kill switch is engaged");
        }

        if (decision.Direction == BrainDirection.Hold)
        {
            return RiskVerdict.Reject("brain chose HOLD — no trade");
        }

        if (decision.Confidence < _settings.MinConfidence)
        {
            return RiskVerdict.Reject(
                $"confidence {decision.Confidence:P0} below minimum {_settings.MinConfidence:P0}");
        }

        if (decision.Stake <= 0)
        {
            return RiskVerdict.Reject("stake must be positive");
        }

        if (decision.Stake > _settings.MaxStake)
        {
            return RiskVerdict.Reject(
                $"stake {decision.Stake:0.##} exceeds max {_settings.MaxStake:0.##}");
        }

        if (context.OpenContracts >= _settings.MaxConcurrentContracts)
        {
            return RiskVerdict.Reject(
                $"already at max concurrent contracts ({context.OpenContracts}/{_settings.MaxConcurrentContracts})");
        }

        if (context.DailyNetProfit <= -_settings.DailyLossCap)
        {
            return RiskVerdict.Reject(
                $"daily loss cap reached (net {context.DailyNetProfit:0.##} ≤ -{_settings.DailyLossCap:0.##})");
        }

        if (context.LastTradeOutcome == ContractStatus.Lost && context.LastTradeAt is { } lastTrade &&
            _timeProvider.GetUtcNow() - lastTrade < TimeSpan.FromMinutes(_settings.CooldownMinutesAfterLoss))
        {
            var remaining = _settings.CooldownMinutesAfterLoss - (_timeProvider.GetUtcNow() - lastTrade).TotalMinutes;
            return RiskVerdict.Reject(
                $"cooldown after loss — {remaining:0.0} min remaining");
        }

        return RiskVerdict.Allow();
    }
}