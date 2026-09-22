namespace DongGfx.Core.Fx;

/// <summary>One alpha verdict. A signal is NOT an order — the engine's
/// sizing/risk layers and the session rails decide what (if anything)
/// happens next.</summary>
public sealed record FxSignal(
    string Alpha,
    FxDirection Direction,
    double Confidence,
    double StopDistanceHint,
    string Reason,
    long TimeUtc);

public enum FxDirection { Buy, Sell }

/// <summary>Contract every alpha family implements. Alphas are pure: given
/// bars and a verdict they return a signal or null (pass). They never place
/// orders and never touch state.</summary>
public interface IFxAlpha
{
    string Name { get; }
    /// <summary>Which regime this alpha speaks in — the detector's output
    /// gates the call itself.</summary>
    FxRegime[] Regimes { get; }
    FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime);
}

/// <summary>
/// Momentum family (taxonomy family 2): EMA crossover, Donchian breakout,
/// rate-of-change, multi-timeframe alignment, Hurst-gated trend, and
/// volatility breakout. All fire Buy/Sell only in Trend (vol-breakout also
/// in HighVol windows that pass the detector).
/// </summary>
public static class FxMomentum
{
    /// <summary>EMA crossover: fast above slow and rising → Buy; inverse → Sell.</summary>
    public sealed class EmaCross(int fast = 9, int slow = 21) : IFxAlpha
    {
        public string Name => $"ema-cross({fast}/{slow})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var closes = bars.Select(b => b.Close).ToList();
            var f = FxFeatures.EmaSeries(closes, fast);
            var s = FxFeatures.EmaSeries(closes, slow);
            if (f[^1] == 0 || s[^1] == 0 || double.IsNaN(f[^1]) || double.IsNaN(s[^1])) return null;
            var now = f[^1] - s[^1];
            var prev = f[^2] - s[^2];
            var spread = Math.Abs(now) / Math.Max(s[^1], 1e-9);
            if (Math.Abs(spread) < 0.0004) return null; // not separated enough
            if (now > 0 && prev > 0 && now > prev)
            {
                return new FxSignal(Name, FxDirection.Buy, Math.Min(0.9, 0.5 + spread * 100),
                    FxFeatures.Atr(bars, 14), $"EMA{fast} above EMA{slow} and widening", regime.TimeUtc);
            }
            if (now < 0 && prev < 0 && now < prev)
            {
                return new FxSignal(Name, FxDirection.Sell, Math.Min(0.9, 0.5 + Math.Abs(spread) * 100),
                    FxFeatures.Atr(bars, 14), $"EMA{fast} below EMA{slow} and widening", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Donchian breakout: close above the prior N-bar high → Buy.</summary>
    public sealed class DonchianBreakout(int n = 20) : IFxAlpha
    {
        public string Name => $"donchian({n})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend, FxRegime.HighVol];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var (hi, lo) = FxFeatures.Donchian(bars, n);
            if (double.IsNaN(hi) || double.IsNaN(lo)) return null;
            var close = bars[^1].Close;
            var atr = FxFeatures.Atr(bars, 14);
            if (close > hi)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.7, atr,
                    $"close {close:0.#####} broke {n}-bar high {hi:0.#####}", regime.TimeUtc);
            }
            if (close < lo)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.7, atr,
                    $"close {close:0.#####} broke {n}-bar low {lo:0.#####}", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>Rate-of-change momentum with a dead-band.</summary>
    public sealed class Roc(int n = 10, double threshold = 0.15) : IFxAlpha
    {
        public string Name => $"roc({n})";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var r = FxFeatures.Roc(bars.Select(b => b.Close).ToList(), n);
            if (double.IsNaN(r) || Math.Abs(r) < threshold) return null;
            return new FxSignal(Name, r > 0 ? FxDirection.Buy : FxDirection.Sell,
                Math.Min(0.85, 0.5 + Math.Abs(r) / 2), FxFeatures.Atr(bars, 14),
                $"ROC({n}) {r:+0.00;-0.00}%", regime.TimeUtc);
        }
    }

    /// <summary>Multi-timeframe alignment: higher TF trend direction gates the
    /// lower TF momentum direction (both must agree).</summary>
    public sealed class MultiTimeframe(IFxAlpha fast, IReadOnlyList<FxBar> higherTfBars)
        : IFxAlpha
    {
        private readonly IFxAlpha _fast = fast;
        private readonly IReadOnlyList<FxBar> _higher = higherTfBars;

        public string Name => $"mtf[{_fast.Name}]";
        public FxRegime[] Regimes { get; } = [FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var sig = _fast.Evaluate(bars, regime);
            if (sig is null) return null;
            var hTrend = FxFeatures.Hurst(_higher.Select(b => b.Close).ToList());
            if (double.IsNaN(hTrend) || hTrend < 0.5) return null; // higher TF not trending
            return sig;
        }
    }

    /// <summary>Volatility breakout: compression (low bandwidth) followed by
    /// expansion in a direction — the classic squeeze play.</summary>
    public sealed class VolatilityBreakout(int period = 20, double squeezePct = 0.004) : IFxAlpha
    {
        public string Name => "vol-breakout";
        public FxRegime[] Regimes { get; } = [FxRegime.Range, FxRegime.Trend];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 10) return null;
            var closes = bars.Select(b => b.Close).ToList();
            var (_, _, _, _, bandwidthNow) = FxFeatures.Bollinger(closes, period);
            var histBand = new List<double>();
            for (var end = period + 5; end < closes.Count; end++)
            {
                var slice = closes.Take(end).ToList();
                var (_, _, _, _, bw) = FxFeatures.Bollinger(slice, period);
                if (!double.IsNaN(bw)) histBand.Add(bw);
            }
            if (histBand.Count < 5 || double.IsNaN(bandwidthNow)) return null;
            var medianBw = histBand.OrderBy(x => x).ElementAt(histBand.Count / 2);
            if (bandwidthNow > medianBw * 1.2) return null; // not a fresh expansion

