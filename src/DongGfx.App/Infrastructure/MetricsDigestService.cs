using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using DongGfx.Core.Analytics;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Periodically composes an operational digest from the live cycle telemetry
/// (latency/error samples collected by the growth runners and the LLM-tab
/// loop) and posts it to the Discord/Slack webhook — the same channel trade
/// settlements use — so monitoring sees session health without anyone
/// exporting manually.
///
/// Machine-side by design: the live telemetry and the webhook URL both exist
/// only where the app runs, so this service owns the posting loop. As a
/// best-effort complement it also dispatches a GitHub
/// <c>metrics-digest</c> repository_dispatch when a token is configured
/// (CI picks it up to digest the machine-published bankroll artifacts);
/// that leg is silent no-op without a token and never blocks the webhook
/// post.
///
/// Mirrors the bankroll publisher's guardrails: a throwing webhook must
/// never break the timer, no digest is posted when there is nothing to
/// report, the first post goes out after the initial delay, and every tick
/// is fully isolated.
/// </summary>
public sealed class MetricsDigestService : IDisposable
{
    /// <summary>Time between digest posts. Tests shrink this to poll fast.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Delay before the first digest (lets the session warm up).</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>True disables the service entirely (no timer, no posts).</summary>
    public bool Disabled { get; set; }

    /// <summary>GitHub API token for the optional repository_dispatch leg.
    /// Null/empty disables that leg (the webhook digest still posts).</summary>
    public string? GitHubToken { get; set; }

    /// <summary>repository/repo for the dispatch leg, e.g. "owner/ayla".</summary>
    public string? GitHubRepo { get; set; }

    /// <summary>Path of docs/real-money-safety-audit.md, or null when the app
    /// runs outside a checkout (the audit leg silently no-ops then).</summary>
    public string? SafetyAuditPath { get; set; }

    /// <summary>Where the last-posted audit hash is persisted. Defaults to a
    /// file next to the app's settings data; tests point it at a temp path.</summary>
    public string SafetyAuditStatePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tf", "notifications", "safety-audit-digest.state");

    /// <summary>Override for tests (and exotic installs): the audit markdown.
    /// Null = load from <see cref="SafetyAuditPath"/>.</summary>.
    public Func<string?>? SafetyAuditContentOverride { get; set; }

    /// <summary>Current real-money session unlock state (which accounts are
    /// armed, since when, and whether the manual surfaces share the unlock).
    /// Every posted digest carries it so monitoring can see real trading was
    /// re-enabled after an app restart — and, just as important, the silence
    /// after a restart means nothing is armed. Null disables the leg.</summary>
    public Func<string?>? UnlockStateProvider { get; set; }

    /// <summary>FX brain state line for the digest: mode, symbols, soak
    /// progress, halt reason, and the latest alpha-scorecard verdict. Null
    /// disables the leg.</summary>
    public Func<string?>? FxStateProvider { get; set; }

    private readonly MetricsCollector _metrics;
    private readonly WebhookService _webhook;
    private readonly HttpClient _http = new();
    private readonly Action<string>? _log;
    private System.Threading.Timer? _timer;

    public MetricsDigestService(MetricsCollector metrics, WebhookService webhook, Action<string>? log = null)
    {
        _metrics = metrics;
        _webhook = webhook;
        _log = log;
    }

    /// <summary>Starts the periodic digest loop (first post after InitialDelay).</summary>
    public void Start()
    {
        if (Disabled)
        {
            return;
        }

        _timer = new System.Threading.Timer(
            _ => TryPostDigest(), null, InitialDelay, Interval);
    }

    /// <summary>Composes the digest body from the current telemetry snapshot.
    /// Returns null when there is nothing worth posting — no samples at all,
    /// no errors, no latency observations, and no safety-audit change.</summary>
    public string? ComposeDigest()
    {
        var latency = _metrics.Latency;
        var errors = _metrics.Errors;
        var audit = ComposeSafetyAuditSection();
        var unlock = ComposeUnlockSection();
        var fx = ComposeFxSection();
        if (latency.Count == 0 && errors.Count == 0 && audit is null && unlock is null && fx is null)
        {
            return null;
        }

        var parts = new List<string>();

        if (latency.Count > 0)
        {
            var values = latency.Select(s => s.Value).OrderBy(v => v).ToList();
            var mean = values.Average();
            var p95 = Percentile(values, 0.95);
            parts.Add($"cycles {latency.Count} | latency mean {mean:0.#}ms · p95 {p95:0.#}ms · max {values[^1]:0.#}ms");
        }

        if (errors.Count > 0)
        {
            var byAccount = errors
                .Where(s => !string.IsNullOrEmpty(s.Account))
                .GroupBy(s => s.Account!)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            var suffix = byAccount.Count > 0 ? $" ({string.Join(", ", byAccount)})" : "";
            parts.Add($"errors {errors.Count}{suffix}");
        }

        if (audit is not null)
        {
            parts.Add(audit);
        }

        if (unlock is not null)
        {
            parts.Add(unlock);
        }

        if (fx is not null)
        {
            parts.Add(fx);
        }

        return string.Join(" | ", parts);
    }

