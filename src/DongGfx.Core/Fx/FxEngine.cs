using System.Text.Json;

namespace DongGfx.Core.Fx;

/// <summary>What the engine did with a signal this cycle — journaled.</summary>
public enum FxDecisionAction { Paper, Ordered, SkippedRegime, SkippedSizing, NoSignal, CycleError }

public static class FxJson
{
    /// <summary>JSON cannot represent NaN/Infinity — any double that fails the
    /// round-trip is nulled out so journaling can never throw.</summary>
    public static double? Sanitize(double v) =>
        double.IsFinite(v) ? v : null;
}

/// <summary>The engine's decision, complete with the why — every cycle is
/// auditable from the journal alone.</summary>
public sealed record FxDecision(
    long TimeUtc,
    FxRegimeVerdict Regime,
    FxSignal? Signal,
    FxDecisionAction Action,
    double SuggestedLots,
    string Detail);

/// <summary>
/// The DON G FX forex brain. Fully pure: the caller (App timer) fetches
/// candles + quotes from the bridge and hands them in — no I/O here, no
/// threads, no sync-over-async. One call = one cycle = one journal triple
/// (FX_REGIME always, FX_SIGNAL when an alpha speaks, FX_DECISION for the
/// action taken). Paper mode stops at journaling; live mode returns an
/// ORDER decision the App executes through the sidecar ticket path, where
/// the same rails as the manual ticket apply (gate → max-lots → kill switch).
/// </summary>
public sealed class FxEngine
{
    private readonly Action<string, string, string> _journal; // category, detail, json
    private readonly List<IFxAlpha> _alphas = new();
    private readonly FxRegimeDetector _regime;
    private readonly double _lotsCap;
    private readonly double _riskFraction;
    private readonly double _atrStopMult;
    private readonly Func<double>? _equityProvider;

    public bool IsLive { get; private set; }
    public string Symbol { get; }
    public string Timeframe { get; }
    public IReadOnlyList<IFxAlpha> Alphas => _alphas;

    public FxEngine(
        string symbol,
        string timeframe,
        Action<string, string, string> journal,
        double lotsCap,
        double riskFraction = 0.02,
        double atrStopMult = 1.5,
        FxRegimeDetector? regimeDetector = null,
        Func<double>? equityProvider = null)
    {
        Symbol = symbol;
        Timeframe = timeframe;
        _journal = journal;
        _lotsCap = lotsCap;
        _riskFraction = riskFraction;
        _atrStopMult = atrStopMult;
        _regime = regimeDetector ?? new FxRegimeDetector();
        _equityProvider = equityProvider;
    }

    public void AddAlpha(IFxAlpha alpha) => _alphas.Add(alpha);

    /// <summary>Paper→live is an explicit, journaled act (a paper soak must
    /// exist first — the App layer enforces the soak window before calling).</summary>
    public void GoLive()
    {
        if (!IsLive)
        {
            IsLive = true;
            _journal("FX_MODE", $"engine LIVE on {Symbol} {Timeframe} (paper soak complete)",
                ToJson(new { Symbol, Timeframe, Live = true }));
        }
    }

    public void GoPaper()
    {
        if (IsLive)
        {
            IsLive = false;
            _journal("FX_MODE", $"engine back to PAPER on {Symbol} {Timeframe}",
                ToJson(new { Symbol, Timeframe, Live = false }));
        }
    }

