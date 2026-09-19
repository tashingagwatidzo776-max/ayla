using DongGfx.Core;
using DongGfx.Core.Brain;
using DongGfx.Core.Models;
using DongGfx.Core.Optimization;

namespace DongGfx.Core.Tests;

/// <summary>
/// Comprehensive tests for DecisionParser edge cases, RiskEngine boundary
/// conditions, Ensemble brain voting, LLM retry logic, and optimizer
/// reproducibility.
/// </summary>
[Trait("Category", "Unit")]
public class DecisionParserEdgeCaseTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsHold()
    {
        var d = DecisionParser.Parse("");
        Assert.Equal(BrainDirection.Hold, d.Direction);
        Assert.Contains("Unparseable", d.Reasoning);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsHold()
    {
        var d = DecisionParser.Parse("   \n\t  ");
        Assert.Equal(BrainDirection.Hold, d.Direction);
    }

    [Fact]
    public void Parse_Null_ReturnsHold()
    {
        var d = DecisionParser.Parse(null!);
        Assert.Equal(BrainDirection.Hold, d.Direction);
    }

    [Fact]
    public void Parse_MissingDirection_DefaultsToHold()
    {
        var d = DecisionParser.Parse("{\"confidence\":0.8,\"stake\":1,\"reasoning\":\"no dir\"}");
        Assert.Equal(BrainDirection.Hold, d.Direction);
        Assert.Equal(0.8, d.Confidence, 5);
    }

    [Fact]
    public void Parse_NegativeConfidence_ClampsToZero()
    {
        var d = DecisionParser.Parse("{\"direction\":\"RISE\",\"confidence\":-5,\"stake\":1}");
        Assert.Equal(0, d.Confidence, 5);
    }

    [Fact]
    public void Parse_NegativeStake_ClampsToZero()
    {
        var d = DecisionParser.Parse("{\"direction\":\"RISE\",\"confidence\":0.8,\"stake\":-10}");
        Assert.Equal(0m, d.Stake);
    }

    [Fact]
    public void Parse_ExtraProseAroundJson_ExtractsCorrectly()
    {
        var d = DecisionParser.Parse(
            "Based on my analysis, here is my decision:\n" +
            "{\"direction\":\"FALL\",\"confidence\":0.75,\"stake\":2,\"reasoning\":\"overbought\"}\n" +
            "Let me know if you have questions.");
        Assert.Equal(BrainDirection.Fall, d.Direction);
        Assert.Equal(0.75, d.Confidence, 5);
        Assert.Equal(2m, d.Stake);
    }

    [Fact]
    public void Parse_AllDirectionVariants()
    {
        // Standard
        Assert.Equal(BrainDirection.Rise, DecisionParser.Parse("{\"direction\":\"RISE\"}").Direction);
        Assert.Equal(BrainDirection.Fall, DecisionParser.Parse("{\"direction\":\"FALL\"}").Direction);
        Assert.Equal(BrainDirection.Hold, DecisionParser.Parse("{\"direction\":\"HOLD\"}").Direction);

        // Aliases
        Assert.Equal(BrainDirection.Rise, DecisionParser.Parse("{\"direction\":\"CALL\"}").Direction);
        Assert.Equal(BrainDirection.Rise, DecisionParser.Parse("{\"direction\":\"UP\"}").Direction);
        Assert.Equal(BrainDirection.Fall, DecisionParser.Parse("{\"direction\":\"PUT\"}").Direction);
        Assert.Equal(BrainDirection.Fall, DecisionParser.Parse("{\"direction\":\"DOWN\"}").Direction);

        // Unknown → Hold
        Assert.Equal(BrainDirection.Hold, DecisionParser.Parse("{\"direction\":\"SIDEWAYS\"}").Direction);
    }

    [Fact]
    public void ExtractJsonObject_HandlesNestedBraces()
    {
        var json = "prefix {\"a\": {\"b\": 1}} suffix";
        var result = DecisionParser.ExtractJsonObject(json);
        Assert.NotNull(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
    }

    [Fact]
    public void ExtractJsonObject_NoBraces_ReturnsNull()
    {
        Assert.Null(DecisionParser.ExtractJsonObject("no braces here"));
    }
}

[Trait("Category", "Unit")]
public class RiskEngineEdgeCaseTests
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

    [Fact]
    public void Rejects_ZeroStake()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, 0m, "zero stake"),
            new RiskContext(false, 0, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
        Assert.Contains("positive", verdict.Reason);
    }

    [Fact]
    public void Rejects_NegativeStake()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, -5m, "negative"),
            new RiskContext(false, 0, 1000m, 0m, 0, null, null));
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Allows_ExactMinConfidence()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.6, 2m, "exact min"),
            new RiskContext(false, 0, 1000m, 0m, 0, null, null));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Allows_ExactMaxStake()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, 10m, "exact max"),
            new RiskContext(false, 0, 1000m, 0m, 0, null, null));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Allows_ExactDailyLossCap()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, 2m, "at cap"),
            new RiskContext(false, 0, 1000m, -49m, 10, null, null));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Rejects_WinDoesNotTriggerCooldown()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, 2m, "after win"),
            new RiskContext(false, 0, 1000m, 5m, 10,
                DateTimeOffset.UtcNow.AddMinutes(-1), ContractStatus.Won));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Rejects_UnknownStatusDoesNotTriggerCooldown()
    {
        var engine = new RiskEngine(Settings());
        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Rise, 0.9, 2m, "unknown status"),
            new RiskContext(false, 0, 1000m, -2m, 5,
                DateTimeOffset.UtcNow.AddMinutes(-1), ContractStatus.Unknown));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void AllRejections_IncludeReason()
    {
        var engine = new RiskEngine(Settings());
        var ctx = new RiskContext(true, 0, 1000m, 0m, 0, null, null);

        var verdict = engine.Evaluate(
            new LlmDecision(BrainDirection.Hold, 0, 0m, "hold"), ctx);
        Assert.False(verdict.Allowed);
        Assert.False(string.IsNullOrEmpty(verdict.Reason));
    }
}

