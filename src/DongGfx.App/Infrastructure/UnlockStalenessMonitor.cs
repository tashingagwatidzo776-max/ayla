using System;
using System.Threading;
using DongGfx.App.Services;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Watches the shared real-money session unlock and raises the staleness
/// alert when it stays armed past <see cref="AppSettings.ArmStalenessHours"/>:
/// a journal entry (<c>REAL_MONEY_UNLOCK_STALE</c>, rendered as the ⏰ line
/// in the Journal tab), a risk-rail toast, and a webhook post — repeating
/// every full threshold while the unlock stays armed. 0 disables the alert.
/// </summary>
/// <remarks>
/// This watch used to live in the multi-account hub (removed with the
/// binary-options integration); the unlock it guards did not — it is what
/// allows real MT5 orders, so the alert rail is restored here against the
/// one shared gate every remaining trade path evaluates. Timer callback
/// marshals its own errors: a throwing rail must never kill the loop.
/// </remarks>
public sealed class UnlockStalenessMonitor : IDisposable
{
    private readonly ManualRealMoneyGate _gate;
    private readonly TradeJournal _journal;
    private readonly NotificationService _notifications;
    private readonly WebhookService _webhook;
    private readonly Func<AppSettings> _settings;
    private System.Threading.Timer? _timer;

    /// <summary>How many full thresholds have already alerted since the
    /// unlock was armed — repeat clicks cannot reset it, only a Reset.</summary>
    private int _warnedThresholds;

    public UnlockStalenessMonitor(
        ManualRealMoneyGate gate,
        TradeJournal journal,
        NotificationService notifications,
        WebhookService webhook,
        Func<AppSettings> settings)
    {
        _gate = gate;
        _journal = journal;
        _notifications = notifications;
        _webhook = webhook;
        _settings = settings;
    }

    /// <summary>Start the watch (first pass after 1 min, then every minute).
    /// Headless: safe to call from tests without a message pump.</summary>
    public void Start() =>
        _timer = new System.Threading.Timer(
            _ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    /// <summary>One evaluation. Public for tests: it never touches the UI
    /// thread directly (toast/webhook/journal all marshal their own work).</summary>
    public void Tick()
    {
        try
        {
            var thresholdHours = _settings().ArmStalenessHours;
            if (thresholdHours <= 0 || !_gate.IsUnlocked || _gate.ArmedAt is not { } armedAt)
            {
                _warnedThresholds = 0;
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - armedAt;
            var threshold = TimeSpan.FromHours(thresholdHours);
            if (elapsed < threshold)
            {
                return;
            }

            var index = (int)(elapsed / threshold);   // 1, 2, 3 … per full threshold
            if (index <= _warnedThresholds)
            {
                return;                               // already reported this threshold
            }

            _warnedThresholds = index;
            var hours = elapsed.TotalHours;
            var line = $"unlock armed {hours:0.#}h ago (threshold {thresholdHours}h) — " +
                       "reset it unless real trading is still intended";

            // Details IS the line (the journal's plain-message path).
            _journal.Log(Guid.Empty, "REAL_MONEY_UNLOCK_STALE", line);
            _notifications.NotifyRiskRailEngaged("Real-money unlock stale", line);
            _webhook.PostRiskRail("Real-money unlock stale", line);
        }
        catch
        {
            // Never let a rail's own failure kill the watch loop.
        }
    }

    public void Dispose() => _timer?.Dispose();
}
