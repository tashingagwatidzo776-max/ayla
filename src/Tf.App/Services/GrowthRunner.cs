using CommunityToolkit.Mvvm.ComponentModel;
using Tf.App.Infrastructure;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.Services;

// BrainRegistry is in Tf.Core.Brain namespace

/// <summary>
/// Drives the deterministic Growth brain on one account via the autonomous
/// scheduler. Bankroll sessions reset daily at <see cref="GrowthPlan.StartBudget"/>
/// and replay that day's settled growth trades, so a restart mid-day resumes
/// exactly where the session left off.
/// </summary>
public sealed partial class GrowthRunner : ObservableObject, IAsyncDisposable
{
    private readonly TradeStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _killSwitch;
    private readonly TradeJournal _journal;
    private readonly PerformanceTracker? _tracker;
    private readonly NotificationService? _notifications;
    private readonly WebhookService? _webhook;

    private AutonomousScheduler? _scheduler;
    private TradingBrain? _brain;
    private GrowthSessionEngine? _engine;
    private GrowthPlan _plan = GrowthPlan.Default;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private string bankrollText = "—";

    [ObservableProperty]
    private string sessionStateText = "not started";

    [ObservableProperty]
    private string lastActivity = "Not started — press Start all.";

    /// <summary>Raised per cycle with a human-readable activity line (any thread).</summary>
    public event Action<string>? Activity;

    public GrowthRunner(AccountConnection connection, TradeStore store,
        Func<AppSettings> settings, Func<bool> killSwitch, TradeJournal journal,
        PerformanceTracker? tracker = null, NotificationService? notifications = null,
        WebhookService? webhook = null)
    {
        Connection = connection;
        _store = store;
        _settings = settings;
        _killSwitch = killSwitch;
        _journal = journal;
        _tracker = tracker;
        _notifications = notifications;
        _webhook = webhook;
    }

    public AccountConnection Connection { get; }
    public GrowthSessionEngine? Engine => _engine;

    public Task StartAsync(GrowthPlan plan)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (!Connection.IsConnected)
        {
            LastActivity = "Account not connected — Connect it first.";
            return Task.CompletedTask;
        }

        if (Connection.IsPaused)
        {
            LastActivity = "Account is paused — resume it first.";
            return Task.CompletedTask;
        }

        _plan = plan;
        _engine = BuildEngine(plan);

        _journal.LogGrowthState(Connection.Config.Id, "started", _engine.Bankroll, 0, $"target {_engine.Target:0.##}");

        var decide = BuildDecision();
        var settings = BuildSettingsFunc(plan);
        var risk = BuildRiskContext;
        var lessons = () => MarketContextBuilder.LessonsFrom(
            _store.ForAccount(Connection.Config.Id, TradeSource.Growth), 3);

        _brain = new TradingBrain(decide, settings, new TradingBrain.DerivAbstraction
        {
            GetProposal = (symbol, direction, stake, currency, duration, ct) =>
                Connection.Client.GetProposalAsync(symbol, direction, stake, currency, duration, ct),
            Buy = (id, price, ct) => Connection.Client.BuyAsync(id, price, ct),
            WaitForSettlement = (id, timeout, ct) => Connection.Client.WaitForSettlementAsync(id, timeout, ct)
        });

        _scheduler = new AutonomousScheduler(_brain, settings,
            () => Connection.Ticks, risk, lessons, OnCycle);

        IsRunning = true;
        RaiseState();
        SessionStateText = "starting…";
        LastActivity = $"Session {Connection.DisplayName}: bankroll ${_engine.Bankroll:0.##} → target ${_engine.Target:0.##}";
        _ = _scheduler.StartAsync();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _scheduler?.Stop();
        _scheduler = null;
        _brain = null;
        IsRunning = false;
        _journal.LogGrowthState(Connection.Config.Id, "stopped", _engine?.Bankroll ?? 0, _engine?.LossStreak ?? 0);
        LastActivity = "Stopped.";
        SessionStateText = IsRunning ? "running" : "stopped";
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private GrowthSessionEngine BuildEngine(GrowthPlan plan)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var engine = new GrowthSessionEngine(plan, plan.StartBudget);

