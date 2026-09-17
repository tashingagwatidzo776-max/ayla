using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.ViewModels;

public sealed partial class BrainViewModel : ObservableObject
{
    private readonly TradingBrain _brain;
    private readonly DerivClient _client;
    private readonly TradeStore _store;
    private readonly DashboardViewModel _dashboard;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly LlmClient _llm;
    private readonly Func<IReadOnlyList<Tick>> _tickWindow;
    private readonly Func<RiskContext> _riskContext;
    private readonly Func<IReadOnlyList<string>> _recentLessons;
    private readonly MetricsCollector _metrics;
    private readonly Func<RealMoneyDecision>? _realMoneyDecision;
    private AutonomousScheduler? _scheduler;

    [ObservableProperty]
    private string statusText = "Idle";

    [ObservableProperty]
    private string lastDecisionText = "No decisions yet — press \"Run decision cycle\".";

    [ObservableProperty]
    private string lessonsText = "No lessons yet.";

    [ObservableProperty]
    private string llmInfo = "";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isAutonomyRunning;

    [ObservableProperty]
    private bool isAutonomyEnabled;

    public ObservableCollection<string> ConfidenceHistory { get; } = new();

    /// <summary>Live cycle telemetry (latency/errors) for the LLM-tab
    /// autonomy loop — feeds the shared metrics export alongside the
    /// growth runners' samples.</summary>
    public MetricsCollector Metrics => _metrics;

    public BrainViewModel(DerivClient client, TradeStore store,
        DashboardViewModel dashboard, Func<AppSettings> settings,
        Func<IReadOnlyList<Tick>> tickWindow, Func<RiskContext> riskContext,
        Func<IReadOnlyList<string>> recentLessons, MetricsCollector? metrics = null,
        Func<RealMoneyDecision>? realMoneyDecision = null)
    {
        _metrics = metrics ?? new MetricsCollector();
        // Optional real-money gate source: when supplied, the manual Brain
        // tab evaluates it before every cycle and autonomy start. Tests and
        // demo-only constructions omit it (the risk context then carries the
        // default passthrough).
        _realMoneyDecision = realMoneyDecision;
        _client = client;
        _store = store;
        _dashboard = dashboard;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tickWindow = tickWindow ?? throw new ArgumentNullException(nameof(tickWindow));
        _riskContext = riskContext ?? throw new ArgumentNullException(nameof(riskContext));
        _recentLessons = recentLessons ?? throw new ArgumentNullException(nameof(recentLessons));

        _llm = new LlmClient();
        _brain = new TradingBrain(_llm, settings, new TradingBrain.DerivAbstraction
        {
            GetProposal = (s, d, a, c, dur, ct) => _client.GetProposalAsync(s, d, a, c, dur, ct),
            Buy = (id, price, ct) => _client.BuyAsync(id, price, ct),
            WaitForSettlement = (id, timeout, ct) => _client.WaitForSettlementAsync(id, timeout, ct)
        });

        RefreshLlm();
    }

    public void Refresh()
    {
        var settings = _settings();
        LlmInfo = $"{settings.LlmModel} @ {settings.LlmBaseUrl}";
        IsAutonomyEnabled = settings.AutonomyEnabled;

        var lessons = MarketContextBuilder.LessonsFrom(_store.Trades, 5);
        LessonsText = lessons.Count == 0
            ? "No lessons yet — complete a few trades first."
            : string.Join(Environment.NewLine, lessons);
    }

    public void RefreshLlm()
    {
        var settings = _settings();
        _llm.BaseUrl = settings.LlmBaseUrl;
        _llm.Model = settings.LlmModel;
        _llm.ApiKey = settings.LlmApiKey;
        LlmInfo = $"{settings.LlmModel} @ {settings.LlmBaseUrl}";
    }

