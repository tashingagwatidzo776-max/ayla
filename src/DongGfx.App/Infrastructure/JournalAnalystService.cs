using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>One prediction the analyst made about the next cycle, persisted
/// so the NEXT cycle can grade it mechanically against what the journal
/// actually shows. This is the whole type-7 learning loop: the model's
/// weights never change; what "learns" is the context — its own graded
/// track record — fed back into each prompt. Grading is mechanical
/// (journal outcomes), never self-assessed by the model.</summary>
public sealed record AnalystPrediction
{
    public DateTimeOffset MadeAt { get; init; }
    /// <summary>When the prediction becomes gradeable.</summary>
    public DateTimeOffset DueAt { get; init; }
    /// <summary>Symbol the prediction is about, or "*" for session-wide.</summary>
    public string Symbol { get; init; } = "*";
    /// <summary>Currently "activity" (signal expected) or "halt" (risk halt expected).</summary>
    public string Kind { get; init; } = "activity";
    public string Details { get; init; } = "";
    public DateTimeOffset? GradedAt { get; set; }
    /// <summary>null while ungraded; otherwise the mechanical verdict.</summary>
    public bool? Outcome { get; set; }
}

/// <summary>Persistence for the analyst's prediction memory. JSON state file
/// under %LOCALAPPDATA%\tf\ai\ — deliberately NOT next to the trading
/// journal, so test suites that wipe %APPDATA%\tf\data can never destroy
/// the analyst's memory (and the analyst can never be mistaken for trading
/// state). Keeps a bounded window; grades prune themselves after 14 days.</summary>
public sealed class AnalystMemory
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };
    private readonly string _path;
    private readonly List<AnalystPrediction> _predictions;

    public AnalystMemory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tf", "ai", "analyst-predictions.json");
        _predictions = Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<AnalystPrediction> Predictions => _predictions;

    private List<AnalystPrediction> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<AnalystPrediction>();
            var loaded = JsonSerializer.Deserialize<List<AnalystPrediction>>(File.ReadAllText(_path));
            return loaded ?? new List<AnalystPrediction>();
        }
        catch
        {
            // Corrupt or locked state file: memory resets rather than breaking
            // the analyst. Losing a prediction streak is acceptable; a throwing
            // timer tick is not.
            return new List<AnalystPrediction>();
        }
    }

    public void RecordPrediction(AnalystPrediction prediction)
    {
        _predictions.Add(prediction);
        Prune(DateTimeOffset.UtcNow);
        Save();
    }

    /// <summary>Grades every due, ungraded prediction against the journal
    /// (most-recent-first). Returns the graded predictions so the caller can
    /// fold the results into the next prompt. "activity" grades
    /// immediately at its due time; "halt" waits one more cycle before
    /// grading so a halt that lands slightly late still counts.</summary>
    public List<AnalystPrediction> GradeDue(DateTimeOffset now,
        Func<string, DateTimeOffset, IReadOnlyList<(string Category, string Details)>> readRecent)
    {
        var graded = new List<AnalystPrediction>();
        foreach (var p in _predictions.Where(p => p.GradedAt is null && p.DueAt <= now).ToList())
        {
            var recent = readRecent(p.Symbol, p.MadeAt);
            if (p.Kind == "halt" && now < p.DueAt.AddMinutes(5))
            {
                continue; // grace window before declaring a halt missed
            }

            p.Outcome = recent.Any(r =>
                r.Category == "FX_RISK" && r.Details.Contains("halt", StringComparison.OrdinalIgnoreCase));
            p.GradedAt = now;
            graded.Add(p);
        }

        if (graded.Count > 0)
        {
            Prune(now);
            Save();
        }

        return graded;
    }

    /// <summary>Hit-rate over graded predictions, as a "x/y" string. The
    /// only self-knowledge the analyst is allowed: computed from journal
    /// outcomes, never from the model's opinion of itself.</summary>
    public (int Hits, int Total) HitRate()
    {
        var graded = _predictions.Where(p => p.GradedAt is not null).ToList();
        return (graded.Count(p => p.Outcome == true), graded.Count);
    }

    private void Prune(DateTimeOffset now)
    {
        // Cap the window: keep the most recent 100 predictions, drop graded
        // ones older than 14 days. Ungraded predictions never expire by age
        // alone (they still get graded first).
        _predictions.RemoveAll(p => p.GradedAt is not null &&
            (now - p.GradedAt.Value).TotalDays > 14);
        while (_predictions.Count > 100)
        {
            _predictions.RemoveAt(0);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_predictions, WriteOptions));
        }
        catch
        {
            // Best-effort persistence: a full disk or AV lock costs us the
            // newest prediction, not the analyst loop.
        }
    }
}

