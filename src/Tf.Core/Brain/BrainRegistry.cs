using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>
/// Registry of available brain types. Each brain is identified by a key
/// and can be instantiated to produce trading decisions.
/// </summary>
public sealed class BrainRegistry
{
    private readonly Dictionary<string, BrainInfo> _brains = new();

    public BrainRegistry()
    {
        // Register all built-in brains
        Register(new BrainInfo
        {
            Key = "LLM",
            DisplayName = "LLM Brain",
            Description = "AI-powered decisions using local or cloud LLM",
            RequiresLlm = true,
            Factory = () => null // LLM brain needs special setup
        });

        Register(new BrainInfo
        {
            Key = "Growth",
            DisplayName = "Growth Brain",
            Description = "Deterministic rules for $5 challenge (RSI mean-reversion)",
            RequiresLlm = false,
            Factory = () => new GrowthBrainWrapper()
        });

        Register(new BrainInfo
        {
            Key = "TrendFollowing",
            DisplayName = "Trend Following",
            Description = "EMA crossover + ADX trend strength",
            RequiresLlm = false,
            Factory = () => new TrendFollowingBrainWrapper()
        });

        Register(new BrainInfo
        {
            Key = "Breakout",
            DisplayName = "Breakout",
            Description = "Bollinger Bands + ATR breakouts",
            RequiresLlm = false,
            Factory = () => new BreakoutBrainWrapper()
        });

        Register(new BrainInfo
        {
            Key = "MeanReversion",
            DisplayName = "Mean Reversion",
            Description = "RSI + Z-score configurable mean reversion",
            RequiresLlm = false,
            Factory = () => new MeanReversionBrainWrapper()
        });

        Register(new BrainInfo
        {
            Key = "Ensemble",
            DisplayName = "Ensemble",
            Description = "Combines multiple brains with weighted voting",
            RequiresLlm = false,
            Factory = () => new EnsembleBrainWrapper()
        });
    }

    public void Register(BrainInfo brain)
    {
        _brains[brain.Key] = brain;
    }

    public BrainInfo? GetBrain(string key)
    {
        return _brains.TryGetValue(key, out var brain) ? brain : null;
    }

    public IReadOnlyList<BrainInfo> GetAllBrains()
    {
        return _brains.Values.ToArray();
    }

    public IReadOnlyList<string> GetBrainKeys()
    {
        return _brains.Keys.ToArray();
    }

    public IBrainDecisionProvider? CreateBrain(string key)
    {
        var info = GetBrain(key);
        return info?.Factory();
    }
}

public sealed class BrainInfo
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool RequiresLlm { get; set; }
    public Func<IBrainDecisionProvider?> Factory { get; set; } = () => null;
}

/// <summary>Interface for all brain decision providers.</summary>
public interface IBrainDecisionProvider
{
    Task<BrainDecision> DecideAsync(
        IReadOnlyList<Tick> window,
        GrowthSessionEngine? session,
        Func<AppSettings> settings,
        Func<RiskContext> risk,
        Func<IReadOnlyList<string>> lessons,
        CancellationToken ct = default);
}

/// <summary>Wrapper for GrowthBrain that implements IBrainDecisionProvider.</summary>
public sealed class GrowthBrainWrapper : IBrainDecisionProvider
{
    public Task<BrainDecision> DecideAsync(
        IReadOnlyList<Tick> window,
        GrowthSessionEngine? session,
        Func<AppSettings> settings,
        Func<RiskContext> risk,
        Func<IReadOnlyList<string>> lessons,
        CancellationToken ct = default)
    {
        if (session == null)
            return Task.FromResult(new BrainDecision(LlmDecision.Hold("No session engine"), "growth: no session"));

        var decision = GrowthBrain.Decide(window, session);
        return Task.FromResult(new BrainDecision(decision, $"growth: {decision.Reasoning}"));
    }
}

/// <summary>Wrapper for TrendFollowingBrain.</summary>
public sealed class TrendFollowingBrainWrapper : IBrainDecisionProvider
{
    public Task<BrainDecision> DecideAsync(
        IReadOnlyList<Tick> window,
        GrowthSessionEngine? session,
        Func<AppSettings> settings,
        Func<RiskContext> risk,
        Func<IReadOnlyList<string>> lessons,
        CancellationToken ct = default)
    {
        var decision = TrendFollowingBrain.Decide(window, TrendFollowingBrain.TrendConfig.Default);

        // If brain suggests a direction, use session to determine stake
        if (decision.Direction != BrainDirection.Hold && session != null)
        {
            var stake = session.SuggestStake();
            decision = new LlmDecision(decision.Direction, decision.Confidence, stake, decision.Reasoning);
        }

        return Task.FromResult(new BrainDecision(decision, $"trend: {decision.Reasoning}"));
    }
}

