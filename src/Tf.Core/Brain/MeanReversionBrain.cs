using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>
/// Configurable mean reversion brain. Trades when price deviates from its mean,
/// expecting a return to average. More flexible than GrowthBrain with configurable
/// RSI thresholds, Z-score, and Bollinger Band filters.
/// </summary>
public static class MeanReversionBrain
{
    /// <summary>Configuration for mean reversion parameters.</summary>
    public sealed record MeanReversionConfig(
        int RsiPeriod = 14,
        double OversoldRsi = 30.0,
        double OverboughtRsi = 70.0,
        int BollingerPeriod = 20,
        double BollingerStdDev = 2.0,
        double MinZScore = 1.5,      // Minimum Z-score for entry
        decimal StakeFraction = 0.18m
    )
    {
        public static MeanReversionConfig Default { get; } = new();
    }

    /// <summary>
    /// Generate a trading decision based on mean reversion analysis.
    /// </summary>
    public static LlmDecision Decide(IReadOnlyList<Tick> window, MeanReversionConfig config)
    {
        if (window == null || window.Count < Math.Max(config.RsiPeriod, config.BollingerPeriod) + 10)
        {
            return LlmDecision.Hold("insufficient data for mean reversion analysis");
        }

        var closes = window.Select(t => t.Quote).ToArray();

        // Calculate RSI
        var rsi = CalculateRsi(closes, config.RsiPeriod);

        // Calculate Bollinger Bands for Z-score
        var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(
            closes, config.BollingerPeriod, config.BollingerStdDev);

        // Calculate Z-score
        var stdDev = (upperBand - middleBand) / config.BollingerStdDev;
        var lastClose = closes[^1];
        var zScore = (lastClose - middleBand) / stdDev;

        // Check for divergence (price making new extreme but RSI not confirming)
        var prevRsi = CalculateRsi(closes[..^1], config.RsiPeriod);
        var prevClose = closes[^2];
        var priceNewLow = lastClose < prevClose && lastClose < middleBand;
        var rsiNotNewLow = rsi > prevRsi;
        var bullishDivergence = priceNewLow && rsiNotNewLow && rsi < 40;

        var priceNewHigh = lastClose > prevClose && lastClose > middleBand;
        var rsiNotNewHigh = rsi < prevRsi;
        var bearishDivergence = priceNewHigh && rsiNotNewHigh && rsi > 60;

        BrainDirection direction;
        double confidence;
        string reasoning;

        if (double.IsNaN(rsi) || double.IsNaN(zScore))
        {
            return LlmDecision.Hold("indicators unavailable");
        }

        // Strong oversold + near lower band
        if (rsi <= config.OversoldRsi && zScore <= -config.MinZScore)
        {
            direction = BrainDirection.Rise;
            confidence = Math.Clamp(0.72 + (config.OversoldRsi - rsi) / 30 * 0.2, 0.72, 0.94);
            reasoning = $"strong oversold (RSI {rsi:0.0}, Z-score {zScore:0.2}), expecting mean reversion";
        }
        // Strong overbought + near upper band
        else if (rsi >= config.OverboughtRsi && zScore >= config.MinZScore)
        {
            direction = BrainDirection.Fall;
            confidence = Math.Clamp(0.72 + (rsi - config.OverboughtRsi) / 30 * 0.2, 0.72, 0.94);
            reasoning = $"strong overbought (RSI {rsi:0.0}, Z-score {zScore:0.2}), expecting mean reversion";
        }
        // Bullish divergence
        else if (bullishDivergence)
        {
            direction = BrainDirection.Rise;
            confidence = 0.68;
            reasoning = $"bullish divergence detected (price new low but RSI rising)";
        }
        // Bearish divergence
        else if (bearishDivergence)
        {
            direction = BrainDirection.Fall;
            confidence = 0.68;
            reasoning = $"bearish divergence detected (price new high but RSI falling)";
        }
        // Moderate oversold
        else if (rsi < config.OversoldRsi + 5 && zScore < -1.0)
        {
            direction = BrainDirection.Rise;
            confidence = 0.62;
            reasoning = $"moderately oversold (RSI {rsi:0.0}, Z-score {zScore:0.2}), looking for entry";
        }
        // Moderate overbought
        else if (rsi > config.OverboughtRsi - 5 && zScore > 1.0)
        {
            direction = BrainDirection.Fall;
            confidence = 0.62;
            reasoning = $"moderately overbought (RSI {rsi:0.0}, Z-score {zScore:0.2}), looking for entry";
        }
        else
        {
            return LlmDecision.Hold($"no mean reversion signal (RSI {rsi:0.0}, Z-score {zScore:0.2})");
        }

        return new LlmDecision(direction, confidence, 0, reasoning);
    }

    private static double CalculateRsi(double[] closes, int period)
    {
        if (closes.Length < period + 1) return double.NaN;

        var gains = new List<double>();
        var losses = new List<double>();

        for (int i = 1; i < closes.Length; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains.Add(change > 0 ? change : 0);
            losses.Add(change < 0 ? -change : 0);
        }

        var avgGain = gains.Take(period).Average();
        var avgLoss = losses.Take(period).Average();

        for (int i = period; i < gains.Count; i++)
        {
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
        }

        if (avgLoss == 0) return 100;
        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    private static (double upper, double middle, double lower) CalculateBollingerBands(
        double[] closes, int period, double stdDevMultiplier)
    {
        var sma = closes[^period..].Average();
        var variance = closes[^period..].Select(x => Math.Pow(x - sma, 2)).Average();
        var stdDev = Math.Sqrt(variance);

        return (sma + stdDevMultiplier * stdDev, sma, sma - stdDevMultiplier * stdDev);
    }
}