    /// <summary>One pure cycle: bars + quote in, decision out. Never throws.</summary>
    public FxDecision RunOnce(DateTimeOffset utcNow, IReadOnlyList<FxBar> bars, double bid, double ask)
    {
        try
        {
            var mid = bid > 0 && ask > 0 ? (bid + ask) / 2 : bid > 0 ? bid : ask;
            var spreadPoints = mid > 0 && ask > bid ? (ask - bid) / PipSizeOf(mid) : 0;

            var verdict = _regime.Evaluate(bars, spreadPoints, utcNow);
            _journal("FX_REGIME",
                $"{Symbol} {Timeframe}: {verdict.Regime} — {verdict.Reason} (ADX {verdict.Adx:0.#}, ATR% {verdict.AtrPct:0.00}, {verdict.Session}, spread {verdict.SpreadPoints:0} pts)",
                ToJson(new { Symbol, Timeframe, Regime = verdict.Regime.ToString(), Reason = verdict.Reason,
                    Adx = FxJson.Sanitize(verdict.Adx), AtrPct = FxJson.Sanitize(verdict.AtrPct), Session = verdict.Session,
                    SpreadPoints = FxJson.Sanitize(verdict.SpreadPoints) }));

            if (verdict.Regime is FxRegime.StandDown or FxRegime.LowLiquidity)
            {
                return new FxDecision(verdict.TimeUtc, verdict, null, FxDecisionAction.SkippedRegime, 0, verdict.Reason);
            }

            FxSignal? winner = null;
            foreach (var alpha in _alphas)
            {
                if (!alpha.Regimes.Contains(verdict.Regime))
                {
                    continue;
                }

                var sig = alpha.Evaluate(bars, verdict);
                if (sig is not null && (winner is null || sig.Confidence > winner.Confidence))
                {
                    winner = sig;
                }
            }

            if (winner is null)
            {
                return new FxDecision(verdict.TimeUtc, verdict, null, FxDecisionAction.NoSignal, 0,
                    "no alpha spoke in this regime");
            }

            _journal("FX_SIGNAL",
                $"{winner.Alpha} → {winner.Direction} conf {winner.Confidence:0.00} — {winner.Reason}",
                ToJson(new { Symbol, winner.Alpha, winner.Direction, winner.Confidence, winner.Reason }));

            var lots = Size(winner, mid);
            if (lots <= 0 || lots > _lotsCap)
            {
                return new FxDecision(verdict.TimeUtc, verdict, winner, FxDecisionAction.SkippedSizing, lots,
                    $"sized {lots:0.##} lots outside cap {_lotsCap:0.##}");
            }

            if (!IsLive)
            {
                _journal("FX_DECISION",
                    $"PAPER: {winner.Direction} {lots:0.##} lots {Symbol} (paper mode — no order)",
                    ToJson(new { Action = "paper", winner.Direction, Lots = lots }));
                return new FxDecision(verdict.TimeUtc, verdict, winner, FxDecisionAction.Paper, lots, "paper mode");
            }

            _journal("FX_DECISION",
                $"ORDER: {winner.Direction} {lots:0.##} lots {Symbol} @ market",
                ToJson(new { Action = "order", winner.Direction, Lots = lots }));
            return new FxDecision(verdict.TimeUtc, verdict, winner, FxDecisionAction.Ordered, lots,
                "live order handed to the bridge ticket path (rails apply there)");
        }
        catch (Exception ex)
        {
            return new FxDecision(utcNow.ToUnixTimeSeconds(),
                new FxRegimeVerdict(FxRegime.StandDown, double.NaN, double.NaN, "n/a", 0, ex.Message, utcNow.ToUnixTimeSeconds()),
                null, FxDecisionAction.CycleError, 0, $"cycle error: {ex.Message}");
        }
    }

    /// <summary>Risk-based sizing: budget = account equity x risk fraction,
    /// risk per lot = stop distance x units per lot (100k for FX, 100 oz for
    /// gold-like prices). Fails closed at no verified equity. Rounds DOWN to
    /// the 0.01 lot step; 0 = don't trade.</summary>
    public double Size(FxSignal signal, double midPrice)
    {
        if (double.IsNaN(signal.StopDistanceHint) || signal.StopDistanceHint <= 0 || midPrice <= 0)
        {
            return 0;
        }

        // Risk budget in account currency: equity x risk fraction. Fails
        // closed - no verified equity (bridge down, first fetch pending)
        // means the engine sizes nothing rather than sizing garbage.
        var equity = _equityProvider?.Invoke() ?? 0;
        if (double.IsNaN(equity) || equity <= 0)
        {
            return 0;
        }

        // Units per standard lot: FX = 100k units; gold-like (price > 500)
        // = 100 oz. Stop hints are in price units, so risk per lot must be
        // priced per those units.
        var unitsPerLot = midPrice > 500 ? 100.0 : 100_000.0;
        var riskPerLot = signal.StopDistanceHint * unitsPerLot;
        if (riskPerLot <= 0) return 0;

        var lots = Math.Floor(equity * _riskFraction / riskPerLot * 100) / 100;
        return lots < 0.01 ? 0 : Math.Min(lots, _lotsCap);
    }

    private static double PipSizeOf(double price) => price switch
    {
        > 500 => 10,      // XAU-like
        > 20 => 0.01,     // index-ish
        _ => 0.0001,      // EURUSD-like
    };

    private static string ToJson(object value) => JsonSerializer.Serialize(value);
}
