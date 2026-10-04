using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>Knobs for a training run. Defaults model the operator's brief:
/// start a $10 account and let the brain try to grow it.</summary>
public sealed record FxTrainingConfig(
    /// <summary>Opening balance in account currency.</summary>
    double StartBalance = 10.0,
    /// <summary>Fraction of live equity risked on each trade (fixed-fractional).</summary>
    double RiskFraction = 0.02,
    /// <summary>The venue's minimum-lot risk in dollars. A small account cannot
    /// size below this, so a $10 book risks this floor even when 2% of equity
    /// is smaller — the honest reason small accounts bleed faster.</summary>
    double MinLotRiskUsd = 0.05,
    /// <summary>Bars of history before the first signal may fire (indicator warm-up).</summary>
    int WarmupBars = 60,
    /// <summary>Rolling window each alpha sees at a bar.</summary>
    int WindowBars = 120,
    /// <summary>Max bars a position is held before a time exit.</summary>
    int HoldBars = 5,
    /// <summary>Stop distance as a multiple of the alpha's ATR stop hint.</summary>
    double StopAtrMult = 1.0,
    /// <summary>Take-profit as a multiple of the stop distance.</summary>
    double RewardRisk = 2.0,
    /// <summary>Hard cap on simulated trades (keeps a huge tape bounded).</summary>
    int MaxTrades = 20000,
    /// <summary>Spread charged per trade in price units (mid-to-mid replay is
    /// otherwise free money); scaled off the symbol's own price when 0.</summary>
    double SpreadPrice = 0.0,
    /// <summary>Hard ceiling on the dollars risked on one trade (0 = uncapped).
    /// Without it a long tape compounds a $10 book into absurd peaks and
    /// collapses back — the cap keeps the simulated equity curve at a scale
    /// a real account could actually size, so the reported drawdown means
    /// something instead of exceeding the ending balance by orders of
    /// magnitude.</summary>
    double MaxRiskUsd = 0.0,
    /// <summary>Skip signals whose confidence falls below this floor (0 = take
    /// every speaker). Confidence is how the roster already ranks a bar; the
    /// floor just refuses to pay spread cost on bars nobody on the roster
    /// believes in — the honest way to trade less but better.</summary>
    double MinConfidence = 0.0,
    /// <summary>Floor the stop at this multiple of the charged spread
    /// (0 = off). Mirrors the live engine's structural stop floor: a stop
    /// inside the venue's spread is a cost trap — on fine-grained tape the
    /// ATR stop can price the spread at 1R+ per trade — so the stop can
    /// never be tighter than spread × this multiple (capped at half the
    /// price, as production does).</summary>
    double MinStopSpreadMult = 0.0,
    /// <summary>Peak-to-equity give-back depth, as a fraction of the
    /// high-water mark, that trips the equity drawdown brake (0 = off,
    /// as is any value ≥ 1). When the book is this far below its peak the
    /// simulator stops opening NEW entries for <see cref="DrawdownBrakeBars"/>
    /// bars — a stand-down, not a halt: positions settle, the pause ends,
    /// and the tape keeps counting toward a full training run. This is the
    /// drawdown gate the UNSTABLE verdict demands: a book that ballooned and
    /// is bleeding must be allowed to sit out, not keep paying spread on
    /// every bar of a losing streak.</summary>
    double DrawdownBrakePct = 0.0,
    /// <summary>How many bars no new entries open once the brake trips
    /// (ignored while <see cref="DrawdownBrakePct"/> is off). Each settle
    /// that is still this deep re-trips the brake, so a persistently bleeding
    /// book trades at most one round-trip per cooldown window.</summary>
    int DrawdownBrakeBars = 60);

/// <summary>One simulated round-trip. R is reward divided by the risked
/// stop distance; PnlUsd is what the small account actually felt.</summary>
public sealed record FxTrainingTrade(
    string Symbol,
    string Alpha,
    string Regime,
    FxDirection Direction,
    long EntryTimeUtc,
    long ExitTimeUtc,
    double EntryPrice,
    double ExitPrice,
    double RMultiple,
    double PnlUsd,
    string ExitReason);

/// <summary>Per-family rollup so the memory can rank what works where.</summary>
public sealed record FxFamilyTrainingStat(
    string Alpha,
    int Trades,
    int Wins,
    double WinRate,
    double TotalR,
    double TotalPnlUsd);

