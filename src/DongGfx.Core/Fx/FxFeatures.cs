commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.Core/Fx/FxFeatures.cs b/src/DongGfx.Core/Fx/FxFeatures.cs
new file mode 100644
index 0000000..788e612
--- /dev/null
+++ b/src/DongGfx.Core/Fx/FxFeatures.cs
@@ -0,0 +1,266 @@
+namespace DongGfx.Core.Fx;
+
+/// <summary>One OHLCV bar — the input unit of the forex brain. Times are
+/// unix seconds (MT5 bridge convention).</summary>
+public readonly record struct FxBar(
+    long Time, double Open, double High, double Low, double Close, double Volume)
+{
+    public double Typical => (High + Low + Close) / 3.0;
+    public double TrueRange(double prevClose) =>
+        prevClose <= 0
+            ? High - Low
+            : Math.Max(High - Low, Math.Max(Math.Abs(High - prevClose), Math.Abs(Low - prevClose)));
+}
+
+/// <summary>
+/// The DON G FX forex feature library: pure, side-effect-free indicators
+/// over bar arrays. Every function returns NaN when it cannot be computed
+/// from the data given (insufficient length, zero variance) — callers must
+/// treat NaN as "no value", never as zero.
+/// </summary>
+public static class FxFeatures
+{
+    public static double Sma(IReadOnlyList<double> v, int period)
+    {
+        if (v.Count < period || period <= 0) return double.NaN;
+        var sum = 0.0;
+        for (var i = v.Count - period; i < v.Count; i++) sum += v[i];
+        return sum / period;
+    }
+
+    /// <summary>Exponential moving average series (Wilder-style seed: SMA of
+    /// the first `period` values). Returns one EMA value per input.</summary>
+    public static double[] EmaSeries(IReadOnlyList<double> v, int period)
+    {
+        var outSeries = new double[v.Count];
+        if (v.Count < period || period <= 0) return outSeries;
+        var k = 2.0 / (period + 1);
+        var sum = 0.0;
+        for (var i = 0; i < period; i++) sum += v[i];
+        outSeries[period - 1] = sum / period;
+        for (var i = period; i < v.Count; i++)
+        {
+            outSeries[i] = v[i] * k + outSeries[i - 1] * (1 - k);
+        }
+        return outSeries;
+    }
+
+    public static double Ema(IReadOnlyList<double> v, int period) =>
+        EmaSeries(v, period) is { Length: > 0 } s && s[^1] != 0 ? s[^1] : double.NaN;
+
+    /// <summary>Relative Strength Index, Wilder smoothing, last value.</summary>
+    public static double Rsi(IReadOnlyList<double> closes, int period = 14)
+    {
+        if (closes.Count < period + 1) return double.NaN;
+        double gain = 0, loss = 0;
+        for (var i = 1; i <= period; i++)
+        {
+            var d = closes[i] - closes[i - 1];
+            if (d >= 0) gain += d; else loss -= d;
+        }
+        var avgGain = gain / period;
+        var avgLoss = loss / period;
+        for (var i = period + 1; i < closes.Count; i++)
+        {
+            var d = closes[i] - closes[i - 1];
+            avgGain = (avgGain * (period - 1) + Math.Max(d, 0)) / period;
+            avgLoss = (avgLoss * (period - 1) + Math.Max(-d, 0)) / period;
+        }
+        if (avgLoss <= 0) return avgGain <= 0 ? 50 : 100;
+        var rs = avgGain / avgLoss;
+        return 100 - 100 / (1 + rs);
+    }
+
+    /// <summary>Average True Range (Wilder), last value.</summary>
+    public static double Atr(IReadOnlyList<FxBar> bars, int period = 14)
+    {
+        if (bars.Count < period + 1) return double.NaN;
+        double sum = 0;
+        for (var i = 1; i <= period; i++) sum += bars[i].TrueRange(bars[i - 1].Close);
+        var atr = sum / period;
+        for (var i = period + 1; i < bars.Count; i++)
+        {
+            atr = (atr * (period - 1) + bars[i].TrueRange(bars[i - 1].Close)) / period;
+        }
+        return atr;
+    }
+
+    /// <summary>Average Directional Index (Wilder): returns (adx, plusDI, minusDI).</summary>
+    public static (double Adx, double PlusDi, double MinusDi) Adx(IReadOnlyList<FxBar> bars, int period = 14)
+    {
+        if (bars.Count < 2 * period + 1) return (double.NaN, double.NaN, double.NaN);
+        var n = bars.Count;
+        var tr = new double[n];
+        var pDm = new double[n];
+        var mDm = new double[n];
+        for (var i = 1; i < n; i++)
+        {
+            tr[i] = bars[i].TrueRange(bars[i - 1].Close);
+            var up = bars[i].High - bars[i - 1].High;
+            var down = bars[i - 1].Low - bars[i].Low;
+            pDm[i] = up > down && up > 0 ? up : 0;
+            mDm[i] = down > up && down > 0 ? down : 0;
+        }
+
+        double Atr2(int end)
+        {
+            double s = 0;
+            for (var i = end - period + 1; i <= end; i++) s += tr[i];
+            return s;
+        }
+
+        var atrS = Atr2(period);
+        double pDiS = 0, mDiS = 0;
+        for (var i = period - period + 1; i <= period; i++) { pDiS += pDm[i]; mDiS += mDm[i]; }
+        var dxs = new List<double>();
+        for (var i = period + 1; i < n; i++)
+        {
+            atrS = atrS - atrS / period + tr[i];
+            pDiS = pDiS - pDiS / period + pDm[i];
+            mDiS = mDiS - mDiS / period + mDm[i];
+            var pdi = atrS > 0 ? 100 * pDiS / atrS : 0;
+            var mdi = atrS > 0 ? 100 * mDiS / atrS : 0;
+            var dx = pdi + mdi > 0 ? 100 * Math.Abs(pdi - mdi) / (pdi + mdi) : 0;
+            dxs.Add(dx);
+        }
+        if (dxs.Count < period) return (double.NaN, double.NaN, double.NaN);
+        var adx = dxs.Take(period).Average();
+        for (var i = period; i < dxs.Count; i++)
+        {
+            adx = (adx * (period - 1) + dxs[i]) / period;
+        }
+        return (adx, double.NaN, double.NaN);
+    }
+
+    /// <summary>Bollinger bands: returns (middle, upper, lower, percentB, bandwidth).</summary>
+    public static (double Mid, double Upper, double Lower, double PercentB, double Bandwidth)
+        Bollinger(IReadOnlyList<double> v, int period = 20, double mult = 2.0)
+    {
+        if (v.Count < period) return (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
+        var mid = Sma(v, period);
+        double var = 0;
+        for (var i = v.Count - period; i < v.Count; i++) var += (v[i] - mid) * (v[i] - mid);
+        var sd = Math.Sqrt(var / period);
+        var up = mid + mult * sd;
+        var lo = mid - mult * sd;
+        var pB = up - lo > 0 ? (v[^1] - lo) / (up - lo) : 0.5;
+        return (mid, up, lo, pB, mid > 0 ? (up - lo) / mid : double.NaN);
+    }
+
+    /// <summary>Donchian channel over the `n` bars BEFORE the last one
+    /// (breakout convention: the current bar closing outside the prior
+    /// channel is the signal).</summary>
+    public static (double Upper, double Lower) Donchian(IReadOnlyList<FxBar> bars, int n = 20)
+    {
+        if (bars.Count < n + 1) return (double.NaN, double.NaN);
+        double hi = double.MinValue, lo = double.MaxValue;
+        for (var i = bars.Count - n - 1; i < bars.Count - 1; i++)
+        {
+            hi = Math.Max(hi, bars[i].High);
+            lo = Math.Min(lo, bars[i].Low);
+        }
+        return (hi, lo);
+    }
+
+    /// <summary>Z-score of the last value over a rolling window (population std).</summary>
+    public static double ZScore(IReadOnlyList<double> v, int period = 20)
+    {
+        if (v.Count < period || period < 2) return double.NaN;
+        var mean = Sma(v, period);
+        double var = 0;
+        for (var i = v.Count - period; i < v.Count; i++) var += (v[i] - mean) * (v[i] - mean);
+        var sd = Math.Sqrt(var / period);
+        return sd > 0 ? (v[^1] - mean) / sd : double.NaN;
+    }
+
+    /// <summary>Rate of change in percent over `n` bars.</summary>
+    public static double Roc(IReadOnlyList<double> v, int n = 10)
+    {
+        if (v.Count < n + 1 || v[v.Count - 1 - n] == 0) return double.NaN;
+        return (v[^1] / v[v.Count - 1 - n] - 1) * 100;
+    }
+
+    /// <summary>Realized volatility: per-bar std of log returns over the window.</summary>
+    public static double RealizedVol(IReadOnlyList<double> closes, int period = 20)
+    {
+        if (closes.Count < period + 1) return double.NaN;
+        var rets = new List<double>(period);
+        for (var i = closes.Count - period; i < closes.Count; i++)
+        {
+            if (closes[i - 1] <= 0) return double.NaN;
+            rets.Add(Math.Log(closes[i] / closes[i - 1]));
+        }
+        var mean = rets.Average();
+        return Math.Sqrt(rets.Sum(r => (r - mean) * (r - mean)) / rets.Count);
+    }
+
+    /// <summary>Parkinson high-low volatility over the window (per-bar).</summary>
+    public static double ParkinsonVol(IReadOnlyList<FxBar> bars, int period = 20)
+    {
+        if (bars.Count < period) return double.NaN;
+        double sum = 0;
+        for (var i = bars.Count - period; i < bars.Count; i++)
+        {
+            if (bars[i].Low <= 0) return double.NaN;
+            var hl = Math.Log(bars[i].High / bars[i].Low);
+            sum += hl * hl;
+        }
+        return Math.Sqrt(sum / (period * 4 * Math.Log(2)));
+    }
+
+    /// <summary>VWAP over the window (typical price weighted by volume).</summary>
+    public static double Vwap(IReadOnlyList<FxBar> bars, int period = 20)
+    {
+        if (bars.Count < period) return double.NaN;
+        double pv = 0, vv = 0;
+        for (var i = bars.Count - period; i < bars.Count; i++)
+        {
+            pv += bars[i].Typical * bars[i].Volume;
+            vv += bars[i].Volume;
+        }
+        return vv > 0 ? pv / vv : double.NaN;
+    }
+
+    /// <summary>Hurst exponent via variance-ratio scaling: H = log(var(k·r)) /
+    /// log(k) averaged over lags 2..maxLag. ~0.5 random walk, >0.5 trending,
+    /// <0.5 mean-reverting. Small samples are noisy — regime uses it as a
+    /// tiebreaker, never alone.</summary>
+    public static double Hurst(IReadOnlyList<double> closes, int maxLag = 16)
+    {
+        if (closes.Count < 2 * maxLag) return double.NaN;
+        var rets = new List<double>(closes.Count - 1);
+        for (var i = 1; i < closes.Count; i++)
+        {
+            if (closes[i - 1] <= 0) return double.NaN;
+            rets.Add(Math.Log(closes[i] / closes[i - 1]));
+        }
+        var vs = new List<double>();
+        var lags = new List<double>();
+        for (var lag = 2; lag <= maxLag; lag++)
+        {
+            var agg = new List<double>();
+            for (var i = 0; i + lag <= rets.Count; i += lag)
+            {
+                var s = 0.0;
+                for (var j = i; j < i + lag; j++) s += rets[j];
+                agg.Add(s);
+            }
+            if (agg.Count < 2) continue;
+            var m = agg.Average();
+            var va = agg.Sum(a => (a - m) * (a - m)) / agg.Count;
+            vs.Add(va);
+            lags.Add(lag);
+        }
+        if (vs.Count < 2) return double.NaN;
+        // linear fit of log(var) ~ H * log(lag) through aggregate slope
+        double sx = 0, sy = 0, sxy = 0, sxx = 0;
+        for (var i = 0; i < vs.Count; i++)
+        {
+            var lx = Math.Log(lags[i]);
+            var ly = Math.Log(Math.Max(vs[i], 1e-12));
+            sx += lx; sy += ly; sxy += lx * ly; sxx += lx * lx;
+        }
+        var denom = vs.Count * sxx - sx * sx;
+        return Math.Abs(denom) < 1e-12 ? double.NaN : (vs.Count * sxy - sx * sy) / denom;
+    }
+}
