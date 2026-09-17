using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Tf.App.Infrastructure;
using Tf.Core.Analytics;

namespace Tf.App.Tests;

/// <summary>
/// Tests for the machine-side cycle-telemetry digest: composition from the
/// shared collector (nothing to report → no post; errors grouped per
/// account; latency percentiles) and the posting loop verified against a
/// real local capture listener.
/// </summary>
[Trait("Category", "Unit")]
public class MetricsDigestServiceTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly string _url;
    private readonly List<string> _bodies = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();

    public MetricsDigestServiceTests()
    {
        (_listener, _port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        _url = $"http://127.0.0.1:{_port}/hook";
        _ = Task.Run(() => CaptureAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { /* best effort */ }
        _cts.Dispose();
    }

    private async Task CaptureAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);

            context.Response.StatusCode = 200;
            context.Response.Close();

            lock (_sync) { _bodies.Add(body); }
        }
    }

    private int Count { get { lock (_sync) { return _bodies.Count; } } }
    private List<string> Bodies { get { lock (_sync) { return _bodies.ToList(); } } }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }

    private MetricsDigestService NewService(MetricsCollector collector) =>
        new(collector, new WebhookService { WebhookUrl = _url, IsDiscord = true, MinInterval = TimeSpan.Zero });

    [Fact]
    public void ComposeDigest_NoSamples_ReturnsNull()
    {
        var digest = NewService(new MetricsCollector());

        Assert.Null(digest.ComposeDigest());
    }

    [Fact]
    public void ComposeDigest_LatencyOnly_ShowsCountMeanAndMax()
    {
        var collector = new MetricsCollector();
        collector.RecordLatency(100, "Alpha", DateTimeOffset.UtcNow);
        collector.RecordLatency(300, "Alpha", DateTimeOffset.UtcNow);

        var text = NewService(collector).ComposeDigest();

        Assert.NotNull(text);
        Assert.Contains("cycles 2", text);
        Assert.Contains("mean 200", text);
        Assert.Contains("max 300", text);
        Assert.DoesNotContain("errors", text);
    }

    [Fact]
    public void ComposeDigest_ErrorsAreGroupedByAccount()
    {
        var collector = new MetricsCollector();
        collector.RecordError("Alpha", DateTimeOffset.UtcNow);
        collector.RecordError("Alpha", DateTimeOffset.UtcNow);
        collector.RecordError("Beta", DateTimeOffset.UtcNow);

        var text = NewService(collector).ComposeDigest();

        Assert.NotNull(text);
        Assert.Contains("errors 3", text);
        Assert.Contains("Alpha: 2", text);
        Assert.Contains("Beta: 1", text);
    }

    [Fact]
    public async Task TryPostDigest_NothingToReport_SendsNothing()
    {
        var digest = NewService(new MetricsCollector());

        digest.TryPostDigest();
        await WaitForAsync(() => Count >= 1, 300);

        Assert.Equal(0, Count);
    }

    [Fact]
    public async Task TryPostDigest_WithTelemetry_PostsStatusToWebhook()
    {
        var collector = new MetricsCollector();
        collector.RecordLatency(120, "Alpha", DateTimeOffset.UtcNow);
        collector.RecordError("Alpha", DateTimeOffset.UtcNow);
        var digest = NewService(collector);

        digest.TryPostDigest();
        await WaitForAsync(() => Count >= 1);

        var body = Assert.Single(Bodies);
        using var doc = JsonDocument.Parse(body);
        var embed = doc.RootElement.GetProperty("embeds")[0];
        Assert.Contains("Cycle metrics digest", embed.GetProperty("title").GetString());
        Assert.Contains("latency", embed.GetProperty("description").GetString());
        Assert.Contains("errors 1", embed.GetProperty("description").GetString());
    }

    [Fact]
    public void Disabled_StartsNoTimer()
    {
        using var digest = NewService(new MetricsCollector());
        digest.Interval = TimeSpan.FromMilliseconds(50);
        digest.Disabled = true;

        digest.Start(); // must not throw or schedule anything

        Assert.Equal(0, Count);
    }

    // ── Safety-audit digest leg ──────────────────────────────────────────

    private const string AuditMarkdown = """
        # Real-money safety rails — coverage audit

        intro text

        ## The rails

        | Rail | Where |
        |---|---|
        | Kill switch | Dashboard latch |

        ## Path 1 — Growth engines

        table rows
        """;

    private MetricsDigestService NewAuditService(string markdown, string statePath) =>
        new(new MetricsCollector(), new WebhookService { WebhookUrl = _url, IsDiscord = true, MinInterval = TimeSpan.Zero })
        {
            SafetyAuditPath = "audit.md", // non-null enables the leg; content comes from the override
            SafetyAuditContentOverride = () => markdown,
            SafetyAuditStatePath = statePath,
        };

    private static string StatePath() =>
        Path.Combine(Path.GetTempPath(), $"tf_audit_{Guid.NewGuid():N}.state");

    [Fact]
    public async Task SafetyAudit_ChangedTable_IsPostedOnce()
    {
        var state = StatePath();
        var digest = NewAuditService(AuditMarkdown, state);

        digest.TryPostDigest();
        await WaitForAsync(() => Count >= 1);

        var body = Assert.Single(Bodies);
        using var doc = JsonDocument.Parse(body);
        var embed = doc.RootElement.GetProperty("embeds")[0];
        Assert.Contains("safety rails changed", embed.GetProperty("description").GetString());
        Assert.Contains("Kill switch", embed.GetProperty("description").GetString());

        // The state now records the posted hash — a second tick is silent
        // (no telemetry, unchanged table ⇒ nothing to report).
        Assert.Equal(SafetyAuditDigest.Hash(
            SafetyAuditDigest.ExtractCoverageTable(AuditMarkdown)!),
            File.ReadAllText(state).Trim());

        digest.TryPostDigest();
        await WaitForAsync(() => Count >= 2, 300);
        Assert.Equal(1, Count);
    }

    [Fact]
    public async Task SafetyAudit_RailEdit_RepostsTheNewTable()
    {
        var state = StatePath();
        var markdown = AuditMarkdown;
        var digest = NewAuditService(markdown, state);
        digest.TryPostDigest();
        await WaitForAsync(() => Count >= 1);

        // A rail change (the release scenario): the edited table posts again.
        markdown = AuditMarkdown.Replace("| Kill switch | Dashboard latch |",
            "| Kill switch | Dashboard latch |\n| Real-money gate | RealMoneyGate.Evaluate |", StringComparison.Ordinal);
        digest.SafetyAuditContentOverride = () => markdown;
        digest.TryPostDigest();
        await WaitForAsync(() => Count >= 2);

        Assert.Equal(2, Count);
        Assert.Contains("RealMoneyGate.Evaluate", Bodies[1]);
    }

    [Fact]
    public void SafetyAudit_MissingCoverageSection_IsSurfacedNotSilenced()
    {
        var digest = NewAuditService("# broken doc — no rails section", StatePath());

        var section = digest.ComposeDigest();

        Assert.NotNull(section);
        Assert.Contains("missing its coverage table", section);
    }

    [Fact]
    public void SafetyAuditDigest_FindAuditPath_WalksToCheckoutAndExtractsTheRealDoc()
    {
        // The real audit doc must yield its table — guards against the doc
        // structure drifting away from the extractor silently.
        var auditPath = SafetyAuditDigest.FindAuditPath(AppContext.BaseDirectory);
        Assert.NotNull(auditPath);
        Assert.True(File.Exists(auditPath));

        var table = SafetyAuditDigest.ExtractCoverageTable(File.ReadAllText(auditPath));
        Assert.NotNull(table);
        Assert.Contains("The rails", table);
        Assert.Contains("RealMoneyGate", table);
    }

    // ── Unlock arm-state leg ────────────────────────────────────────────

    [Fact]
    public void UnlockState_NothingArmed_IsSilent()
    {
        var digest = NewService(new MetricsCollector());
        digest.UnlockStateProvider = () => null; // nothing armed → silence

        Assert.Null(digest.ComposeDigest()); // no telemetry, no arms → no post
    }

    [Fact]
    public void UnlockState_Armed_AccountsPost_EvenWithoutTelemetry()
    {
        var digest = NewService(new MetricsCollector());
        digest.UnlockStateProvider = () =>
            "🔒 real-money unlock ARMED this session: Beta (3m ago), manual surfaces";

        var text = digest.ComposeDigest();

        // This is the restart-visibility guarantee: real trading was
        // re-enabled and the digest says so even on a quiet machine.
        Assert.NotNull(text);
        Assert.Contains("ARMED", text);
        Assert.Contains("Beta", text);
        Assert.Contains("manual surfaces", text);
    }

    [Fact]
    public void UnlockState_ProviderThrows_IsDroppedWithoutBreakingTheDigest()
    {
        var collector = new MetricsCollector();
        collector.RecordError("Alpha", DateTimeOffset.UtcNow);
        var digest = NewService(collector);
        digest.UnlockStateProvider = () => throw new InvalidOperationException("boom");

        var text = digest.ComposeDigest();

        Assert.NotNull(text);
        Assert.Contains("errors", text);
        Assert.DoesNotContain("boom", text);
    }
}
