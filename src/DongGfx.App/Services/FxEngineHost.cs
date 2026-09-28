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
    private readonly Func<decimal> _equityFloorFloor;
    private readonly Func<bool> _realMoneyUnlocked;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly TimeSpan _cycleOffset;
    private bool _firstCycle = true;
    private bool _cycleRunning;
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

    /// <summary>Portfolio veto (multi-symbol): returns a refusal reason when
    /// the requested lots would exceed the shared exposure cap.</summary>
    private readonly Func<double, Task<string?>>? _preOrderVeto;

    /// <summary>News veto: high-impact calendar window refusal.</summary>
    private readonly Func<(bool Blackout, string Reason)>? _newsVeto;

    /// <summary>Bridge equity refreshed every cycle - the engine's sizing
    /// budget reads it and fails closed at 0 while it is unknown.</summary>
    private double _lastEquity;

    /// <summary>The venue's lot geometry for <see cref="Symbol"/>, fetched
    /// once from the bridge /symbols snapshot — sizing ground truth
    /// (contract size and volume grid) instead of a price heuristic.</summary>
    private FxVenueSymbolSpec? _venueSpec;

    public int PaperSignalsSeen { get; private set; }
    public bool PaperSoakComplete => PaperSignalsSeen >= PaperSoakSignalsRequired;

    /// <summary>Whether one engine decision advances the paper soak: a
    /// signal must have spoken (any action — ordered, paper, or a sizing
    /// skip still proves the alpha fired) while the engine is in PAPER.
    /// Internal static so tests can pin the rule without fake-market
    /// plumbing.</summary>
    internal static bool CountsTowardSoak(FxDecision decision, bool engineIsLive)
        => !engineIsLive && decision.Signal is not null;

    /// <summary>The demo account IS the paper account: a paper-exec fill
    /// is allowed only on a venue the bridge VERIFIED as demo (trade_mode,
    /// else the demo-server heuristic). Real or unverified refuses — paper
    /// practice can never leak into real money. Internal static so tests
    /// pin the rule without fake-market plumbing.</summary>
    internal static bool PaperExecutionAllowed(bool? verifiedVirtual)
        => verifiedVirtual is true;

    /// <summary>Per-symbol order cooldown. Each symbol runs its own host,
    /// so this spacing is per-symbol by construction: after a dispatch
    /// ATTEMPT (fill or refusal — the point is to stop hammering a venue
    /// that just refused us), no further order goes out for this symbol
    /// until the cooldown elapses. Signals still journal and still count
    /// toward the soak; only the dispatch is throttled.</summary>
    internal TimeSpan OrderCooldown { get; set; } = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _lastOrderDispatchUtc;

    /// <summary>The pure cooldown rule: null (never dispatched) or an old
    /// enough last attempt allows a dispatch. Internal static so tests pin
    /// it without fake-market plumbing.</summary>
    internal static bool OrderCooldownActive(DateTimeOffset? lastDispatchUtc, DateTimeOffset now, TimeSpan cooldown)
        => lastDispatchUtc is { } last && now - last < cooldown;

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
        WebhookService? webhook = null,
        Func<double, Task<string?>>? preOrderVeto = null,
        Func<(bool Blackout, string Reason)>? newsVeto = null,
        TimeSpan cycleOffset = default)
    {
        _cycleOffset = cycleOffset;
        _preOrderVeto = preOrderVeto;
        _newsVeto = newsVeto;
        _mt5 = mt5;
        _journal = journal;
        Symbol = symbol;
        _killSwitchEngaged = killSwitchEngaged;
        _lotsCap = lotsCap;
        _equityFloorFloor = equityFloor ?? (() => 0m);
        _realMoneyUnlocked = realMoneyUnlocked;
        Supervisor = supervisor ?? new FxSupervisor(
            journal,
            killSwitchEngaged,
            governorTripped ?? (() => false),
            dailyLossCap ?? (() => 0m),
            equityFloor ?? (() => 0m),
            webhook);
        _engine = new FxEngine(symbol, Timeframe, Journal, lotsCap: (double)_lotsCap(), riskFraction: riskFraction,
            equityProvider: () => _lastEquity);
        // The canonical 20-family roster (FxFamilies.All): the engine picks
        // the highest-confidence speaker per regime, so twenty voices widen
        // the vote without touching the risk model.
        foreach (var alpha in FxFamilies.All())
        {
            _engine.AddAlpha(alpha);
        }

        // Fire-and-forget: the venue's lot geometry (contract size, volume
        // grid) arrives once from the bridge; sizing uses the heuristic
        // fallback until then and switches when the spec lands.
        _ = LoadVenueSpecAsync();

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) =>
        {
            if (_cycleRunning)
            {
                return;   // a slow cycle skips its tick instead of stacking requests
            }
            if (_firstCycle)
            {
                _firstCycle = false;
                // Stagger symbols so their requests never hit the bridge in
                // one burst: the sidecar serializes MT5 access, so four
                // simultaneous cycles queue behind each other and read as
                // timeouts (2026-09-28 bridge congestion).
                await Task.Delay(_cycleOffset).ConfigureAwait(true);
            }
            _cycleRunning = true;
            try
            {
                await RunCycleAsync().ConfigureAwait(true);
            }
            finally
            {
                _cycleRunning = false;
            }
        };
    }

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _timer.Start();
        if (Supervisor.SessionStartBalance is null)
        {
            _ = AnchorSupervisorAsync();   // first host to start anchors; siblings share
        }

        _ = FirstCycleAsync();
    }

    /// <summary>The first cycle after Start: delayed by the symbol's
    /// stagger offset (portfolio hosts must not burst the bridge) and
    /// guarded against stacking with the timer tick.</summary>
    private async Task FirstCycleAsync()
    {
        if (_cycleRunning)
        {
            return;
        }
        if (_firstCycle)
        {
            _firstCycle = false;
            await Task.Delay(_cycleOffset).ConfigureAwait(true);
        }
        _cycleRunning = true;
        try
        {
            await RunCycleAsync().ConfigureAwait(true);
        }
        finally
        {
            _cycleRunning = false;
        }
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
        StatusChanged?.Invoke("paper: fills route to the connected demo account");
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
            if (_venueSpec is null) await LoadVenueSpecAsync().ConfigureAwait(true);   // first snapshot may have failed
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
            _lastEquity = account?.Equity ?? 0;
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

            // Paper soak: an alpha SPOKE while the engine is in paper — that
            // is one observed signal toward this symbol's soak bar. Live
            // signals are proven by execution, not counted here. Without
            // this the counter never advanced and GO LIVE could never fire.
            if (CountsTowardSoak(decision, _engine.IsLive))
            {
                PaperSignalsSeen++;
                Journal("FX_MODE",
                    $"paper soak {PaperSignalsSeen}/{PaperSoakSignalsRequired} on {Symbol}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Symbol,
                        Seen = PaperSignalsSeen,
                        Required = PaperSoakSignalsRequired,
                        Alpha = decision.Signal!.Alpha,
                    }));
            }

            // Exit brain: manage the positions this brain owns (comment-
            // stamped) BEFORE considering a new entry — risk management
            // outranks new exposure.
            await ManageOwnedPositionsAsync(bars, decision.Regime.Regime).ConfigureAwait(true);

            if (decision.Action is FxDecisionAction.Ordered or FxDecisionAction.PaperExecuted
                && decision.Signal is not null)
            {
                var now = DateTimeOffset.UtcNow;
                if (OrderCooldownActive(_lastOrderDispatchUtc, now, OrderCooldown))
                {
                    // Throttle, not censor: the signal already journaled and
                    // counted toward the soak. Only the dispatch waits —
                    // re-sending every cycle into a refusing venue is noise,
                    // not edge (and spams the journal while AutoTrading is
                    // off, which is exactly what this stops).
                    Journal("FX_ORDER",
                        $"dispatch throttled — per-symbol cooldown active ({OrderCooldown.TotalMinutes:0} min), signal kept: {decision.Signal.Alpha}",
                        "{}");
                    return;
                }

                _lastOrderDispatchUtc = now;
                // Paper-exec: the demo account IS the paper account — the
                // signal fills as a real (demo) MT5 order.
                // ExecuteOrderAsync re-verifies the venue is demo first.
                await ExecuteOrderAsync(decision,
                    paperExec: decision.Action == FxDecisionAction.PaperExecuted).ConfigureAwait(true);
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

    private async Task ExecuteOrderAsync(FxDecision decision, bool paperExec = false)
    {
        // Paper-exec hard guard FIRST: the demo account is the paper
        // account, so a paper fill is only ever allowed on a venue the
        // bridge has VERIFIED as demo (trade_mode, else the demo-server
        // heuristic). A real account — even with the session unlock armed —
        // or an unverified one refuses here: paper practice can never leak
        // into real money.
        if (paperExec)
        {
            var account0 = await _mt5.GetAccountAsync().ConfigureAwait(true);
            if (!PaperExecutionAllowed(account0?.GateVerifiedVirtual))
            {
                Journal("FX_ORDER",
                    "refused: paper execution requires a VERIFIED demo account — " +
                    (account0 is null
                        ? "bridge unavailable (account unknown)"
                        : account0.GateVerifiedVirtual is false
                            ? "connected account is REAL — paper never routes to real money"
                            : "account demo/real state unverified — connect a demo account"),
                    "{}");
                StatusChanged?.Invoke("paper execution refused: connected account is not a verified demo");
                return;
            }
        }

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

        // Rails, order 3: news blackout (high-impact calendar window).
        if (_newsVeto is not null)
        {
            var (blackout, reason) = _newsVeto();
            if (blackout)
            {
                Journal("FX_RISK", $"order refused — {reason}", "{}");
                StatusChanged?.Invoke($"order refused: {reason}");
                return;
            }
        }

        // Rails, order 4: portfolio exposure cap (shared across symbols).
        if (_preOrderVeto is not null)
        {
            var veto = await _preOrderVeto(decision.SuggestedLots).ConfigureAwait(true);
            if (veto is not null)
            {
                Journal("FX_ORDER", $"refused: portfolio — {veto}", "{}");
                StatusChanged?.Invoke($"order refused: {veto}");
                return;
            }
        }

        // Same venue verdict as the manual order card: trade_mode from the
        // bridge, demo-only heuristic fallback (see Mt5Account).
        var verifiedVirtual = account.GateVerifiedVirtual;
        var unlocked = _realMoneyUnlocked();
        var gate = RealMoneyGate.Evaluate(configIsDemo: verifiedVirtual is true,
                                          apiVerifiedVirtual: verifiedVirtual,
                                          unlockArmed: unlocked);
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            Journal("FX_ORDER", $"refused: real-money gate — {RealMoneyGate.Explain(gate)}", "{}");
            StatusChanged?.Invoke($"order refused: {RealMoneyGate.Explain(gate)}");
            return;
        }

        // Rails, order 5 (pre-flight): no live order on a guessed size.
        if (LiveOrderBlockedByMissingSpec)
        {
            Journal("FX_ORDER",
                "refused: live order without venue lot geometry (contract " +
                "size/volume grid unknown) — fix the bridge /symbols snapshot first", "{}");
            StatusChanged?.Invoke("order refused: venue spec unavailable");
            return;
        }

        var side = decision.Signal!.Direction == FxDirection.Buy ? "buy" : "sell";
        var result = await _mt5.PlaceOrderAsync(
            Symbol, side, "market", lots, null, null, null, null,
            comment: Core.Fx.FxExitBrain.OwnershipComment).ConfigureAwait(true);
        // The comment stamp is how the exit engine recognizes the positions
        // it owns — manual trades are never managed.

        Journal("FX_ORDER",
            result.Ok
                ? $"{(paperExec ? "paper-exec fill (demo): " : string.Empty)}{side} {lots:0.##} lots {Symbol} @ {result.Price:0.#####} — ticket {result.Order ?? result.Deal}"
                : $"{side} {lots:0.##} lots {Symbol} refused: {result.RetcodeName}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Side = side,
                Lots = lots,
                PaperExec = paperExec,
                result.Retcode,
                result.Order,
                result.Deal,
                result.Price,
                Server = account.Server,
                Signal = decision.Signal.Alpha,
                // The lot geometry the size was computed against: an audit
                // entry can be re-derived against the venue's rules later
                // (was the 100x XAUUSDmicro bug the spec's fault or the math's?).
                VenueSpec = _venueSpec is { } spec
                    ? new { spec.ContractSize, spec.VolumeMin, spec.VolumeStep, spec.VolumeMax }
                    : null,
            }));

        if (result.Ok)
        {
            var ticket = result.Order ?? result.Deal ?? 0;
            if (ticket != 0)
            {
                // The thesis engine needs the regime the trade was born in.
                _entryRegimes[ticket] = decision.Regime.Regime;
            }
        }

        StatusChanged?.Invoke(result.Ok
            ? $"{(paperExec ? "paper filled (demo): " : "filled: ")}{side} {lots:0.##} {Symbol} @ {result.Price:0.#####}"
            : $"refused: {result.RetcodeName}");
    }

    /// <summary>True once the venue's lot geometry for <see cref="Symbol"/>
    /// has landed from the bridge. Until then sizing uses the heuristic
    /// fallback — good enough for paper mode, never trusted for live orders.</summary>
    public bool VenueSpecLoaded { get; private set; }

    private bool _specWarned;

    // ---- Exit brain state ------------------------------------------------
    // Per-position tracking (MFE/MAE in R, bars held) keyed by ticket, plus
    // the entry regime each position was born in (thesis engine input).
    private readonly Dictionary<long, Core.Fx.FxPositionState> _exitStates = new();
    private readonly Dictionary<long, Core.Fx.FxRegime> _entryRegimes = new();

    /// <summary>One-shot fetch of this symbol's venue spec from the bridge
    /// /symbols snapshot. Fire-and-forget and retry-safe: failures leave the
    /// heuristic fallback in place and the next cycle retries. The first
    /// failure journals a loud warning so the gap is visible in monitoring
    /// instead of silently sizing on a guess.</summary>
    private async Task LoadVenueSpecAsync()
    {
        try
        {
            var symbols = await _mt5.GetSymbolsAsync().ConfigureAwait(false);
            var match = symbols.FirstOrDefault(s =>
                s.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase));
            if (match is null || match.ContractSize <= 0)
            {
                WarnSpecMissing(
                    match is null
                        ? "the bridge /symbols snapshot does not name this symbol"
                        : "the snapshot carries no contract size for this symbol (old sidecar?)");
                return;
            }

            _venueSpec = new FxVenueSymbolSpec(
                ContractSize: match.ContractSize,
                VolumeMin: match.VolumeMin > 0 ? match.VolumeMin : 0.01,
                VolumeStep: match.VolumeStep > 0 ? match.VolumeStep : 0.01,
                VolumeMax: match.VolumeMax > 0 ? match.VolumeMax : 100.0);
            _engine.SetVenueSpec(_venueSpec);
            VenueSpecLoaded = true;
        }
        catch
        {
            WarnSpecMissing("the /symbols probe failed (bridge down?)");
        }
    }

    /// <summary>Journal + surface the missing-venue-geometry condition once
    /// per host: sizing keeps its heuristic fallback, loudly.</summary>
    private void WarnSpecMissing(string why)
    {
        if (_specWarned)
        {
            return;
        }

        _specWarned = true;
        Journal("FX_ORDER",
            $"pre-flight warning: venue lot geometry unavailable — {why}. " +
            "Sizing is on the fallback heuristic; the venue's own volume grid " +
            "will reject off-grid sizes at its gate.", "{}");
        StatusChanged?.Invoke("venue spec missing — sizing on fallback heuristic");
    }

    /// <summary>Final pre-flight rail: a LIVE order may never be sized
    /// without the venue's own lot geometry (contract size + volume grid).
    /// Paper mode keeps the heuristic fallback — the soak is where the gap
    /// gets noticed, the money path refuses to gamble on a guess.</summary>
    internal bool LiveOrderBlockedByMissingSpec => IsLiveEngine && _venueSpec is null;

    /// <summary>Spread guard for the exit override, in pip points — far
    /// above the venue's normal quote (Deriv gold ~27 pts), so only a
    /// genuinely abnormal market trips it.</summary>
    internal const double MaxAbnormalSpreadPoints = 250;

    /// <summary>The Exit Brain's per-cycle pass: evaluate every position
    /// this brain owns (comment-stamped entries) and act on the resolver's
    /// decision — full/partial closes via /close, tighten via /modify SL.
    /// Everything lands in the journal as FX_EXIT (score, votes, MFE/MAE):
    /// the settlement trail fills, and the data-hungry engines of v2 get
    /// their substrate. Manual positions (no stamp) are never touched.</summary>
    private async Task ManageOwnedPositionsAsync(IReadOnlyList<FxBar> bars, FxRegime currentRegime)
    {
        var positions = await _mt5.GetPositionsAsync().ConfigureAwait(true);
        var owned = positions.Where(p => Core.Fx.FxExitBrain.Owns(p.Comment)).ToList();
        if (owned.Count == 0)
        {
            return;
        }

        var atr = bars.Count >= 15 ? Core.Fx.FxFeatures.Atr(bars, 14) : double.NaN;
        var atrMedian = Core.Fx.FxFeatures.AtrMedian(bars, 20);
        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        var tick = await _mt5.GetTickAsync(Symbol).ConfigureAwait(true);
        var mid = tick is { } t && t.Ask > t.Bid ? (t.Ask + t.Bid) / 2 : bars[^1].Close;
        var spreadPoints = tick is { } t2 && t2.Ask > t2.Bid
            ? (t2.Ask - t2.Bid) / Math.Max(PipSizeFor(mid), 1e-9)
            : 0;

        foreach (var p in owned)
        {
            if (!_exitStates.TryGetValue(p.Ticket, out var st))
            {
                // First sighting: seed the tracking state. The entry regime
                // may be unknown (app restart) — fall back to the current
                // regime so the thesis engine stays neutral instead of
                // inventing a flip that never happened.
                _entryRegimes[p.Ticket] = currentRegime;
                st = new Core.Fx.FxPositionState(
                    p.Ticket, p.Symbol, p.Side, p.PriceOpen, p.Volume,
                    Core.Fx.FxExitBrain.RiskPerLot(p.PriceOpen, p.Sl, double.IsNaN(atr) ? 0 : atr),
                    MfeR: 0, MaeR: 0, BarsHeld: 0);
            }

            var price = p.PriceCurrent > 0 ? p.PriceCurrent : bars[^1].Close;
            st = Core.Fx.FxExitBrain.UpdateState(st, price, st.BarsHeld + 1);
            _exitStates[p.Ticket] = st;
            var entryRegime = _entryRegimes[p.Ticket];

            var decision = Core.Fx.FxExitBrain.Evaluate(
                st, price, p.Volume,
                double.IsNaN(atr) ? 0 : atr, atrMedian,
                spreadPoints, MaxAbnormalSpreadPoints,
                account?.Equity ?? 0, (double)_equityFloorFloor(),
                bridgeUp: true,   // a dead bridge never reaches this method
                bars, currentRegime, entryRegime);

            Journal("FX_EXIT",
                $"{p.Symbol} #{p.Ticket}: {decision.Action} score {decision.Score:0} — {decision.Reason}",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Ticket = p.Ticket,
                    p.Symbol,
                    p.Side,
                    Action = decision.Action,
                    Score = Core.Fx.FxJson.Sanitize(decision.Score),
                    MfeR = Core.Fx.FxJson.Sanitize(decision.MfeR),
                    MaeR = Core.Fx.FxJson.Sanitize(decision.MaeR),
                    ProfitR = Core.Fx.FxJson.Sanitize(decision.ProfitR),
                    Override = decision.OverrideEngine,
                    Votes = decision.Votes.Select(v => new
                    {
                        v.Engine,
                        Exit = Core.Fx.FxJson.Sanitize(v.Exit),
                        Reason = v.Reason,
                    }).ToList(),
                }));

            if (decision.Action == "full"
                || (decision.Action == "partial" && decision.LotsToClose > 0))
            {
                var lots = decision.Action == "full" ? (double?)null : SnapLots(decision.LotsToClose);
                var close = await _mt5.ClosePositionAsync(p.Ticket, lots).ConfigureAwait(true);
                Journal("FX_EXIT",
                    close.Ok
                        ? $"closed {(lots.HasValue ? $"{lots:0.##} lots of " : string.Empty)}#{p.Ticket} — deal {close.Deal}"
                        : $"close refused for #{p.Ticket}: {close.RetcodeName}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = p.Ticket,
                        Partial = lots.HasValue,
                        Lots = lots,
                        close.Retcode,
                    }));
                if (close.Ok && decision.Action == "full")
                {
                    _exitStates.Remove(p.Ticket);
                    _entryRegimes.Remove(p.Ticket);
                }
            }
            else if (decision.Action == "tighten" && decision.NewSl > 0)
            {
                var mod = await _mt5.ModifyPositionAsync(p.Ticket, sl: decision.NewSl).ConfigureAwait(true);
                Journal("FX_EXIT",
                    mod.Ok
                        ? $"trailed #{p.Ticket} stop to {decision.NewSl:0.#####} (+0.2R)"
                        : $"stop trail refused for #{p.Ticket}: {mod.RetcodeName}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = p.Ticket,
                        NewSl = Core.Fx.FxJson.Sanitize(decision.NewSl),
                        mod.Retcode,
                    }));
            }
        }
    }

    /// <summary>Snap an exit's lots to the venue's volume step (never below
    /// one step; /close validates the upper bound).</summary>
    private double SnapLots(double lots)
    {
        var step = _venueSpec is { } spec && spec.VolumeStep > 0 ? spec.VolumeStep : 0.01;
        return Math.Round(Math.Max(step, Math.Round(lots / step) * step), 2);
    }

    /// <summary>Pip-size heuristic for the spread override (gold-like 0.1,
    /// JPY-class 0.01, everything else 0.0001).</summary>
    private static double PipSizeFor(double price) =>
        price >= 400 ? 0.1 : price >= 20 ? 0.01 : 0.0001;

    private void Journal(string category, string detail, string json) =>
        _journal.Log(Guid.Empty, category, detail, json);

    public FxDecision? LastDecision { get; private set; }

    public void Dispose() => _timer.Stop();
}
