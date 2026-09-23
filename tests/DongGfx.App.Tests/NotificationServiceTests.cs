using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The notification gate (disposed/enabled/rate-limit) decides whether a
/// notification fires; the seven message paths produce the right title,
/// body, and severity. Toast dispatch is captured via the SendToast seam —
/// no PowerShell process is ever spawned in tests.
/// </summary>
[Trait("Category", "Unit")]
public class NotificationServiceTests : IDisposable
{
    private readonly CapturingNotifier _svc = new();

    private sealed class CapturingNotifier : NotificationService
    {
        public List<(string Title, string Body, string Severity)> Sent { get; } = new();
        public bool Capture = true;

        public CapturingNotifier()
        {
            MinInterval = TimeSpan.Zero;   // content tests send several in a row
        }

        internal override void SendToast(string title, string body, string severity)
        {
            if (Capture) { Sent.Add((title, body, severity)); return; }
            base.SendToast(title, body, severity);
        }
    }

    public void Dispose() => _svc.Dispose();

    [Fact]
    public void TradeSettled_Win_And_Loss_Bodies()
    {
        _svc.NotifyTradeSettled("Deriv-Demo", won: true, profit: 12.5m, symbol: "XAUUSDmicro");
        _svc.NotifyTradeSettled("Deriv-Demo", won: false, profit: -8.2m, symbol: "EURUSD");

        Assert.Equal(2, _svc.Sent.Count);
        Assert.Contains("✅", _svc.Sent[0].Title);
        Assert.Contains("Deriv-Demo", _svc.Sent[0].Title);
        Assert.Contains("XAUUSDmicro", _svc.Sent[0].Body);
        Assert.Contains("WIN", _svc.Sent[0].Body);
        Assert.Equal("success", _svc.Sent[0].Severity);
        Assert.Contains("❌", _svc.Sent[1].Title);
        Assert.Contains("LOSS", _svc.Sent[1].Body);
        Assert.Equal("warning", _svc.Sent[1].Severity);
    }

    [Fact]
    public void TargetHit_FloorHit_CircuitBreaker_ConnectionError_RiskRail_Update()
    {
        _svc.NotifyTargetHit("acct", 1234.5m);
        _svc.NotifyFloorHit("acct", 900m);
        _svc.NotifyCircuitBreakerTripped("acct", 3);
        _svc.NotifyConnectionError("acct", "bridge unreachable");
        _svc.NotifyRiskRailEngaged("kill switch", "manual halt");
        _svc.NotifyUpdateAvailable("v0.7.1");

        Assert.Equal(6, _svc.Sent.Count);
        Assert.Contains("Target Reached", _svc.Sent[0].Title);
        Assert.Contains("$1234.5", _svc.Sent[0].Body);
        Assert.Equal("success", _svc.Sent[0].Severity);

        Assert.Contains("Floor Hit", _svc.Sent[1].Title);
        Assert.Equal("error", _svc.Sent[1].Severity);

        Assert.Contains("Circuit Breaker", _svc.Sent[2].Title);
        Assert.Contains("3 consecutive failures", _svc.Sent[2].Body);
        Assert.Equal("warning", _svc.Sent[2].Severity);

        Assert.Contains("Connection Lost", _svc.Sent[3].Title);
        Assert.Contains("bridge unreachable", _svc.Sent[3].Body);

        Assert.Contains("Risk rail engaged", _svc.Sent[4].Title);
        Assert.Contains("kill switch — manual halt", _svc.Sent[4].Body);

        Assert.Contains("Update Available", _svc.Sent[5].Title);
        Assert.Contains("v0.7.1", _svc.Sent[5].Body);
        Assert.Equal("info", _svc.Sent[5].Severity);
    }

    [Fact]
    public void MinInterval_RateLimits_Second_Message()
    {
        _svc.MinInterval = TimeSpan.FromMinutes(5);
        _svc.NotifyUpdateAvailable("1");
        _svc.NotifyUpdateAvailable("2");   // inside the window → dropped

        var msg = Assert.Single(_svc.Sent);
        Assert.Contains("1", msg.Body);
    }

    [Fact]
    public void ZeroInterval_Lets_Everything_Through()
    {
        _svc.MinInterval = TimeSpan.Zero;
        _svc.NotifyUpdateAvailable("1");
        _svc.NotifyUpdateAvailable("2");
        Assert.Equal(2, _svc.Sent.Count);
    }

    [Fact]
    public void Disabled_Kills_Every_Path()
    {
        _svc.Enabled = false;
        _svc.MinInterval = TimeSpan.Zero;
        _svc.NotifyUpdateAvailable("1");
        _svc.NotifyFloorHit("a", 1m);
        Assert.Empty(_svc.Sent);
    }

    [Fact]
    public void Disposed_Kills_Every_Path()
    {
        _svc.MinInterval = TimeSpan.Zero;
        _svc.Dispose();
        _svc.NotifyUpdateAvailable("1");
        _svc.NotifyTargetHit("a", 1m);
        Assert.Empty(_svc.Sent);
    }

    [Fact]
    public void CaptureOff_Falls_Back_To_Base_No_Process_Escapes()
    {
        // Base SendToast spawns a hidden powershell.exe with a 5s kill;
        // it is fire-and-forget and must never throw from the caller.
        _svc.Capture = false;
        var ex = Record.Exception(() => _svc.NotifyUpdateAvailable("smoke"));
        Assert.Null(ex);
    }
}
