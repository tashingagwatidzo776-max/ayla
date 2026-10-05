namespace DongGfx.Core.Fx;

/// <summary>
/// The canonical rule-family roster — the taxonomy's 20 tradable families
/// in one place. The brain's engine, the scorecard, and the genetic lab all
/// draw from <see cref="All"/> so "what the brain thinks with" is a single,
/// auditable list.
///
/// Composition:
/// - 6 families wired since the first brain (FxMomentum/FxMeanReversion);
/// - 2 that existed in code but were never wired (VolatilityBreakout,
///   OuHalfLife);
/// - 12 new small alphas composed from the existing primitives
///   (FxFeatures, FxIndicators, KalmanSlope) — no new math, no new state.
///
/// MultiTimeframe is deliberately NOT in the roster: it needs a second
/// bar feed per symbol, which the single-stream cycle does not carry (it
/// remains available for lab experiments). Every alpha is pure: bars in,
/// signal or pass out — the engine picks the highest-confidence speaker
/// per regime, so twenty voices widen the vote without changing the risk
/// model.
/// </summary>
public static class FxFamilies
{
    /// <summary>The roster, in taxonomy order (momentum → mean-reversion
    /// → advanced). Fresh instances every call — alphas are stateless.</summary>
    public static IReadOnlyList<IFxAlpha> All() => new IFxAlpha[]
    {
        // -- wired since the first brain --
        new FxMomentum.EmaCross(),
        new FxMomentum.DonchianBreakout(),
        new FxMomentum.Roc(),
        new FxMeanReversion.ZScore(),
        new FxMeanReversion.BollingerReversion(),
        new FxMeanReversion.VwapReversion(),

        // -- existed, never wired --
        new FxMomentum.VolatilityBreakout(),
        new FxMeanReversion.OuHalfLife(),

        // -- new this iteration --
        new MacdCross(),
        new Rsi2Reversion(),
        new KeltnerBreakout(),
        new KalmanTrend(),
        new AdxPullback(),
        new DonchianPullback(),
        new EmaSlopeAtr(),
        new BollingerSqueeze(),
        new OpenRangeBreakout(),
        new VwapTrend(),
        new RsiMomentum(),
        new HurstTrend(),
    };

    /// <summary>The quiet-FX extension roster: three voices tuned for the
    /// quiet majors (EURUSD/GBPUSD/USDJPY/USDCAD/USDCHF/AUDUSD/NZDUSD) —
    /// low ATR%, tight spreads, session-shaped tape where overnight
    /// structure, band extremes and slow drift carry the edge instead of
    /// explosive momentum. TRAINING-ONLY: these are deliberately NOT in
    /// <see cref="All"/> — production parity holds until FxTrain's sweep
    /// says a variant earns them (roster modes 4/5 add them per variant).</summary>
    public static IReadOnlyList<IFxAlpha> QuietFx() => new IFxAlpha[]
    {
        new AsiaBreak(),
        new BandFade(),
        new RangeDrift(),
    };

