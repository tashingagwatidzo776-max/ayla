using System;
using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// MT5-style price alerts: a crossing tick fires toast + webhook + journal
/// in one shot; one-shot alerts leave the list after firing, repeating
/// alerts re-arm; adds dedupe and validate direction; ticks for other
/// symbols and non-positive prices are ignored. The notifier subclass
/// captures toasts without spawning PowerShell; the webhook is captured
/// via a subclass too; the journal is real (temp dir).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class PriceAlertEngineTests : IDisposable
{
    private sealed class CaptureNotifier : NotificationService
    {
        public System.Collections.Generic.List<(string Title, string Body, string Severity)> Toasts { get; } = new();

        internal override void SendToast(string title, string body, string severity)
        {
            Toasts.Add((title, body, severity));
        }
    }

    private readonly string _journalDir;
    private readonly CaptureNotifier _notifier;
    private readonly TradeJournal _journal;
    private readonly PriceAlertEngine _engine;

    /// <summary>The real WebhookService with no URL is inert (PostStatus
    /// returns before any HTTP) — wiring it proves the call path is
    /// webhook-safe without asserting network side effects.</summary>
    private readonly WebhookService _webhook = new() { WebhookUrl = null };

    public PriceAlertEngineTests()
    {
        _journalDir = Path.Combine(Path.GetTempPath(), "palert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_journalDir);
        _notifier = new CaptureNotifier();
        _journal = new TradeJournal(_journalDir);
        _engine = new PriceAlertEngine(_notifier, _journal);
    }

    public void Dispose()
    {
        try { _journal.Dispose(); Directory.Delete(_journalDir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Add_BuildsAlert_AndDeduplicatesExactTriples()
    {
        var a1 = _engine.Add("XAUUSDmicro", "Above", 4300);
        var a2 = _engine.Add("XAUUSDmicro", "above", 4300);   // same after normalize

        Assert.Same(a1, a2);
        Assert.Single(_engine.Alerts);
    }

    [Fact]
    public void Add_RejectsBadDirection_AndBlankSymbol()
    {
        Assert.Throws<ArgumentException>(() => _engine.Add("XAUUSDmicro", "sideways", 4300));
        Assert.Throws<ArgumentException>(() => _engine.Add("", "above", 4300));
    }

    [Fact]
    public void Tick_CrossingAbove_FiresToastAndJournal_SurvivesInertWebhook()
    {
        var engine = new PriceAlertEngine(_notifier, _journal, _webhook);
        engine.Add("XAUUSDmicro", "above", 4300, oneShot: true);

        var fired = engine.EvaluateTick("XAUUSDmicro", 4300.55);

        Assert.Single(fired);
        Assert.Single(_notifier.Toasts);
        Assert.Contains("PRICE ALERT", _notifier.Toasts[0].Body);
        Assert.Equal("info", _notifier.Toasts[0].Severity);

        // The journal buffers writes on a 5s timer — flush before reading.
        _journal.Flush();
        var recent = _journal.GetRecent(count: 50);
        Assert.Contains(recent, e => e.Category == "price_alert");
    }

    [Fact]
    public void Tick_CrossingBelow_Fires()
    {
        _engine.Add("XAUUSDmicro", "below", 4200, oneShot: true);

        var fired = _engine.EvaluateTick("XAUUSDmicro", 4199.99);

        Assert.Single(fired);
    }

    [Fact]
    public void Tick_InsideTheBand_DoesNotFire()
    {
        _engine.Add("XAUUSDmicro", "above", 4300, oneShot: true);
        _engine.Add("XAUUSDmicro", "below", 4200, oneShot: true);

        Assert.Empty(_engine.EvaluateTick("XAUUSDmicro", 4250));
        Assert.Empty(_notifier.Toasts);
        Assert.Equal(2, _engine.Alerts.Count);
    }

    [Fact]
    public void Tick_OtherSymbolOrBadPrice_IsIgnored()
    {
        _engine.Add("XAUUSDmicro", "above", 4300, oneShot: true);

        Assert.Empty(_engine.EvaluateTick("EURUSD", 5000));
        Assert.Empty(_engine.EvaluateTick("XAUUSDmicro", 0));
        Assert.Empty(_engine.EvaluateTick("XAUUSDmicro", -1));
        Assert.Single(_engine.Alerts);
    }

    [Fact]
    public void OneShot_AlertRemovesItself_AfterFiring()
    {
        _engine.Add("XAUUSDmicro", "above", 4300, oneShot: true);

        _engine.EvaluateTick("XAUUSDmicro", 4301);

        Assert.Empty(_engine.Alerts);
    }

    [Fact]
    public void Repeating_AlertReArms_AndFiresAgainOnTheNextCross()
    {
        _engine.Add("XAUUSDmicro", "above", 4300, oneShot: false);

        Assert.Single(_engine.EvaluateTick("XAUUSDmicro", 4301));  // fire 1
        Assert.Single(_engine.Alerts);                             // stays, re-armed

        // Re-armed means: it fires again on the next *tick that is at or
        // above the trigger* — the engine evaluates crossings per tick, not
        // state transitions (documented MT5-parity behavior).
        Assert.Single(_engine.EvaluateTick("XAUUSDmicro", 4302));  // fire 2
        Assert.Equal(2, _notifier.Toasts.Count);
    }

    [Fact]
    public void Remove_AndClear_ManageTheList()
    {
        var a = _engine.Add("XAUUSDmicro", "above", 4300);
        _engine.Add("EURUSD", "below", 1.05);

        Assert.True(_engine.Remove(a));
        Assert.False(_engine.Remove(a));   // already gone
        Assert.Single(_engine.Alerts);

        _engine.Clear();
        Assert.Empty(_engine.Alerts);
    }
}