        // Replay today's settled growth trades in order so a mid-day start or
        // app restart resumes the exact bankroll, loss streak, and limits.
        foreach (var trade in _store.ForAccount(Connection.Config.Id, TradeSource.Growth)
                     .Where(t => t.SettledAt.Date == today))
        {
            engine.ApplySettlement(trade.IsWin, trade.Profit);
        }

        return engine;
    }

    /// <summary>Build decision function based on selected brain type.</summary>
    private TradingBrain.Decide BuildDecision()
    {
        var brainKey = Connection.Config.BrainKey;
        var registry = new BrainRegistry();
        var brainProvider = registry.CreateBrain(brainKey, () => Connection.Ticks, _engine);

        return async (_, _, _, _) =>
        {
            if (brainProvider != null)
            {
                var result = await brainProvider.DecideAsync(
                    Connection.Ticks,
                    _engine,
                    _settings,
                    BuildRiskContext,
                    () => MarketContextBuilder.LessonsFrom(
                        _store.ForAccount(Connection.Config.Id, TradeSource.Growth), 3));
                return result;
            }

            // Fallback to GrowthBrain
            var decision = GrowthBrain.Decide(Connection.Ticks, _engine!);
            return new BrainDecision(decision, $"growth-rules: {decision.Reasoning}");
        };
    }

    private Func<AppSettings> BuildSettingsFunc(GrowthPlan plan) => () =>
    {
        var baseSettings = _settings();
        var account = Connection.Config;
        return new AppSettings
        {
            AppId = baseSettings.AppId,
            IsDemo = account.IsDemo,
            Symbol = account.Symbol,
            Currency = account.Currency,
            DurationMinutes = account.DurationMinutes,
            AutonomyEnabled = baseSettings.AutonomyEnabled,
            DecisionIntervalMinutes = plan.IntervalMinutes,
            CooldownMinutesAfterLoss = plan.CooldownMinutesAfterLoss,
            MaxStake = baseSettings.MaxStake,
            MaxConcurrentContracts = 1,
            DailyLossCap = baseSettings.DailyLossCap,
            MinConfidence = baseSettings.MinConfidence
        };
    };

    private RiskContext BuildRiskContext()
    {
        var now = DateTimeOffset.UtcNow;
        var todayGrowth = _store.ForAccount(Connection.Config.Id, TradeSource.Growth)
            .Where(t => t.SettledAt.Date == now.Date)
            .ToArray();
        var last = todayGrowth.Length > 0 ? todayGrowth[^1] : null;
        var net = todayGrowth.Sum(t => t.Profit);

        return new RiskContext(
            KillSwitchEngaged: _killSwitch(),
            OpenContracts: 0,
            Balance: Connection.Balance.Balance,
            DailyNetProfit: net,
            TradesToday: todayGrowth.Length,
            LastTradeAt: last?.SettledAt,
            LastTradeOutcome: last?.Outcome);
    }

    private void OnCycle(BrainCycleResult result)
    {
        // Skip recording if the account is paused (scheduler still runs to stay warm).
        if (Connection.IsPaused)
        {
            LastActivity = $"{DateTime.Now:HH:mm:ss} Paused — skipping cycle";
            RaiseState();
            return;
        }

        // Skip if outside market hours (when enabled).
        var settings = _settings();
        if (settings.RespectMarketHours)
        {
            var marketHours = new MarketHours();
            var now = DateTimeOffset.UtcNow;
            if (!marketHours.IsOpen(now))
            {
                var nextOpen = marketHours.TimeUntilNextOpen(now);
                LastActivity = $"{DateTime.Now:HH:mm:ss} Market closed ({marketHours.GetActiveSessions(now)}) — opens in {nextOpen.TotalHours:0.#}h";
                RaiseState();
                return;
            }

            if (settings.OverlapsOnly && !marketHours.IsOverlap(now))
            {
                LastActivity = $"{DateTime.Now:HH:mm:ss} Waiting for overlap window ({marketHours.GetActiveSessions(now)})";
                RaiseState();
                return;
            }
        }

        var line = Describe(result);
        LastActivity = line;
        Activity?.Invoke(line);
        RaiseState();

        // Log the brain decision
        var decision = result.Decision;
        _journal.LogBrainDecision(
            Connection.Config.Id,
            "Growth",
            "frxEURUSD",
            decision.Direction.ToString(),
            decision.Stake,
            decision.Confidence,
            decision.Reasoning,
            "growth-rules");

        // Log settlement if trade executed
        if (result.ExecutedTrade is { } trade)
        {
            _journal.LogTradeSettlement(
                Connection.Config.Id,
                trade.ContractId,
                trade.IsWin,
                trade.Stake + trade.Profit,  // approximate payout
                trade.Profit,
                _engine?.Bankroll ?? 0);
        }
    }

    private string Describe(BrainCycleResult result)
    {
        var decision = result.Decision;
        var engine = _engine;
        var verdict = result.Verdict.Allowed ? "PASSED risk" : $"blocked: {result.Verdict.Reason}";

        if (result.ExecutedTrade is { } trade)
        {
            var tagged = trade with
            {
                AccountId = Connection.Config.Id,
                AccountName = Connection.DisplayName,
                Source = TradeSource.Growth
            };
            _store.Add(tagged);
            _tracker?.RecordTrade(tagged);
            _tracker?.Save();
            engine?.ApplySettlement(trade.IsWin, trade.Profit);

            // Fire notifications for trade settlements.
            _notifications?.NotifyTradeSettled(Connection.DisplayName, trade.IsWin, trade.Profit, trade.Symbol);
            _webhook?.PostTradeSettled(Connection.DisplayName, trade.IsWin, trade.Profit,
                trade.Symbol, trade.Direction.ToString(), trade.Stake, engine?.Bankroll ?? 0);

            var pnl = trade.Profit >= 0 ? $"+{trade.Profit:0.##}" : $"{trade.Profit:0.##}";
            return $"{DateTime.Now:HH:mm:ss} {trade.Direction} {decision.Stake:0.##} → {trade.Outcome} " +
                   $"({pnl}) · bankroll ${engine?.Bankroll ?? 0:0.##} · {verdict}";
        }

        if (decision.Direction == BrainDirection.Hold)
        {
            return $"{DateTime.Now:HH:mm:ss} HOLD — {decision.Reasoning} · bankroll ${engine?.Bankroll ?? 0:0.##}";
        }

        return $"{DateTime.Now:HH:mm:ss} {decision.Direction} stake {decision.Stake:0.##} " +
               $"(conf {decision.Confidence:P0}) · {verdict} · bankroll ${engine?.Bankroll ?? 0:0.##}";
    }

    /// <summary>Refreshes observable row fields (may run on a background thread).</summary>
    private void RaiseState()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        var start = engine.StartBankroll;
        var growthPct = start <= 0 ? 0 : (engine.Bankroll - start) / start * 100m;
        BankrollText = $"${engine.Bankroll:0.##} ({growthPct:+0.0;-0.0;0.0}%)";

        var prevState = SessionStateText;
        SessionStateText = engine.TargetHit ? "target reached 🎯"
            : engine.FloorHit ? "floor hit — stopped"
            : IsRunning ? "running" : "stopped";

        // Fire notifications on state transitions.
        if (prevState != SessionStateText)
        {
            if (engine.TargetHit)
            {
                _notifications?.NotifyTargetHit(Connection.DisplayName, engine.Bankroll);
                _webhook?.PostMilestone(Connection.DisplayName, "Target reached", engine.Bankroll);
            }
            else if (engine.FloorHit)
            {
                _notifications?.NotifyFloorHit(Connection.DisplayName, engine.Bankroll);
                _webhook?.PostMilestone(Connection.DisplayName, "Floor hit — stopped", engine.Bankroll);
            }
        }
    }
}