/// <summary>The full result of one symbol's training run.</summary>
public sealed record FxTrainingReport(
    string Symbol,
    DateTimeOffset RanAt,
    int Bars,
    double StartBalance,
    double EndBalance,
    int TradeCount,
    int Wins,
    double WinRate,
    double MaxDrawdownUsd,
    double ProfitFactor,
    bool AccountBlown,
    IReadOnlyList<FxTrainingTrade> Trades,
    IReadOnlyList<FxFamilyTrainingStat> FamilyStats,
    string Verdict)
{
    public double NetUsd => EndBalance - StartBalance;
}

/// <summary>
/// Offline training simulator: replays the REAL production alpha roster
/// (<see cref="FxFamilies.All"/>) through the REAL regime detector over
/// archived bars, and sizes every trade off a live equity curve so a small
/// account compounds (or bleeds) the way it actually would.
///
/// This is deliberately NOT a trade path. It holds no bridge, journal,
/// supervisor or order reference; it is a pure function of (bars, config) —
/// identical input always yields an identical report. Its output is EVIDENCE:
/// the report lands in a markdown file and the per-family record lands in
/// <see cref="FxBrainMemory"/>, where a human reads it. Nothing here can
/// place, size, or schedule a live order, and the paper soak still gates
/// go-live exactly as before.
/// </summary>
public static class FxTrainingSimulator
{
    /// <summary>Run one symbol. Never throws on thin data — a short tape
    /// yields an empty, clearly-labeled report rather than an error.</summary>
    public static FxTrainingReport Run(
        string symbol,
        IReadOnlyList<FxBar> bars,
        FxTrainingConfig? config = null,
        IReadOnlyList<IFxAlpha>? families = null,
        double spreadPoints = 10.0,
        double spreadMaxPoints = 60.0)
    {
        var cfg = config ?? new FxTrainingConfig();
        var roster = families ?? FxFamilies.All();
        var now = DateTimeOffset.UtcNow;

        if (bars.Count < cfg.WarmupBars + cfg.HoldBars + 5)
        {
            return Empty(symbol, bars.Count, cfg, now,
                "not enough bars to train — record more soak time");
        }

        // The venue's own spread is the baseline, not a spike — a crypto
        // venue quotes thousands of points, which the default 60-point
        // ceiling would otherwise veto on every bar.
        var detector = new FxRegimeDetector(spreadMaxPoints: spreadMaxPoints);
        var trades = new List<FxTrainingTrade>();
        var equity = cfg.StartBalance;
        var peak = equity;
        var maxDd = 0.0;
        var grossWin = 0.0;
        var grossLoss = 0.0;
        var accountBlown = false;
        var window = new List<FxBar>(cfg.WindowBars);
        // Bar index before which the equity drawdown brake keeps new entries
        // closed (−1 = not engaged). The window keeps sliding while braked so
        // the indicators stay warm and the resume is seamless.
        var brakeUntil = -1;

        for (var i = cfg.WarmupBars; i < bars.Count - 1 && trades.Count < cfg.MaxTrades; i++)
        {
            if (equity <= 0)
            {
                accountBlown = true;
                break;
            }

            // Rolling window that always ends at bar i. The previous
            // bars.Skip(...).Take(...) re-walked the whole tape on every
            // bar — quadratic on a 100k+ bar deep tape. Equivalent output:
            // partial windows grow until WindowBars, then slide by one.
            if (i == cfg.WarmupBars)
            {
                var from = Math.Max(0, i - cfg.WindowBars + 1);
                for (var k = from; k <= i; k++)
                {
                    window.Add(bars[k]);
                }
            }
            else
            {
                if (window.Count >= cfg.WindowBars)
                {
                    window.RemoveAt(0);
                }

                window.Add(bars[i]);
            }

            if (window.Count < 31)
            {
                continue;
            }

            if (i < brakeUntil)
            {
                // The equity brake is engaged — sit this bar out. Only
                // entries are gated; there is never a position in flight
                // (each trade settles within its hold window), so a flat
                // pause is all there is to enforce.
                continue;
            }

            var regime = detector.Evaluate(window, spreadPoints,
                DateTimeOffset.FromUnixTimeSeconds(bars[i].Time));
            if (regime.Regime is FxRegime.StandDown or FxRegime.LowLiquidity)
            {
                continue;
            }

            // The highest-confidence speaker in the regime owns the bar —
            // the same "one voice" discipline the live engine applies.
            FxSignal? best = null;
            foreach (var alpha in roster)
            {
                var signal = alpha.Evaluate(window, regime);
                if (signal is null)
                {
                    continue;
                }

                if (best is null || signal.Confidence > best.Confidence)
                {
                    best = signal;
                }
            }

            if (best is null)
            {
                continue;
            }

            if (best.Confidence < cfg.MinConfidence)
            {
                // Nobody on the roster is confident enough — pass on the
                // bar rather than pay the spread for a coin flip.
                continue;
            }

            var entry = bars[i].Close;
            if (entry <= 0)
            {
                continue;
            }

            var spread = cfg.SpreadPrice > 0 ? cfg.SpreadPrice : entry * 2e-5;
            var stopDistance = StopDistance(best, entry, cfg, spread);
            if (stopDistance <= 0)
            {
                continue;
            }
            var direction = best.Direction == FxDirection.Buy ? 1 : -1;
            var stop = entry - direction * stopDistance;
            var target = entry + direction * stopDistance * cfg.RewardRisk;

            var (exitPrice, exitTime, reason) = Settle(
                bars, i, direction, stop, target, cfg.HoldBars);

            // Cost: pay the spread once, on entry, in R terms.
            var move = direction * (exitPrice - entry) - spread;
            var r = move / stopDistance;
            r = Clamp(r, -1.0, cfg.RewardRisk + 1.0);

            var riskUsd = Math.Max(cfg.RiskFraction * equity, cfg.MinLotRiskUsd);
            if (cfg.MaxRiskUsd > 0)
            {
                riskUsd = Math.Min(riskUsd, cfg.MaxRiskUsd);
            }

            var pnl = r * riskUsd;
            equity += pnl;
            if (equity < 0)
            {
                equity = 0;
            }

            peak = Math.Max(peak, equity);
            maxDd = Math.Min(maxDd, equity - peak);

            // Trip (or re-trip) the brake when this settle leaves the book
            // at or below the configured depth from its high-water mark.
            // Re-tripping on every deep settle means a book that is still
            // bleeding after the cooldown earns at most one more attempt
            // per window instead of reopening into the same slide.
            if (cfg.DrawdownBrakePct > 0 && cfg.DrawdownBrakePct < 1 &&
                peak > 0 && (peak - equity) / peak >= cfg.DrawdownBrakePct)
            {
                brakeUntil = i + cfg.DrawdownBrakeBars;
            }

            if (pnl > 0)
            {
                grossWin += pnl;
            }
            else
            {
                grossLoss += -pnl;
            }

            trades.Add(new FxTrainingTrade(
                symbol, best.Alpha, regime.Regime.ToString(), best.Direction,
                bars[i].Time, exitTime, entry, exitPrice, r, pnl, reason));
        }

        var wins = trades.Count(t => t.PnlUsd > 0);
        var winRate = trades.Count > 0 ? (double)wins / trades.Count : 0;
        var profitFactor = grossLoss > 0 ? grossWin / grossLoss
            : grossWin > 0 ? double.PositiveInfinity : 0;

        var stats = RollUp(trades);
        var verdict = BuildVerdict(cfg, equity, trades.Count, winRate, maxDd, peak, accountBlown);

        return new FxTrainingReport(
            symbol, now, bars.Count, cfg.StartBalance, Math.Round(equity, 2),
            trades.Count, wins, winRate, Math.Round(maxDd, 2),
            double.IsInfinity(profitFactor) ? 999.0 : Math.Round(profitFactor, 2),
            accountBlown, trades, stats, verdict);
    }