    /// <summary>MACD histogram flip: 12/26 EMA spread crossing zero in the
    /// direction of the latest bar.</summary>
    public sealed class MacdCross(int fast = 12, int slow = 26) : IFxAlpha
    {
        public string Name => $"macd({fast}/{slow})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < slow + 5) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var f = FxFeatures.EmaSeries(closes, fast);
            var s = FxFeatures.EmaSeries(closes, slow);
            if (f[^1] == 0 || s[^1] == 0 || f[^2] == 0 || s[^2] == 0) return null;
            if (double.IsNaN(f[^1]) || double.IsNaN(s[^1])) return null;
            var histNow = f[^1] - s[^1];
            var histPrev = f[^2] - s[^2];
            var atr = FxFeatures.Atr(bars, 14);
            if (histPrev <= 0 && histNow > 0)
            {
                return new FxSignal(Name, FxDirection.Buy,
                    Math.Min(0.85, 0.55 + Math.Abs(histNow) / Math.Max(atr, 1e-9) / 2),
                    atr, $"MACD histogram flipped positive ({histNow:+0.00000;-0.00000})", regime.TimeUtc);
            }
            if (histPrev >= 0 && histNow < 0)
            {
                return new FxSignal(Name, FxDirection.Sell,
                    Math.Min(0.85, 0.55 + Math.Abs(histNow) / Math.Max(atr, 1e-9) / 2),
                    atr, $"MACD histogram flipped negative ({histNow:+0.00000;-0.00000})", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>RSI(2) extreme reversion: the classic short-horizon
    /// overbought/oversold snap in ranging tape.</summary>
    public sealed class Rsi2Reversion(int entry = 10) : IFxAlpha
    {
        public string Name => $"rsi2-rev({entry})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < 16) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var rsi = FxFeatures.Rsi(closes, 2);
            if (double.IsNaN(rsi)) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (rsi <= entry)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.6, atr,
                    $"RSI(2) {rsi:0} washed out below {entry}", regime.TimeUtc);
            }
            if (rsi >= 100 - entry)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.6, atr,
                    $"RSI(2) {rsi:0} stretched above {100 - entry}", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Keltner channel breakout: close beyond EMA(20) ± 2·ATR.</summary>
    public sealed class KeltnerBreakout(int period = 20, double mult = 2.0) : IFxAlpha
    {
        public string Name => $"keltner({period}x{mult:0.#})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend, FxRegime.HighVol];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 5) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var ema = FxFeatures.EmaSeries(closes, period);
            if (ema[^1] == 0 || double.IsNaN(ema[^1])) return null;
            var atr = FxFeatures.Atr(bars, period);
            if (double.IsNaN(atr) || atr <= 0) return null;
            var close = closes[^1];
            if (close > ema[^1] + mult * atr)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.7, atr,
                    $"close {close:0.#####} above Keltner upper {ema[^1] + mult * atr:0.#####}", regime.TimeUtc);
            }
            if (close < ema[^1] - mult * atr)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.7, atr,
                    $"close {close:0.#####} below Keltner lower {ema[^1] - mult * atr:0.#####}", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Kalman posterior slope: fires when the filter's drift
    /// estimate clears both its own uncertainty and an ATR-scale sanity
    /// floor — the taxonomy's Bayesian family, made tradeable.</summary>
    public sealed class KalmanTrend(double minAtrFrac = 0.05) : IFxAlpha
    {
        public string Name => "kalman-trend";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < 40) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0) return null;
            var kf = new KalmanSlope();
            foreach (var b in bars)
            {
                kf.Update(b.Close);
            }

            if (Math.Abs(kf.Drift) < 2 * kf.DriftUncertainty
                || Math.Abs(kf.Drift) < minAtrFrac * atr)
            {
                return null;
            }

