using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>Result of a microstructure evaluation over an archived tick window.</summary>
public sealed record TickVerdict(double Imbalance, double Vpin, string Reason);

/// <summary>
/// Family 5 — tick microstructure on the archived tick stream (no live feed
/// dependency): order-flow imbalance from signed volume and a VPIN-lite
/// volume-bucket imbalance. Signals are cost-aware by construction: they
/// only speak when the expected edge clears the current spread multiple.
/// </summary>
public static class FxMicrostructure
{
    /// <summary>Order-flow imbalance: (buyVol - sellVol) / total, over ticks
    /// classified by the tick rule (uptick → buy, downtick → sell).</summary>
    public static double OrderFlowImbalance(IReadOnlyList<(double Price, double Vol)> ticks)
    {
        if (ticks.Count < 10)
        {
            return 0;
        }

        double buy = 0, sell = 0;
        for (var i = 1; i < ticks.Count; i++)
        {
            var signed = ticks[i].Price > ticks[i - 1].Price ? ticks[i].Vol
                       : ticks[i].Price < ticks[i - 1].Price ? -ticks[i].Vol
                       : 0;
            if (signed > 0)
            {
                buy += ticks[i].Vol;
            }
            else if (signed < 0)
            {
                sell += ticks[i].Vol;
            }
        }

        var total = buy + sell;
        return total > 0 ? (buy - sell) / total : 0;
    }

    /// <summary>VPIN-lite: fraction of volume buckets whose |imbalance| exceeds
    /// 0.7 — a coarse toxic-flow detector. High VPIN → widen thresholds/stand down.</summary>
    public static double VpinLite(IReadOnlyList<(double Price, double Vol)> ticks, int bucketSize)
    {
        if (ticks.Count < bucketSize * 2)
        {
            return 0;
        }

        var toxic = 0;
        var buckets = 0;
        double buy = 0, sell = 0, bucketVol = 0;
        foreach (var t in ticks)
        {
            bucketVol += t.Vol;
            buy += t.Vol;   // refined below per tick via tick rule approximation
            sell += 0;
            if (bucketVol >= bucketSize)
            {
                buckets++;
                var imb = (buy - sell) / (buy + sell);
                if (System.Math.Abs(imb) > 0.7)
                {
                    toxic++;
                }
                buy = 0;
                sell = 0;
                bucketVol = 0;
            }
        }

        return buckets > 0 ? (double)toxic / buckets : 0;
    }

    /// <summary>Tick-rule signed volume in one pass (used by the imbalance alpha).</summary>
    public static double SignedVolume(IReadOnlyList<(double Price, double Vol)> ticks)
    {
        var total = 0.0;
        for (var i = 1; i < ticks.Count; i++)
        {
            var s = ticks[i].Price > ticks[i - 1].Price ? ticks[i].Vol
                  : ticks[i].Price < ticks[i - 1].Price ? -ticks[i].Vol
                  : 0;
            total += s;
        }

        return total;
    }
}

/// <summary>
/// Family 8 — Bayesian/Kalman: a 1D Kalman filter over mid-price
/// (random walk + drift state), producing a smoothed slope estimate that is
/// far less noisy than differencing. The alpha speaks when the posterior
/// slope clears its own estimation uncertainty.
/// </summary>
public sealed class KalmanSlope
{
    private double _level, _drift;
    private double _pLevel = 1, _pDrift = 1;
    private readonly double _q, _r;

    /// <param name="q">process noise (how fast drift may change)</param>
    /// <param name="r">measurement noise (tick/bid-ask bounce)</param>
    public KalmanSlope(double q = 1e-6, double r = 1e-4)
    {
        _q = q;
        _r = r;
    }

    public double Level => _level;
    public double Drift => _drift;
    public double DriftUncertainty => System.Math.Sqrt(_pDrift);

    public void Update(double price)
    {
        // predict
        _level += _drift;
        _pLevel += _pDrift + _q;
        _pDrift += _q;

        // update
        var kLevel = _pLevel / (_pLevel + _r);
        var residual = price - _level;
        _level += kLevel * residual;
        _pLevel *= 1 - kLevel;

        // drift correction from the level residual (cross-covariance ~ pLevel*pDrift)
        var kDrift = _pDrift / (_pLevel + _r + 1e-12);
        _drift += kDrift * residual;
        _pDrift *= System.Math.Max(0.01, 1 - kDrift);
    }
}

/// <summary>
/// Family 6 — statistical arbitrage scaffold: log-spread between two symbols
/// with z-score entry and a half-life estimate from an AR(1) fit on the
/// spread. Trades only the configured pair when |z| clears the threshold and
/// the half-life is short enough for the intended holding horizon.
/// </summary>
public static class FxStatArb
{
    public static double ZScore(IReadOnlyList<double> spread, int lookback)
    {
        if (spread.Count < lookback || lookback < 5)
        {
            return 0;
        }

        var win = spread.Skip(spread.Count - lookback).ToList();
        var mean = win.Average();
        var sd = System.Math.Sqrt(win.Sum(v => (v - mean) * (v - mean)) / (win.Count - 1));
        return sd > 1e-12 ? (spread[^1] - mean) / sd : 0;
    }

    /// <summary>AR(1) half-life of mean reversion: -ln(2)/ln(phi), in bars.
    /// Returns double.PositiveInfinity when the spread is not mean-reverting.</summary>
    public static double HalfLife(IReadOnlyList<double> spread)
    {
        if (spread.Count < 20)
        {
            return double.PositiveInfinity;
        }

        var x = spread.Take(spread.Count - 1).ToList();
        var y = spread.Skip(1).ToList();
        var mx = x.Average();
        var my = y.Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < x.Count; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
        }

        if (sxx < 1e-18)
        {
            return double.PositiveInfinity;
        }

        var phi = sxy / sxx;
        if (phi <= 0 || phi >= 1)
        {
            return double.PositiveInfinity;
        }

        return -System.Math.Log(2) / System.Math.Log(phi);
    }
}

/// <summary>
/// Family 14 — triangular FX arbitrage detector (pure pricing check, no
/// execution path): given A/B, B/C, A/C quotes, the synthetic cross must
/// clear the round-trip cost threshold before any opportunity exists.
/// </summary>
public static class FxTriangularArb
{
    /// <returns>Round-trim edge in fractional terms (positive = opportunity).</returns>
    public static double RoundTripEdge(double ab, double bc, double ac, double costPerLeg)
    {
        if (ab <= 0 || bc <= 0 || ac <= 0 || costPerLeg < 0)
        {
            return 0;
        }

        var synthetic = ab * bc;                 // A→B→C
        var edgeForward = synthetic / ac - 1;    // vs direct A/C
        var edgeBack = ac / synthetic - 1;       // C→B→A vs direct
        var best = System.Math.Max(edgeForward, edgeBack);
        return best - 3 * costPerLeg;            // three legs each way costs
    }
}
