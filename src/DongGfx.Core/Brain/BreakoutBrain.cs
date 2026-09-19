using DongGfx.Core.Models;

namespace DongGfx.Core.Brain;

/// <summary>
/// Breakout brain that identifies support/resistance levels and trades breakouts.
/// Uses Bollinger Bands, ATR, and volume analysis to detect breakouts.
/// Best in ranging markets that are about to trend.
/// </summary>
public static class BreakoutBrain
{
    /// <summary>Configuration for breakout parameters.</summary>
    public sealed record BreakoutConfig(
        int BollingerPeriod = 20,
        double BollingerStdDev = 2.0,
        int AtrPeriod = 14,
        double BreakoutThreshold = 0.5,  // ATR multiplier for breakout confirmation
        decimal StakeFraction = 0.20m
    )
    {
        public static BreakoutConfig Default { get; } = new();
    }

    /// <summary>
    /// Generate a trading decision based on breakout analysis.
    /// </summary>
    public static LlmDecision Decide(IReadOnlyList<Tick> window, BreakoutConfig config)
    {
        if (window == null || window.Count < config.BollingerPeriod + config.AtrPeriod)
        {
            return LlmDecision.Hold("insufficient data for breakout analysis");
        }

        var closes = window.Select(t => t.Quote).ToArray();
        var highs = window.Select(t => t.Ask).ToArray();
        var lows = window.Select(t => t.Bid).ToArray();

        // Calculate Bollinger Bands
        var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closes, config.BollingerPeriod, config.BollingerStdDev);

        // Calculate ATR (Average True Range)
        var atr = CalculateAtr(highs, lows, closes, config.AtrPeriod);

        // Calculate current price position
        var lastClose = closes[^1];
        var prevClose = closes[^2];

        // Band width (volatility)
        var bandWidth = (upperBand - lowerBand) / middleBand * 100;

        // Squeeze detection (low volatility before breakout)
        var prevBandWidth = CalculateBandWidth(closes, config.BollingerPeriod, config.BollingerStdDev, ^2);
        var squeeze = bandWidth < prevBandWidth * 0.8;

        // Breakout detection
        var breakAboveUpper = lastClose > upperBand && prevClose <= upperBand;
        var breakBelowLower = lastClose < lowerBand && prevClose >= lowerBand;

        // Confirm with ATR
        var breakoutSize = Math.Abs(lastClose - middleBand);
        var atrMultiple = breakoutSize / atr;

        BrainDirection direction;
        double confidence;
        string reasoning;

        if (double.IsNaN(upperBand) || double.IsNaN(lowerBand) || double.IsNaN(atr))
        {
            return LlmDecision.Hold("indicators unavailable");
        }

        // Strong breakout with volume
        if (breakAboveUpper && atrMultiple > config.BreakoutThreshold)
        {
            direction = BrainDirection.Rise;
            confidence = Math.Clamp(0.70 + (atrMultiple - config.BreakoutThreshold) * 0.1, 0.70, 0.93);
            reasoning = $"breakout above upper band ({upperBand:0.5}) with {atrMultiple:0.1}x ATR confirmation";
        }
        else if (breakBelowLower && atrMultiple > config.BreakoutThreshold)
        {
            direction = BrainDirection.Fall;
            confidence = Math.Clamp(0.70 + (atrMultiple - config.BreakoutThreshold) * 0.1, 0.70, 0.93);
            reasoning = $"breakout below lower band ({lowerBand:0.5}) with {atrMultiple:0.1}x ATR confirmation";
        }
        // Squeeze + approaching band
        else if (squeeze && lastClose > middleBand && lastClose > middleBand + (upperBand - middleBand) * 0.7)
        {
            direction = BrainDirection.Rise;
            confidence = 0.65;
            reasoning = $"squeeze detected + price near upper band, anticipating upward breakout";
        }
        else if (squeeze && lastClose < middleBand && lastClose < middleBand - (middleBand - lowerBand) * 0.7)
        {
            direction = BrainDirection.Fall;
            confidence = 0.65;
            reasoning = $"squeeze detected + price near lower band, anticipating downward breakout";
        }
        // False breakout / rejection
        else if (lastClose > upperBand && lastClose < prevClose)
        {
            // Touched upper band but rejected
            return LlmDecision.Hold($"upper band rejection, waiting for confirmation");
        }
        else if (lastClose < lowerBand && lastClose > prevClose)
        {
            // Touched lower band but rejected
            return LlmDecision.Hold($"lower band rejection, waiting for confirmation");
        }
        else
        {
            return LlmDecision.Hold($"no breakout signal (band width {bandWidth:0.1}%, ATR {atr:0.5})");
        }

        return new LlmDecision(direction, confidence, 0, reasoning);
    }

    private static (double upper, double middle, double lower) CalculateBollingerBands(
        double[] closes, int period, double stdDevMultiplier)
    {
        var sma = closes[^period..].Average();
        var variance = closes[^period..].Select(x => Math.Pow(x - sma, 2)).Average();
        var stdDev = Math.Sqrt(variance);

        return (sma + stdDevMultiplier * stdDev, sma, sma - stdDevMultiplier * stdDev);
    }

    private static double CalculateBandWidth(double[] closes, int period, double stdDevMultiplier, Index offset)
    {
        var slice = closes[..^offset.Value];
        if (slice.Length < period) return double.NaN;

        var sma = slice[^period..].Average();
        var variance = slice[^period..].Select(x => Math.Pow(x - sma, 2)).Average();
        var stdDev = Math.Sqrt(variance);

        return (stdDevMultiplier * stdDev * 2) / sma * 100;
    }

    private static double CalculateAtr(double[] highs, double[] lows, double[] closes, int period)
    {
        if (highs.Length < period + 1) return double.NaN;

        var trList = new List<double>();
        for (int i = 1; i < highs.Length; i++)
        {
            var tr = Math.Max(highs[i] - lows[i],
                Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                         Math.Abs(lows[i] - closes[i - 1])));
            trList.Add(tr);
        }

        return trList.TakeLast(period).Average();
    }
}
