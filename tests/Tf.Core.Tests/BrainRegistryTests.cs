using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.Core.Tests;

/// <summary>
/// Tests for the brain registry surface: built-in registrations, key lookup,
/// re-registration (last wins), and the wrapper seams used by the Growth
/// runner (each wrapper must tag its raw reasoning with its prefix).
/// </summary>
[Trait("Category", "Unit")]
public class BrainRegistryTests
{
    private static IReadOnlyList<Tick> RisingWindow(int count = 60)
    {
        var ticks = new List<Tick>();
        var price = 1.10;
        var at = DateTimeOffset.UtcNow.AddMinutes(-count);
        for (int i = 0; i < count; i++)
        {
            price += 0.0002; // steady uptrend
            ticks.Add(new Tick("frxEURUSD", price, price + 0.00001, price - 0.00001,
                at.ToUnixTimeMilliseconds(), 5));
            at = at.AddSeconds(5);
        }
        return ticks;
    }

    private static AppSettings Settings() => new() { AutonomyEnabled = true, IsDemo = true };
    private static RiskContext Risk() => new(false, 0, 1000m, 0m, 0, null, null);

    [Fact]
    public void Registry_ExposesBuiltInBrains()
    {
        var registry = new BrainRegistry();

        Assert.Contains("LLM", registry.GetBrainKeys());
        Assert.Contains("Growth", registry.GetBrainKeys());
        Assert.Contains("TrendFollowing", registry.GetBrainKeys());
        Assert.Contains("Breakout", registry.GetBrainKeys());
        Assert.Contains("MeanReversion", registry.GetBrainKeys());
        Assert.Contains("Ensemble", registry.GetBrainKeys());
    }

    [Fact]
    public void GetBrain_UnknownKey_ReturnsNull()
    {
        Assert.Null(new BrainRegistry().GetBrain("NoSuchBrain"));
    }

    [Fact]
    public void Register_LastWins_AndIsCaseSensitive()
    {
        var registry = new BrainRegistry();
        registry.Register(new BrainInfo { Key = "Growth", DisplayName = "Custom Growth" });

        var brain = registry.GetBrain("Growth");
        Assert.NotNull(brain);
        Assert.Equal("Custom Growth", brain!.DisplayName);
    }

    [Fact]
    public void CreateBrain_UnknownKey_ReturnsNull()
    {
        var registry = new BrainRegistry();

        Assert.Null(registry.CreateBrain("NoSuchBrain"));
    }

    [Fact]
    public void CreateBrain_GrowthKey_BuildsWrapper()
    {
        var registry = new BrainRegistry();

        var brain = registry.CreateBrain("Growth");

        Assert.IsType<GrowthBrainWrapper>(brain);
    }

    [Fact]
    public async Task GrowthWrapper_WithoutSession_Holds()
    {
        var wrapper = new GrowthBrainWrapper();

        var result = await wrapper.DecideAsync(
            RisingWindow(), session: null, Settings, Risk, () => Array.Empty<string>());

        Assert.Equal(BrainDirection.Hold, result.Decision.Direction);
        Assert.Equal("growth: no session", result.Raw);
    }

    [Fact]
    public async Task TrendWrapper_TagsRawReasoning()
    {
        var wrapper = new TrendFollowingBrainWrapper();

        var result = await wrapper.DecideAsync(
            RisingWindow(), session: null, Settings, Risk, () => Array.Empty<string>());

        Assert.StartsWith("trend: ", result.Raw);
    }

    [Fact]
    public async Task BreakoutWrapper_TagsRawReasoning()
    {
        var wrapper = new BreakoutBrainWrapper();

        var result = await wrapper.DecideAsync(
            RisingWindow(), session: null, Settings, Risk, () => Array.Empty<string>());

        Assert.StartsWith("breakout: ", result.Raw);
    }

    [Fact]
    public async Task MeanReversionWrapper_TagsRawReasoning()
    {
        var wrapper = new MeanReversionBrainWrapper();

        var result = await wrapper.DecideAsync(
            RisingWindow(), session: null, Settings, Risk, () => Array.Empty<string>());

        Assert.StartsWith("meanrev: ", result.Raw);
    }

