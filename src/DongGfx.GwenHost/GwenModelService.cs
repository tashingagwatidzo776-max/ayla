using System.Reflection;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace DongGfx.GwenHost;

/// <summary>The GWEN model itself: a Qwen3.5-4B f16 GGUF loaded through
/// llama.cpp (LLamaSharp) inside this process.
///
/// Contract:
///  - Lazy load: /health answers instantly even while the first request is
///    still paging gigabytes off disk; the load happens under a lock so only
///    one request ever pays it.
///  - Single-flight: one generation at a time (CPU-only desk — parallel
///    generations would only thrash the same cores).
///  - CPU only (GpuLayerCount 0): the desk's GPU cannot hold the model.
///  - Never throws out of GenerateAsync for MODEL problems: a load or
///    inference fault surfaces as an exception the server maps to a 500,
///    and the app's client turns that into "no verdict" — the fail-neutral
///    law that must never become an unexplained trading halt.</summary>
public sealed class GwenModelService : IDisposable
{
    public const string ModelName = "gwen-fx";

    /// <summary>Generation temperature — the Modelfile used 0.1: verdicts
    /// must be deterministic-ish, not creative.</summary>
    public const float Temperature = 0.1f;

    /// <summary>Hard cap on generated tokens. NOT a token quota (this desk
    /// has none — inference is local and unmetered); it is the format guard
    /// that keeps a mis-answering model from talking past the one-JSON-
    /// object contract the parser requires — and, because the gate is
    /// single-flight, the bound on how long ONE runaway generation can
    /// stall every symbol's verdict. Measured 2026-10-24 (cold + 4 warm
    /// runs against the published host): verdicts ran 273–397 chars
    /// ≈ up to ~110 real tokens (Qwen BPE ≈ 3.6 chars/token on verdict
    /// JSON) and generation early-stops at the first parseable object, so
    /// the cap never binds on a healthy answer. 160 keeps ~45% headroom
    /// over the longest observed verdict while bounding a runaway to
    /// ~3.5 min at the desk's measured ~1.3 s/token (was ~5.4 min at
    /// 256). Trimmed from 256 on this data — a lower cap (128) would
    /// crowd the longest valid verdicts and risk truncating one.</summary>
    public const int MaxReplyTokens = 160;

    public const uint ContextSize = 4096;

    private readonly string _ggufPath;
    private readonly string _systemPrompt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _model;
    private string? _lastError;

    public GwenModelService(string ggufPath, string? systemPrompt = null)
    {
        _ggufPath = ggufPath;
        _systemPrompt = systemPrompt ?? EmbeddedSystemPrompt();
    }

    public bool IsLoaded => _model is not null;
    public string? LastError => _lastError;

