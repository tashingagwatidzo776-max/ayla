using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DongGfx.GwenHost;

/// <summary>A loopback-only HTTP endpoint for the GWEN host.
///
/// Wire shape: the Ollama subset the desk's client already speaks —
/// POST /api/chat {"model","messages":[{"role","content"}]} →
/// {"message":{"role":"assistant","content"},"done":true} — so
/// GwenBrainClient's transport swapped from the third-party Ollama app to
/// this built-in host with zero protocol churn (and every existing client
/// test keeps passing against the same contract).
///
/// Built on a raw TcpListener bound to 127.0.0.1: no HTTP.sys URL
/// reservations, no LAN exposure possible, no admin rights needed.
/// The generate delegate is whatever the host wires in (the real model in
/// production, a fake in tests).</summary>
public sealed class GwenHostServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, CancellationToken, Task<string>> _generate;
    private readonly Func<bool> _modelLoaded;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>Hard backstop for one /api/chat generation (queue wait +
    /// load + prompt eval + reply). Warm assessments land well under this;
    /// it exists for orphaned sockets (the desk's client timed out and
    /// vanished) and hung inference, so one stuck generation frees the
    /// model's single-flight gate instead of wedging every later verdict
    /// into a queue behind a dead connection. Cancellation surfaces as a
    /// 500 — the client maps any non-success to "no verdict" (fail-neutral).
    /// </summary>
    public static readonly TimeSpan GenerationDeadline = TimeSpan.FromMinutes(15);

    public GwenHostServer(int port, Func<string, CancellationToken, Task<string>> generate, Func<bool>? modelLoaded = null)
    {
        _port = port;
        _generate = generate;
        _modelLoaded = modelLoaded ?? (() => true);
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => _port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 30_000;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }
                var (method, path, body) = request.Value;

                if (method == "GET" && path.StartsWith("/health", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 200, new
                    {
                        status = "ok",
                        model = GwenModelService.ModelName,
                        model_loaded = _modelLoaded(),
                    }).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path.StartsWith("/api/chat", StringComparison.Ordinal))
                {
                    var prompt = ExtractLastUserMessage(body);
                    if (prompt is null)
                    {
                        await WriteJsonAsync(stream, 400, new { error = "no user message in request" }).ConfigureAwait(false);
                        return;
                    }
                    using var genCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    genCts.CancelAfter(GenerationDeadline);
                    var reply = await _generate(prompt, genCts.Token).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, new
                    {
                        model = JsonSerializer.Deserialize<JsonElement>(body).TryGetProperty("model", out var m)
                            ? m.GetString() ?? GwenModelService.ModelName
                            : GwenModelService.ModelName,
                        message = new { role = "assistant", content = reply },
                        done = true,
                        done_reason = "stop",
                    }).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(stream, 404, new { error = "not found" }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let a request fault take the host down; the client
                // treats a dropped/failed response as "no verdict". Log the
                // leaf cause so ops can see WHY a verdict came back null.
                Console.Error.WriteLine(
                    $"gwen-host: /api/chat failed: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    var stream = client.GetStream();
                    await WriteJsonAsync(stream, 500, new { error = "gwen host internal error" }).ConfigureAwait(false);
                }
                catch
                {
                    // connection already gone — nothing to do
                }
            }
        }
    }

    /// <summary>The last user-role message — the desk's client sends the
    /// full assessment prompt as one user message.</summary>
    internal static string? ExtractLastUserMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            string? last = null;
            foreach (var msg in messages.EnumerateArray())
            {
                if (msg.TryGetProperty("role", out var role) &&
                    role.GetString() == "user" &&
                    msg.TryGetProperty("content", out var content))
                {
                    last = content.GetString();
                }
            }
            return string.IsNullOrWhiteSpace(last) ? null : last;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new List<byte>(1024);
        var buf = new byte[1024];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buf).ConfigureAwait(false);
            if (n <= 0)
            {
                return null;
            }
            for (var i = 0; i < n; i++)
            {
                head.Add(buf[i]);
            }
            headerEnd = FindHeaderEnd(head);
            if (head.Count > 64 * 1024)
            {
                return null;   // header flood guard
            }
        }

        var headerText = Encoding.ASCII.GetString(head.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 2)
        {
            return null;
        }
        var method = parts[0];
        var path = parts[1];

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
            }
        }
        if (contentLength is < 0 or > 1024 * 1024)
        {
            return null;   // 1 MB body guard: prompts are tiny
        }

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var have = head.Count - bodyStart;
        if (have > contentLength)
        {
            have = contentLength;
        }
        Array.Copy(head.ToArray(), bodyStart, body, 0, have);
        while (have < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(have)).ConfigureAwait(false);
            if (n <= 0)
            {
                return null;
            }
            have += n;
        }
        return (method, path, Encoding.UTF8.GetString(body));
    }

    private static int FindHeaderEnd(List<byte> head)
    {
        for (var i = 3; i < head.Count; i++)
        {
            if (head[i - 3] == '\r' && head[i - 2] == '\n' && head[i - 1] == '\r' && head[i] == '\n')
            {
                return i - 3;
            }
        }
        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Internal Server Error",
        };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
