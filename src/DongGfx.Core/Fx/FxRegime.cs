commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.Core/Fx/FxRegime.cs b/src/DongGfx.Core/Fx/FxRegime.cs
new file mode 100644
index 0000000..fa5aaea
--- /dev/null
+++ b/src/DongGfx.Core/Fx/FxRegime.cs
@@ -0,0 +1,134 @@
+namespace DongGfx.Core.Fx;
+
+/// <summary>Market regime verdicts. The regime layer sits upstream of every
+/// alpha and can veto: alphas are only asked to speak in their regime.</summary>
+public enum FxRegime
+{
+    /// <summary>Directional market — momentum family owns this regime.</summary>
+    Trend,
+
+    /// <summary>Range-bound market — mean-reversion family owns this regime.</summary>
+    Range,
+
+    /// <summary>Volatility expansion — breakout-only or stand down.</summary>
+    HighVol,
+
+    /// <summary>Illiquid session — no trading.</summary>
+    LowLiquidity,
+
+    /// <summary>Veto: do not evaluate any alpha (news window, stalled feed).</summary>
+    StandDown,
+}
+
+/// <summary>Why the detector landed on its verdict — journaled with it.</summary>
+public sealed record FxRegimeVerdict(
+    FxRegime Regime,
+    double Adx,
+    double AtrPct,
+    string Session,
+    double SpreadPoints,
+    string Reason,
+    long TimeUtc);
+
+/// <summary>
+/// Regime detector v1: rule-based slice of the taxonomy's family 8.
+/// Trend/range via ADX, volatility terciles via ATR% history, session by
+/// UTC clock, spread-state vs its own median. Deliberately deterministic
+/// and unit-testable; Markov-switching and change-point detectors slot in
+/// behind the same interface later.
+/// </summary>
+public sealed class FxRegimeDetector
+{
+    private readonly Queue<double> _atrHistory = new();
+    private readonly Queue<double> _spreadHistory = new();
+    private readonly int _atrWindow;
+    private readonly double _highVolPct;   // ATR% above this = HighVol
+    private readonly double _adxTrendFloor;
+    private readonly double _spreadMaxPoints;
+
+    public FxRegimeDetector(
+        int atrWindow = 60,
+        double highVolPct = 0.45,
+        double adxTrendFloor = 22.0,
+        double spreadMaxPoints = 60)
+    {
+        _atrWindow = atrWindow;
+        _highVolPct = highVolPct;
+        _adxTrendFloor = adxTrendFloor;
+        _spreadMaxPoints = spreadMaxPoints;
+    }
+
+    /// <summary>Session from UTC time: Asia 00–07, London 07–13, overlap
+    /// 13–16, NY 16–21, late 21–24. Late + overlap-open are the thin ones.</summary>
+    public static string SessionOf(DateTimeOffset utc) => utc.Hour switch
+    {
+        >= 0 and < 7 => "asia",
+        >= 7 and < 13 => "london",
+        >= 13 and < 16 => "ldn-ny",
+        >= 16 and < 21 => "new-york",
+        _ => "late",
+    };
+
+    /// <summary>Evaluate the current regime from recent bars + the live quote.</summary>
+    public FxRegimeVerdict Evaluate(IReadOnlyList<FxBar> bars, double spreadPoints, DateTimeOffset utcNow)
+    {
+        var session = SessionOf(utcNow);
+
+        // Hard vetoes first.
+        if (bars.Count < 30)
+        {
+            return new FxRegimeVerdict(FxRegime.StandDown, double.NaN, double.NaN,
+                session, spreadPoints, "insufficient bars", utcNow.ToUnixTimeSeconds());
+        }
+
+        var closes = bars.Select(b => b.Close).ToList();
+        var atr = FxFeatures.Atr(bars, 14);
+        var last = bars[^1].Close;
+        var atrPct = last > 0 ? atr / last * 100 : double.NaN;
+        var (adx, _, _) = FxFeatures.Adx(bars, 14);
+
+        var medianSpread = Median(_spreadHistory);   // typical spread BEFORE this quote
+        _spreadHistory.Enqueue(spreadPoints);
+        while (_spreadHistory.Count > 100) _spreadHistory.Dequeue();
+        if (spreadPoints > _spreadMaxPoints || (medianSpread > 0 && spreadPoints > medianSpread * 3))
+        {
+            return new FxRegimeVerdict(FxRegime.StandDown, adx, atrPct, session, spreadPoints,
+                $"spread spike ({spreadPoints:0} pts vs median {medianSpread:0})", utcNow.ToUnixTimeSeconds());
+        }
+
+        if (session is "late")
+        {
+            return new FxRegimeVerdict(FxRegime.LowLiquidity, adx, atrPct, session, spreadPoints,
+                "late session — thin liquidity", utcNow.ToUnixTimeSeconds());
+        }
+
+        _atrHistory.Enqueue(atrPct);
+        while (_atrHistory.Count > _atrWindow) _atrHistory.Dequeue();
+        if (atrPct > _highVolPct && _atrHistory.Count >= 20 && atrPct > Percentile(_atrHistory, 0.85))
+        {
+            return new FxRegimeVerdict(FxRegime.HighVol, adx, atrPct, session, spreadPoints,
+                $"ATR% {atrPct:0.00} in top decile", utcNow.ToUnixTimeSeconds());
+        }
+
+        if (!double.IsNaN(adx) && adx >= _adxTrendFloor)
+        {
+            return new FxRegimeVerdict(FxRegime.Trend, adx, atrPct, session, spreadPoints,
+                $"ADX {adx:0.0} ≥ {_adxTrendFloor:0}", utcNow.ToUnixTimeSeconds());
+        }
+
+        return new FxRegimeVerdict(FxRegime.Range, adx, atrPct, session, spreadPoints,
+            $"ADX {adx:0.0} below trend floor", utcNow.ToUnixTimeSeconds());
+    }
+
+    private static double Median(IEnumerable<double> v)
+    {
+        var arr = v.OrderBy(x => x).ToArray();
+        return arr.Length == 0 ? double.NaN : arr[arr.Length / 2];
+    }
+
+    private static double Percentile(IEnumerable<double> v, double p)
+    {
+        var arr = v.OrderBy(x => x).ToArray();
+        return arr.Length == 0 ? double.NaN : arr[Math.Min(arr.Length - 1, (int)(p * arr.Length))];
+    }
+}