            return new FxSignal(Name, kf.Drift > 0 ? FxDirection.Buy : FxDirection.Sell,
                Math.Min(0.8, 0.55 + Math.Abs(kf.Drift) / Math.Max(atr, 1e-9)),
                atr, $"Kalman drift {kf.Drift:+0.#####;-0.#####}/bar ({kf.DriftUncertainty:0.#####} ±)",
                regime.TimeUtc);
        }
    }

    /// <summary>ADX-confirmed pullback: strong trend (+DI/−DI dominance)
    /// with price back at the EMA mean — buy strength on a dip, not a chase.</summary>
    public sealed class AdxPullback(int period = 14, double adxMin = 25) : IFxAlpha
    {
        public string Name => $"adx-pullback({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period * 3) return null;
            var (adx, plusDi, minusDi) = FxFeatures.Adx(bars, period);
            if (double.IsNaN(adx) || adx < adxMin) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var ema = FxFeatures.EmaSeries(closes, 21);
            if (ema[^1] == 0 || double.IsNaN(ema[^1])) return null;
            var atr = FxFeatures.Atr(bars, period);
            if (double.IsNaN(atr) || atr <= 0) return null;
            var close = closes[^1];
            var dist = Math.Abs(close - ema[^1]);
            if (dist > 0.35 * atr) return null;   // not at the mean — no pullback
            if (plusDi > minusDi && close <= ema[^1] + 0.1 * atr)
            {
                return new FxSignal(Name, FxDirection.Buy, Math.Min(0.8, 0.55 + adx / 100),
                    atr, $"ADX {adx:0} up-trend pullback at EMA21 (DI+ {plusDi:0} vs DI- {minusDi:0})",
                    regime.TimeUtc);
            }
            if (minusDi > plusDi && close >= ema[^1] - 0.1 * atr)
            {
                return new FxSignal(Name, FxDirection.Sell, Math.Min(0.8, 0.55 + adx / 100),
                    atr, $"ADX {adx:0} down-trend pullback at EMA21 (DI- {minusDi:0} vs DI+ {plusDi:0})",
                    regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Donchian pullback resume: trend intact (EMA21 rising or
    /// falling), price revisits the mean, then reclaims the prior bar's
    /// extreme — the dip that resumes.</summary>
    public sealed class DonchianPullback(int meanPeriod = 21) : IFxAlpha
    {
        public string Name => $"donchian-pullback({meanPeriod})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < meanPeriod + 10) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var ema = FxFeatures.EmaSeries(closes, meanPeriod);
            if (ema[^1] == 0 || ema[^4] == 0 || double.IsNaN(ema[^1])) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0) return null;

            var uptrend = ema[^1] > ema[^4];
            var last3 = bars.TakeLast(3).ToList();
            var touchedMean = last3.Any(b => b.Low <= ema[^1] + 0.1 * atr)
                || last3.Any(b => b.High >= ema[^1] - 0.1 * atr);
            var reclaimed = bars[^1].Close > bars[^2].High;
            var broke = bars[^1].Close < bars[^2].Low;
            if (uptrend && touchedMean && reclaimed)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.6, atr,
                    "uptrend pullback reclaimed the prior bar's high", regime.TimeUtc);
            }
            if (!uptrend && touchedMean && broke)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.6, atr,
                    "downtrend pullback broke the prior bar's low", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>EMA slope with an ATR significance floor: the plainest
    /// trend filter — is the mean itself moving, in price terms?</summary>
    public sealed class EmaSlopeAtr(int period = 21, double minFrac = 0.3) : IFxAlpha
    {
        public string Name => $"ema-slope({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 10) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var ema = FxFeatures.EmaSeries(closes, period);
            if (ema[^1] == 0 || ema[^4] == 0 || double.IsNaN(ema[^1])) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0) return null;
            var slope = ema[^1] - ema[^4];
            if (Math.Abs(slope) < minFrac * atr) return null;
            return new FxSignal(Name, slope > 0 ? FxDirection.Buy : FxDirection.Sell,
                Math.Min(0.75, 0.5 + Math.Abs(slope) / Math.Max(atr, 1e-9) / 2),
                atr, $"EMA{period} slope {slope:+0.#####;-0.#####} over 3 bars (ATR {atr:0.#####})",
                regime.TimeUtc);
        }
    }

    /// <summary>Bollinger squeeze ignition: bandwidth in its historical
    /// bottom quintile while the close breaks the short range — fire while
    /// the coil is still coiled, unlike vol-breakout's expansion trigger.</summary>
    public sealed class BollingerSqueeze(int period = 20, int lookback = 60) : IFxAlpha
    {
        public string Name => $"bb-squeeze({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range, FxRegime.HighVol];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < lookback + period) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var (_, _, _, _, bwNow) = FxFeatures.Bollinger(closes, period);
            if (double.IsNaN(bwNow)) return null;
            var hist = new List<double>();
            for (var end = closes.Count - lookback; end < closes.Count - 2; end++)
            {
                var (_, _, _, _, bw) = FxFeatures.Bollinger(closes.Take(end).ToList(), period);
                if (!double.IsNaN(bw)) hist.Add(bw);
            }
            if (hist.Count < 10) return null;
            hist.Sort();
            var p20 = hist[(int)(0.2 * (hist.Count - 1))];
            if (bwNow > p20) return null;   // not compressed — no squeeze edge

            var hi = bars.TakeLast(11).Take(10).Max(b => b.High);
            var lo = bars.TakeLast(11).Take(10).Min(b => b.Low);
            var close = closes[^1];
            var atr = FxFeatures.Atr(bars, 14);
            if (close > hi)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.65, atr,
                    $"squeeze ignition up (bw {bwNow:0.0000} ≤ p20 {p20:0.0000}, 10-bar high broke)", regime.TimeUtc);
            }
            if (close < lo)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.65, atr,
                    $"squeeze ignition down (bw {bwNow:0.0000} ≤ p20 {p20:0.0000}, 10-bar low broke)", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Opening-range breakout, session-agnostic: the first block
    /// of the visible window sets a range and the close escapes it.</summary>
    public sealed class OpenRangeBreakout(int rangeBars = 30, int recentExclude = 3) : IFxAlpha
    {
        public string Name => $"open-range({rangeBars})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend, FxRegime.HighVol];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < rangeBars + recentExclude + 2) return null;
            var window = bars.Skip(bars.Count - rangeBars - recentExclude)
                .Take(rangeBars).ToList();
            var hi = window.Max(b => b.High);
            var lo = window.Min(b => b.Low);
            var close = bars[^1].Close;
            var atr = FxFeatures.Atr(bars, 14);
            if (close > hi)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.7, atr,
                    $"close broke the {rangeBars}-bar opening range high {hi:0.#####}", regime.TimeUtc);
            }
            if (close < lo)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.7, atr,
                    $"close broke the {rangeBars}-bar opening range low {lo:0.#####}", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>VWAP trend ride: price and VWAP(30) agree on direction —
    /// the institutional reference line as a trend filter, not a fade.</summary>
    public sealed class VwapTrend(int period = 30) : IFxAlpha
    {
        public string Name => $"vwap-trend({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 10) return null;
            var vwapNow = FxFeatures.Vwap(bars, period);
            var vwapPrev = FxFeatures.Vwap(bars.Take(bars.Count - 5).ToList(), period);
            if (double.IsNaN(vwapNow) || double.IsNaN(vwapPrev) || vwapNow <= 0) return null;
            var close = bars[^1].Close;
            var atr = FxFeatures.Atr(bars, 14);
            if (close > vwapNow && vwapNow > vwapPrev)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.6, atr,
                    $"price and VWAP both rising ({vwapNow:0.#####} vs {vwapPrev:0.#####})", regime.TimeUtc);
            }
            if (close < vwapNow && vwapNow < vwapPrev)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.6, atr,
                    $"price and VWAP both falling ({vwapNow:0.#####} vs {vwapPrev:0.#####})", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>RSI momentum regime: crosses of the 55/45 momentum lines —
    /// a slower companion to ema-cross that confirms rather than leads.</summary>
    public sealed class RsiMomentum(int period = 14) : IFxAlpha
    {
        public string Name => $"rsi-mom({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 5) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var now = FxFeatures.Rsi(closes, period);
            var prev = FxFeatures.Rsi(closes.Take(closes.Count - 1).ToList(), period);
            if (double.IsNaN(now) || double.IsNaN(prev)) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (prev <= 55 && now > 55)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.6, atr,
                    $"RSI({period}) crossed up through 55 ({prev:0} → {now:0})", regime.TimeUtc);
            }
            if (prev >= 45 && now < 45)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.6, atr,
                    $"RSI({period}) crossed down through 45 ({prev:0} → {now:0})", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Asia-range breakout: the quiet majors' signature session
    /// shape — a contained overnight (asia 00–07 UTC) range, then London
    /// (or the NY overlap/drive) closes outside it. Requires a fresh
    /// drive session, a real asia block in the tape, and a range quiet
    /// enough (≤ maxRangeAtr × ATR) that the break is structure, not noise.</summary>
    public sealed class AsiaBreak(int minAsiaBars = 4, double maxRangeAtr = 4.0) : IFxAlpha
    {
        public string Name => $"asia-break({minAsiaBars})";
        public FxRegime[] Regimes { get; } =
            [FxRegime.Range, FxRegime.Trend, FxRegime.HighVol];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < minAsiaBars + 5) return null;
            static (string Session, DateOnly Date) Stamp(FxBar b)
            {
                var utc = DateTimeOffset.FromUnixTimeSeconds(b.Time).UtcDateTime;
                return (FxRegimeDetector.SessionOf(DateTimeOffset.FromUnixTimeSeconds(b.Time)),
                    DateOnly.FromDateTime(utc));
            }

            var last = Stamp(bars[^1]);
            if (last.Session is not ("london" or "ldn-ny" or "new-york"))
            {
                return null;   // still asia, or the thin late tape — no drive yet
            }

            // Walk back over today's drive (everything non-asia since the
            // overnight block), then over the contiguous same-day asia
            // block that precedes it. A drive that reaches back past the
            // day boundary has no overnight range to break — stand down.
            var i = bars.Count - 1;
            while (i >= 0)
            {
                var s = Stamp(bars[i]);
                if (s.Date != last.Date || s.Session == "asia") break;
                i--;
            }

            var driveStart = i + 1;
            while (i >= 0)
            {
                var s = Stamp(bars[i]);
                if (s.Date != last.Date || s.Session != "asia") break;
                i--;
            }

            var asiaStart = i + 1;
            var asiaBars = driveStart - asiaStart;
            if (asiaBars < minAsiaBars)
            {
                return null;   // no (or truncated) overnight range to break
            }

            var hi = double.NegativeInfinity;
            var lo = double.PositiveInfinity;
            for (var k = asiaStart; k < driveStart; k++)
            {
                hi = Math.Max(hi, bars[k].High);
                lo = Math.Min(lo, bars[k].Low);
            }

            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0 || hi <= lo) return null;
            if (hi - lo > maxRangeAtr * atr)
            {
                return null;   // overnight range too wide — that's trend, not structure
            }

            var close = bars[^1].Close;
            if (close > hi)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.7, atr,
                    $"close {close:0.#####} broke the {asiaBars}-bar asia range high {hi:0.#####}",
                    regime.TimeUtc);
            }

            if (close < lo)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.7, atr,
                    $"close {close:0.#####} broke the {asiaBars}-bar asia range low {lo:0.#####}",
                    regime.TimeUtc);
            }

            return null;
        }
    }

    /// <summary>Band-fade: close pinned at a decade extreme (≥90% / ≤10%)
    /// of a CONTAINED period-bar range (≤ maxRangeAtr × ATR). The
    /// containment gate is the quiet-FX half — a close at the top of a
    /// trend is a breakout (donchian owns that), a close at the top of a
    /// coil is a fade.</summary>
    public sealed class BandFade(int period = 50, double maxRangeAtr = 10.0) : IFxAlpha
    {
        public string Name => $"band-fade({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 5) return null;
            var hi = double.NegativeInfinity;
            var lo = double.PositiveInfinity;
            for (var i = bars.Count - period; i < bars.Count; i++)
            {
                hi = Math.Max(hi, bars[i].High);
                lo = Math.Min(lo, bars[i].Low);
            }

            var range = hi - lo;
            if (range <= 0) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0) return null;
            if (range > maxRangeAtr * atr) return null;   // trend — not a band

            var pos = (bars[^1].Close - lo) / range;
            if (pos >= 0.9)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.7, atr,
                    $"close at {pos:P0} of a contained {period}-bar band — fade the extreme",
                    regime.TimeUtc);
            }

            if (pos <= 0.1)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.7, atr,
                    $"close at {pos:P0} of a contained {period}-bar band — fade the extreme",
                    regime.TimeUtc);
            }

            return null;
        }
    }

    /// <summary>Range drift: a monotone EMA glide inside a Range verdict —
    /// the quiet majors' slow directional crawl that the detector still
    /// classes as range (ADX &lt; 22), where every reversion voice fades
    /// against it and bleeds. Rides it instead, gated on a minimum quiet
    /// slope so flat coils stay with the faders.</summary>
    public sealed class RangeDrift(int period = 21, int confirm = 5, double minDriftAtr = 0.4)
        : IFxAlpha
    {
        public string Name => $"range-drift({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + confirm + 5) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var ema = FxFeatures.EmaSeries(closes, period);
            if (ema.Length < confirm) return null;

            var rising = true;
            var falling = true;
            for (var i = ema.Length - confirm; i < ema.Length; i++)
            {
                var now = ema[i];
                var prev = ema[i - 1];
                if (double.IsNaN(now) || double.IsNaN(prev) || now == 0 || prev == 0)
                {
                    return null;
                }

                rising &= now > prev;
                falling &= now < prev;
            }

            if (rising == falling)
            {
                return null;   // flat or mixed — not a glide
            }

            var delta = ema[^1] - ema[^confirm];
            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0 || Math.Abs(delta) < minDriftAtr * atr)
            {
                return null;   // too quiet even for the drift floor
            }

            var close = bars[^1].Close;
            if ((rising && close <= ema[^1]) || (falling && close >= ema[^1]))
            {
                return null;   // price not on the glide's side of the mean
            }

            return new FxSignal(Name, rising ? FxDirection.Buy : FxDirection.Sell,
                Math.Min(0.8, 0.55 + Math.Abs(delta) / Math.Max(atr, 1e-9) * 0.3),
                atr, $"EMA{period} gliding {delta:+0.#####;-0.#####} over {confirm - 1} bars inside Range",
                regime.TimeUtc);
        }
    }

    /// <summary>Hurst-gated momentum: only trade ROC when the series is
    /// actually persistent (H > 0.55) — the taxonomy's persistence filter
    /// as a standalone family.</summary>
    public sealed class HurstTrend(double hurstMin = 0.55, int rocPeriod = 10) : IFxAlpha
    {
        public string Name => "hurst-trend";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < 40) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var h = FxFeatures.Hurst(closes);
            if (double.IsNaN(h) || h < hurstMin) return null;
            var roc = FxFeatures.Roc(closes, rocPeriod);
            if (double.IsNaN(roc) || Math.Abs(roc) < 0.05) return null;
            var atr = FxFeatures.Atr(bars, 14);
            return new FxSignal(Name, roc > 0 ? FxDirection.Buy : FxDirection.Sell,
                Math.Min(0.8, 0.5 + (h - hurstMin)),
                atr, $"Hurst {h:0.00} persistent, ROC({rocPeriod}) {roc:+0.00;-0.00}%", regime.TimeUtc);
        }
    }
}
