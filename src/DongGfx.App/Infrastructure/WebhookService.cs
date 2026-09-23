using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Sends trade events to a Discord or Slack webhook URL. Supports both
/// Discord and Slack payload formats. Thread-safe; fire-and-forget calls.
/// </summary>
public sealed class WebhookService : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    /// <param name="timeout">HTTP timeout. Defaults to a fail-fast 10 s —
    /// webhook failures must never hold up trading or the settings UI.</param>
    public WebhookService(TimeSpan? timeout = null)
    {
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    /// <summary>Webhook URL (Discord or Slack). Null/empty disables sending.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Whether to use Discord payload format (true) or Slack (false).</summary>
    public bool IsDiscord { get; set; } = true;

    /// <summary>Minimum interval between webhook posts to avoid rate limits.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(5);

    private DateTimeOffset _lastPost = DateTimeOffset.MinValue;

    /// <summary>Post a trade settlement event.</summary>
    public void PostTradeSettled(string accountName, bool won, decimal profit,
        string symbol, string direction, decimal stake, decimal bankroll)
    {
        if (string.IsNullOrEmpty(WebhookUrl)) return;
        if (DateTimeOffset.UtcNow - _lastPost < MinInterval) return;
        _lastPost = DateTimeOffset.UtcNow;

        var emoji = won ? "✅" : "❌";
        var color = won ? 0x00D4AA : 0xFF5C5C;
        var title = $"{emoji} Trade Settled — {accountName}";
        var body = $"{symbol} {direction} | Stake: ${stake:0.##} | {(won ? "WIN" : "LOSS")} | P&L: {profit:+0.##;-0.##;0} | Bankroll: ${bankroll:0.##}";

        _ = PostAsync(title, body, color);
    }

    /// <summary>Post a growth engine milestone (target/floor hit).</summary>
    public void PostMilestone(string accountName, string milestone, decimal bankroll)
    {
        if (string.IsNullOrEmpty(WebhookUrl)) return;

        var color = milestone.Contains("target") ? 0x00D4AA : 0xFF5C5C;
        _ = PostAsync($"🎯 {milestone}", $"{accountName} — bankroll ${bankroll:0.##}", color);
    }

    /// <summary>Post a circuit breaker alert.</summary>
    public void PostCircuitBreaker(string accountName, int failures)
    {
        if (string.IsNullOrEmpty(WebhookUrl)) return;

        _ = PostAsync("⚠ Circuit Breaker Tripped",
            $"{accountName}: {failures} consecutive failures", 0xFFC857);
    }

    /// <summary>Post a dashboard risk-rail alert (governor, kill switch,
    /// circuit breakers, exhausted restarts — or an all-clear).</summary>
    public void PostRiskRail(string title, string body)
    {
        if (string.IsNullOrEmpty(WebhookUrl)) return;

        _ = PostAsync(title, body, 0xFFC857);
    }

    /// <summary>Post a general status message.</summary>
    public void PostStatus(string title, string message)
    {
        if (string.IsNullOrEmpty(WebhookUrl)) return;

        _ = PostAsync(title, message, 0x9AA3B2);
    }

    /// <summary>Synchronous connectivity probe for the settings UI: posts a
    /// small test payload and reports whether the webhook accepted it.
    /// Unlike the fire-and-forget senders this surfaces failure instead of
    /// swallowing it, and bypasses MinInterval — a bad URL is caught now,
    /// before a real trade event silently fails.</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync()
    {
        if (string.IsNullOrEmpty(WebhookUrl))
        {
            return (false, "No webhook URL configured");
        }

        var format = IsDiscord ? "discord" : "slack";
        try
        {
            object payload;
            if (IsDiscord)
            {
                payload = new { embeds = new[] { new { title = "🔔 tf webhook test",
                    description = "Connection test from the tf settings tab — if you can read this, the webhook works.",
                    color = 0x00D4AA } } };
            }
            else
            {
                payload = new { attachments = new[] { new { fallback = "tf webhook test",
                    color = "#00D4AA", title = "🔔 tf webhook test",
                    text = "Connection test from the tf settings tab — if you can read this, the webhook works.",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } } };
            }

            using var response = await _http.PostAsJsonAsync(WebhookUrl, payload);
            return response.IsSuccessStatusCode
                ? (true, $"Test message sent ({format})")
                : (false, $"Webhook returned HTTP {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task PostAsync(string title, string body, int color)
    {
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (IsDiscord)
                {
                    var payload = new
                    {
                        embeds = new[]
                        {
                            new
                            {
                                title,
                                description = body,
                                color,
                                timestamp = DateTimeOffset.UtcNow.ToString("o")
                            }
                        }
                    };
                    var response = await _http.PostAsJsonAsync(WebhookUrl, payload);
                    if (response.IsSuccessStatusCode) return;
                }
                else
                {
                    // Slack format
                    var payload = new
                    {
                        attachments = new[]
                        {
                            new
                            {
                                fallback = $"{title}: {body}",
                                color = $"#{color:X6}",
                                title,
                                text = body,
                                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            }
                        }
                    };
                    var response = await _http.PostAsJsonAsync(WebhookUrl, payload);
                    if (response.IsSuccessStatusCode) return;
                }
            }
            catch when (attempt < maxRetries)
            {
                // Transient failure: retry with exponential backoff.
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
            }
            catch
            {
                // Final attempt failed; webhook failures must not affect trading.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