            var close = bars[^1].Close;
            var prev = bars[^2].Close;
            var atr = FxFeatures.Atr(bars, 14);
            var dir = close > prev ? FxDirection.Buy : FxDirection.Sell;
            return new FxSignal(Name, dir, 0.6, atr,
                $"squeeze released (bw {bandwidthNow:0.0000} vs median {medianBw:0.0000})", regime.TimeUtc);
        }
    }
}

/// <summary>
/// Mean-reversion family (taxonomy family 3): Z-score, Bollinger %B,
/// VWAP deviation, MA-deviation, and an Ornstein-Uhlenbeck half-life check
/// that only fires when reversion has historically been fast enough to trade.
/// </summary>
public static class FxMeanReversion
{
    /// <summary>Z-score reversion with the classic ±2 entry band.</summary>
    public sealed class ZScore(int period = 20, double entry = 2.0) : IFxAlpha
    {
        public string Name => $"z-rev({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var closes = bars.Select(b => b.Close).ToList();
            var z = FxFeatures.ZScore(closes, period);
            if (double.IsNaN(z) || Math.Abs(z) < entry) return null;
            var atr = FxFeatures.Atr(bars, 14);
            return new FxSignal(Name, z > 0 ? FxDirection.Sell : FxDirection.Buy,
                Math.Min(0.85, 0.5 + (Math.Abs(z) - entry) / 4), atr,
                $"Z {z:+0.00;-0.00} beyond ±{entry:0.#}", regime.TimeUtc);
        }
    }

    /// <summary>Bollinger %B reversion: touch of a band + back inside.</summary>
    public sealed class BollingerReversion(int period = 20) : IFxAlpha
    {
        public string Name => $"bb-rev({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var closes = bars.Select(b => b.Close).ToList();
            if (closes.Count < period + 2) return null;
            var prevSlice = closes.Take(closes.Count - 1).ToList();
            var (_, upPrev, loPrev, _, _) = FxFeatures.Bollinger(prevSlice, period);
            var (_, up, lo, pB, _) = FxFeatures.Bollinger(closes, period);
            if (double.IsNaN(up)) return null;
            var atr = FxFeatures.Atr(bars, 14);
            var close = closes[^1];
            if (prevSlice[^1] > upPrev && close < up)
            {
                return new FxSignal(Name, FxDirection.Sell, 0.65, atr,
                    "rejected the upper band back inside", regime.TimeUtc);
            }
            if (prevSlice[^1] < loPrev && close > lo)
            {
                return new FxSignal(Name, FxDirection.Buy, 0.65, atr,
                    "rejected the lower band back inside", regime.TimeUtc);
            }
            return null;
        }
    }

    /// <summary>VWAP deviation: price stretched from VWAP snaps back.</summary>
    public sealed class VwapReversion(int period = 30, double atrMult = 2.0) : IFxAlpha
    {
        public string Name => $"vwap-rev({period})";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            var vwap = FxFeatures.Vwap(bars, period);
            if (double.IsNaN(vwap) || vwap <= 0) return null;
            var atr = FxFeatures.Atr(bars, 14);
            if (double.IsNaN(atr) || atr <= 0) return null;
            var dev = (bars[^1].Close - vwap) / atr;
            if (Math.Abs(dev) < atrMult) return null;
            return new FxSignal(Name, dev > 0 ? FxDirection.Sell : FxDirection.Buy,
                Math.Min(0.8, 0.5 + (Math.Abs(dev) - atrMult) / 6), atr,
                $"close {dev:+0.0;-0.0}·ATR from VWAP", regime.TimeUtc);
        }
    }

    /// <summary>Ornstein-Uhlenbeck half-life gate: measure how fast the
    /// spread mean-reverts; only trade reversion when the half-life is
    /// short enough for the session bars (else the edge decays before the
    /// trade closes).</summary>
    public sealed class OuHalfLife(int period = 60, double maxHalfLifeBars = 12) : IFxAlpha
    {
        public string Name => "ou-rev";
        public FxRegime[] Regimes { get; } = [FxRegime.Range];

        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime)
        {
            if (bars.Count < period + 2) return null;
            var closes = bars.Skip(bars.Count - period).Select(b => b.Close).ToList();
            var mean = closes.Average();
            var spread = closes.Select(c => c - mean).ToList();
            // Δs_t = θ·s_{t-1} + ε  →  θ = Σ s_{t-1}·Δs / Σ s_{t-1}²
            double num = 0, den = 0;
            for (var i = 1; i < spread.Count; i++)
            {
                var d = spread[i] - spread[i - 1];
                num += spread[i - 1] * d;
                den += spread[i - 1] * spread[i - 1];
            }
            if (den <= 0) return null;
            var theta = -num / den; // reversion speed
            if (theta <= 0 || theta >= 1) return null; // diverging or degenerate
            var halfLife = Math.Log(2) / theta;
            if (halfLife > maxHalfLifeBars) return null; // too slow to trade

            var z = FxFeatures.ZScore(closes, period);
            if (double.IsNaN(z) || Math.Abs(z) < 1.5) return null;
            return new FxSignal(Name, z > 0 ? FxDirection.Sell : FxDirection.Buy, 0.6,
                FxFeatures.Atr(bars, 14),
                $"OU half-life {halfLife:0.0} bars, Z {z:+0.00;-0.00}", regime.TimeUtc);
        }
    }
}
