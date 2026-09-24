using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Controls;
using DongGfx.Core.Models;

namespace DongGfx.App.ViewModels;

/// <summary>
/// The slimmed dashboard: the app-wide kill switch (which crosses venues —
/// engaging it stops the FX brain and flattens every open MT5 position),
/// the FX brain's status line, and one alert line per latched risk rail.
/// The binary-options panels (growth drill-down, combined growth P&amp;L,
/// Deriv connection/chart feed) went away with that integration; the risk
/// rails and their toast/webhook machinery are deliberately unchanged —
/// they are what stops a bad day.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly Infrastructure.NotificationService? _notifications;
    private readonly Infrastructure.WebhookService? _webhook;

    /// <summary>Top-line status (kill switch state / bridge reachability).</summary>
    [ObservableProperty]
    private string statusText = "Disconnected";

    /// <summary>One line describing the FX brain: mode (PAPER/LIVE/stopped),
    /// symbols and halt state. Kept current by the Terminal via
    /// <see cref="ReportFxStatus"/>.</summary>
    [ObservableProperty]
    private string fxStatusText = "FX brain idle";

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string symbolText = "—";

    [ObservableProperty]
    private string lastPriceText = "—";

    [ObservableProperty]
    private string tickCountText = "0 ticks";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isKillSwitchEngaged;

    /// <summary>True while the FX brain's loss stop / risk halt is latched
    /// (the FX equivalent of the old portfolio governor: the supervisor's
    /// latched daily-loss stop, equity floor or kill-switch cross-venue
    /// halt). Driven by <see cref="ReportFxStatus"/>.</summary>
    [ObservableProperty]
    private bool isGovernorLatched;

    /// <summary>True while the supervisor is close to its loss cap (80% of
    /// the daily-loss budget used) — the pre-trip warning equivalent.</summary>
    [ObservableProperty]
    private bool isGovernorWarned;

    /// <summary>Today's realised FX P/L (fed from the deal feed).</summary>
    [ObservableProperty]
    private string combinedGrowthPnlText = "$0.00";

    /// <summary>Per-risk-rail summary lines for the dashboard card.</summary>
    public ObservableCollection<string> RiskRailAlerts { get; } = new();

    /// <summary>The live chart view (attached by MainWindow; VM stays UI-agnostic).</summary>
    public TickChartControl? Chart { get; set; }

    /// <summary>MT5-style candle chart (attached by MainWindow; fed when
    /// ChartStyle is Candles — ticks aggregate into synthetic 5-second
    /// bars so zoom/pan work on live data).</summary>
    public Controls.CandleChartControl? CandleChart { get; set; }

    /// <summary>Dashboard chart style — MT5 shows candles by default.</summary>
    [ObservableProperty]
    private bool candlesPreferred;

    [RelayCommand]
    private void ToggleChartStyle()
    {
        CandlesPreferred = !CandlesPreferred;
        if (CandlesPreferred && CandleChart is not null)
        {
            // Seed the candle view from the ticks the line chart already
            // holds, so the switch isn't an empty chart.
            CandleChart.SetBars(System.Array.Empty<Core.Fx.FxBar>());
            PushCandles(Chart?.Ticks ?? new System.Collections.Generic.List<Tick>());
        }
    }

    /// <summary>Aggregates ticks into ~5s bars for the candle view
    /// (called on the UI thread after each feed update).</summary>
    private void PushCandles(IReadOnlyList<Tick> ticks)
    {
        if (!CandlesPreferred || CandleChart is null)
        {
            return;
        }

        foreach (var t in ticks)
        {
            var second = t.Epoch / 1000 / 5 * 5;
            if (CandleChart.Bars.Count > 0 && CandleChart.Bars[^1].Time == second)
            {
                var prev = CandleChart.Bars[^1];
                CandleChart.UpdateLast(prev with
                {
                    High = Math.Max(prev.High, t.Quote),
                    Low = Math.Min(prev.Low, t.Quote),
                    Close = t.Quote,
                });
            }
            else
            {
                CandleChart.UpsertBar(new Core.Fx.FxBar(second, t.Quote, t.Quote, t.Quote, t.Quote, 0));
            }
        }
    }

    public DashboardViewModel(
        Infrastructure.NotificationService? notifications = null,
        Infrastructure.WebhookService? webhook = null)
    {
        _notifications = notifications;
        _webhook = webhook;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Refreshes the risk-rail card after any state change (also
    /// fires the toast/webhook for rails that newly engaged).</summary>
    private void RefreshRiskRails()
    {
        var alerts = BuildRiskRailAlerts();
        if (alerts.SequenceEqual(RiskRailAlerts))
        {
            return;
        }

        var previous = RiskRailAlerts.ToList();

        RiskRailAlerts.Clear();
        foreach (var alert in alerts)
        {
            RiskRailAlerts.Add(alert);
        }

        NotifyOnRiskRailChange(previous, alerts);
    }

    /// <summary>Builds one alert line per currently latched risk rail.</summary>
    private List<string> BuildRiskRailAlerts()
    {
        var alerts = new List<string>();

        if (IsGovernorLatched)
        {
            alerts.Add("FX loss stop latched — re-arm on the Terminal tab");
        }
        else if (IsGovernorWarned)
        {
            alerts.Add("FX drawdown warning — 80% of the daily-loss cap used");
        }

        if (IsKillSwitchEngaged)
        {
            alerts.Add("Global kill switch engaged");
        }

        return alerts;
    }

    /// <summary>Fires the toast/webhook alerts for rails that newly engaged
    /// (and one all-clear when the last rail releases).</summary>
    private void NotifyOnRiskRailChange(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var engaged = current.Where(a => !previous.Contains(a)).ToList();
        if (engaged.Count > 0)
        {
            var change = string.Join("; ", engaged);
            var summary = current.Count == 0 ? "no rails latched" : string.Join("; ", current);
            _notifications?.NotifyRiskRailEngaged(change, summary);
            _webhook?.PostRiskRail("⚠ Risk rail engaged", $"{change} — {summary}");
        }
        else if (previous.Count > 0 && current.Count == 0)
        {
            _webhook?.PostRiskRail("✅ Risk rails clear", "all latched risk rails have been released");
        }
    }

    /// <summary>Terminal pushes the FX brain's state here: status line,
    /// realised P/L and the supervisor's halt/warning flags. Any thread.</summary>
    public void ReportFxStatus(
        string status, bool halted, bool warned, decimal? dayPnl = null) =>
        OnUiThread(() =>
        {
            FxStatusText = status;
            IsGovernorLatched = halted;
            IsGovernorWarned = warned && !halted;
            if (dayPnl.HasValue)
            {
                CombinedGrowthPnlText =
                    $"{(dayPnl.Value >= 0 ? "+" : "−")}${Math.Abs(dayPnl.Value):0.00}";
            }

            RefreshRiskRails();
        });

    public void SetSymbol(string symbol) => OnUiThread(() => SymbolText = symbol);

    public void SetBalance(string balance) => OnUiThread(() => BalanceText = balance);

    public void AddHistory(IReadOnlyList<Tick> ticks)
    {
        OnUiThread(() =>
        {
            Chart?.Clear();
            if (ticks.Count > 0)
            {
                Chart?.AddTicks(ticks);
                PushCandles(ticks);
                LastPriceText = ticks[^1].Quote.ToString("0.00000");
                TickCountText = $"{ticks.Count} ticks";
            }
        });
    }

    [RelayCommand]
    private void ToggleKillSwitch()
    {
        IsKillSwitchEngaged = !IsKillSwitchEngaged;
        if (IsKillSwitchEngaged)
        {
            StatusText = "KILL SWITCH ENGAGED — engines stopped, positions flattening";

            // Cross-venue: stop the FX brain and flatten every open MT5
            // position so the kill switch covers the MT5 leg too.
            // (Ioc-guarded: tests construct this VM without the container.)
            try
            {
                _ = Ioc.Default.GetRequiredService<TerminalViewModel>().FxEmergencyFlattenAsync("kill switch");
            }
            catch (InvalidOperationException)
            {
                // no TerminalViewModel registered (tests) — the latch above
                // still refuses every subsequent order path.
            }
        }
        else
        {
            StatusText = "Engines re-armed — press the Terminal's brain switch to resume";
        }

        RefreshRiskRails();
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
