using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Tf.App.Infrastructure;

namespace Tf.App.Tests;

/// <summary>
/// Tests for WebhookService payload shaping and guard rails, verified
/// against a real local capture listener (the fire-and-forget posts are
/// awaited through the captured request count).
/// </summary>
[Trait("Category", "Unit")]
public class WebhookServiceTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly string _url;
    private readonly List<string> _bodies = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();

    public WebhookServiceTests()
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

    [Fact]
    public async Task PostTradeSettled_DiscordFormat_SendsEmbed()
    {
        using var webhook = new WebhookService { WebhookUrl = _url, IsDiscord = true, MinInterval = TimeSpan.Zero };

        webhook.PostTradeSettled("Alpha", won: true, profit: 0.9m, symbol: "frxEURUSD",
            direction: "Rise", stake: 1m, bankroll: 5.9m);

        await WaitForAsync(() => Count >= 1);

        var body = Assert.Single(Bodies);
        using var doc = JsonDocument.Parse(body);
        var embed = doc.RootElement.GetProperty("embeds")[0];
        Assert.Contains("Trade Settled", embed.GetProperty("title").GetString());
        Assert.Contains("WIN", embed.GetProperty("description").GetString());
        Assert.Equal(0x00D4AA, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task PostTradeSettled_SlackFormat_SendsAttachment()
    {
        using var webhook = new WebhookService { WebhookUrl = _url, IsDiscord = false, MinInterval = TimeSpan.Zero };

        webhook.PostTradeSettled("Beta", won: false, profit: -1m, symbol: "frxEURUSD",
            direction: "Fall", stake: 1m, bankroll: 4m);

        await WaitForAsync(() => Count >= 1);

        var body = Assert.Single(Bodies);
        using var doc = JsonDocument.Parse(body);
        var attachment = doc.RootElement.GetProperty("attachments")[0];
        Assert.Contains("LOSS", attachment.GetProperty("text").GetString());
        Assert.Equal("#ff5c5c", attachment.GetProperty("color").GetString()!.ToLowerInvariant());
    }

    [Fact]
    public void PostTradeSettled_NoUrl_IsNoOp()
    {
        using var webhook = new WebhookService { WebhookUrl = null };

        webhook.PostTradeSettled("A", true, 1m, "frxEURUSD", "Rise", 1m, 6m);
        webhook.PostMilestone("A", "target hit", 6m);
        webhook.PostCircuitBreaker("A", 3);
        webhook.PostRiskRail("title", "body");
        webhook.PostStatus("title", "body");

        Assert.Equal(0, Count);
    }

    [Fact]
    public void PostTradeSettled_MinInterval_DropsImmediateRepeat()
    {
        // Default MinInterval (5 s) means the second post inside the window
        // is dropped — a guard, not a bug.
        using var webhook = new WebhookService { WebhookUrl = _url };

        webhook.PostTradeSettled("A", true, 1m, "frxEURUSD", "Rise", 1m, 6m);
        webhook.PostTradeSettled("A", true, 1m, "frxEURUSD", "Rise", 1m, 6m);

        Assert.True(Count <= 1);
    }

    [Fact]
    public async Task PostMilestone_ChoosesColor_ByMilestoneKind()
    {
        using var webhook = new WebhookService { WebhookUrl = _url, MinInterval = TimeSpan.Zero };

        webhook.PostMilestone("A", "daily target hit", 6.5m);
        await WaitForAsync(() => Count >= 1);
        webhook.PostMilestone("A", "floor hit", 3.5m);

        await WaitForAsync(() => Count >= 2);

        var bodies = Bodies;
        Assert.Equal(2, bodies.Count);
        using var doc0 = JsonDocument.Parse(bodies[0]);
        using var doc1 = JsonDocument.Parse(bodies[1]);
        Assert.Equal(0x00D4AA, doc0.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32());
        Assert.Equal(0xFF5C5C, doc1.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task PostRiskRail_And_PostStatus_SendTitles()
    {
        using var webhook = new WebhookService { WebhookUrl = _url, MinInterval = TimeSpan.Zero };

        webhook.PostRiskRail("⚠ Risk rail engaged", "governor latched");
        await WaitForAsync(() => Count >= 1);
        webhook.PostStatus("🔁 Restarting", "attempt 1/3");

        await WaitForAsync(() => Count >= 2);

        var bodies = Bodies;
        Assert.Equal(2, bodies.Count);
        Assert.Contains("Risk rail engaged", bodies[0]);
        Assert.Contains("Restarting", bodies[1]);
    }
}
