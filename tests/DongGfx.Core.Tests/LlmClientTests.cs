using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DongGfx.Core.Brain;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class LlmClientTests
{
    private sealed class FakeHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<JsonElement, string> _responder;
        private readonly int _statusCode;

        public FakeHttpServer(Func<JsonElement, string> responder, int statusCode = 200)
        {
            _responder = responder;
            _statusCode = statusCode;

            (_listener, var port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
            Url = $"http://127.0.0.1:{port}/v1";
        }

        public string Url { get; }
        public string? LastRequestJson { get; private set; }
        public string? LastRequestPath { get; private set; }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                LastRequestJson = body;
                LastRequestPath = context.Request.Url?.AbsolutePath;

                using var doc = JsonDocument.Parse(body);
                var response = _responder(doc.RootElement);
                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.StatusCode = _statusCode;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes, ct);
                context.Response.Close();
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { /* best effort */ }
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    private static string ChatResponse(string content) =>
        $"{{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"choices\":[" +
        $"{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(content)}}}," +
        $"\"finish_reason\":\"stop\"}}]}}";

    [Fact]
    public async Task CompleteJsonAsync_ReturnsContentAndSendsHeaders()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new FakeHttpServer(_ =>
            ChatResponse("{\"direction\":\"RISE\",\"confidence\":0.7,\"stake\":1,\"reasoning\":\"trend\"}"));
        var run = server.RunAsync(cts.Token);

        var client = new LlmClient
        {
            BaseUrl = server.Url,
            Model = "gpt-4o-mini",
            ApiKey = "sk-test"
        };

        var content = await client.CompleteJsonAsync("system", "user", cts.Token);

        Assert.Contains("\"direction\":\"RISE\"", content);

        // The client must have hit /v1/chat/completions with auth + json mode.
        Assert.Equal("/v1/chat/completions", server.LastRequestPath);
        using var sent = JsonDocument.Parse(server.LastRequestJson!);
        Assert.Equal("gpt-4o-mini", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", sent.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(2, sent.RootElement.GetProperty("messages").GetArrayLength());

        cts.Cancel();
        await run;
        await server.DisposeAsync();
    }

    [Fact]
    public async Task CompleteJsonAsync_OllamaMode_OmitsResponseFormat()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new FakeHttpServer(_ => ChatResponse("{\"direction\":\"HOLD\"}"));
        var run = server.RunAsync(cts.Token);

        // Force Ollama behavior while still pointing at the hermetic fake server.
        var client = new ForcedOllamaClient { BaseUrl = server.Url, Model = "llama3" };

        var content = await client.CompleteJsonAsync("s", "u", cts.Token);
        Assert.Contains("HOLD", content);

        using var sent = JsonDocument.Parse(server.LastRequestJson!);
        Assert.False(sent.RootElement.TryGetProperty("response_format", out _));

        cts.Cancel();
        await run;
        await server.DisposeAsync();
    }

    [Fact]
    public void IsOllamaUrl_DetectsLocalOllama()
    {
        Assert.True(LlmClient.IsOllamaUrl("http://localhost:11434/v1"));
        Assert.True(LlmClient.IsOllamaUrl("http://127.0.0.1:11434/v1"));
        Assert.False(LlmClient.IsOllamaUrl("https://api.openai.com/v1"));
    }

    /// <summary>Simulates an Ollama endpoint without the port heuristic.</summary>
    private sealed class ForcedOllamaClient : LlmClient
    {
        public override bool UseResponseFormat => false;
    }

    [Fact]
    public async Task CompleteJsonAsync_NonSuccess_ThrowsLlmException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new FakeHttpServer(
            _ => "{\"error\":\"boom\"}", statusCode: 500);
        var run = server.RunAsync(cts.Token);

        var client = new LlmClient { BaseUrl = server.Url, Model = "m" };
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync("s", "u", cts.Token));

        cts.Cancel();
        await run;
        await server.DisposeAsync();
    }
}