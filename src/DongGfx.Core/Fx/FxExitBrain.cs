namespace DongGfx.Core.Fx;

/// <summary>One engine's vote on an open position.</summary>
/// <param name="Engine">Stable engine name for the journal.</param>
/// <param name="Exit">0..1 confidence that the position should be exited now.</param>
/// <param name="Weight">Relative weight in the conflict resolver.</param>
/// <param name="Reason">Human-readable evidence for the journal.</param>
public sealed record FxExitVote(string Engine, double Exit, double Weight, string Reason);

/// <summary>The conflict resolver's decision for one position.</summary>
public sealed record FxExitDecision(
    long Ticket,
    string Action,          // hold | monitor | tighten | partial | full
    double Score,           // weighted ensemble score 0..100
    IReadOnlyList<FxExitVote> Votes,
    double LotsToClose,     // venue-stepped lots for partial/full
    double NewSl,           // 0 = leave the stop alone
    double MfeR,            // max favorable excursion in R
    double MaeR,            // max adverse excursion in R
    double ProfitR,         // current P/L in R
    string? OverrideEngine, // non-null: a hard safety override fired
    string Reason);

/// <summary>The state the brain tracks per open position between cycles.</summary>
public sealed record FxPositionState(
    long Ticket,
    string Symbol,
    string Side,            // "buy" | "sell"
    double EntryPrice,
    double InitialLots,
    double RiskPerLot,      // |entry - initial SL| price distance; R-unit = this distance
    double MfeR,
    double MaeR,
    int BarsHeld,
    // The stop distance the trade was SIZED with (the engine's structural
    // R unit) when known — the honest ruler even when the venue normalized
    // the placed SL. 0 when unknown.
    double InitialStopDistance = 0);

/// <summary>
/// The Exit Brain v1 — independent evidence engines, a weighted conflict
/// resolver, and hard safety overrides (docs/ai-agent-program.md).
///
/// Design law: more engines ≠ more robustness. Each engine contributes
/// independent evidence with a confidence; the resolver weights and bands
/// them; hard overrides (bridge loss, abnormal spread, equity floor)
/// outrank any vote. v1 ships six real engines — the data-hungry ones
/// (probability/counterfactual/AI, order-flow, correlation, news) are
/// documented stubs until the journal holds the history they need.
/// Pure: snapshots in, decision out; no I/O, no clock, no orders.
/// </summary>
public static class FxExitBrain
{
    public const string OwnershipComment = "donggfx-brain";

    // Resolver bands (score 0..100).
    public const double FullExitScore = 85.0;
    public const double PartialExitScore = 70.0;
    public const double TightenScore = 55.0;
    public const double MonitorScore = 35.0;

    /// <summary>Time engine: a trade that never got going is closed.</summary>
    public const int MaxStagnantBars = 36;   // ~36 min on M1
    /// <summary>Time engine: MFE never reached a fraction of 1R.</summary>
    public const double StagnantMfeFraction = 0.5;
    /// <summary>Volatility engine: ATR collapsed to this fraction of its
    /// 20-bar median — the market went to sleep in the position.</summary>
    public const double VolCollapseFraction = 0.45;
    /// <summary>Volatility engine: ATR expanded beyond this multiple —
    /// the regime turned hostile; protect the book.</summary>
    public const double VolShockMultiple = 2.6;
    /// <summary>Drawdown engine: MAE (in R) beyond this = abnormal adverse
    /// excursion — an override-grade emergency.</summary>
    public const double MaeEmergencyR = 1.6;
    /// <summary>Profit-floor override: a trade that peaked ≥1R and returned
    /// to ≤ this R has ROUND-TRIPPED — the 2026-09-29 backtest's +20.25R
    /// failure mode, promoted to the override tier (safety outranks the
    /// consensus that failed to see it).</summary>
    public const double ProfitFloorR = 0.2;
    /// <summary>Giveback vote: deep give-back of a major (≥2R) peak.</summary>
    public const double GivebackVoteRatio = 0.75;
    /// <summary>Giveback watch: heavy give-back of a real (≥1.5R) peak.</summary>
    public const double GivebackWatchRatio = 0.60;
    /// <summary>Profit-floor override: deep give-back bar for a major peak
    /// (fraction of the peak returned) — the exact bar the 2026-09-29
    /// backtest validated against six round-trip losses.</summary>
    public const double GivebackOverrideRatio = 0.75;
    /// <summary>Structure engine: bars scanned back for the swing.</summary>
    public const int SwingLookback = 12;

