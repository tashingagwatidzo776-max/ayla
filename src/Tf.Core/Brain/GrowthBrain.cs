using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>
/// Configuration for the deterministic "growth" brain. The profile is built
/// around a small synthetic bankroll (default $5): stakes are fractions of
/// that bankroll, a loss ladder doubles the next stake (up to a step cap),
/// and the session stops at a floor or a daily profit target.
/// </summary>
public sealed record GrowthPlan(
    decimal StartBudget = 5.00m,
    double RiskFraction = 0.20,
    int MaxRecoverySteps = 3,
    double DailyTargetFraction = 1.00,
    double FloorFraction = 0.40,
    decimal MinStake = 1.00m,
    int IntervalMinutes = 1,
    int CooldownMinutesAfterLoss = 1,
    double FailureBackoffSeconds = 5.0,
    int MaxAutoRestarts = 3,
    double RestartBaseDelaySeconds = 5.0,
    double RestartBackoffFactor = 3.0,
    decimal? PortfolioDailyDrawdownCap = null,
    double OversoldRsi = 32.0,
    double OverboughtRsi = 68.0)
{
    public static GrowthPlan Default { get; } = new();

    /// <summary>Parses the portfolio daily-drawdown cap from free text.</summary>
    public static decimal? TryCreateDrawdownCap(string? text) => PlanTextParser.TryCreateDrawdownCap(text);
}

/// <summary>
/// Pure bankroll state machine for one account session. The bankroll starts
/// at the plan budget and moves with every settled <see cref="TradeSource.Growth"/>
/// trade; the brain trades only while the bankroll sits between the floor and
/// the daily target and the recovery ladder is not exhausted. Thread-safe.
/// </summary>
public sealed class GrowthSessionEngine
{
    private readonly GrowthPlan _plan;
    private readonly object _sync = new();

    private decimal _bankroll;
    private int _lossStreak;
    private bool _floorHit;
    private bool _targetHit;

    public GrowthSessionEngine(GrowthPlan plan, decimal startBankroll)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _bankroll = startBankroll;
        StartBankroll = startBankroll;
        _floorHit = startBankroll <= Floor;
        _targetHit = startBankroll >= Target;
    }

    public GrowthPlan Plan => _plan;
    public decimal StartBankroll { get; }

    public decimal Bankroll
    {
        get { lock (_sync) { return _bankroll; } }
    }

    public int LossStreak
    {
        get { lock (_sync) { return _lossStreak; } }
    }

    /// <summary>Bankroll below which no new trades are opened.</summary>
    public decimal Floor => _plan.StartBudget * (decimal)_plan.FloorFraction;

    /// <summary>Bankroll at which the session stops for the day (target hit).</summary>
    public decimal Target => _plan.StartBudget * (1m + (decimal)_plan.DailyTargetFraction);

    public bool FloorHit
    {
        get { lock (_sync) { return _floorHit; } }
    }

    public bool TargetHit
    {
        get { lock (_sync) { return _targetHit; } }
    }

    public bool RecoveryExhausted
    {
        get { lock (_sync) { return _lossStreak >= _plan.MaxRecoverySteps; } }
    }

    public bool CanTrade
    {
        get
        {
            lock (_sync)
            {
                return !_floorHit && !_targetHit && _lossStreak < _plan.MaxRecoverySteps;
            }
        }
    }

    /// <summary>Reason the session is currently unable to trade ("" when it can).</summary>
    public string BlockReason
    {
        get
        {
            lock (_sync)
            {
                if (_floorHit)
                {
                    return $"session floor reached (bankroll {_bankroll:0.##} ≤ {Floor:0.##})";
                }

                if (_targetHit)
                {
                    return $"daily target reached (bankroll {_bankroll:0.##} ≥ {Target:0.##})";
                }

                if (_lossStreak >= _plan.MaxRecoverySteps)
                {
                    return $"recovery ladder exhausted after {_lossStreak} losses";
                }

                return "";
            }
        }
    }

    /// <summary>
    /// Next stake: risk fraction of the bankroll, doubled per consecutive loss,
    /// capped at half the bankroll so a losing streak can never wipe the session
    /// in one step.
    /// </summary>
    public decimal SuggestStake()
    {
        lock (_sync)
        {
            if (_floorHit || _targetHit || _lossStreak >= _plan.MaxRecoverySteps)
            {
                return 0m;
            }

            var multiplier = Math.Pow(2, Math.Min(_lossStreak, _plan.MaxRecoverySteps));
            var raw = _bankroll * (decimal)_plan.RiskFraction * (decimal)multiplier;
            var cap = _bankroll * 0.5m;

            var stake = Math.Min(raw, cap);
            stake = Math.Floor(stake * 100m) / 100m;

            if (stake < _plan.MinStake)
            {
                // Only raise to the minimum when it still respects the cap.
                stake = _plan.MinStake <= cap ? _plan.MinStake : 0m;
            }

            return stake < 0 ? 0m : stake;
        }
    }

    /// <summary>Applies a settled trade. Net profit may be negative for losses.</summary>
    public void ApplySettlement(bool won, decimal netProfit)
    {
        lock (_sync)
        {
            _bankroll += netProfit;
            _lossStreak = won ? 0 : _lossStreak + 1;
            _floorHit = _bankroll <= Floor;
            _targetHit = _bankroll >= Target;
        }
    }
}

/// <summary>
/// Deterministic decision brain for the small-bankroll challenge. No LLM call:
/// it waits for an RSI extreme (mean-reversion setup), then stakes a fraction
/// of the session bankroll via <see cref="GrowthSessionEngine"/>.
/// </summary>
public static class GrowthBrain
{
    // Default thresholds kept as constants for backward compatibility.
    public const double DefaultOversoldRsi = 32.0;
    public const double DefaultOverboughtRsi = 68.0;

    public static LlmDecision Decide(IReadOnlyList<Tick> window, GrowthSessionEngine session)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (!session.CanTrade)
        {
            return LlmDecision.Hold(session.BlockReason);
        }

        if (window is null || window.Count < 21)
        {
            return LlmDecision.Hold("insufficient market data for a signal");
        }

        var closes = window.Select(t => t.Quote).ToArray();
        var rsi = Indicators.Rsi(closes, 14);
        if (double.IsNaN(rsi))
        {
            return LlmDecision.Hold("RSI unavailable — insufficient data");
        }

        // Use the plan's configurable RSI thresholds (defaults: 32/68).
        var oversold = session.Plan.OversoldRsi;
        var overbought = session.Plan.OverboughtRsi;

        BrainDirection direction;
        if (rsi <= oversold)
        {
            direction = BrainDirection.Rise;
        }
        else if (rsi >= overbought)
        {
            direction = BrainDirection.Fall;
        }
        else
        {
            return LlmDecision.Hold($"no extreme signal (RSI {rsi:0.0})");
        }

        var stake = session.SuggestStake();
        if (stake < session.Plan.MinStake)
        {
            return LlmDecision.Hold($"bankroll too small to stake {session.Plan.MinStake:0.##}");
        }

        var extreme = Math.Abs(rsi - 50.0);
        var confidence = Math.Clamp(0.60 + (extreme - (50.0 - oversold)) / overbought * 0.35, 0.60, 0.95);

        var label = direction == BrainDirection.Rise ? "RISE (oversold bounce)" : "FALL (overbought pullback)";
        return new LlmDecision(
            direction,
            confidence,
            stake,
            $"{label} · RSI {rsi:0.0} · bankroll {session.Bankroll:0.##} · " +
            $"streak {session.LossStreak} loss(es)");
    }
}
