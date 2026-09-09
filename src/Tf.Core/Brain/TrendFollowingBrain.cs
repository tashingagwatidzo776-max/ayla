using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>
/// Trend-following brain that trades in the direction of strong trends.
/// Uses EMA crossover (fast/slow) and ADX to identify trending markets.
/// Best in volatile, trending conditions; avoids choppy markets.
/// </summary>
public static class TrendFollowingBrain
{
    /// <summary>Configuration for trend-following parameters.</summary>
    public sealed record TrendConfig(
        int FastEma = 9,
        int SlowEma = 21,
        int AdxPeriod = 14,
        double MinAdx = 25.0,      // Minimum ADX for a valid trend
        double MaxAdx = 70.0,      // Over-extended trend
        decimal StakeFraction = 0.15m
    )
    {
        public static TrendConfig Default { get; } = new();
    }

    /// <summary>
    /// Generate a trading decision based on trend analysis.
    /// </summary>
    public static LlmDecision Decide(IReadOnlyList<Tick> window, TrendConfig config)
    {
        if (window == null || window.Count < config.SlowEma + config.AdxPeriod)
        {
            return LlmDecision.Hold("insufficient data for trend analysis");
        }

        var closes = window.Select(t => t.Quote).ToArray();

        // Calculate EMAs
        var fastEma = CalculateEma(closes, config.FastEma);
        var slowEma = CalculateEma(closes, config.SlowEma);

        // Calculate ADX (Average Directional Index)
        var adx = CalculateAdx(window, config.AdxPeriod);

        if (double.IsNaN(adx) || double.IsNaN(fastEma) || double.IsNaN(slowEma))
        {
            return LlmDecision.Hold("indicators unavailable");
        }

        // Current values
        var currentFast = fastEma;
        var currentSlow = slowEma;
        var prevFast = CalculateEma(closes[..^1], config.FastEma);
        var prevSlow = CalculateEma(closes[..^1], config.SlowEma);

        // Check for EMA crossover
        var bullishCross = prevFast <= prevSlow && currentFast > currentSlow;
        var bearishCross = prevFast >= prevSlow && currentFast < currentSlow;

        // Trend strength
        var trendStrength = adx / 100.0;
        var overExtended = adx > config.MaxAdx;

        BrainDirection direction;
        double confidence;
        string reasoning;

        if (adx < config.MinAdx)
        {
            // No clear trend
            return LlmDecision.Hold($"no trend (ADX {adx:0.0} < {config.MinAdx})");
        }

        if (overExtended)
        {
            // Trend may be over-extended, wait for pullback
            return LlmDecision.Hold($"trend over-extended (ADX {adx:0.0}), waiting for pullback");
        }

        if (bullishCross && currentFast > currentSlow)
        {
            // Bullish crossover in uptrend
            direction = BrainDirection.Rise;
            confidence = Math.Clamp(0.65 + trendStrength * 0.3, 0.65, 0.92);
            reasoning = $"bullish EMA crossover ({config.FastEma}/{config.SlowEma}) + strong uptrend (ADX {adx:0.0})";
        }
        else if (bearishCross && currentFast < currentSlow)
        {
            // Bearish crossover in downtrend
            direction = BrainDirection.Fall;
            confidence = Math.Clamp(0.65 + trendStrength * 0.3, 0.65, 0.92);
            reasoning = $"bearish EMA crossover ({config.FastEma}/{config.SlowEma}) + strong downtrend (ADX {adx:0.0})";
        }
        else if (currentFast > currentSlow && adx > config.MinAdx + 10)
        {
            // Already in uptrend, wait for pullback to fast EMA
            var lastClose = closes[^1];
            var pullbackToFast = (lastClose - currentFast) / currentFast * 100;

            if (pullbackToFast < 0.1 && pullbackToFast > -0.2) // Near fast EMA
            {
                direction = BrainDirection.Rise;
                confidence = Math.Clamp(0.60 + trendStrength * 0.25, 0.60, 0.88);
                reasoning = $"pullback to fast EMA in uptrend (ADX {adx:0.0}), expecting continuation";
            }
            else
            {
                return LlmDecision.Hold($"in uptrend but waiting for pullback (distance: {pullbackToFast:0.00}%)");
            }
        }
        else if (currentFast < currentSlow && adx > config.MinAdx + 10)
        {
            // Already in downtrend, wait for pullback to fast EMA
            var lastClose = closes[^1];
            var pullbackToFast = (currentFast - lastClose) / lastClose * 100;

            if (pullbackToFast < 0.1 && pullbackToFast > -0.2)
            {
                direction = BrainDirection.Fall;
                confidence = Math.Clamp(0.60 + trendStrength * 0.25, 0.60, 0.88);
                reasoning = $"pullback to fast EMA in downtrend (ADX {adx:0.0}), expecting continuation";
            }
            else
            {
                return LlmDecision.Hold($"in downtrend but waiting for pullback (distance: {pullbackToFast:0.00}%)");
            }
        }
        else
        {
            return LlmDecision.Hold($"no clear signal (fast {currentFast:0.5}, slow {currentSlow:0.5}, ADX {adx:0.0})");
        }

        return new LlmDecision(direction, confidence, 0, reasoning);
    }

