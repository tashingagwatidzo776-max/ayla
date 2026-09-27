using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.App.ViewModels;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Tests for AI agent 1 (docs/ai-agent-program.md): the journal analyst.
/// Covers the local stats engine, the deterministic template fallback (LLM
/// unreachable, LLM failure, disabled service), the webhook post against a real
/// local capture listener, and the guarded type-7 prediction-memory loop —
/// recording, mechanical grading against journal outcomes, and the hit-rate
/// the analyst is allowed to know about itself.
/// </summary>
[Trait("Category", "Unit")]
public class JournalAnalystServiceTests : IDisposable
{
    private readonly string _journalDir = Path.Combine(Path.GetTempPath(), $"tf_analyst_{Guid.NewGuid():N}");
    private readonly string _memoryPath = Path.Combine(Path.GetTempPath(), $"tf_analyst_mem_{Guid.NewGuid():N}", "memory.json");
    private readonly TradeJournal _journal;
    private readonly WebhookService _webhook;

    public JournalAnalystServiceTests()
    {
        _journal = new TradeJournal(_journalDir);
        _webhook = new WebhookService();
    }

    public void Dispose()
    {
        _journal.Dispose();
        _webhook.Dispose();
        try { Directory.Delete(_journalDir, recursive: true); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(_memoryPath)!, recursive: true); } catch { }
    }

    // The service always has a default endpoint (local Ollama), so "LLM
    // unavailable" is simulated with a reserved port that refuses fast —
    // hermetic whether or not a daemon happens to be running on this box.
    private const string DeadLlmUrl = "http://127.0.0.1:1/v1";

    private JournalAnalystService NewService(
        string? baseUrl = DeadLlmUrl, Func<string?>? model = null, WebhookService? webhook = null)
    {
        return new JournalAnalystService(_journal, webhook ?? _webhook,
            memory: new AnalystMemory(_memoryPath))
        {
            BaseUrlOverride = () => baseUrl,
            ModelOverride = model,
            ApiKeyOverride = () => "test-key"
        };
    }

    private void Seed(params (string Category, string Details)[] entries)
    {
        foreach (var (category, details) in entries)
        {
            _journal.Log(Guid.Empty, category, details);
        }
        _journal.Flush();
    }

    [Fact]
    public async Task EmptyJournal_PostsNothing()
    {
        var service = NewService();
        Assert.Null(await service.AnalyzeOnceAsync());
    }

    [Fact]
    public async Task LlmUnavailable_TemplateNarrative_PostsAndJournalsAiCall()
    {
        Seed(
            ("FX_DECISION", "cycle ok: {\"Symbol\":\"XAUUSDmicro\"}"),
            ("FX_SIGNAL", "donchian(20) → Buy: {\"Symbol\":\"XAUUSDmicro\"}"),
            ("FX_SIGNAL", "donchian(20) → Sell: {\"Symbol\":\"USDJPY\"}"),
            ("TRADE_SETTLEMENT", "{\"ContractId\":\"1\",\"Won\":true,\"Payout\":4.2}"),
            ("TRADE_SETTLEMENT", "{\"ContractId\":\"2\",\"Won\":false,\"Payout\":0}"),
            ("FX_RISK", "transient halt — trading halted"),
            ("FX_RISK", "transient halt cleared — conditions recovered"));

        var service = NewService();
        var narrative = await service.AnalyzeOnceAsync();

        Assert.NotNull(narrative);
        Assert.StartsWith("📊", narrative);           // template, not LLM
        Assert.Contains("1 decisions", narrative);
        Assert.Contains("2 signals", narrative);
        Assert.Contains("2 settled (1W/1L)", narrative);
        Assert.Contains("2 risk halt(s)", narrative);
        Assert.Contains("1 auto-recovered", narrative);

        // The audit trail is the feature: one AI_CALL per post.
        _journal.Flush();
        var calls = _journal.GetRecent(null, 50).Where(e => e.Category == "AI_CALL").ToList();
        Assert.Single(calls);
        Assert.Contains("template", calls[0].Details);
    }

    [Fact]
    public async Task DeadLlmEndpoint_FallsBackToTemplate()
    {
        Seed(("FX_DECISION", "cycle ok"));
        // A reserved port that refuses fast — no 20s timeout in tests.
        var service = NewService(baseUrl: "http://127.0.0.1:1/v1");
        var narrative = await service.AnalyzeOnceAsync();
        Assert.NotNull(narrative);
        Assert.StartsWith("📊", narrative);
    }

    [Fact]
    public async Task ToggleOff_NoPostNoCall()
    {
        Seed(("FX_DECISION", "cycle ok"));
        var service = NewService();
        service.AnalystEnabledToggle = () => false;
        Assert.Null(await service.AnalyzeOnceAsync());
        Assert.Empty(_journal.GetRecent(null, 50).Where(e => e.Category == "AI_CALL"));
    }

    [Fact]
    public async Task MemoryLoop_RecordsGradesAndFeedsHitRateBack()
    {
        Seed(
            ("FX_DECISION", "cycle ok: {\"Symbol\":\"XAUUSDmicro\"}"),
            ("FX_SIGNAL", "signal: {\"Symbol\":\"XAUUSDmicro\"}"));

        var service = NewService();
        Assert.NotNull(await service.AnalyzeOnceAsync());   // cycle 1: records a prediction

        var memory = service.Memory;
        Assert.Single(memory.Predictions);
        Assert.Null(memory.Predictions[0].Outcome);

        // Fast-forward the prediction to due; the symbol went quiet (no new
        // signal) — the mechanical verdict must be a miss.
        var p = memory.Predictions[0];
        typeof(AnalystPrediction).GetProperty(nameof(AnalystPrediction.DueAt))!
            .SetValue(p, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.NotNull(await service.AnalyzeOnceAsync());   // cycle 2: grades it

        Assert.Equal(1, memory.HitRate().Total);
        Assert.Equal(0, memory.HitRate().Hits);             // quiet → missed call, honestly graded
        _journal.Flush();
        Assert.Contains("AI_MEMORY",
            _journal.GetRecent(null, 50).Select(e => e.Category));
    }

    [Fact]
    public async Task MemoryDisabled_NoRecordingNoGrading()
    {
        Seed(("FX_DECISION", "cycle ok"));
        var service = NewService();
        service.MemoryEnabledToggle = () => false;
        Assert.NotNull(await service.AnalyzeOnceAsync());
        Assert.Empty(service.Memory.Predictions);
    }

    [Fact]
    public async Task LiveLlm_StubServer_NarrativeComesFromEndpoint()
    {
        Seed(("FX_DECISION", "cycle ok: {\"Symbol\":\"EURUSD\"}"));
        var (listener, port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        try
        {
            var serverTask = Task.Run(async () =>
            {
                var ctx = listener.GetContext();
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                Assert.Contains("qwen3-test", body);
                var json = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = "Session was orderly; watch EURUSD ranges." } } }
                });
                var buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer);
                ctx.Response.Close();
            });

            var service = NewService(baseUrl: $"http://127.0.0.1:{port}/v1", model: () => "qwen3-test");
            var narrative = await service.AnalyzeOnceAsync();
            await serverTask;

            Assert.NotNull(narrative);
            Assert.StartsWith("🤖", narrative);
            Assert.Contains("Session was orderly", narrative);

            _journal.Flush();
            var call = _journal.GetRecent(null, 50).First(e => e.Category == "AI_CALL");
            Assert.Contains("llm", call.Details);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public void Toggles_RoundTripThroughSettingsAndViewModel()
    {
        var vm = new SettingsViewModel(new SettingsService());

        vm.Load(new AppSettings { AnalystEnabled = false, AnalystMemoryEnabled = false });
        Assert.False(vm.AnalystEnabled);
        Assert.False(vm.BuildSettings().AnalystEnabled);
        Assert.False(vm.BuildSettings().AnalystMemoryEnabled);

        vm.Load(new AppSettings());
        Assert.True(vm.AnalystEnabled);
        Assert.True(vm.BuildSettings().AnalystMemoryEnabled);
    }
}