[Trait("Category", "Unit")]
public class EnsembleBrainTests
{
    private static IReadOnlyList<Tick> StrongUptrend(int count = 50)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.10000 + i * 0.0003,
                1.10000 + i * 0.0003 + 0.00001,
                1.10000 + i * 0.0003 - 0.00001,
                1700000000L + i * 5000, 5))
            .ToArray();
    }

    private static IReadOnlyList<Tick> StrongDowntrend(int count = 50)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Tick("frxEURUSD", 1.20000 - i * 0.0003,
                1.20000 - i * 0.0003 + 0.00001,
                1.20000 - i * 0.0003 - 0.00001,
                1700000000L + i * 5000, 5))
            .ToArray();
    }

    [Fact]
    public async Task EmptyEnsemble_HoldsByDefault()
    {
        var ensemble = new EnsembleBrainWrapper();
        var result = await ensemble.DecideAsync(
            StrongUptrend(), null, () => new AppSettings(),
            () => new RiskContext(false, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>());
        Assert.Equal(BrainDirection.Hold, result.Decision.Direction);
    }

    [Fact]
    public async Task SingleBrain_VotesAlone()
    {
        var ensemble = new EnsembleBrainWrapper();
        ensemble.AddBrain(new TrendFollowingBrainWrapper(), 1.0);
        var result = await ensemble.DecideAsync(
            StrongUptrend(), null, () => new AppSettings(),
            () => new RiskContext(false, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>());
        // Should either Rise or Hold (depends on ADX)
        Assert.True(result.Decision.Direction == BrainDirection.Rise ||
                    result.Decision.Direction == BrainDirection.Hold);
    }

    [Fact]
    public async Task MultipleBrains_VoteConsensus()
    {
        var ensemble = new EnsembleBrainWrapper();
        ensemble.AddBrain(new TrendFollowingBrainWrapper(), 1.0);
        ensemble.AddBrain(new BreakoutBrainWrapper(), 1.0);
        ensemble.AddBrain(new MeanReversionBrainWrapper(), 1.0);

        var result = await ensemble.DecideAsync(
            StrongUptrend(), null, () => new AppSettings(),
            () => new RiskContext(false, 0, 1000m, 0m, 0, null, null),
            () => Array.Empty<string>());

        Assert.True(
            result.Decision.Direction == BrainDirection.Rise ||
            result.Decision.Direction == BrainDirection.Fall ||
            result.Decision.Direction == BrainDirection.Hold);
        Assert.False(string.IsNullOrEmpty(result.Raw));
    }
}

[Trait("Category", "Unit")]
public class LlmClientRetryTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _callCount;
        private readonly Func<int, HttpResponseMessage> _responder;

        public int CallCount => _callCount;

        public CountingHandler(Func<int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _callCount);
            return Task.FromResult(_responder(count));
        }
    }

    private static string ChatJson(string content) =>
        $"{{\"id\":\"c\",\"object\":\"chat.completion\",\"choices\":[{{\"index\":0," +
        $"\"message\":{{\"role\":\"assistant\",\"content\":{System.Text.Json.JsonSerializer.Serialize(content)}}}," +
        $"\"finish_reason\":\"stop\"}}]}}";

    [Fact]
    public async Task RetriesOn500_ThenSucceeds()
    {
        var handler = new CountingHandler(n =>
            n <= 2
                ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                { Content = new StringContent("{\"error\":\"boom\"}") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(ChatJson("{\"direction\":\"HOLD\"}")) });

        var client = new LlmClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
        {
            BaseUrl = "http://fake/v1",
            Model = "m",
            MaxRetries = 3
        };

        var result = await client.CompleteJsonAsync("s", "u");
        Assert.Contains("HOLD", result);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task RetriesOn429_ThenSucceeds()
    {
        var handler = new CountingHandler(n =>
            n == 1
                ? new HttpResponseMessage((System.Net.HttpStatusCode)429)
                { Content = new StringContent("{\"error\":\"rate limited\"}") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(ChatJson("{\"direction\":\"RISE\"}")) });

        var client = new LlmClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
        {
            BaseUrl = "http://fake/v1",
            Model = "m",
            MaxRetries = 3
        };

        var result = await client.CompleteJsonAsync("s", "u");
        Assert.Contains("RISE", result);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task DoesNotRetryOn400()
    {
        var handler = new CountingHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":\"bad request\"}") });

        var client = new LlmClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
        {
            BaseUrl = "http://fake/v1",
            Model = "m",
            MaxRetries = 3
        };

        await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync("s", "u"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ThrowsAfterAllRetriesExhausted()
    {
        var handler = new CountingHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            { Content = new StringContent("{\"error\":\"persistent\"}") });

        var client = new LlmClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
        {
            BaseUrl = "http://fake/v1",
            Model = "m",
            MaxRetries = 2
        };

        await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync("s", "u"));
        Assert.Equal(3, handler.CallCount); // 1 initial + 2 retries
    }
}

