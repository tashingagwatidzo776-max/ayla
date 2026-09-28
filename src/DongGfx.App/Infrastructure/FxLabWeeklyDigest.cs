using System.IO;
using System.Text;
using System.Text.Json;
using DongGfx.Core.Logging;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Weekly FX_LAB digest: the genetic lab journals every walk-forward run as
/// <c>FX_LAB</c>; this service rolls the last 7 days of them into one
/// summary — webhook post (same channel as settlements) plus an append to
/// the soak evidence doc, where committing evidence stays a deliberate human
/// act (the working-copy edit is left uncommitted like every soak entry).
/// Journal-only by construction: it reads the journal and posts text — it
/// never touches the bridge, the engine, or the order path. Guardrails
/// mirror <see cref="MetricsDigestService"/>: timer + Disabled + a busy
/// latch, and every failure is swallowed into a log line, never a crash.
/// </summary>
public sealed class FxLabWeeklyDigest : IDisposable
{
    /// <summary>Time between digests. Default: weekly. Tests shrink this.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Delay before the first digest.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>True disables the service entirely (no timer, no posts).</summary>
    public bool Disabled { get; set; }

    /// <summary>Markdown file the digest appends to. Null (or a missing
    /// parent dir) skips the append leg — the webhook still posts.</summary>
    public string? SoakDocPath { get; set; }

    /// <summary>Override for tests: the entries the digest sees. Null =
    /// read the real journal.</summary>
    public Func<IReadOnlyList<JournalEntry>>? EntriesOverride { get; set; }

    private readonly TradeJournal _journal;
    private readonly WebhookService? _webhook;
    private System.Threading.Timer? _timer;
    private int _busy;

    public FxLabWeeklyDigest(TradeJournal journal, WebhookService? webhook = null)
    {
        _journal = journal;
        _webhook = webhook;
    }

    /// <summary>Starts the weekly loop.</summary>
    public void Start()
    {
        if (Disabled)
        {
            return;
        }

        _timer = new System.Threading.Timer(
            _ => _ = RunOnceAsync(), null, InitialDelay, Interval);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One digest pass: read, build, post, append. Returns the
    /// summary text (empty when there was nothing to report — silence with
    /// no lab runs is correct, not an error).</summary>
    public async Task<string> RunOnceAsync()
    {
        if (Disabled || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return string.Empty;
        }

        try
        {
            var entries = EntriesOverride?.Invoke()
                ?? _journal.GetRecent(null, 20_000);
            var (message, markdown) = Build(entries, DateTimeOffset.UtcNow);
            if (message.Length == 0)
            {
                return string.Empty;
            }

            _webhook?.PostStatus("FX lab weekly digest", message);

            if (SoakDocPath is { } path
                && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            {
                await File.AppendAllTextAsync(
                    path, markdown, Encoding.UTF8).ConfigureAwait(false);
            }

            return message;
        }
        catch
        {
            // A digest is an observability nicety — never a crash surface.
            return string.Empty;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Pure builder: the last 7 days of FX_LAB entries in, webhook
    /// message + markdown append out. Empty strings = nothing to report.</summary>
    internal static (string Message, string Markdown) Build(
        IReadOnlyList<JournalEntry> entries, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(7);
        var runs = new List<(DateTimeOffset At, string Symbol, int Bars, int Folds, int Positive, bool Approved, string Verdict)>();
        foreach (var e in entries)
        {
            if (e.Category != "FX_LAB" || e.Timestamp < cutoff)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(e.Details);
                var r = doc.RootElement;
                runs.Add((
                    e.Timestamp,
                    r.TryGetProperty("Symbol", out var s) ? s.GetString() ?? "?" : "?",
                    r.TryGetProperty("Bars", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0,
                    r.TryGetProperty("Folds", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0,
                    r.TryGetProperty("Positive", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                    r.TryGetProperty("Approved", out var a) && a.ValueKind == JsonValueKind.True,
                    r.TryGetProperty("Verdict", out var v) ? v.GetString() ?? "" : ""));
            }
            catch
            {
                // Malformed/legacy entry — skip it, digest the rest.
            }
        }

        if (runs.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var approved = runs.Count(x => x.Approved);
        var symbols = runs.Select(x => x.Symbol).Distinct().OrderBy(x => x).ToList();
        var lines = runs
            .OrderBy(x => x.At)
            .Select(x => $"- {x.At:yyyy-MM-dd HH:mm} [{x.Symbol}] {x.Bars} bars, {x.Folds} folds, {x.Positive} OOS positive — {(x.Approved ? "APPROVED-FOR-REVIEW" : "not approved")}")
            .ToList();

        var message =
            $"{runs.Count} lab run(s) this week across {symbols.Count} symbol(s): {string.Join(", ", symbols)}. " +
            $"{approved} approved-for-review, {runs.Count - approved} not approved. Human port remains a reviewed PR — nothing auto-promotes.";
        var markdown =
            $"\n\n## FX lab weekly digest — {now:yyyy-MM-dd}\n\n" +
            $"{runs.Count} run(s), {approved} approved-for-review.\n\n" +
            string.Join("\n", lines) + "\n";
        return (message, markdown);
    }
}