    /// <summary>True when the brain owns this position (by order comment).</summary>
    public static bool Owns(string positionComment) =>
        positionComment.Contains(OwnershipComment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Approximate pip size by price scale — mirrors the engine's
    /// own heuristic (FxEngine.PipSizeOf). Used only to keep fallback risk
    /// units at sane market scale.</summary>
    public static double PipSizeOf(double price) => price switch
    {
        > 500 => 10,      // XAU-like
        > 20 => 0.01,     // index-ish
        _ => 0.0001,      // EURUSD-like
    };

    /// <summary>The exit brain's R-unit: the initial-stop distance in price
    /// terms. When no SL was set at entry it falls back to an ATR multiple
    /// floored at one pip — a sub-pip ATR (quiet M1 micro-bars, thin
    /// sessions) once made the 1.6R emergency bar measure less than a pip
    /// of adverse movement, cutting positions on spread noise.</summary>
    public static double RiskPerLot(double entry, double initialSl, double atrAtEntry)
    {
        // initialSl == 0 means "no stop was set" — never treat the whole
        // entry price as risk distance.
        var stopDist = initialSl > 0 ? Math.Abs(entry - initialSl) : 0;
        if (stopDist > 1e-9)
        {
            return stopDist;
        }

        var pip = PipSizeOf(entry);
        return Math.Max(Math.Max(atrAtEntry, 0) * 1.5, pip);
    }

    /// <summary>Clamp a desired stop distance up to what the venue accepts:
    /// stops_level points (converted to price by the symbol's point). A
    /// stop inside that band is rejected outright by MT5, so flooring here
    /// is cheaper than a refused order. Returns null when the distance is
    /// degenerate (nothing sane to place).</summary>
    public static double? NormalizedStopDistance(double desired, double stopsLevel, double point)
    {
        if (!double.IsFinite(desired) || desired <= 0)
        {
            return null;
        }

        var floor = Math.Max(stopsLevel, 0) * Math.Max(point, 1e-9);
        return double.IsFinite(floor) ? Math.Max(desired, floor) : null;
    }

    /// <summary>Evaluate one open position: engines vote, the resolver
    /// decides. bars is the symbol's recent M1 series (chronological).</summary>
    public static FxExitDecision Evaluate(
        FxPositionState state,
        double currentPrice,
        double currentLots,
        double atr,
        double atrMedian20,
        double spreadPoints,
        double maxSpreadPoints,
        double equity, double equityFloor,
        bool bridgeUp,
        IReadOnlyList<FxBar> bars,
        FxRegime currentRegime,
        FxRegime entryRegime)
    {
        var votes = new List<FxExitVote>();
        var isBuy = state.Side == "buy";
        var dir = isBuy ? 1.0 : -1.0;

        // ---- MFE / MAE bookkeeping (in R) --------------------------------
        var plR = dir * (currentPrice - state.EntryPrice) / state.RiskPerLot;
        var mfeR = Math.Max(state.MfeR, plR);
        var maeR = Math.Max(state.MaeR, -plR);

        // ---- Engines (independent evidence) -------------------------------
        votes.Add(StructureVote(bars, isBuy));
        votes.Add(MomentumVote(bars, isBuy));
        votes.Add(VolatilityVote(atr, atrMedian20));
        votes.Add(TimeVote(state.BarsHeld, mfeR, plR));
        votes.Add(ThesisVote(currentRegime, entryRegime, isBuy));
        votes.Add(DrawdownVote(maeR, mfeR, plR));

        // ---- Shadow engines (observation only) ----------------------------
        // Future engines ride at weight 0: journaled every cycle but never
        // moving the score, until their recorded accuracy earns real weight
        // (FxExitShadow.Grade -> Promote; a human PR ships the change after
        // the Monte-Carlo harness reports stable).
        foreach (var sv in ShadowVotes(bars, isBuy, plR, mfeR))
        {
            votes.Add(new FxExitVote(sv.Engine, sv.Exit, 0, sv.Reason));
        }

        // ---- Weighted ensemble score --------------------------------------
        double weightSum = 0, weighted = 0;
        foreach (var v in votes)
        {
            var exit = double.Clamp(v.Exit, 0, 1);
            weighted += exit * v.Weight;
            weightSum += v.Weight;
        }
        var score = weightSum > 0 ? weighted / weightSum * 100.0 : 0;

        // ---- Hard safety overrides (outrank the vote) ----------------------
        string? overrideEngine = null;
        var overrideReason = "";
        if (!bridgeUp)
        {
            overrideEngine = "bridge";
            overrideReason = "bridge down — cannot manage the position; emergency close";
        }
        else if (spreadPoints > maxSpreadPoints)
        {
            overrideEngine = "spread";
            overrideReason = $"spread {spreadPoints:0} pts beyond the abnormal bar {maxSpreadPoints:0} — exit before conditions worsen";
        }
        else if (equityFloor > 0 && equity < equityFloor)
        {
            overrideEngine = "equity-floor";
            overrideReason = $"equity {equity:0.##} below the absolute floor {equityFloor:0.##} — emergency close";
        }
        else if (maeR >= MaeEmergencyR)
        {
            overrideEngine = "drawdown";
            overrideReason = $"MAE {maeR:0.00}R beyond the {MaeEmergencyR:0.#}R emergency bar — adverse movement is abnormal; emergency close";
        }
        else if ((mfeR >= 1.0 && plR <= ProfitFloorR)
                 || (mfeR >= 2.0 && (mfeR - plR) / mfeR >= GivebackOverrideRatio))
        {
            // The profit-floor override: the mirror of the MAE emergency.
            // A trade that EARNED a real peak must not hand it all back —
            // the ensemble's structural blindness to profit-side giveback
            // (six round-trips, +20.25R, 2026-09-29) is exactly the class
            // of failure overrides exist to outrank.
            overrideEngine = "profit-floor";
            overrideReason = $"gave back a {mfeR:0.0}R peak to {plR:0.00}R — the profit floor protects what was earned; emergency close";
        }

        if (overrideEngine is not null)
        {
            return new FxExitDecision(state.Ticket, "full", 100, votes, currentLots, 0,
                mfeR, maeR, plR, overrideEngine, overrideReason);
        }

        // ---- Resolver bands ------------------------------------------------
        var action = Resolve(score);

        // Partial exits scale out a third of the book; full closes the rest.
        var lotsToClose = action switch
        {
            "full" => currentLots,
            "partial" => currentLots / 3.0,
            _ => 0.0,
        };

        // Tighten: trail the stop to break-even-plus once MFE paid for it.
        var newSl = 0.0;
        if (action == "tighten" && mfeR >= 1.0)
        {
            var bePlus = state.EntryPrice + dir * 0.2 * state.RiskPerLot;
            var improves = isBuy ? bePlus > state.EntryPrice : bePlus < state.EntryPrice;
            if (improves)
            {
                newSl = bePlus;
            }
        }

        var reason = action switch
        {
            "full" => $"ensemble {score:0}/100 — consensus exit (top evidence: {Top(votes)})",
            "partial" => $"ensemble {score:0}/100 — scale out a third (top evidence: {Top(votes)})",
            "tighten" => mfeR >= 1.0
                ? $"ensemble {score:0}/100 — MFE {mfeR:0.00}R paid, trail stop to +0.2R"
                : $"ensemble {score:0}/100 — tighten risk",
            "monitor" => $"ensemble {score:0}/100 — monitor closely",
            _ => $"ensemble {score:0}/100 — hold",
        };

        return new FxExitDecision(state.Ticket, action, score, votes, lotsToClose, newSl,
            mfeR, maeR, plR, null, reason);
    }

    /// <summary>The conflict resolver: weighted score to action. The
    /// bands from the architecture spec (0-35 hold, 35-55 monitor,
    /// 55-70 tighten, 70-85 partial, 85-100 full). Internal so tests pin
    /// the exact edges.</summary>
    internal static string Resolve(double score) => score switch
    {
        >= FullExitScore => "full",
        >= PartialExitScore => "partial",
        >= TightenScore => "tighten",
        >= MonitorScore => "monitor",
        _ => "hold",
    };

    /// <summary>The canonical engine -> weight roster. The vote builders
    /// inline the same numbers; FxExitBrainTests pins them in sync so a
    /// weight edit in one place cannot silently drift from the other.
    /// Shadow engines ride at 0 until promoted from recorded accuracy.</summary>
    public static readonly IReadOnlyDictionary<string, double> EngineWeights =
        new Dictionary<string, double>
        {
            ["structure"] = 1.6,
            ["momentum"] = 1.0,
            ["volatility"] = 1.2,
            ["time"] = 1.1,
            ["thesis"] = 1.4,
            ["drawdown"] = 2.0,
            ["counterfactual"] = 0,   // shadow: the opposite entry's verdict
            ["giveback"] = 0,         // shadow: peak give-back ratio
        };

    /// <summary>The shadow engines: computed every cycle, journaled with
    /// their reasons, weight 0 — pure observation until promoted.</summary>
    public static IReadOnlyList<FxExitVote> ShadowVotes(
        IReadOnlyList<FxBar> bars, bool isBuy, double plR, double mfeR) =>
        [
            CounterfactualVote(plR),
            GivebackVote(plR, mfeR),
        ];

    /// <summary>Counterfactual shadow (spec engine 15, v1 form): had the
    /// brain entered the OPPOSITE side at the same time, it would now be
    /// up exactly the trade's loss. Persistent counter-evidence that the
    /// thesis is losing its bet.</summary>
    private static FxExitVote CounterfactualVote(double plR) =>
        plR < 0
            ? new FxExitVote("counterfactual", Math.Min(1.0, -plR / 1.5), 0,
                $"counterfactual: the opposite entry would be {-plR:0.00}R up — the thesis is losing its bet")
            : new FxExitVote("counterfactual", 0, 0, "counterfactual: the thesis is winning; no counter-evidence");

    /// <summary>Give-back shadow: how much of the trade's peak it has
    /// returned. A large round-trip from a real peak is the classic
    /// "should have taken it" pattern; measuring it now is how the
    /// promotion data accumulates.</summary>
    private static FxExitVote GivebackVote(double plR, double mfeR) =>
        mfeR >= 0.5 && plR < mfeR * 0.4
            ? new FxExitVote("giveback", Math.Min(1.0, 1.0 - (plR / mfeR)), 0,
                $"giveback: returned {1.0 - plR / mfeR:P0} of its {mfeR:0.00}R peak")
            : new FxExitVote("giveback", 0, 0, "giveback: holding most of its peak");

    private static string Top(IReadOnlyList<FxExitVote> votes)
    {
        FxExitVote? best = null;
        foreach (var v in votes)
        {
            if (best is null || v.Exit * v.Weight > best.Exit * best.Weight)
            {
                best = v;
            }
        }
        return best is null ? "n/a" : $"{best.Engine} {best.Exit * 100:0}%";
    }

    /// <summary>Structure engine: a close through the opposing N-bar swing
    /// is a change of character against the position.</summary>
    private static FxExitVote StructureVote(IReadOnlyList<FxBar> bars, bool isBuy)
    {
        if (bars.Count < SwingLookback + 2)
        {
            return new FxExitVote("structure", 0, 1.6, "not enough bars for a swing");
        }

        var window = bars.TakeLast(SwingLookback + 1).Take(SwingLookback).ToList();
        var close = bars[^1].Close;
        if (isBuy)
        {
            var swingLow = window.Min(b => b.Low);
            var exit = close < swingLow ? 0.9 : 0.0;
            return new FxExitVote("structure", exit, 1.6,
                exit > 0
                    ? $"close {close:0.#####} broke the {SwingLookback}-bar swing low {swingLow:0.#####} — CHOCH against the long"
                    : $"holding above the {SwingLookback}-bar swing low");
        }

        var swingHigh = window.Max(b => b.High);
        var sellExit = close > swingHigh ? 0.9 : 0.0;
        return new FxExitVote("structure", sellExit, 1.6,
            sellExit > 0
                ? $"close {close:0.#####} broke the {SwingLookback}-bar swing high {swingHigh:0.#####} — CHOCH against the short"
                : $"holding below the {SwingLookback}-bar swing high");
    }

    /// <summary>Momentum engine: RSI pushed to the extreme AGAINST the
    /// position is exhaustion of the trade's own direction.</summary>
    private static FxExitVote MomentumVote(IReadOnlyList<FxBar> bars, bool isBuy)
    {
        if (bars.Count < 20)
        {
            return new FxExitVote("momentum", 0, 1.0, "not enough bars for RSI");
        }

        var closes = bars.Select(b => b.Close).ToList();
        var rsi = FxFeatures.Rsi(closes, 14);
        if (double.IsNaN(rsi))
        {
            return new FxExitVote("momentum", 0, 1.0, "RSI not ready");
        }

        var exit = isBuy
            ? (rsi >= 78 ? 0.85 : rsi >= 70 ? 0.55 : 0.0)
            : (rsi <= 22 ? 0.85 : rsi <= 30 ? 0.55 : 0.0);
        return new FxExitVote("momentum", exit, 1.0,
            exit > 0 ? $"RSI {rsi:0} stretched {(isBuy ? "up" : "down")} against the position" : $"RSI {rsi:0} not extreme");
    }

    /// <summary>Volatility engine: collapse (dead market) or shock (hostile
    /// regime) both argue for leaving.</summary>
    private static FxExitVote VolatilityVote(double atr, double atrMedian20)
    {
        if (atrMedian20 <= 0 || double.IsNaN(atr) || double.IsNaN(atrMedian20))
        {
            return new FxExitVote("volatility", 0, 1.2, "ATR baseline not ready");
        }

        if (atr < atrMedian20 * VolCollapseFraction)
        {
            return new FxExitVote("volatility", 0.6, 1.2,
                $"ATR collapsed to {atr / atrMedian20:P0} of its median — the market went to sleep");
        }
        if (atr > atrMedian20 * VolShockMultiple)
        {
            return new FxExitVote("volatility", 0.75, 1.2,
                $"ATR {atr / atrMedian20:0.0}x its median — volatility shock, protect the book");
        }
        return new FxExitVote("volatility", 0, 1.2, $"ATR healthy ({atr / atrMedian20:P0} of median)");
    }

    /// <summary>Time engine: a trade that went nowhere (or round-tripped
    /// back from its peak) is a dead trade — measured on current
    /// excursion, not the high-water mark.</summary>
    private static FxExitVote TimeVote(int barsHeld, double mfeR, double plR)
    {
        if (barsHeld < MaxStagnantBars)
        {
            return new FxExitVote("time", 0, 1.1, $"{barsHeld} bars held");
        }
        if (plR >= StagnantMfeFraction)
        {
            return new FxExitVote("time", 0, 1.1,
                $"{barsHeld} bars held, currently {plR:0.00}R (peak {mfeR:0.00}R)");
        }
        if (mfeR < StagnantMfeFraction)
        {
            return new FxExitVote("time", 0.7, 1.1,
                $"{barsHeld} bars held, MFE never passed {StagnantMfeFraction:0.#}R — the trade is dead");
        }
        return new FxExitVote("time", 0.7, 1.1,
            $"{barsHeld} bars held, peaked {mfeR:0.00}R but round-tripped below {StagnantMfeFraction:0.#}R — stale");
    }

    /// <summary>Thesis monitor: the entry's regime disappeared.</summary>
    private static FxExitVote ThesisVote(FxRegime current, FxRegime entry, bool isBuy)
    {
        if (current == entry)
        {
            return new FxExitVote("thesis", 0, 1.4, $"entry regime {entry} still holds");
        }

        var hostile = current switch
        {
            FxRegime.StandDown or FxRegime.LowLiquidity or FxRegime.HighVol => true,
            FxRegime.Range => entry is FxRegime.Trend,
            FxRegime.Trend => entry is FxRegime.Range,
            _ => false,
        };
        return new FxExitVote("thesis", hostile ? 0.8 : 0.4, 1.4,
            hostile
                ? $"entry regime {entry} → {current}: the trade's thesis is invalidated"
                : $"entry regime {entry} → {current}: thesis weakened");
    }

    /// <summary>Drawdown engine (ensemble voice). Two independent
    /// evidences, one heavyweight voice: (1) adverse excursion — the
    /// classic MAE ramp toward the emergency bar; (2) PROFIT GIVEBACK —
    /// the 2026-09-29 backtest (docs/soak/) showed +20.25R of round-trip
    /// losses the ensemble never voted on because every other engine is
    /// loss-side: structure/momentum/volatility/time all see a "healthy"
    /// trade at give-back from peak. The curve below gives the ensemble
    /// its missing profit-side voice. Weight unchanged (2.0), so the
    /// Monte-Carlo fingerprint gate is not triggered.</summary>
    private static FxExitVote DrawdownVote(double maeR, double mfeR, double plR)
    {
        // Profit-side giveback evidence (dominates the MAE ramp when both
        // are present — the round-trip is the emergency).
        if (mfeR >= 1.0 && plR <= ProfitFloorR)
        {
            return new("drawdown", 0.95, 2.0,
                $"round-trip: peaked {mfeR:0.0}R, now {plR:0.00}R — the move was given back");
        }
        if (mfeR >= 2.0 && (mfeR - plR) / mfeR >= GivebackVoteRatio)
        {
            return new("drawdown", 0.85, 2.0,
                $"give-back {(mfeR - plR) / mfeR:P0} of a {mfeR:0.0}R peak — protect what remains");
        }
        if (mfeR >= 1.5 && (mfeR - plR) / mfeR >= GivebackWatchRatio)
        {
            return new("drawdown", 0.65, 2.0,
                $"give-back {(mfeR - plR) / mfeR:P0} of a {mfeR:0.0}R peak — giveback building");
        }
        return maeR >= 1.0
            ? new("drawdown", Math.Min(1.0, 0.45 + (maeR - 1.0) * 1.375), 2.0,
                $"MAE {maeR:0.00}R at full risk — watch closely")
            : new FxExitVote("drawdown", 0, 2.0, $"MAE {maeR:0.00}R within budget");
    }

    /// <summary>True when a settled exit was a PROFIT-FLOOR SAVE: the
    /// giveback tier itself fired (the profit-floor override, or a deep
    /// giveback drawdown vote the resolver acted on). This is the evidence
    /// class the shadow ledger credits — without it, "the engine saved a
    /// winner's give-back" never lands in the promotion substrate because
    /// the classic Helped() rule only credits exits the ensemble DECLINED.
    /// Marker strings mirror DrawdownVote's reasons exactly.</summary>
    public static bool IsProfitFloorSave(FxExitDecision decision) =>
        decision.OverrideEngine == "profit-floor"
        || decision.Votes.Any(v => v.Engine == "drawdown"
            && v.Exit >= GivebackVoteRatio   // 0.85 — the deep-giveback vote
            && (v.Reason.Contains("given back") || v.Reason.Contains("give-back")));

    /// <summary>Snapshot a position's tracking state (MFE/MAE monotone up).</summary>
    public static FxPositionState UpdateState(FxPositionState state, double currentPrice, int barsHeld)
    {
        var dir = state.Side == "buy" ? 1.0 : -1.0;
        var plR = dir * (currentPrice - state.EntryPrice) / state.RiskPerLot;
        return state with
        {
            MfeR = Math.Max(state.MfeR, plR),
            MaeR = Math.Max(state.MaeR, -plR),
            BarsHeld = Math.Max(state.BarsHeld, barsHeld),
        };
    }
}