    /// <summary>The FX-brain section for this tick, or null when no provider
    /// is wired or it has nothing to say.</summary>
    private string? ComposeFxSection()
    {
        if (FxStateProvider is null)
        {
            return null;
        }

        try
        {
            return FxStateProvider();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"fx-state digest read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The real-money unlock arm-state section, or null when no
    /// provider is wired or nothing is armed. Read-only; re-computed every
    /// tick so the arm age stays current.</summary>
    private string? ComposeUnlockSection()
    {
        if (UnlockStateProvider is null)
        {
            return null;
        }

        try
        {
            return UnlockStateProvider();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"unlock-state digest read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The safety-audit section for this tick, or null when the
    /// audit is unchanged/missing. Composing has a side effect only in
    /// TryPostDigest, which persists the hash after a successful post —
    /// this method itself is read-only.</summary>
    private string? ComposeSafetyAuditSection()
    {
        if (SafetyAuditPath is null)
        {
            return null;
        }

        try
        {
            var markdown = SafetyAuditContentOverride is not null
                ? SafetyAuditContentOverride()
                : File.ReadAllText(SafetyAuditPath);
            var table = SafetyAuditDigest.ExtractCoverageTable(markdown);
            if (table is null)
            {
                // A structurally broken audit must not post a misleading
                // "rails unchanged" digest — surface it instead.
                return "⚠ real-money-safety-audit.md is missing its coverage table — " +
                       "check the audit doc structure";
            }

            var hash = SafetyAuditDigest.Hash(table);
            var last = SafetyAuditDigest.ReadStateHash(SafetyAuditStatePath);
            return last == hash
                ? null // unchanged since the last post — silence is healthy
                : $"🛡 safety rails changed (audit hash {last ?? "first"} → {hash}):\n{table}";
        }
        catch (IOException ex)
        {
            _log?.Invoke($"safety-audit digest read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>One posting tick — webhook first, dispatch best-effort.
    /// Never throws. The safety-audit leg posts (and records its hash) only
    /// when the coverage table actually changed; a throwing webhook never
    /// breaks the timer.</summary>
    public void TryPostDigest()
    {
        try
        {
            var digest = ComposeDigest();
            if (digest is null)
            {
                return; // nothing to report — silence is the healthy case
            }

            _webhook.PostStatus("📊 Cycle metrics digest", digest);

            // Persist the audit hash only after the post went out (or was
            // accepted fire-and-forget), so a failed webhook retries the
            // table on the next tick rather than swallowing the change.
            if (SafetyAuditPath is not null)
            {
                try
                {
                    var markdown = SafetyAuditContentOverride is not null
                        ? SafetyAuditContentOverride()
                        : File.ReadAllText(SafetyAuditPath);
                    var table = SafetyAuditDigest.ExtractCoverageTable(markdown);
                    if (table is not null)
                    {
                        SafetyAuditDigest.WriteStateHash(
                            SafetyAuditStatePath, SafetyAuditDigest.Hash(table));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log?.Invoke($"safety-audit state write failed: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(GitHubToken) && !string.IsNullOrEmpty(GitHubRepo))
            {
                TryDispatch(digest);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"metrics digest tick failed: {ex.Message}");
        }
    }

    private void TryDispatch(string digest)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.github.com/repos/{GitHubRepo}/dispatches");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GitHubToken);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("tf-metrics-digest");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    @event_type = "metrics-digest",
                    client_payload = new { digest }
                }),
                Encoding.UTF8,
                "application/json");

            using var response = _http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"metrics digest dispatch returned {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"metrics digest dispatch failed: {ex.Message}");
        }
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}
