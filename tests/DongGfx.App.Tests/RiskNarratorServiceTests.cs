using System;
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
/// Tests for AI agent 2 (docs/ai-agent-program.md): the risk narrator.
/// The critical property under test is the L1 boundary — it reacts to halt
/// events and posts prose; nothing it does touches the halt machinery
/// itself. Plus: template fallback, live-LLM via a stub server, the
/// cooldown debounce, and the persisted toggle.
/// </summary>
[Trait("Category", "Unit")]
public class RiskNarratorServiceTests : IDisposable
{
    private readonly string _journalDir = Path.Combine(Path.GetTempPath(), $"tf_narrator_{Guid.NewGuid():N}");
    private readonly TradeJournal _journal;
    private readonly WebhookService _webhook;

    public RiskNarratorServiceTests()
    {
        _journal = new TradeJournal(_journalDir);
        _webhook = new WebhookService();
    }

    public void Dispose()
    {
        _journal.Dispose();
        _webhook.Dispose();
        try { Directory.Delete(_journalDir, recursive: true); } catch { }
    }

    private RiskNarratorService NewService(WebhookService? webhook = null, string? baseUrl = null) =>
        new(_journal, webhook ?? _webhook)
        {
            BaseUrlOverride = () => baseUrl,
            ModelOverride = () => "qwen3-test",
            ApiKeyOverride = () => "test-key"
        };

    private static JournalEntry Entry(string category, string details) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Category = category,
        Details = details
    };

    [Theory]
    [InlineData("FX_RISK", "MT5 bridge unreachable — trading halted", true)]
    [InlineData("FX_RISK", "transient halt cleared — conditions recovered", false)]
    [InlineData("FX_RISK", "FX loss stop re-armed, baseline 100.00", false)]
    [InlineData("FX_DECISION", "cycle ok", false)]
    public void IsNarratable_Halts_Only(string category, string details, bool expected)
    {
        Assert.Equal(expected, RiskNarratorService.IsNarratable(Entry(category, details)));
    }

    [Fact]
    public async Task NoLlmConfigured_TemplateNarrative_PostsAndJournals()
    {
        _journal.Log(Guid.Empty, "FX_DECISION", "cycle ok: {\"Symbol\":\"XAUUSDmicro\"}");
        var service = NewService();

        var narrative = await service.NarrateAsync(
            Entry("FX_RISK", "daily loss cap breached — trading halted"));

        Assert.NotNull(narrative);
        Assert.StartsWith("📊", narrative);
        Assert.Contains("loss cap", narrative);
        _journal.Flush();
        Assert.Contains(_journal.GetRecent(null, 50), e => e.Category == "AI_CALL");
    }

    [Fact]
    public async Task ToggleOff_Silent()
    {
        var service = NewService();
        service.NarratorEnabledToggle = () => false;
        Assert.Null(await service.NarrateAsync(
            Entry("FX_RISK", "bridge down — trading halted")));
        _journal.Flush();
        Assert.DoesNotContain(_journal.GetRecent(null, 50), e => e.Category == "AI_CALL");
    }

    [Fact]
    public async Task Cooldown_SecondHaltWithinWindow_Suppressed()
    {
        var service = NewService();
        service.Cooldown = TimeSpan.FromMinutes(2);

        Assert.NotNull(await service.NarrateAsync(Entry("FX_RISK", "halt one — trading halted")));
        Assert.Null(await service.NarrateAsync(Entry("FX_RISK", "halt two — trading halted")));
    }

    [Fact]
    public async Task LiveLlm_StubServer_NarrativeComesFromEndpoint()
    {
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
                    choices = new[] { new { message = new { content = "The bridge dropped; the brain parked itself safely." } } }
                });
                var buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer);
                ctx.Response.Close();
            });

            var service = NewService(baseUrl: $"http://127.0.0.1:{port}/v1");
            var narrative = await service.NarrateAsync(Entry("FX_RISK", "bridge down — trading halted"));
            await serverTask;

            Assert.NotNull(narrative);
            Assert.StartsWith("🤖", narrative);
            Assert.Contains("bridge dropped", narrative);
            _journal.Flush();
            Assert.Contains(_journal.GetRecent(null, 50),
                e => e.Category == "AI_CALL" && e.Details.Contains("risk narrative"));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public void Toggle_RoundTripsThroughSettingsAndViewModel()
    {
        var vm = new SettingsViewModel(new SettingsService());

        vm.Load(new AppSettings { RiskNarratorEnabled = false });
        Assert.False(vm.RiskNarratorEnabled);
        Assert.False(vm.BuildSettings().RiskNarratorEnabled);

        vm.Load(new AppSettings());
        Assert.True(vm.RiskNarratorEnabled);
    }
}