    /// <summary>Replay every symbol and keep them separate — a $10 account
    /// is one book, but the lesson is per-symbol, so reports stay per-symbol
    /// and the memory keys on the symbol.</summary>
    public static IReadOnlyList<FxTrainingReport> RunAll(
        IReadOnlyDictionary<string, IReadOnlyList<FxBar>> barsBySymbol,
        FxTrainingConfig? config = null,
        IReadOnlyList<IFxAlpha>? families = null)
    {
        var results = new List<FxTrainingReport>();
        foreach (var (symbol, bars) in barsBySymbol.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(Run(symbol, bars, config, families));
        }

        return results;
    }

    private static (double Price, long Time, string Reason) Settle(
        IReadOnlyList<FxBar> bars, int entryIndex, int direction,
        double stop, double target, int holdBars)
    {
        var last = Math.Min(bars.Count - 1, entryIndex + holdBars);
        for (var j = entryIndex + 1; j <= last; j++)
        {
            var bar = bars[j];
            if (direction > 0)
            {
                // Conservative: when a bar straddles both levels, assume the
                // stop filled first — never book a win the tape cannot prove.
                if (bar.Low <= stop)
                {
                    return (stop, bar.Time, "stop");
                }

                if (bar.High >= target)
                {
                    return (target, bar.Time, "target");
                }
            }
            else
            {
                if (bar.High >= stop)
                {
                    return (stop, bar.Time, "stop");
                }

                if (bar.Low <= target)
                {
                    return (target, bar.Time, "target");
                }
            }
        }

        return (bars[last].Close, bars[last].Time, "time");
    }

