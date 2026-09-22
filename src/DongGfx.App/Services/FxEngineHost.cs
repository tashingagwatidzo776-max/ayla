using DongGfx.App.Infrastructure;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>
/// App-side host for the DON G FX forex brain: owns the DispatcherTimer,
/// fetches candles/quotes from the MT5 bridge, runs pure engine cycles, and
/// executes ORDER decisions through the SAME rail sequence as the manual
/// ticket (kill switch → Mt5MaxLots → real-money gate → bridge order).
/// Every state lands in the journal (FX_REGIME / FX_SIGNAL / FX_DECISION /
/// FX_ORDER / FX_MODE). Paper by default; demo-live is an explicit flip.
/// </summary>
public sealed class FxEngineHost : IDisposable
{
    private readonly Mt5BridgeClient _mt5;
    private readonly TradeJournal _journal;
    private readonly Func<bool> _killSwitchEngaged;
    private readonly Func<decimal> _lotsCap;
    private readonly Func<bool> _realMoneyUnlocked;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly FxEngine _engine;
    private bool _busy;

    /// <summary>Cross-venue risk layer: kill switch, governor, loss stops.
    /// Evaluated BEFORE every cycle and BEFORE every order.</summary>
    public FxSupervisor Supervisor { get; }

    public string Symbol { get; }
    public string Timeframe { get; } = "M1";
    public bool IsRunning => _timer.IsEnabled;
    public bool IsLiveEngine => _engine.IsLive;
    public event Action<string>? StatusChanged;

    /// <summary>Paper soak requirement: the host will not GoLive until at
    /// least this many paper signals have been journaled (plan guardrail).</summary>
    public int PaperSoakSignalsRequired { get; set; } = 10;

    public int PaperSignalsSeen { get; private set; }
    public bool PaperSoakComplete => PaperSignalsSeen >= PaperSoakSignalsRequired;