    /// <summary>
    /// Runs one brain cycle: context → LLM decision → risk check. Places the
    /// trade only when settings.AutonomyEnabled is on (guardrails apply).
    /// </summary>
    [RelayCommand]
    private async Task RunCycleAsync()
    {
        RefreshLlm();

        var window = _tickWindow() ?? Array.Empty<Tick>();
        if (window.Count == 0)
        {
            StatusText = "No market data yet — connect on the Dashboard first.";
            return;
        }

        // Manual cycles obey the same real-money gate as the growth engines:
        // a refusal here means no LLM decision may act on this account.
        var gate = CurrentRealMoneyDecision();
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            StatusText = RealMoneyGate.Explain(gate);
            LastDecisionText = StatusText;
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = IsAutonomyEnabled ? "Thinking → acting…" : "Thinking…";
            var risk = _riskContext();
            var lessons = _recentLessons();

            var result = await _brain.RunCycleAsync(
                window, risk, lessons, allowTrading: IsAutonomyEnabled);

            RecordExecuted(result);

            AddConfidenceEntry($"{DateTime.Now:HH:mm:ss}  {result.Decision.Direction}  " +
                $"conf {result.Decision.Confidence:P0}  " +
                (result.Verdict.Allowed ? "PASSED risk" : $"BLOCKED: {result.Verdict.Reason}"));

            LastDecisionText =
                $"Direction: {result.Decision.Direction}\n" +
                $"Confidence: {result.Decision.Confidence:P0}\n" +
                $"Stake: {result.Decision.Stake:0.##} {_settings().Currency}\n" +
                $"Risk check: {(result.Verdict.Allowed ? "PASSED" : "BLOCKED — " + result.Verdict.Reason)}\n" +
                $"Reasoning: {result.Decision.Reasoning}";

            StatusText = result.Verdict.Allowed && IsAutonomyEnabled
                ? "Trade placed — awaiting settlement…"
                : result.Verdict.Allowed
                    ? "Decision ready (autonomy off — not traded)"
                    : "Idle";
        }
        catch (Exception ex)
        {
            LastDecisionText = $"Cycle failed: {ex.Message}";
            StatusText = "Idle (error)";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddConfidenceEntry(string entry)
    {
        if (_dispatcher.CheckAccess())
        {
            ConfidenceHistory.Insert(0, entry);
        }
        else
        {
            _dispatcher.BeginInvoke(() => ConfidenceHistory.Insert(0, entry));
        }
    }

    [RelayCommand]
    private void StartAutonomyCommand()
    {
        StartAutonomy();
    }

    [RelayCommand]
    private void StopAutonomyCommand()
    {
        StopAutonomy();
    }

    /// <summary>
    /// Starts the autonomous scheduler. Only runs when the autonomy setting
    /// is enabled; the scheduler itself stops the moment the kill switch is
    /// engaged (via the risk context it re-reads every cycle).
    /// </summary>
    public void StartAutonomy()
    {
        if (IsAutonomyRunning || !IsAutonomyEnabled)
        {
            return;
        }

        // Starting autonomy is a deliberate act: evaluate the real-money
        // gate up front so a locked/unverified account never enters the loop.
        var gate = CurrentRealMoneyDecision();
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            StatusText = RealMoneyGate.Explain(gate);
            LastDecisionText = StatusText;
            return;
        }

        _scheduler = new AutonomousScheduler(
            _brain, _settings, _tickWindow, _riskContext, _recentLessons, OnScheduledCycle,
            onCycleLatencyMs: ms => _metrics.RecordLatency(ms, "LlmTab", DateTimeOffset.UtcNow),
            onCycleError: exType => _metrics.RecordError("LlmTab", DateTimeOffset.UtcNow));
        IsAutonomyRunning = true;
        StatusText = "Autonomy running — waiting for the first decision…";

        _ = _scheduler.StartAsync();
    }

    /// <summary>Persists a settled trade so the log/lessons reflect it.</summary>
    private void RecordExecuted(BrainCycleResult result)
    {
        if (result.ExecutedTrade is { } trade)
        {
            _store.Add(trade with { Source = TradeSource.Llm });
        }
    }

    /// <summary>Surfaces a scheduler-completed cycle into the UI (thread-safe).</summary>
    private void OnScheduledCycle(BrainCycleResult result)
    {
        RecordExecuted(result);

        void Update()
        {
            AddConfidenceEntry($"{DateTime.Now:HH:mm:ss}  {result.Decision.Direction}  " +
                $"conf {result.Decision.Confidence:P0}  " +
                (result.Verdict.Allowed ? "PASSED risk" : $"BLOCKED: {result.Verdict.Reason}"));

            LastDecisionText =
                $"Direction: {result.Decision.Direction}\n" +
                $"Confidence: {result.Decision.Confidence:P0}\n" +
                $"Stake: {result.Decision.Stake:0.##} {_settings().Currency}\n" +
                $"Risk check: {(result.Verdict.Allowed ? "PASSED" : "BLOCKED — " + result.Verdict.Reason)}\n" +
                $"Reasoning: {result.Decision.Reasoning}";

            StatusText = result.Verdict.Allowed && IsAutonomyEnabled
                ? "Trade placed — awaiting settlement…"
                : result.Verdict.Allowed
                    ? "Decision ready (autonomy off — not traded)"
                    : "Idle";
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    public void StopAutonomy()
    {
        _scheduler?.Stop();
        IsAutonomyRunning = false;
    }

    public RiskContext BuildRiskContext()
    {
        var today = _store.SummaryFor(DateTimeOffset.Now);
        var last = _store.Trades.FirstOrDefault();

        return new RiskContext(
            KillSwitchEngaged: _dashboard.IsKillSwitchEngaged,
            OpenContracts: 0,
            Balance: _client.Balance.Balance,
            DailyNetProfit: today.NetProfit,
            TradesToday: today.Count,
            LastTradeAt: last?.SettledAt,
            LastTradeOutcome: last?.Outcome,
            RealMoney: _realMoneyDecision?.Invoke() ?? RealMoneyDecision.DemoPassthrough);
    }

    /// <summary>Evaluates the real-money gate for this tab (null source →
    /// passthrough, the demo default). Used by the cycle pre-check and the
    /// autonomy start guard.</summary>
    private RealMoneyDecision CurrentRealMoneyDecision() =>
        _realMoneyDecision?.Invoke() ?? RealMoneyDecision.DemoPassthrough;
}