    private static double CalculateEma(double[] data, int period)
    {
        if (data.Length < period) return double.NaN;

        var multiplier = 2.0 / (period + 1);
        var ema = data[..period].Average();

        for (int i = period; i < data.Length; i++)
        {
            ema = (data[i] - ema) * multiplier + ema;
        }

        return ema;
    }

    private static double CalculateAdx(IReadOnlyList<Tick> window, int period)
    {
        if (window.Count < period + 1) return double.NaN;

        var trList = new List<double>();
        var plusDmList = new List<double>();
        var minusDmList = new List<double>();

        for (int i = 1; i < window.Count; i++)
        {
            var high = window[i].Ask;
            var low = window[i].Bid;
            var prevHigh = window[i - 1].Ask;
            var prevLow = window[i - 1].Bid;
            var prevClose = window[i - 1].Quote;

            // True Range
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trList.Add(tr);

            // Directional Movement
            var upMove = high - prevHigh;
            var downMove = prevLow - low;

            plusDmList.Add(upMove > downMove && upMove > 0 ? upMove : 0);
            minusDmList.Add(downMove > upMove && downMove > 0 ? downMove : 0);
        }

        if (trList.Count < period) return double.NaN;

        // Smooth TR, +DM, -DM
        var smoothedTr = trList.Take(period).Average();
        var smoothedPlusDm = plusDmList.Take(period).Average();
        var smoothedMinusDm = minusDmList.Take(period).Average();

        var diPlusList = new List<double>();
        var diMinusList = new List<double>();

        for (int i = period; i < trList.Count; i++)
        {
            smoothedTr = smoothedTr - smoothedTr / period + trList[i];
            smoothedPlusDm = smoothedPlusDm - smoothedPlusDm / period + plusDmList[i];
            smoothedMinusDm = smoothedMinusDm - smoothedMinusDm / period + minusDmList[i];

            if (smoothedTr > 0)
            {
                diPlusList.Add(smoothedPlusDm / smoothedTr * 100);
                diMinusList.Add(smoothedMinusDm / smoothedTr * 100);
            }
        }

        if (diPlusList.Count < period) return double.NaN;

        // Calculate DX and then ADX
        var dxList = new List<double>();
        for (int i = 0; i < diPlusList.Count; i++)
        {
            var diSum = diPlusList[i] + diMinusList[i];
            if (diSum > 0)
            {
                dxList.Add(Math.Abs(diPlusList[i] - diMinusList[i]) / diSum * 100);
            }
        }

        return dxList.Count >= period ? dxList.TakeLast(period).Average() : double.NaN;
    }
}
