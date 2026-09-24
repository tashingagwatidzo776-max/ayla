using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;

namespace DongGfx.App.Services;

/// <summary>One user price alert on the Market Watch.</summary>
public partial class PriceAlert : ObservableObject
{
    public PriceAlert(string symbol, string direction, double trigger, bool oneShot = true)
    {
        Symbol = symbol;
        Direction = direction;   // "above" | "below"
        Trigger = trigger;
        OneShot = oneShot;
    }

    public string Symbol { get; }
    public string Direction { get; }
    public double Trigger { get; }
    public bool OneShot { get; }

    [ObservableProperty]
    private bool _fired;

    public override string ToString() => $"{Symbol} {Direction} {Trigger:0.#####}";
}

/// <summary>
/// MT5-style price alerts: the user pins a symbol + direction + trigger;
/// every tick is evaluated and a crossing fires a toast, a journal entry
/// and (when configured) the risk-rail webhook — reusing
/// <see cref="NotificationService"/> exactly like the gate's alerts.
/// One-shot alerts remove themselves after firing; repeating alerts re-arm.
/// </summary>
public sealed class PriceAlertEngine
{
    private readonly NotificationService _notifier;
    private readonly WebhookService? _webhook;
    private readonly TradeJournal? _journal;
    private readonly object _lock = new();

    public PriceAlertEngine(NotificationService notifier, TradeJournal? journal = null, WebhookService? webhook = null)
    {
        _notifier = notifier;
        _journal = journal;
        _webhook = webhook;
    }

    /// <summary>The user's alerts, oldest first. Bound to the UI.</summary>
    public ObservableCollection<PriceAlert> Alerts { get; } = new();

    /// <summary>Adds an alert; duplicate symbol+direction+trigger collapses.</summary>
    public PriceAlert Add(string symbol, string direction, double trigger, bool oneShot = true)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("symbol is required", nameof(symbol));
        }

        var dir = direction?.Trim().ToLowerInvariant() switch
        {
            "above" => "above",
            "below" => "below",
            _ => throw new ArgumentException("direction must be above|below", nameof(direction)),
        };

        lock (_lock)
        {
            var existing = Alerts.FirstOrDefault(a =>
                a.Symbol == symbol && a.Direction == dir && Math.Abs(a.Trigger - trigger) < 1e-9);
            if (existing is not null)
            {
                return existing;
            }

            var alert = new PriceAlert(symbol, dir, trigger, oneShot);
            Alerts.Add(alert);
            return alert;
        }
    }

    public bool Remove(PriceAlert alert)
    {
        lock (_lock)
        {
            return Alerts.Remove(alert);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Alerts.Clear();
        }
    }

    /// <summary>Evaluates every alert for <paramref name="symbol"/> against a
    /// fresh bid. Returns the alerts that fired this tick.</summary>
    public IReadOnlyList<PriceAlert> EvaluateTick(string symbol, double bid)
    {
        if (string.IsNullOrWhiteSpace(symbol) || bid <= 0)
        {
            return Array.Empty<PriceAlert>();
        }

        List<PriceAlert>? fired = null;
        lock (_lock)
        {
            foreach (var alert in Alerts.Where(a => a.Symbol == symbol && !a.Fired))
            {
                var crossed = alert.Direction == "above"
                    ? bid >= alert.Trigger
                    : bid <= alert.Trigger;
                if (!crossed)
                {
                    continue;
                }

                alert.Fired = true;
                (fired ??= new List<PriceAlert>()).Add(alert);

                var message = $"PRICE ALERT · {alert.Symbol} {alert.Direction} {alert.Trigger:0.#####} — last {bid:0.#####}";
                _notifier.SendToast("🔔 Price alert", message, "info");
                _webhook?.PostStatus("🔔 Price alert", message);
                _journal?.Log(Guid.Empty, "price_alert", message);
            }

            // One-shots leave the list; repeating alerts re-arm for the next cross.
            for (var i = Alerts.Count - 1; i >= 0; i--)
            {
                var a = Alerts[i];
                if (a.Fired && a.OneShot)
                {
                    Alerts.RemoveAt(i);
                }
                else if (a.Fired)
                {
                    a.Fired = false;
                }
            }
        }

        return fired ?? (IReadOnlyList<PriceAlert>)Array.Empty<PriceAlert>();
    }
}