/// <summary>
/// The Journal Analyst — AI agent 1 of the program documented in
/// docs/ai-agent-program.md. Read-only by construction: it reads the trade
/// journal, computes stats locally, optionally asks a local LLM (Ollama /
/// Qwen3 by default, OpenAI-compatible protocol) to narrate them, and posts
/// the narrative to the same Discord/Slack webhook trade settlements use.
///
/// Type-7 guard rules it implements (see the program doc): weights are
/// frozen — "learning" is the prediction-memory loop only; output is text
/// that is never parsed into an order; every LLM call journals an AI_CALL
/// entry; any failure degrades to the deterministic template narrative; the
/// service never throws out of a tick.
///
/// LLM endpoint comes from the environment (TF_LLM_BASE_URL, TF_LLM_MODEL,
/// TF_LLM_API_KEY) so no secret ever lands in settings.json. Defaults point
/// at the local Ollama daemon (http://127.0.0.1:11434/v1, model qwen3:0.6b).
/// </summary>
public sealed class JournalAnalystService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>True disables the service entirely (no timer, no posts).</summary>
    public bool Disabled { get; set; }

    /// <summary>Live read-through of the persisted AnalystEnabled toggle —
    /// wired from settings so the Settings checkbox applies without a
    /// restart (same pattern as the milestone gate). Null/true = enabled.</summary>
    public Func<bool>? AnalystEnabledToggle { get; set; }

    /// <summary>Live read-through of AnalystMemoryEnabled. Null/true = on.</summary>
    public Func<bool>? MemoryEnabledToggle { get; set; }

    /// <summary>Overrides for tests (and exotic installs). Null = read the
    /// TF_LLM_* environment variables at call time.</summary>
    public Func<string?>? BaseUrlOverride { get; set; }
    public Func<string?>? ModelOverride { get; set; }
    public Func<string?>? ApiKeyOverride { get; set; }

    /// <summary>Prediction-memory state. Tests point this at a temp file.</summary>
    public AnalystMemory Memory { get; }

    private readonly TradeJournal _journal;
    private readonly WebhookService _webhook;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly bool _ownsHttpClient;
    private System.Threading.Timer? _timer;
    private int _busy;

    public JournalAnalystService(TradeJournal journal, WebhookService webhook, Action<string>? log = null,
        HttpClient? http = null, AnalystMemory? memory = null)
    {
        _journal = journal;
        _webhook = webhook;
        _log = log;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5)
        });
        _ownsHttpClient = http is null;
        Memory = memory ?? new AnalystMemory();
    }

    /// <summary>Starts the periodic analyst loop (first post after InitialDelay).</summary>
    public void Start()
    {
        if (Disabled)
        {
            return;
        }

        _timer = new System.Threading.Timer(_ => _ = TryPostAsync(), null, InitialDelay, Interval);
    }

    /// <summary>Reads TF_LLM_BASE_URL (default: local Ollama) at call time so
    /// tests and users can change the endpoint without a restart.</summary>
    private string BaseUrl => (BaseUrlOverride?.Invoke()
        ?? Environment.GetEnvironmentVariable("TF_LLM_BASE_URL"))
        ?.Trim().TrimEnd('/') is { Length: > 0 } url ? url : "http://127.0.0.1:11434/v1";

    private string Model => (ModelOverride?.Invoke()
        ?? Environment.GetEnvironmentVariable("TF_LLM_MODEL"))
        ?.Trim() is { Length: > 0 } model ? model : "qwen3:0.6b";

    private string? ApiKey => ApiKeyOverride?.Invoke()
        ?? Environment.GetEnvironmentVariable("TF_LLM_API_KEY");

    /// <summary>Read-only snapshot of recent entries for grading (flattened
    /// for the memory API). Symbol filter "*" means any symbol.</summary>
    private IReadOnlyList<(string Category, string Details)> ReadRecent(string symbol, DateTimeOffset since)
    {
        IEnumerable<JournalEntry> entries = _journal.GetRecent(null, 500);
        if (symbol is not (null or "*" or ""))
        {
            entries = entries.Where(e =>
                e.Details.Contains($"\"{symbol}\"", StringComparison.OrdinalIgnoreCase));
        }

        return entries
            .Where(e => e.Timestamp >= since)
            .Select(e => (e.Category, e.Details))
            .ToList();
    }

    private sealed record AnalystStats(
        int Decisions, int Signals, int Halts, int HaltRecoveries,
        int Settlements, int Wins, IReadOnlyList<KeyValuePair<string, int>> TopSymbols);

    private AnalystStats ComputeStats(IReadOnlyList<JournalEntry> entries)
    {
        var decisions = entries.Count(e => e.Category == "FX_DECISION");
        var signals = 0;
        var bySymbol = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => e.Category == "FX_SIGNAL"))
        {
            signals++;
            var symbol = ExtractString(e.Details, "Symbol");
            if (symbol is not null)
            {
                bySymbol[symbol] = bySymbol.TryGetValue(symbol, out var n) ? n + 1 : 1;
            }
        }

        var halts = entries.Count(e => e.Category == "FX_RISK" &&
            e.Details.Contains("halt", StringComparison.OrdinalIgnoreCase));
        var recoveries = entries.Count(e => e.Category == "FX_RISK" &&
            e.Details.Contains("recovered", StringComparison.OrdinalIgnoreCase));

        var settlements = 0;
        var wins = 0;
        foreach (var e in entries.Where(e => e.Category == "TRADE_SETTLEMENT"))
        {
            settlements++;
            try
            {
                using var doc = JsonDocument.Parse(e.Details);
                if (doc.RootElement.TryGetProperty("Won", out var won) && won.ValueKind == JsonValueKind.True)
                {
                    wins++;
                }
            }
            catch
            {
                if (e.Details.Contains("\"Won\":true")) wins++;
            }
        }

        return new AnalystStats(decisions, signals, halts, recoveries, settlements, wins,
            bySymbol.OrderByDescending(kv => kv.Value).ToList());
    }

    private static string? ExtractString(string json, string property)
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

    /// <summary>One analyst tick: compute → narrate (LLM or template) →
    /// remember → post. Returns the narrative, or null when nothing was
    /// posted (disabled, or a journal with nothing to say). Never throws.</summary>
    public async Task<string?> AnalyzeOnceAsync()
    {
        if (Disabled || AnalystEnabledToggle?.Invoke() == false)
        {
            return null;
        }

        try
        {
            var entries = _journal.GetRecent(null, 400);
            if (entries.Count == 0)
            {
                return null; // nothing to say — silence is the healthy case
            }

            var stats = ComputeStats(entries);

            // ── prediction memory: grade what came due, then remember anew ──
            string memoryLine = "";
            if (MemoryEnabledToggle?.Invoke() ?? true)
            {
                var graded = Memory.GradeDue(DateTimeOffset.UtcNow, ReadRecent);
                var (hits, total) = Memory.HitRate();
                if (graded.Count > 0)
                {
                    _journal.Log(Guid.Empty, "AI_MEMORY",
                        $"graded {graded.Count} prediction(s): {hits}/{total} on target");
                }

                memoryLine = total > 0
                    ? $"Prediction accuracy so far: {hits}/{total}. Be precise; last cycle's misses are above."
                    : "";

                // The guarded "learning": record what we expect next cycle.
                var lastDecision = entries.FirstOrDefault(e => e.Category == "FX_DECISION");
                var expectedSymbol = ExtractString(lastDecision?.Details ?? "", "Symbol") ?? "*";
                var laggards = stats.TopSymbols
                    .Where(kv => kv.Value == 0)
                    .Select(kv => kv.Key)
                    .ToList();
                Memory.RecordPrediction(new AnalystPrediction
                {
                    MadeAt = DateTimeOffset.UtcNow,
                    DueAt = DateTimeOffset.UtcNow.Add(Interval),
                    Symbol = laggards.Count > 0 ? laggards[0] : expectedSymbol,
                    Kind = stats.Halts > 0 ? "halt" : "activity",
                    Details = stats.Halts > 0
                        ? "risk conditions active — another halt is plausible"
                        : $"signal activity expected for {expectedSymbol}"
                });
            }

            var narrative = await ComposeNarrativeAsync(stats, memoryLine);

            _journal.Log(Guid.Empty, "AI_CALL",
                $"{Model} narrative ({(narrative.StartsWith("🤖", StringComparison.Ordinal) ? "llm" : "template")})");

            _webhook.PostStatus("🤖 Journal analyst", narrative);
            return narrative;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"analyst tick failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>LLM narrative when the endpoint answers, deterministic
    /// template otherwise. The prompt carries ONLY journal-derived stats —
    /// no credentials, no webhook URL, no file paths.</summary>
    private async Task<string> ComposeNarrativeAsync(AnalystStats stats, string memoryLine)
    {
        var prompt =
            $"Trading journal summary for the last {Interval.TotalHours:0.#}h: " +
            $"{stats.Decisions} decisions, {stats.Signals} signals, " +
            $"{stats.Settlements} settled trades ({stats.Wins} wins), " +
            $"{stats.Halts} risk halts ({stats.HaltRecoveries} auto-recovered). " +
            (stats.TopSymbols.Count > 0
                ? "Signal leaders: " + string.Join(", ", stats.TopSymbols.Take(4).Select(kv => $"{kv.Key} {kv.Value}")) + ". "
                : "") +
            memoryLine +
            " Write a tight 3-sentence trader's brief: what the session did, what to watch next, and one risk note. No advice, no disclaimers.";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
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
                    new { role = "system", content = "You are a concise trading-session analyst. Plain text, three sentences max, no markdown." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.3,
                max_tokens = 200
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

            _log?.Invoke($"analyst LLM ok in {sw.ElapsedMilliseconds} ms");
            return "🤖 " + text.Trim();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"analyst LLM failed ({sw.ElapsedMilliseconds} ms): {ex.Message} — using template");
            return ComposeTemplate(stats, memoryLine);
        }
    }

    private static string ComposeTemplate(AnalystStats s, string memoryLine)
    {
        var sb = new StringBuilder("📊 ");
        sb.Append($"{s.Decisions} decisions, {s.Signals} signals");
        if (s.Settlements > 0)
        {
            sb.Append($", {s.Settlements} settled ({s.Wins}W/{s.Settlements - s.Wins}L)");
        }

        sb.Append('.');
        if (s.Halts > 0)
        {
            sb.Append($" ⚠ {s.Halts} risk halt(s){(s.HaltRecoveries > 0 ? $" ({s.HaltRecoveries} auto-recovered)" : "")}.");
        }

        if (s.TopSymbols.Count > 0)
        {
            sb.Append(" Leaders: " + string.Join(", ", s.TopSymbols.Take(3).Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        }

        if (memoryLine.Length > 0)
        {
            sb.Append(' ').Append(memoryLine);
        }

        return sb.ToString();
    }

    private async Task TryPostAsync()
    {
        // Timer callbacks can overlap when a tick outlives the interval
        // (LLM latency); an Interlocked guard keeps ticks sequential —
        // same shape as MetricsDigestService._busy.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            await AnalyzeOnceAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