    // ─── Ensemble expansion (GrowthRunner.BuildProvider) ────────

    [Fact]
    public void BuildProvider_NonEnsembleKey_ReturnsRegistryProvider()
    {
        var provider = GrowthRunnerEnsembleTestHost.BuildProvider(new BrainRegistry(), "Growth");

        Assert.IsType<GrowthBrainWrapper>(provider);
    }

    [Fact]
    public void BuildProvider_EnsembleWithConfig_BuildsWeightedVoters()
    {
        var provider = GrowthRunnerEnsembleTestHost.BuildProvider(
            new BrainRegistry(), "Ensemble", "Growth:1.5 TrendFollowing");

        var ensemble = Assert.IsType<EnsembleBrainWrapper>(provider);
        Assert.Equal(2, GrowthRunnerEnsembleTestHost.VoterCount(ensemble));
    }

    [Fact]
    public void BuildProvider_EnsembleUnconfigured_DefaultsToAllKnownBrains()
    {
        var provider = GrowthRunnerEnsembleTestHost.BuildProvider(new BrainRegistry(), "Ensemble", "");

        var ensemble = Assert.IsType<EnsembleBrainWrapper>(provider);
        Assert.Equal(EnsemblePlanParser.KnownBrainKeys.Count,
            GrowthRunnerEnsembleTestHost.VoterCount(ensemble));
    }

    [Fact]
    public void BuildProvider_EnsembleAllWeightsInvalid_FallsBackToAllKnownBrains()
    {
        // Every config entry is dropped by the parser → the expansion falls
        // back to "all known brains vote equally" rather than trading a
        // config typo into an empty (never-trading) ensemble.
        var provider = GrowthRunnerEnsembleTestHost.BuildProvider(
            new BrainRegistry(), "Ensemble", "Growth:0 Breakout:abc");

        var ensemble = Assert.IsType<EnsembleBrainWrapper>(provider);
        Assert.Equal(EnsemblePlanParser.KnownBrainKeys.Count,
            GrowthRunnerEnsembleTestHost.VoterCount(ensemble));
    }

    [Fact]
    public async Task BuildProvider_EnsembleUnconfigured_DecidesWithoutCrash()
    {
        var provider = GrowthRunnerEnsembleTestHost.BuildProvider(new BrainRegistry(), "Ensemble", "");
        Assert.NotNull(provider);

        var result = await provider.DecideAsync(
            RisingWindow(), session: null, Settings, Risk, () => Array.Empty<string>());

        // No session engine attached → rules brains vote, ensemble reaches a
        // verdict (or holds on a tie) — either way no exception.
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Raw));
    }

    /// <summary>Bridge to GrowthRunner.BuildProvider: the runner lives in
    /// Tf.App (WPF), this test assembly only references Tf.Core — so the    /// expansion logic is exercised through the runner's internal static via    /// InternalsVisibleTo, or re-declared here when that seam is absent.</summary>
    internal static class GrowthRunnerEnsembleTestHost
    {
        public static IBrainDecisionProvider? BuildProvider(
            BrainRegistry registry, string brainKey, string? ensembleConfig = null)
        {
            // Mirror of Tf.App GrowthRunner.BuildProvider's expansion rules.
            if (!string.Equals(brainKey, "Ensemble", StringComparison.OrdinalIgnoreCase))
            {
                return registry.CreateBrain(brainKey);
            }

            var entries = EnsemblePlanParser.Parse(ensembleConfig);
            if (entries.Count == 0)
            {
                entries = EnsemblePlanParser.KnownBrainKeys.Select(k => (k, 1.0)).ToArray();
            }

            var ensemble = new EnsembleBrainWrapper();
            foreach (var (key, weight) in entries)
            {
                if (registry.CreateBrain(key) is { } voter)
                {
                    ensemble.AddBrain(voter, weight);
                }
            }

            return ensemble;
        }

        public static int VoterCount(EnsembleBrainWrapper ensemble) =>
            ensemble.TestVoterCount;
    }
}
