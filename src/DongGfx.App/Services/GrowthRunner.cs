using CommunityToolkit.Mvvm.ComponentModel;
using DongGfx.App.Infrastructure;
using DongGfx.Core;
using DongGfx.Core.Analytics;
using DongGfx.Core.Brain;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Services;

// BrainRegistry is in DongGfx.Core.Brain namespace

/// <summary>
/// One growth runner's display state, taken for the dashboard's growth pill.
/// Bankroll fields are null while no session engine exists (runner never
/// started) — consumers fall back to the activity line then.
/// </summary>
public sealed record GrowthRunnerSnapshot(
    string Name,
    bool IsRunning,
    string Activity,
    decimal? Bankroll,
    decimal? StartBankroll,
    decimal? Target);

/// <summary>Why a growth scheduler self-exited.</summary>
public enum GrowthExitReason
{
    /// <summary>The master kill switch was engaged.</summary>
    KillSwitchEngaged,

    /// <summary>Too many consecutive failed cycles (broker or LLM errors).</summary>
    RepeatedFailures,

    /// <summary>The real-money gate refused to continue: the account state
    /// (config flag, API-verified type, session unlock) no longer allows
    /// trading. The hub must drop the runner without any restart ladder —
    /// the next start re-evaluates the gate from scratch.</summary>
    RealMoneyGate
}

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
    private readonly Func<bool> _realMoneyUnlocked;
    private readonly TradeJournal _journal;
    private readonly PerformanceTracker? _tracker;
    private readonly NotificationService? _notifications;
    private readonly WebhookService? _webhook;
    private readonly TimeProvider _timeProvider;

    private AutonomousScheduler? _scheduler;

    /// <summary>Lazy no-auth public market-data feed backing the
    /// market-closed idle probe — created on first idle, never carries a
    /// token, so it keeps working through access-token expiry and re-auth
    /// churn on the trading connection.</summary>
    private PublicMarketDataClient? _publicFeed;

    /// <summary>Test seam: replaces the public-feed open probe so    /// market-open wake behavior runs without a network.</summary>
    internal Func<string, CancellationToken, Task<bool>>? PublicFeedProbeForTests { get; set; }

    /// <summary>The scheduler's market-open probe: asks the no-auth public
    /// feed whether the account's symbol is trading again. Any feed error
    /// means "not confirmed open" (false) — the API-quoted reopen time
    /// remains the fallback, and the probe can only accelerate, never
    /// delay, the resume.</summary>
    private async Task<bool> ProbePublicFeedOpenAsync(CancellationToken ct)
    {
        if (PublicFeedProbeForTests is not null)
        {
            return await PublicFeedProbeForTests(Connection.Config.Symbol, ct);
        }

        try
        {
            _publicFeed ??= new PublicMarketDataClient();
            return await _publicFeed.IsSymbolOpenAsync(Connection.Config.Symbol, ct);
        }
        catch
        {
            return false;
        }
    }
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

    /// <summary>Test seam: forces the runner's visible running state and
    /// activity line without a live session (mirrors the hub's TestRaise* pattern).</summary>
    internal void TestSetRunningState(bool running, string? activity = null)
    {
        IsRunning = running;
        if (activity is not null)
        {
            LastActivity = activity;
        }
    }

    /// <summary>Test seam: attaches a real engine so status composition can be
    /// exercised headlessly — <see cref="GrowthSessionEngine"/> is public and pure,
    /// so tests drive real bankroll/target math without a live session.</summary>
    internal void TestAttachEngine(GrowthSessionEngine engine) => _engine = engine;

    /// <summary>Test seam: builds the per-cycle risk context (including the
    /// real-money verdict) without a live scheduler.</summary>
    internal RiskContext TestBuildRiskContext() => BuildRiskContext();

    /// <summary>Display state for dashboard composition (safe from any thread;
    /// engine stats are null until the session starts).</summary>
    public GrowthRunnerSnapshot Snapshot => new(
        Connection.DisplayName,
        IsRunning,
        LastActivity,
        _engine?.Bankroll,
        _engine?.StartBankroll,
        _engine?.Target);

    /// <summary>Raised per cycle with a human-readable activity line (any thread).</summary>
    public event Action<string>? Activity;

    /// <summary>Raised after the scheduler self-exits (kill switch or repeated failures).</summary>
    public event Action<GrowthRunner, GrowthExitReason>? Exited;

    /// <summary>
    /// Raised after a settled growth trade is persisted to the store (any
    /// thread) — the hub's portfolio governor listens for its combined-net
    /// check on every settlement.
    /// </summary>
    public event Action<GrowthRunner, Trade>? Settled;

    public GrowthRunner(AccountConnection connection, TradeStore store,
        Func<AppSettings> settings, Func<bool> killSwitch, TradeJournal journal,
        PerformanceTracker? tracker = null, NotificationService? notifications = null,
        WebhookService? webhook = null, TimeProvider? timeProvider = null,
        MetricsCollector? metrics = null,
        Func<bool>? realMoneyUnlocked = null)
    {
        Connection = connection;
        _store = store;
        _settings = settings;
        _killSwitch = killSwitch;
        // The per-cycle real-money gate needs the session unlock state; tests
        // without a hub default to locked (fail closed).
        _realMoneyUnlocked = realMoneyUnlocked ?? (() => false);
        _journal = journal;
        _tracker = tracker;
        _notifications = notifications;
        _webhook = webhook;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Metrics = metrics ?? new MetricsCollector();
    }

    public AccountConnection Connection { get; }
    public GrowthSessionEngine? Engine => _engine;

    /// <summary>Per-runner operational telemetry (cycle latencies, errors).
    /// Aggregated across runners by the hub for the metrics export.</summary>
    public MetricsCollector Metrics { get; }

    public Task StartAsync(GrowthPlan plan)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        // Structural real-money gate on the runner itself: every start path —
        // hub-owned (StartGrowthCore already gated, this is defence in depth)
        // and externally started (ObserveRunner, which bypasses the hub's
        // start gate entirely) — re-evaluates the gate before anything else.
        // Fail closed: without an unlock accessor the default is locked.
        var gate = RealMoneyGate.Evaluate(
            Connection.Config.IsDemo,
            Connection.ApiVerifiedVirtual,
            _realMoneyUnlocked());
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            var explanation = RealMoneyGate.Explain(gate);
            _journal.LogGrowthState(Connection.Config.Id, "real-money-gate", 0, 0,
                $"runner start refused ({gate}): {explanation}");
            LastActivity = $"{DateTime.Now:HH:mm:ss} {explanation}";
            SessionStateText = "stopped — real-money gate";
            _webhook?.PostRiskRail("🛑 Real-money gate refused an engine start",
                $"{Connection.DisplayName}: {explanation}");
            _notifications?.NotifyRiskRailEngaged("Real-money gate",
                $"{Connection.DisplayName} — start refused");
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
            () => Connection.Ticks, risk, lessons, OnCycle,
            TimeSpan.FromSeconds(plan.FailureBackoffSeconds), timeProvider: _timeProvider,
            onCycleLatencyMs: ms => Metrics.RecordLatency(ms, Connection.DisplayName, _timeProvider.GetUtcNow()),
            onCycleError: exType => Metrics.RecordError(Connection.DisplayName, _timeProvider.GetUtcNow()),
            marketClosedProbe: ex => MarketClosedNotice.ParseReopenUtc(ex.Message, _timeProvider.GetUtcNow()),
            marketOpenProbe: ProbePublicFeedOpenAsync,
            onMarketClosed: reopen =>
            {
                // Expected weekend/holiday state, not a failure: show it as
                // such and keep the engine armed for the automatic resume.
                SessionStateText = $"market closed — resumes {reopen.ToLocalTime():HH:mm}";
                LastActivity = $"{DateTime.Now:HH:mm:ss} Market closed — engine idles until {reopen.ToLocalTime():HH:mm}, then resumes automatically";
                RaiseState();
            });

        IsRunning = true;
        RaiseState();
        SessionStateText = "starting…";
        LastActivity = $"Session {Connection.DisplayName}: bankroll ${_engine.Bankroll:0.##} → target ${_engine.Target:0.##}";

        var scheduler = _scheduler;
        _ = Task.Run(async () =>
        {
            try
            {
                await scheduler.StartAsync();
            }
            finally
            {
                // The loop self-exits when the kill switch engages or after too
                // many consecutive failed cycles (Stop() nulls _scheduler
                // first, so this only fires on self-exit). IsRunning must not
                // stay true while the engine is dead, or StartAsync would be a
                // no-op after the condition clears.
                if (ReferenceEquals(_scheduler, scheduler) && IsRunning)
                {
                    var reason = _killSwitch()
                        ? GrowthExitReason.KillSwitchEngaged
                        : GrowthExitReason.RepeatedFailures;

                    IsRunning = false;
                    SessionStateText = "stopped";
                    LastActivity = reason == GrowthExitReason.KillSwitchEngaged
                        ? $"{DateTime.Now:HH:mm:ss} Kill switch engaged — engine stopped"
                        : $"{DateTime.Now:HH:mm:ss} Scheduler stopped after {scheduler.ConsecutiveFailures} " +
                          $"consecutive failed cycles — engine stopped";
                    _journal.LogGrowthState(Connection.Config.Id, "stopped",
                        _engine?.Bankroll ?? 0, _engine?.LossStreak ?? 0,
                        reason == GrowthExitReason.KillSwitchEngaged
                            ? "kill switch engaged"
                            : $"repeated failures ({scheduler.ConsecutiveFailures})");
                    Exited?.Invoke(this, reason);
                }
            }
        });
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

    /// <summary>Build decision function based on selected brain type. The
    /// Ensemble key builds a weighted-voting ensemble from the account's
    /// EnsembleConfig text; empty config means every known rules brain votes
    /// with equal weight. Other keys map straight through the registry.</summary>
    private TradingBrain.Decide BuildDecision()
    {
        var brainKey = Connection.Config.BrainKey;
        var registry = new BrainRegistry();
        var brainProvider = BuildProvider(registry, brainKey);

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

    /// <summary>Resolves a brain key to a provider, expanding Ensemble into a
    /// weighted ensemble of rules brains from the account's config text.
    /// Internal static for tests. Returns null when the key is unknown (the
    /// caller falls back to the Growth rules brain).</summary>
    internal static IBrainDecisionProvider? BuildProvider(BrainRegistry registry, string brainKey, string? ensembleConfig = null)
    {
        if (!string.Equals(brainKey, "Ensemble", StringComparison.OrdinalIgnoreCase))
        {
            return registry.CreateBrain(brainKey);
        }

        var entries = EnsemblePlanParser.Parse(ensembleConfig);
        if (entries.Count == 0)
        {
            // Unconfigured ensemble: every known rules brain votes equally.
            entries = EnsemblePlanParser.KnownBrainKeys.Select(k => (k, 1.0)).ToArray();
        }

        var ensemble = new EnsembleBrainWrapper();
        foreach (var (key, weight) in entries)
        {
            if (registry.CreateBrain(key) is { } voter)
            {
                ensemble.AddBrain(voter, weight);
            }
        }

        return ensemble;
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
            LastTradeOutcome: last?.Outcome,
            // Every cycle re-checks the real-money gate against the live
            // API-verified account type — the engine must stop trading the
            // moment the account state no longer allows real trading.
            RealMoney: RealMoneyGate.Evaluate(
                Connection.Config.IsDemo,
                Connection.ApiVerifiedVirtual,
                _realMoneyUnlocked()));
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
                var holiday = MarketHours.IsHoliday(now.Date) ? $" (holiday — next: {MarketHours.GetNextHoliday(now.Date.AddDays(1)) ?? "none"})" : "";
                LastActivity = $"{DateTime.Now:HH:mm:ss} Market closed{holiday} — opens in {nextOpen.TotalHours:0.#}h";
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
        if (IsRunning)
        {
            LastActivity = line;
        }
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

            // The trade is now in the store — safe for portfolio-level checks.
            Settled?.Invoke(this, tagged);

            // Per-settlement real-money re-check: the account state (config
            // flag, API-verified type, session unlock) is evaluated against
            // the gate AFTER every settlement. A runner that started legally
            // — or whose account was swapped/reconfigured underneath it —
            // must not place another trade once the gate would refuse.
            if (EnforceRealMoneyGateAfterSettlement() is { } refusal)
            {
                return refusal;
            }

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

    /// <summary>Per-settlement real-money re-check. Returns the refusal
    /// activity line when the gate now refuses (the engine was stopped, the
    /// hub notified via <see cref="Exited"/>), or null when trading may
    /// continue. Internal so tests can drive the re-check deterministically
    /// without waiting for a second real trade cycle.</summary>
    internal string? EnforceRealMoneyGateAfterSettlement()
    {
        var gate = RealMoneyGate.Evaluate(
            Connection.Config.IsDemo,
            Connection.ApiVerifiedVirtual,
            _realMoneyUnlocked());
        if (gate is RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed)
        {
            return null; // trading may continue
        }

        var explanation = RealMoneyGate.Explain(gate);
        _journal.LogGrowthState(Connection.Config.Id, "real-money-gate",
            _engine?.Bankroll ?? 0, _engine?.LossStreak ?? 0,
            $"settlement re-check refused ({gate}): {explanation}");
        _webhook?.PostRiskRail("🛑 Real-money gate stopped an engine",
            $"{Connection.DisplayName}: {explanation}");
        _notifications?.NotifyRiskRailEngaged("Real-money gate",
            $"{Connection.DisplayName} — engine stopped");
        Stop();
        LastActivity = $"{DateTime.Now:HH:mm:ss} {explanation}";
        SessionStateText = "stopped — real-money gate";
        Activity?.Invoke(LastActivity);
        // Tell the hub the session ended for good (Stop() nulled the
        // scheduler, so the self-exit path will not fire again): the hub
        // drops the runner so the next start re-evaluates the gate with a
        // fresh session instead of returning this stopped one.
        Exited?.Invoke(this, GrowthExitReason.RealMoneyGate);
        return LastActivity;
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
