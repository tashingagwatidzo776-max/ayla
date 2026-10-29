using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DongGfx.Core.Fx;

namespace DongGfx.App.Services;

/// <summary>The GWEN brain's transport: a loopback-only client for the
/// desk's OWN model host (DongGfx.GwenHost — Qwen3.5-4B through
/// llama.cpp/LLamaSharp, shipped and supervised by this app; no
/// third-party software on the machine — see docs/gwen-brain.md).
///
/// Behavior contract:
///  - Loopback URLs only, exactly like Mt5BridgeClient — the model is a
///    local process, never a remote endpoint. Infinite local tokens: no
///    provider, quota, or meter anywhere in the path.
///  - Fail-NEUTRAL: any error (server down, model unloaded, malformed
///    reply) yields a null verdict and the desk trades as if GWEN did not
///    exist. A missing LLM must never become an unexplained trading halt.
///  - Result cache: the same market state within the TTL costs zero
///    inference. This matters on CPU-only hardware where one assessment
///    takes seconds — the cache keeps the 60s brain cycle honest.
///
/// LAW: advisory with veto power only — assessments feed FxGwenAdvisor's
/// gate; this client never places, sizes or schedules anything.</summary>
public sealed class GwenBrainClient : IDisposable
{
    public const string DefaultModel = "gwen-fx";

    /// <summary>The built-in host's default loopback endpoint (kept off
    /// Ollama's old 11434 so a stray third-party daemon can never answer
    /// for GWEN).</summary>
    public static Uri DefaultUri { get; } = new("http://127.0.0.1:11435/");

    /// <summary>Sized from MEASURED latency on this desk (2026-10-25
    /// bench, Q4_K_M, published host): cold 781 s (a fresh process pages
    /// the model in), warm ~245 s — llama-bench ceiling on this CPU is
    /// ~1.9 t/s prompt eval / ~0.8 t/s generation, so minutes per verdict
    /// is this hardware's floor. 880 s is the first value that covers the
    /// measured cold: the old 720 s timed out at exactly 720 s on the
    /// 2026-10-26 restart (two fail-open verdicts while the model warmed)
    /// — while staying under the host's 900 s GenerationDeadline so OUR
    /// timeout fires first. Anything slower is a null verdict, never an
    /// error (fail-neutral — the desk trades as if GWEN never spoke).
    /// Worst-case inline wait for the brain cycle is bounded by this
    /// number, and only on a cache miss behind a qualifying signal.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(880);

    /// <summary>How long the model stays resident in RAM after a call. Idle
    /// desk → the RAM comes back; active desk → no reload penalty.</summary>
    public string KeepAlive { get; set; } = "5m";

    public string Model { get; set; } = DefaultModel;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _timeout;
    private readonly GwenHostSupervisor? _supervisor;
    private readonly object _gate = new();
    private readonly Dictionary<string, (FxGwenVerdict Verdict, DateTimeOffset At)> _cache = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private const int CacheMaxEntries = 16;

    public GwenBrainClient()
        : this(DefaultUri)
    {
    }

    public GwenBrainClient(Uri baseAddress)
        : this(new HttpClient(), baseAddress, DefaultTimeout, ownsHttp: true)
    {
    }

    /// <summary>The desk's wire-up: the host supervisor makes sure the
    /// built-in model process exists before each inference attempt (and
    /// restarts it after a fault — the model can crash; the desk cannot).</summary>
    public GwenBrainClient(Uri baseAddress, GwenHostSupervisor supervisor)
        : this(new HttpClient(), baseAddress, DefaultTimeout, ownsHttp: true)
    {
        _supervisor = supervisor;
    }

    /// <summary>Test seam: an injected handler (the loopback stub servers the
    /// bridge tests already use).</summary>
    public GwenBrainClient(HttpMessageHandler handler, Uri baseAddress, TimeSpan? timeout = null)
        : this(new HttpClient(handler), baseAddress, timeout ?? DefaultTimeout, ownsHttp: true)
    {
    }

    private GwenBrainClient(HttpClient http, Uri baseAddress, TimeSpan timeout, bool ownsHttp)
    {
        if (baseAddress.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
        {
            throw new ArgumentException(
                "GWEN is a local model — loopback base addresses only", nameof(baseAddress));
        }
        _http = http;
        _http.BaseAddress = baseAddress;
        _http.Timeout = timeout;
        _timeout = timeout;
        _ownsHttp = ownsHttp;
    }

    /// <summary>Best-effort assessment: a cached or fresh verdict, or null
    /// when the model cannot speak (down, slow, malformed). Never throws.</summary>
    public async Task<FxGwenVerdict?> AssessAsync(FxGwenRequest req, CancellationToken ct = default)
    {
        var key = CacheKey(req);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit) &&
                DateTimeOffset.UtcNow - hit.At < CacheTtl)
            {
                return hit.Verdict;
            }
        }

        try
        {
            // Best-effort host supervision (cache miss only): if the host
            // cannot be (re)started the POST fails fast and this returns
            // null — the fail-neutral contract holds either way.
            if (_supervisor is not null)
            {
                await _supervisor.EnsureAsync(ct).ConfigureAwait(false);
            }
            var prompt = FxGwenAdvisor.BuildPrompt(req);
            var sw = Stopwatch.StartNew();
            var reply = await PostChatAsync(prompt, ct).ConfigureAwait(false);
            sw.Stop();

            var verdict = FxGwenAdvisor.Parse(reply, sw.Elapsed.TotalMilliseconds);
            if (verdict is not null)
            {
                lock (_gate)
                {
                    if (_cache.Count >= CacheMaxEntries)
                    {
                        var oldest = _cache.OrderBy(kv => kv.Value.At).First().Key;
                        _cache.Remove(oldest);
                    }
                    _cache[key] = (verdict, DateTimeOffset.UtcNow);
                }
            }
            return verdict;
        }
        catch (Exception)
        {
            // Fail-neutral by contract: the caller journals "no verdict" and
            // the deterministic desks trade exactly as before.
            return null;
        }
    }

    /// <summary>The assessment key: the same proposed trade on the same bar
    /// reuses the cached verdict instead of paying another inference.</summary>
    internal static string CacheKey(FxGwenRequest req)
    {
        var lastBar = req.Bars is { Count: > 0 } ? req.Bars[^1].Time : 0L;
        var confBucket = (int)(Math.Clamp(req.AlphaConfidence, 0, 1) * 10);
        return string.Join('|', req.Symbol, req.Side, req.Alpha, req.Regime,
            confBucket, lastBar.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>One non-streaming chat round-trip against the Ollama HTTP
    /// API. Returns the assistant text, or null on a non-success status.
    /// Tests inject a handler through the public constructor (the same seam
    /// Mt5BridgeClient exposes) instead of overriding.</summary>
    private async Task<string?> PostChatAsync(string prompt, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            stream = false,
            keep_alive = KeepAlive,
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("api/chat", content, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("message", out var msg) &&
               msg.TryGetProperty("content", out var text)
            ? text.GetString()
            : null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
