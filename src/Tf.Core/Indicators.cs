namespace Tf.Core;

/// <summary>Pure technical indicators used by the market context packet.</summary>
public static class Indicators
{
    /// <summary>Simple moving average over the last <paramref name="period"/> closes.</summary>
    public static double Sma(IReadOnlyList<double> closes, int period)
    {
        if (period <= 0 || closes.Count < period)
        {
            return double.NaN;
        }

        double sum = 0;
        for (var i = closes.Count - period; i < closes.Count; i++)
        {
            sum += closes[i];
        }

        return sum / period;
    }

    /// <summary>
    /// Relative Strength Index (Wilder smoothing). Returns a value in 0..100,
    /// or NaN when there is not enough data or no price change occurred.
    /// </summary>
    public static double Rsi(IReadOnlyList<double> closes, int period = 14)
    {
        if (closes.Count < period + 1)
        {
            return double.NaN;
        }

        double avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change >= 0)
            {
                avgGain += change;
            }
            else
            {
                avgLoss -= change;
            }
        }

        avgGain /= period;
        avgLoss /= period;

        for (var i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            avgGain = (avgGain * (period - 1) + Math.Max(change, 0)) / period;
            avgLoss = (avgLoss * (period - 1) + Math.Max(-change, 0)) / period;
        }

        if (avgLoss == 0)
        {
            return 100; // no downward movement — fully overbought
        }

        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    /// <summary>Last-to-first percent change across the window.</summary>
    public static double PercentChange(IReadOnlyList<double> closes)
    {
        if (closes.Count < 2)
        {
            return 0;
        }

        var first = closes[0];
        var last = closes[^1];
        return first == 0 ? 0 : (last - first) / first * 100;
    }
}