    private static double StopDistance(
        FxSignal signal, double entry, FxTrainingConfig cfg, double spread)
    {
        var hint = signal.StopDistanceHint;
        if (double.IsNaN(hint) || hint <= 0)
        {
            // No ATR hint (degenerate tape): fall back to a tiny fraction of
            // price so the trade is still measurable, never sized on zero.
            hint = entry * 1e-4;
        }

        var stop = Math.Max(hint * cfg.StopAtrMult, entry * 1e-6);
        if (cfg.MinStopSpreadMult > 0 && spread > 0)
        {
            // The structural floor, mirroring the live engine's stops_level
            // band: on an M1 segment the ATR stop can price the venue spread
            // at 1R+ per trade, so the stop can never be tighter than
            // spread × mult — capped at half the price, as production is.
            stop = Math.Max(stop, Math.Min(spread * cfg.MinStopSpreadMult, 0.5 * entry));
        }

        return stop;
    }

    private static IReadOnlyList<FxFamilyTrainingStat> RollUp(IReadOnlyList<FxTrainingTrade> trades) =>
        trades.GroupBy(t => t.Alpha, StringComparer.Ordinal)
            .Select(g => new FxFamilyTrainingStat(
                g.Key,
                g.Count(),
                g.Count(t => t.PnlUsd > 0),
                g.Count() > 0 ? (double)g.Count(t => t.PnlUsd > 0) / g.Count() : 0,
                Math.Round(g.Sum(t => t.RMultiple), 3),
                Math.Round(g.Sum(t => t.PnlUsd), 4)))
            .OrderByDescending(s => s.TotalR)
            .ToList();

    private static FxTrainingReport Empty(
        string symbol, int bars, FxTrainingConfig cfg, DateTimeOffset now, string why) =>
        new(symbol, now, bars, cfg.StartBalance, cfg.StartBalance, 0, 0, 0, 0, 0, false,
            Array.Empty<FxTrainingTrade>(), Array.Empty<FxFamilyTrainingStat>(), why);

    /// <summary>Peak-to-final give-back above which a growing book is called
    /// UNSTABLE instead of GREW: a $10 account that balloons into the thousands
    /// and finishes far below its peak has not demonstrated growth, it
    /// demonstrated variance. Measured peak-to-END (not peak-to-trough) so a
    /// mid-run wobble that recovered does not taint a clean grower, while a
    /// collapse that persists to the final balance cannot hide. Half the peak
    /// is the classic “it halved” line.</summary>
    private const double UnstableRelDd = 0.5;

    private static string BuildVerdict(
        FxTrainingConfig cfg, double end, int trades, double winRate,
        double maxDd, double peak, bool blown)
    {
        if (blown)
        {
            return $"BLOWN — the ${cfg.StartBalance:0.##} account hit zero after {trades} trades; " +
                   "the min-lot floor outran the risk fraction (record more/cleaner tape)";
        }

        if (trades < 10)
        {
            return $"inconclusive — only {trades} trades; not enough tape to judge growth";
        }

        var net = end - cfg.StartBalance;
        var pct = cfg.StartBalance > 0 ? net / cfg.StartBalance : 0;
        var giveback = peak > 0 ? (peak - end) / peak : 0;
        if (net > 0 && giveback >= UnstableRelDd)
        {
            return $"UNSTABLE — ${cfg.StartBalance:0.##} → ${end:0.00} ({pct:+0%;-0%}) over {trades} trades, " +
                   $"win {winRate:P0}, but finished {giveback:P0} below its ${peak:0.##} peak. " +
                   "Ballooning and not recovering is variance, not growth — a human must not " +
                   "port this without a drawdown gate. Evidence only.";
        }

        var shape = net > 0 ? "GREW" : net < 0 ? "bled" : "flat";
        return $"{shape} — ${cfg.StartBalance:0.##} → ${end:0.##} ({pct:+0%;-0%}) over {trades} trades, " +
               $"win {winRate:P0}, worst drawdown ${Math.Abs(maxDd):0.##}. " +
               "Evidence only: a human ports anything worth keeping.";
    }

    private static double Clamp(double v, double lo, double hi) =>
        v < lo ? lo : v > hi ? hi : v;
}