    /// <summary>The desk's GWEN persona, embedded at build time from
    /// scripts/gwen_system_prompt.txt (the same file the fine-tuning
    /// dataset builder reads — serving and training cannot drift).</summary>
    public static string EmbeddedSystemPrompt()
    {
        var asm = Assembly.GetExecutingAssembly();
        const string name = "DongGfx.GwenHost.GwenSystemPrompt.txt";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded prompt missing: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    /// <summary>One assessment: the user message content, framed with the
    /// GWEN system prompt through the model's own chat template.</summary>
    public async Task<string> GenerateAsync(string userPrompt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return await GenerateCore(userPrompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Remember why, so /health can tell the operator; the caller
            // maps this to HTTP 500 and the app stays fail-neutral.
            _lastError = ex.GetType().Name + ": " + ex.Message;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ModelParams Params() => new(_ggufPath)
    {
        ContextSize = ContextSize,
        GpuLayerCount = 0,   // CPU-only desk (2 GB GPU cannot hold it)
        // Use every logical core for BOTH phases. Measured on this exact
        // desk with llama-bench (b10221, same GGUF, desk load on): 2 threads
        // beat 1 on both phases — tg 0.79 vs 0.61 t/s, pp 1.90 vs 1.23 t/s.
        // The earlier 1-thread "physical-core convention" was a regression:
        // this CPU reports 1C/2T and SMT helps decode here rather than
        // hurting it.
        Threads = Math.Max(1, Environment.ProcessorCount),
        BatchThreads = Math.Max(1, Environment.ProcessorCount),
    };

    private void EnsureLoaded()
    {
        if (_model is not null)
        {
            return;
        }
        _model = LLamaWeights.LoadFromFile(Params());
    }

    private async Task<string> GenerateCore(string userPrompt, CancellationToken ct)
    {
        var model = _model ?? throw new InvalidOperationException("model not loaded");
        // A FRESH context per assessment, weights shared and resident.
        // Each verdict must be an independent, stateless read — no KV
        // bleed between market states — and reusing one LLamaContext
        // across ChatSessions faults llama.cpp on the second call (the
        // old session's KV state is still resident). Cost of a fresh
        // context is milliseconds; the 2.5 GB of weights are never
        // re-read.
        using var context = model.CreateContext(Params());
        // History carries ONLY the system prompt: ChatAsync appends the
        // user message itself (session state must not double-add it).
        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, _systemPrompt);

        var executor = new InteractiveExecutor(context);
        // The GGUF carries NO embedded chat template (tokenizer_config.json
        // had none), so LLamaSharp's default "System:/User:" plain-text
        // rendering applies — a format this Qwen checkpoint was never
        // trained on: it echoed the prompt back instead of answering.
        // The Qwen role-tagged template is applied explicitly here.
        var session = new ChatSession(executor, history)
            .WithHistoryTransform(new QwenChatTemplate());
        var inference = new InferenceParams
        {
            MaxTokens = MaxReplyTokens,
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = Temperature },
        };

        var body = new System.Text.StringBuilder();
        await foreach (var text in session.ChatAsync(
            new ChatHistory.Message(AuthorRole.User, userPrompt), inference).WithCancellation(ct))
        {
            body.Append(text);
            // Stop generation at the FIRST complete, parseable object —
            // generation only advances when MoveNextAsync is awaited, so
            // breaking here halts the model instead of paying for (and
            // being corrupted by) whatever it rambles on with afterwards.
            if (CompleteJsonObject(body.ToString()) is { } verdict)
            {
                return verdict;
            }
        }
        return body.ToString();
    }

    /// <summary>Reconstructs the answer object while it streams. The '{'
    /// prefill lives in the PROMPT, not in the streamed text, so the raw
    /// stream is the object BODY — prepend the brace back, and accept the
    /// first BALANCED slice that actually parses as a JSON object.
    /// Measured 2026-10-23: without this cut-off the model completed one
    /// perfect verdict and then looped into a second truncated copy, which
    /// left the client's tolerant first-'{'-to-last-'}' parser with the
    /// opening brace AFTER the only closing one → null verdict despite a
    /// correct answer being present. Returns null while incomplete.</summary>
    private static string? CompleteJsonObject(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        string candidate = trimmed.Length > 0 && trimmed[0] == '{'
            ? body                    // the model opened its own brace
            : "{" + body;            // restore the prompt-side prefill
        var end = BalancedEnd(candidate);
        if (end <= 0)
        {
            return null;
        }
        var slice = candidate[..end];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(slice);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                return slice;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // balanced but not valid JSON (e.g. a stray early '}') — keep generating
        }
        return null;
    }

    /// <summary>Index just past the brace that closes the OUTERMOST '{',
    /// string-aware (a reason may legally contain braces); 0 = unbalanced.</summary>
    private static int BalancedEnd(string s)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') { depth++; continue; }
            if (c == '}')
            {
                if (--depth == 0) return i + 1;
                if (depth < 0) return 0;
            }
        }
        return 0;
    }

    public void Dispose()
    {
        _model?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Renders chat history in Qwen's own role-tagged template
/// (the format in the checkpoint's chat_template.jinja):
///   &lt;|im_start|&gt;system\n{content}&lt;|im_end|&gt;\n …
///   &lt;|im_start|&gt;user\n{content}&lt;|im_end|&gt;\n
///   &lt;|im_start|&gt;assistant\n
/// with the turn left open for generation. Written by hand because the
/// GGUF ships without an embedded template, and LLamaSharp's built-in
/// default transform uses plain "System:" lines this model misreads.</summary>
internal sealed class QwenChatTemplate : LLama.Abstractions.IHistoryTransform
{
    private const string Start = "\u003c|im_start|\u003e";
    private const string End = "\u003c|im_end|\u003e";

    public LLama.Abstractions.IHistoryTransform Clone() => new QwenChatTemplate();

    public string HistoryToText(LLama.Common.ChatHistory history)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in history.Messages)
        {
            sb.Append(Start).Append(Role(m.AuthorRole)).Append('\n')
              .Append(m.Content).Append(End).Append('\n');
        }
        // The assistant turn is OPEN — and it deliberately opens with '{'
        // (answer-first prefill). This reasoning-distilled checkpoint ignores
        // prose asking it not to deliberate: it emitted <think>, analyzed a
        // while, then EOS'd mid-thought — NO JSON, so no verdict at all
        // (measured 2026-10-23: an 870 s assessment, zero verdict). Once the
        // assistant turn has visibly begun with '{', the only continuation
        // is the JSON object the parser requires.
        sb.Append(Start).Append("assistant\n{");
        return sb.ToString();
    }

    public LLama.Common.ChatHistory TextToHistory(LLama.Common.AuthorRole role, string text)
    {
        var h = new LLama.Common.ChatHistory();
        h.AddMessage(role, text);
        return h;
    }

    private static string Role(LLama.Common.AuthorRole role) =>
        role == LLama.Common.AuthorRole.System ? "system" :
        role == LLama.Common.AuthorRole.User ? "user" : "assistant";
}
