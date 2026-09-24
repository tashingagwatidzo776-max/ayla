using System;
using System.Collections.Generic;

namespace DongGfx.Core.Fx;

/// <summary>One indicator overlay point aligned with a candle index.</summary>
/// <param name="Index">Candle index the value belongs to.</param>
/// <param name="Value">Overlay value, or null during the indicator's warm-up.</param>
public sealed record IndicatorPoint(int Index, double? Value);

/// <summary>
/// The Terminal chart's indicator overlays: the classic three MT5 ships —
/// moving averages, Bollinger Bands, RSI. Pure functions over the OHLC
/// close series; no terminal, no bridge, no state.
/// </summary>
public static class FxIndicators
{
    /// <summary>Exponential moving average. Seed = SMA of the first
    /// <paramref name="period"/> closes (MT5's EMA convention); null before
    /// the seed completes.</summary>
    public static IReadOnlyList<IndicatorPoint> Ema(IReadOnlyList<double> closes, int period)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        var result = new IndicatorPoint[closes.Count];
        var alpha = 2.0 / (period + 1);

        double? prev = null;
        double sum = 0;
        for (var i = 0; i < closes.Count; i++)
        {
            if (i < period - 1)
            {
                sum += closes[i];
                result[i] = new IndicatorPoint(i, null);
                continue;
            }

            if (prev is null)
            {
                sum += closes[i];                 // SMA seed over the window
                prev = sum / period;
            }
            else
            {
                prev = alpha * closes[i] + (1 - alpha) * prev.Value;
            }

            result[i] = new IndicatorPoint(i, prev);
        }

        return result;
    }

    /// <summary>Bollinger Bands: SMA(period) ± multiplier·stdev(period).
    /// Middle/upper/lower are null during warm-up.</summary>
    public static IReadOnlyList<(int Index, double? Middle, double? Upper, double? Lower)> Bollinger(
        IReadOnlyList<double> closes, int period = 20, double multiplier = 2.0)
    {
        if (period < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        var result = new (int, double?, double?, double?)[closes.Count];

        for (var i = 0; i < closes.Count; i++)
        {
            if (i < period - 1)
            {
                result[i] = (i, null, null, null);
                continue;
            }

            double sum = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                sum += closes[j];
            }

            var mean = sum / period;

            double sq = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                sq += (closes[j] - mean) * (closes[j] - mean);
            }

            var sd = Math.Sqrt(sq / period);      // population stdev, MT5 default

            result[i] = (i, mean, mean + multiplier * sd, mean - multiplier * sd);
        }

        return result;
    }

    /// <summary>Wilder's RSI. Null until <paramref name="period"/> deltas
    /// have seeded the average gain/loss; 100 when the seeded series is
    /// pure gains, 0 when pure losses.</summary>
    public static IReadOnlyList<IndicatorPoint> Rsi(IReadOnlyList<double> closes, int period = 14)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        var result = new IndicatorPoint[closes.Count];

        double avgGain = 0, avgLoss = 0;
        for (var i = 0; i < closes.Count; i++)
        {
            if (i == 0)
            {
                result[i] = new IndicatorPoint(0, null);
                continue;
            }

            var change = closes[i] - closes[i - 1];
            var gain = Math.Max(change, 0);
            var loss = Math.Max(-change, 0);

            if (i <= period)
            {
                // Simple-average seed (Wilder) over the first `period` deltas.
                avgGain += gain / period;
                avgLoss += loss / period;
                result[i] = i < period
                    ? new IndicatorPoint(i, null)
                    : new IndicatorPoint(i, Compute(avgGain, avgLoss));
                continue;
            }

            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
            result[i] = new IndicatorPoint(i, Compute(avgGain, avgLoss));
        }

        return result;
    }

    private static double Compute(double avgGain, double avgLoss)
    {
        if (avgLoss == 0)
        {
            return 100.0;       // all-gain series
        }

        var rs = avgGain / avgLoss;
        return 100.0 - 100.0 / (1.0 + rs);
    }
}