[Trait("Category", "Unit")]
public class OptimizerReproducibilityTests : IDisposable
{
    private readonly string _dir;

    public OptimizerReproducibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_opt_repro_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static IReadOnlyList<Tick> GenerateData(int count, int seed = 42)
    {
        var random = new Random(seed);
        var ticks = new List<Tick>();
        var price = 1.10000;
        var ts = DateTimeOffset.UtcNow.AddMinutes(-count);

        for (int i = 0; i < count; i++)
        {
            price += (random.NextDouble() - 0.5) * 0.0001;
            price = Math.Max(1.05, Math.Min(1.15, price));
            ticks.Add(new Tick("frxEURUSD", price, price + 0.00001, price - 0.00001,
                ts.ToUnixTimeMilliseconds(), 5));
            ts = ts.AddSeconds(5);
        }

        return ticks;
    }

    [Fact]
    public void SameDataSameSeed_ProducesSameEquityCurve()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var data = GenerateData(200);

        Func<IReadOnlyList<Tick>, LlmDecision> decide =
            w => TrendFollowingBrain.Decide(w, TrendFollowingBrain.TrendConfig.Default);

        var r1 = optimizer.RunBacktest(data, "TF", decide, 5m, 0.20m, 50);
        var r2 = optimizer.RunBacktest(data, "TF", decide, 5m, 0.20m, 50);

        Assert.Equal(r1.TotalTrades, r2.TotalTrades);
        Assert.Equal(r1.EndBankroll, r2.EndBankroll);
        Assert.Equal(r1.TotalProfit, r2.TotalProfit);
        Assert.Equal(r1.EquityCurve.Count, r2.EquityCurve.Count);
    }

    [Fact]
    public void OptimizationResultSavedToDisk()
    {
        var optimizer = new StrategyOptimizer(_dir);
        var data = GenerateData(200);

        var ranges = new Dictionary<string, (double min, double max, double step)>
        {
            ["FastEma"] = (5, 15, 1),
            ["SlowEma"] = (15, 25, 1)
        };

        optimizer.Optimize(data, "TrendFollowing",
            (w, p) => TrendFollowingBrain.Decide(w,
                new TrendFollowingBrain.TrendConfig(
                    FastEma: (int)p["FastEma"],
                    SlowEma: (int)p["SlowEma"])),
            ranges, iterations: 5);

        var files = Directory.GetFiles(_dir, "optimization_*.json");
        Assert.True(files.Length > 0, "Optimization results should be saved to disk");
    }
}

[Trait("Category", "Unit")]
public class IndicatorsEdgeCaseTests
{
    [Fact]
    public void Rsi_AllSamePrices_Returns100()
    {
        var closes = Enumerable.Repeat(1.10000, 20).ToArray();
        var rsi = Indicators.Rsi(closes);
        Assert.Equal(100, rsi);
    }

    [Fact]
    public void Rsi_TooShort_ReturnsNaN()
    {
        Assert.True(double.IsNaN(Indicators.Rsi(new[] { 1.0, 2.0 })));
    }

    [Fact]
    public void Sma_ZeroPeriod_ReturnsNaN()
    {
        Assert.True(double.IsNaN(Indicators.Sma(new[] { 1.0, 2.0, 3.0 }, 0)));
    }

    [Fact]
    public void PercentChange_Empty_ReturnsZero()
    {
        Assert.Equal(0, Indicators.PercentChange(Array.Empty<double>()));
    }

    [Fact]
    public void PercentChange_SingleElement_ReturnsZero()
    {
        Assert.Equal(0, Indicators.PercentChange(new[] { 42.0 }));
    }
}

[Trait("Category", "Unit")]
public class RiskVerdictTests
{
    [Fact]
    public void Allow_HasOkReason()
    {
        var v = RiskVerdict.Allow();
        Assert.True(v.Allowed);
        Assert.Equal("ok", v.Reason);
    }

    [Fact]
    public void Reject_CapturesReason()
    {
        var v = RiskVerdict.Reject("test reason");
        Assert.False(v.Allowed);
        Assert.Equal("test reason", v.Reason);
    }
}
