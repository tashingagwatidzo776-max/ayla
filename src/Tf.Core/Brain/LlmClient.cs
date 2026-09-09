using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tf.Core.Brain;

/// <summary>
/// Minimal OpenAI-compatible /v1/chat/completions client. Works with OpenAI,
/// DeepSeek, OpenRouter, or a local Ollama server via a configurable base URL.
/// </summary>
public class LlmClient
{
    private readonly HttpClient _http;

    public LlmClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Asks for a JSON object response. Ollama's OpenAI shim ignores unknown
    /// parameters, so <c>response_format</c> is omitted for local Ollama URLs.
    /// </summary>
    public virtual bool UseResponseFormat => !IsOllamaUrl(BaseUrl);

    public static bool IsOllamaUrl(string baseUrl) => baseUrl.Contains("11434");

    /// <summary>Maximum number of retry attempts for transient failures.</summary>
    public int MaxRetries { get; set; } = 3;

    public virtual async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var url = BaseUrl.TrimEnd('/') + "/chat/completions";

        object payload;
        if (!UseResponseFormat)
        {
            payload = new
            {
                model = Model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.2
            };
        }
        else
        {
            payload = new
            {
                model = Model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.2,
                response_format = new { type = "json_object" }
            };
        }

        var lastException = (Exception?)null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload)
                };

                if (!string.IsNullOrEmpty(ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                }

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    // Retry on transient server errors (429, 500, 502, 503, 504)
                    if (IsTransient(statusCode) && attempt < MaxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue;
                    }

                    var snippet = body.Length > 250 ? body[..250] : body;
                    throw new LlmException($"LLM request failed (HTTP {statusCode}): {snippet}");
                }

                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                return content.Trim();
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw; // Don't retry on cancellation
            }
        }

        throw new LlmException($"LLM request failed after {MaxRetries + 1} attempts: {lastException?.Message}");
    }

    private static bool IsTransient(int statusCode) =>
        statusCode == 429 || statusCode == 500 || statusCode == 502 || statusCode == 503 || statusCode == 504;
}