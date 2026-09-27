using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.Core.Logging;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// AI agent 2 of docs/ai-agent-program.md — the Risk Narrator.
///
/// When the FX supervisor halts (daily-loss cap, equity floor, kill switch,
/// governor, bridge-down), it journals an FX_RISK entry; this service hears
/// that entry the moment it is queued (TradeJournal.EntryAdded), asks the
/// local LLM for a short human explanation of what happened, and posts it
/// to the same webhook the rail alert used — within seconds, not at the
/// next digest.
///
/// Authority: L1, and doubly so — it is a pure SUBSCRIBER to the journal
/// event stream. It holds no reference to the supervisor, the gate, or any
/// order path; it cannot re-arm, clear, delay, or influence a halt even by
/// accident. Every post is journaled as AI_CALL; any failure (LLM down, no
/// env config) falls back to the deterministic template; nothing it does
/// can throw out of the journal's logging thread.
/// </summary>
public sealed class RiskNarratorService : IDisposable
{
    /// <summary>Minimum time between narratives — a flapping bridge must
    /// not turn into an LLM bill or a flooded channel. Configurable for
    /// tests.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Live read-through of the persisted RiskNarratorEnabled
    /// toggle. Null/true = enabled.</summary>
    public Func<bool>? NarratorEnabledToggle { get; set; }

    public Func<string?>? BaseUrlOverride { get; set; }
    public Func<string?>? ModelOverride { get; set; }
    public Func<string?>? ApiKeyOverride { get; set; }

    private readonly TradeJournal _journal;
    private readonly WebhookService _webhook;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly bool _ownsHttpClient;
    private DateTimeOffset _lastNarrative = DateTimeOffset.MinValue;
    private int _busy;

    public RiskNarratorService(TradeJournal journal, WebhookService webhook, Action<string>? log = null,
        HttpClient? http = null)
    {
        _journal = journal;
        _webhook = webhook;
        _log = log;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5)
        });
        _ownsHttpClient = http is null;
        _journal.EntryAdded += OnEntryAdded;
    }

    private string BaseUrl => (BaseUrlOverride?.Invoke()
        ?? Environment.GetEnvironmentVariable("TF_LLM_BASE_URL"))
        ?.Trim().TrimEnd('/') is { Length: > 0 } url ? url : "http://127.0.0.1:11434/v1";

    private string Model => (ModelOverride?.Invoke()
        ?? Environment.GetEnvironmentVariable("TF_LLM_MODEL"))
        ?.Trim() is { Length: > 0 } model ? model : "qwen3:0.6b";

    private string? ApiKey => ApiKeyOverride?.Invoke()
        ?? Environment.GetEnvironmentVariable("TF_LLM_API_KEY");

    /// <summary>Whether this entry deserves a narrative: an FX_RISK halt
    /// event (not the recovery, not the re-arm — those are routine).</summary>
    internal static bool IsNarratable(JournalEntry entry) =>
        entry.Category == "FX_RISK" &&
        entry.Details.Contains("halted", StringComparison.OrdinalIgnoreCase) &&
        !entry.Details.Contains("recovered", StringComparison.OrdinalIgnoreCase);

    private void OnEntryAdded(JournalEntry entry)
    {
        if (NarratorEnabledToggle?.Invoke() == false || !IsNarratable(entry))
        {
            return;
        }

        // EntryAdded fires on the journal's logging thread — never block it.
        // Hand the work to the pool; all failures stay inside the task.
        _ = Task.Run(() => _ = SafeNarrateAsync(entry));
    }

    /// <summary>Test hook: runs the narrative for one entry synchronously
    /// (async) instead of via the event, so tests need no timing races.
    /// Returns the posted narrative, or null when suppressed.</summary>
    public Task<string?> NarrateAsync(JournalEntry entry) => SafeNarrateAsync(entry);

    private async Task<string?> SafeNarrateAsync(JournalEntry entry)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return null; // a narrative is already in flight — don't stack
        }

        try
        {
            // The toggle gates the actual work (not just the event fast-path):
            // disabled means no LLM call, no post, no AI_CALL entry.
            if (NarratorEnabledToggle?.Invoke() == false)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastNarrative < Cooldown)
            {
                return null;
            }

            var context = BuildContext(entry);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string narrative;
            try
            {
                narrative = await AskLlmAsync(context, entry).WaitAsync(TimeSpan.FromSeconds(20));
                narrative = "🤖 " + narrative.Trim();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"risk narrator LLM failed ({sw.ElapsedMilliseconds} ms): {ex.Message} — using template");
                narrative = ComposeTemplate(entry, context);
            }

            _journal.Log(Guid.Empty, "AI_CALL", $"{Model} risk narrative (halt)");
            _lastNarrative = DateTimeOffset.UtcNow;
            _webhook.PostStatus("🤖 Risk narrator — what just happened", narrative);
            return narrative;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"risk narrator failed: {ex.Message}");
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Journal-derived context for the prompt: the halt message
    /// plus a small statistical snapshot. No credentials, no paths, no
    /// webhook URL.</summary>
    internal string BuildContext(JournalEntry entry)
    {
        var recent = _journal.GetRecent(null, 200);
        var since = DateTimeOffset.UtcNow.AddHours(-6);
        var window = recent.Where(e => e.Timestamp >= since).ToList();
        var decisions = window.Count(e => e.Category == "FX_DECISION");
        var signals = window.Count(e => e.Category == "FX_SIGNAL");
        var halts = window.Count(e => e.Category == "FX_RISK");
        var symbols = string.Join(", ", window
            .Where(e => e.Category == "FX_SIGNAL")
            .Select(e => Extract(e.Details, "Symbol"))
            .Where(s => s is not null)
            .Distinct()
            .Take(4));
        return $"Halt message: {entry.Details}. " +
               $"Last 6h: {decisions} decision cycles, {signals} signals" +
               (symbols.Length > 0 ? $" ({symbols})" : "") + $", {halts} risk events. ";
    }

    private async Task<string> AskLlmAsync(string context, JournalEntry entry)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
        if (!string.IsNullOrEmpty(ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }

        request.Content = JsonContent.Create(new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content =
                    "You explain trading risk halts to a trader in plain language. " +
                    "Two sentences: (1) what most likely just happened, (2) what the " +
                    "trader should check. No advice to trade, no disclaimers, no markdown." },
                new { role = "user", content = context +
                    "Explain this halt. Do not suggest re-arming or trading." }
            },
            temperature = 0.2,
            max_tokens = 120
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"LLM returned HTTP {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        var text = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("LLM returned an empty completion");
        }
        return text;
    }

    private static string ComposeTemplate(JournalEntry entry, string context) =>
        "📊 The FX supervisor halted trading. " + entry.Details +
        " The brain is safely on the sidelines; check the bridge, the kill switch, " +
        "and today's loss cap before re-arming. " + context;

    private static string? Extract(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var el) &&
                   el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _journal.EntryAdded -= OnEntryAdded;
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