/// <summary>Wrapper for BreakoutBrain.</summary>
public sealed class BreakoutBrainWrapper : IBrainDecisionProvider
{
    public Task<BrainDecision> DecideAsync(
        IReadOnlyList<Tick> window,
        GrowthSessionEngine? session,
        Func<AppSettings> settings,
        Func<RiskContext> risk,
        Func<IReadOnlyList<string>> lessons,
        CancellationToken ct = default)
    {
        var decision = BreakoutBrain.Decide(window, BreakoutBrain.BreakoutConfig.Default);

        if (decision.Direction != BrainDirection.Hold && session != null)
        {
            var stake = session.SuggestStake();
            decision = new LlmDecision(decision.Direction, decision.Confidence, stake, decision.Reasoning);
        }

        return Task.FromResult(new BrainDecision(decision, $"breakout: {decision.Reasoning}"));
    }
}

/// <summary>Wrapper for MeanReversionBrain.</summary>
public sealed class MeanReversionBrainWrapper : IBrainDecisionProvider
{
    public Task<BrainDecision> DecideAsync(
        IReadOnlyList<Tick> window,
        GrowthSessionEngine? session,
        Func<AppSettings> settings,
        Func<RiskContext> risk,
        Func<IReadOnlyList<string>> lessons,
        CancellationToken ct = default)
    {
        var decision = MeanReversionBrain.Decide(window, MeanReversionBrain.MeanReversionConfig.Default);

        if (decision.Direction != BrainDirection.Hold && session != null)
        {
            var stake = session.SuggestStake();
            decision = new LlmDecision(decision.Direction, decision.Confidence, stake, decision.Reasoning);
        }

        return Task.FromResult(new BrainDecision(decision, $"meanrev: {decision.Reasoning}"));
    }
}

/// <summary>
/// Ensemble brain that combines multiple brains with weighted voting.
/// Each brain votes, and the final decision is based on majority with confidence weighting.
/// </summary>
public sealed class EnsembleBrainWrapper : IBrainDecisionProvider
{
    private readonly List<(IBrainDecisionProvider Brain, double Weight)> _brains = new();

    public void AddBrain(IBrainDecisionProvider brain, double weight = 1.0)
    {
        _brains.Add((brain, weight));
    }

    public async Task<BrainDecision> DecideAsync(
        IReadOnlyList<Tick> window,
        GrowthSessionEngine? session,
        Func<AppSettings> settings,
        Func<RiskContext> risk,
        Func<IReadOnlyList<string>> lessons,
        CancellationToken ct = default)
    {
        if (_brains.Count == 0)
        {
            return new BrainDecision(
                LlmDecision.Hold("Ensemble has no brains configured"),
                "ensemble: no brains");
        }

        var votes = new List<(BrainDirection Direction, double Confidence, double Weight, string Reasoning)>();

        foreach (var (brain, weight) in _brains)
        {
            var brainResult = await brain.DecideAsync(window, session, settings, risk, lessons, ct).ConfigureAwait(false);
            if (brainResult.Decision.Direction != BrainDirection.Hold)
            {
                votes.Add((brainResult.Decision.Direction, brainResult.Decision.Confidence, weight, brainResult.Raw));
            }
        }

        if (votes.Count == 0)
        {
            return new BrainDecision(
                LlmDecision.Hold("No brain voted for a trade"),
                "ensemble: no consensus");
        }

        // Weighted vote
        var riseWeight = votes.Where(v => v.Direction == BrainDirection.Rise).Sum(v => v.Weight * v.Confidence);
        var fallWeight = votes.Where(v => v.Direction == BrainDirection.Fall).Sum(v => v.Weight * v.Confidence);

        BrainDirection finalDirection;
        double finalConfidence;
        string reasoning;

        if (riseWeight > fallWeight && riseWeight > 0)
        {
            finalDirection = BrainDirection.Rise;
            finalConfidence = riseWeight / _brains.Count;
            reasoning = $"ensemble rise ({votes.Count(v => v.Direction == BrainDirection.Rise)} votes, strength {riseWeight:0.2})";
        }
        else if (fallWeight > riseWeight && fallWeight > 0)
        {
            finalDirection = BrainDirection.Fall;
            finalConfidence = fallWeight / _brains.Count;
            reasoning = $"ensemble fall ({votes.Count(v => v.Direction == BrainDirection.Fall)} votes, strength {fallWeight:0.2})";
        }
        else
        {
            return new BrainDecision(
                LlmDecision.Hold("Ensemble split - no clear majority"),
                $"ensemble: tied (rise {riseWeight:0.2} vs fall {fallWeight:0.2})");
        }

        // Use session for stake if available
        decimal stake = 0;
        if (session != null)
        {
            stake = session.SuggestStake();
        }

        var finalDecision = new LlmDecision(finalDirection, Math.Clamp(finalConfidence, 0.6, 0.95), stake, reasoning);
        return new BrainDecision(finalDecision, reasoning);
    }
}