    public FxEngineHost(
        Mt5BridgeClient mt5,
        TradeJournal journal,
        string symbol,
        Func<bool> killSwitchEngaged,
        Func<decimal> lotsCap,
        Func<bool> realMoneyUnlocked,
        double riskFraction = 0.02,
        FxSupervisor? supervisor = null,
        Func<bool>? governorTripped = null,
        Func<decimal>? dailyLossCap = null,
        Func<decimal>? equityFloor = null,
        WebhookService? webhook = null)
    {
        _mt5 = mt5;
        _journal = journal;
        Symbol = symbol;
        _killSwitchEngaged = killSwitchEngaged;
        _lotsCap = lotsCap;
        _realMoneyUnlocked = realMoneyUnlocked;
        Supervisor = supervisor ?? new FxSupervisor(
            journal,
            killSwitchEngaged,
            governorTripped ?? (() => false),
            dailyLossCap ?? (() => 0m),
            equityFloor ?? (() => 0m),
            webhook);
        _engine = new FxEngine(symbol, Timeframe, Journal, lotsCap: (double)_lotsCap(), riskFraction: riskFraction);
        _engine.AddAlpha(new FxMomentum.EmaCross());
        _engine.AddAlpha(new FxMomentum.DonchianBreakout());
        _engine.AddAlpha(new FxMomentum.Roc());
        _engine.AddAlpha(new FxMeanReversion.ZScore());
        _engine.AddAlpha(new FxMeanReversion.BollingerReversion());
        _engine.AddAlpha(new FxMeanReversion.VwapReversion());

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) => await RunCycleAsync().ConfigureAwait(true);
    }

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _timer.Start();
        _ = AnchorSupervisorAsync();
        _ = RunCycleAsync();
    }

    /// <summary>Anchor the supervisor's loss baseline to the live balance
    /// (network call → fire-and-forget off Start).</summary>
    private async Task AnchorSupervisorAsync()
    {
        var acct = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (acct is not null)
        {
            Supervisor.AnchorSession((decimal)acct.Balance);
        }
    }

    public void Stop() => _timer.Stop();

    public void GoLive()
    {
        if (!PaperSoakComplete)
        {
            Journal("FX_MODE", $"go-live refused — paper soak incomplete ({PaperSignalsSeen}/{PaperSoakSignalsRequired} signals)",
                "{}");
            StatusChanged?.Invoke($"go-live refused — paper soak {PaperSignalsSeen}/{PaperSoakSignalsRequired}");
            return;
        }

        _engine.GoLive();
        StatusChanged?.Invoke("engine LIVE — demo orders enabled");
    }

    public void GoPaper()
    {
        _engine.GoLive();
        _engine.GoPaper();
        StatusChanged?.Invoke("engine back to paper");
    }

    /// <summary>Operator re-arm after a loss stop: forget the latch and
    /// re-anchor the supervisor to the live balance.</summary>
    public void ReArmLossStop()
    {
        _ = ReArmLossStopAsync();
    }

    private async Task ReArmLossStopAsync()
    {
        var acct = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (acct is not null)
        {
            Supervisor.ReAnchor((decimal)acct.Balance);
            StatusChanged?.Invoke("loss stop re-armed");
        }
    }

    /// <summary>One brain cycle: bridge data in → decision → maybe order.
    /// The order path re-checks the rails (they can change mid-session).</summary>
    public async Task RunCycleAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var candles = await _mt5.GetCandlesAsync(Symbol, Timeframe, 120).ConfigureAwait(true);
            if (candles.Count < 35)
            {
                return;
            }

            var bars = candles.Select(c => new FxBar(c.Time, c.Open, c.High, c.Low, c.Close, 0)).ToList();
            var tick = await _mt5.GetTickAsync(Symbol).ConfigureAwait(true);
            double bid = 0, ask = 0;
            if (tick is { } t)
            {
                bid = t.Bid;
                ask = t.Ask;
            }
            else
            {
                var last = bars[^1];
                bid = ask = last.Close;
            }

            // Supervisor gate — the brain does not even think while halted.
            var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
            var verdict = Supervisor.Evaluate(account is not null, (decimal)(account?.Balance ?? 0), (decimal)(account?.Equity ?? 0));
            if (!verdict.TradingAllowed)
            {
                if (_engine.IsLive)
                {
                    _engine.GoPaper();
                    Journal("FX_RISK", $"live engine returned to PAPER — {verdict.Reason}", "{}");
                }

                LastDecision = null;
                StatusChanged?.Invoke($"halted: {verdict.Reason}");
                return;
            }

            // A transient halt (bridge/kill/governor) that has recovered.
            Supervisor.ClearTransientHalts();

            var decision = _engine.RunOnce(DateTimeOffset.UtcNow, bars, bid, ask);
            LastDecision = decision;

            if (decision.Action == FxDecisionAction.Ordered && decision.Signal is not null)
            {
                await ExecuteOrderAsync(decision).ConfigureAwait(true);
            }
        }
        catch
        {
            // a failed cycle is a skipped cycle — the next timer tick retries
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ExecuteOrderAsync(FxDecision decision)
    {
        // Rails, in order, at execution time.
        if (_killSwitchEngaged())
        {
            Journal("FX_ORDER", "refused: kill switch engaged", "{}");
            StatusChanged?.Invoke("order refused: kill switch engaged");
            return;
        }

        var cap = _lotsCap();
        if (cap <= 0)
        {
            Journal("FX_ORDER", "refused: Mt5MaxLots = 0 (MT5 orders disabled)", "{}");
            StatusChanged?.Invoke("order refused: MT5 orders disabled (cap 0)");
            return;
        }

        var lots = Math.Min((double)cap, decision.SuggestedLots);
        if (lots < 0.01)
        {
            Journal("FX_ORDER", $"refused: sized {decision.SuggestedLots:0.##} lots below minimum", "{}");
            return;
        }

        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (account is null)
        {
            Journal("FX_ORDER", "refused: bridge unavailable", "{}");
            return;
        }

        // Rails, order 2: the supervisor re-checked at execution time.
        var execVerdict = Supervisor.Evaluate(true, (decimal)account.Balance, (decimal)account.Equity);
        if (!execVerdict.TradingAllowed)
        {
            Journal("FX_ORDER", $"refused: supervisor — {execVerdict.Reason}", "{}");
            StatusChanged?.Invoke($"order refused: {execVerdict.Reason}");
            return;
        }

        var isDemo = account.Server.Contains("demo", StringComparison.OrdinalIgnoreCase)
                     || account.Login > 500_000_000;
        var unlocked = _realMoneyUnlocked();
        var gate = RealMoneyGate.Evaluate(isDemo, isDemo ? true : null, unlocked);
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            Journal("FX_ORDER", $"refused: real-money gate — {RealMoneyGate.Explain(gate)}", "{}");
            StatusChanged?.Invoke($"order refused: {RealMoneyGate.Explain(gate)}");
            return;
        }

        var side = decision.Signal!.Direction == FxDirection.Buy ? "buy" : "sell";
        var result = await _mt5.PlaceOrderAsync(
            Symbol, side, "market", lots, null, null, null, null).ConfigureAwait(true);

        Journal("FX_ORDER",
            result.Ok
                ? $"{side} {lots:0.##} lots {Symbol} @ {result.Price:0.#####} — ticket {result.Order ?? result.Deal}"
                : $"{side} {lots:0.##} lots {Symbol} refused: {result.RetcodeName}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Side = side,
                Lots = lots,
                result.Retcode,
                result.Order,
                result.Deal,
                result.Price,
                Server = account.Server,
                Signal = decision.Signal.Alpha,
            }));

        StatusChanged?.Invoke(result.Ok
            ? $"filled: {side} {lots:0.##} {Symbol} @ {result.Price:0.#####}"
            : $"refused: {result.RetcodeName}");
    }

    private void Journal(string category, string detail, string json) =>
        _journal.Log(Guid.Empty, category, detail, json);

    public FxDecision? LastDecision { get; private set; }

    public void Dispose() => _timer.Stop();
}
