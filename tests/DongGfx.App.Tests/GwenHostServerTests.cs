using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.GwenHost;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The built-in GWEN host's wire contract: the Ollama subset the desk's
/// client speaks (/api/chat) and the OpenAI subset every OTHER caller
/// needs now that the third-party daemon is gone (/v1/chat/completions —
/// JournalAnalyst, RiskNarrator and the alpha proposer all speak it),
/// plus the per-request system-prompt override and token budget behind
/// both dialects. Loopback-only, fake generate — no model load.
/// </summary>
[Trait("Category", "Unit")]
public class GwenHostServerTests : IDisposable
{
    private GwenHostServer? _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public void Dispose()
    {
        _server?.Dispose();
        _http.Dispose();
    }

    /// <summary>Probe a free loopback port and hand it to a real server;
    /// retry on the probe-to-bind race (another test can win the gap).</summary>
    private GwenHostServer StartServer(Func<GwenChatRequest, CancellationToken, Task<string>> generate)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                var server = new GwenHostServer(port, generate, () => true);
                server.Start();
                _server = server;
                return server;
            }
            catch (SocketException) when (attempt < 10)
            {
                Thread.Sleep(25);
            }
        }
    }

    private async Task<JsonElement> PostAsync(string path, string body)
    {
        var resp = await _http.PostAsync(
            $"http://127.0.0.1:{_server!.Port}{path}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    // ── pure parsing ─────────────────────────────────────────────

    [Fact]
    public void ExtractChatRequest_UserOnly_DefaultsNull()
    {
        var chat = GwenHostServer.ExtractChatRequest(
            """{"model":"gwen-fx","messages":[{"role":"user","content":"assess this"}]}""");
        Assert.NotNull(chat);
        Assert.Equal("assess this", chat!.UserPrompt);
        Assert.Null(chat.SystemPrompt);   // desk path → host's own GWEN prompt
        Assert.Null(chat.MaxTokens);      // desk path → the 160-token cap
    }

    [Fact]
    public void ExtractChatRequest_SystemOverride_And_OllamaBudget()
    {
        var chat = GwenHostServer.ExtractChatRequest(
            """{"messages":[{"role":"system","content":"propose alphas"},{"role":"user","content":"go"}],"options":{"num_predict":400}}""");
        Assert.NotNull(chat);
        Assert.Equal("propose alphas", chat!.SystemPrompt);
        Assert.Equal("go", chat.UserPrompt);
        Assert.Equal(400, chat.MaxTokens);
    }

    [Fact]
    public void ExtractChatRequest_OpenAiMaxTokens_And_LastUserWins()
    {
        var chat = GwenHostServer.ExtractChatRequest(
            """{"messages":[{"role":"user","content":"first"},{"role":"assistant","content":"mid"},{"role":"user","content":"last"}],"max_tokens":123}""");
        Assert.NotNull(chat);
        Assert.Equal("last", chat!.UserPrompt);   // last USER, not the assistant
        Assert.Equal(123, chat.MaxTokens);        // OpenAI dialect
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"assistant","content":"only"}]}""")]  // no user
    [InlineData("""{"messages":[]}""")]
    [InlineData("not json")]
    public void ExtractChatRequest_NoUserOrNull_ReturnsNull(string body)
    {
        Assert.Null(GwenHostServer.ExtractChatRequest(body));
    }

    [Theory]
    [InlineData(null, 160)]   // desk default (the measured verdict cap)
    [InlineData(700, 512)]    // proposer's ask → hard ceiling
    [InlineData(50, 50)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void ResolveMaxTokens_Clamps(int? requested, int expected)
    {
        Assert.Equal(expected, GwenModelService.ResolveMaxTokens(requested));
    }

    // ── live loopback, both dialects ─────────────────────────────

    [Fact]
    public async Task ApiChat_OllamaDialect_PassesSystemAndBudget_ReplyShape()
    {
        GwenChatRequest? seen = null;
        StartServer((req, _) =>
        {
            seen = req;
            return Task.FromResult("{\"bias\":\"flat\",\"confidence\":85}");
        });

        var json = await PostAsync("/api/chat",
            """{"model":"gwen-fx","messages":[{"role":"system","content":"SYS"},{"role":"user","content":"USER"}],"options":{"num_predict":400}}""");

        Assert.NotNull(seen);
        Assert.Equal("SYS", seen!.SystemPrompt);
        Assert.Equal("USER", seen.UserPrompt);
        Assert.Equal(400, seen.MaxTokens);
        Assert.Equal("{\"bias\":\"flat\",\"confidence\":85}",
            json.GetProperty("message").GetProperty("content").GetString());
        Assert.True(json.GetProperty("done").GetBoolean());
        Assert.Equal("gwen-fx", json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task V1ChatCompletions_OpenAiDialect_ReplyShape()
    {
        GwenChatRequest? seen = null;
        StartServer((req, _) =>
        {
            seen = req;
            return Task.FromResult("{\"name\":\"orb_break\",\"entry\":{},\"exit\":{}}");
        });

        var json = await PostAsync("/v1/chat/completions",
            """{"model":"gwen-fx","messages":[{"role":"system","content":"propose"},{"role":"user","content":"alpha"}],"max_tokens":123}""");

        Assert.NotNull(seen);
        Assert.Equal("propose", seen!.SystemPrompt);
        Assert.Equal("alpha", seen.UserPrompt);
        Assert.Equal(123, seen.MaxTokens);
        Assert.Equal("chat.completion", json.GetProperty("object").GetString());
        Assert.Equal("gwen-fx", json.GetProperty("model").GetString());
        var choice = json.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("{\"name\":\"orb_break\",\"entry\":{},\"exit\":{}}",
            choice.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ApiChat_NoUserMessage_Is400()
    {
        StartServer((_, _) => Task.FromResult("never"));
        var resp = await _http.PostAsync(
            $"http://127.0.0.1:{_server!.Port}/api/chat",
            new StringContent("""{"messages":[{"role":"assistant","content":"x"}]}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
