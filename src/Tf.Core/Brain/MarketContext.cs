using System.Text;
using System.Text.Json;
using Tf.Core.Models;

namespace Tf.Core.Brain;

/// <summary>
/// A snapshot of the market + account state the brain reasons over. Built
/// from a recent tick window, indicators, balance, and recent trade lessons.
/// </summary>
public sealed record MarketContext(
    string Symbol,
    int TickCount,
    double LastPrice,
    double PercentChange,
    double SmaFast,
    double SmaSlow,
    double Rsi,
    string Trend,
    decimal Balance,
    string Currency,
    IReadOnlyList<string> Lessons);

public static class MarketContextBuilder
{
    public const string SystemPrompt =
        "You are the decision engine of an automated binary-options trading system " +
        "on a DEMO account. You receive a market context packet and recent trade " +
        "lessons. Reply with a single JSON object, no prose: " +
        "{\"direction\":\"RISE\"|\"FALL\"|\"HOLD\",\"confidence\":0.0-1.0,\"stake\":<number>," +
        "\"reasoning\":\"short explanation\"}. Direction HOLD means no trade. " +
        "Never trade when confidence is low or the setup is unclear. " +
        "Stake must be a positive number when trading, 0 when holding. " +
        "Binary options are probabilistic; prefer caution.";

    /// <summary>
    /// Builds the context packet. <paramref name="lessons"/> are recent outcomes
    /// fed back so the brain can adapt (e.g. \"last 3 RISE on frxEURUSD lost\").
    /// </summary>
    public static MarketContext Build(
        IReadOnlyList<Tick> window, decimal balance, string currency, IReadOnlyList<string> lessons)
    {
        var closes = window.Select(t => t.Quote).ToArray();

        var fast = Indicators.Sma(closes, 5);
        var slow = Indicators.Sma(closes, 20);
        var rsi = Indicators.Rsi(closes, 14);

        string trend;
        if (double.IsNaN(fast) || double.IsNaN(slow))
        {
            trend = "insufficient data";
        }
        else if (fast > slow * 1.0001)
        {
            trend = "up";
        }
        else if (fast < slow * 0.9999)
        {
            trend = "down";
        }
        else
        {
            trend = "flat";
        }

        return new MarketContext(
            window.Count > 0 ? window[^1].Symbol : "",
            window.Count,
            closes.Length > 0 ? closes[^1] : 0,
            Indicators.PercentChange(closes),
            fast,
            slow,
            rsi,
            trend,
            balance,
            currency,
            lessons.ToArray());
    }

    /// <summary>Renders the packet as the compact JSON the LLM is prompted with.</summary>
    public static string ToPromptJson(MarketContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"symbol\":\"{ctx.Symbol}\",");
        sb.Append($"\"ticks\":{ctx.TickCount},");
        sb.Append($"\"last_price\":{ctx.LastPrice:F5},");
        sb.Append($"\"change_pct\":{ctx.PercentChange:F3},");
        sb.Append($"\"sma_fast\":{FormatNum(ctx.SmaFast)},");
        sb.Append($"\"sma_slow\":{FormatNum(ctx.SmaSlow)},");
        sb.Append($"\"rsi\":{FormatNum(ctx.Rsi)},");
        sb.Append($"\"trend\":\"{ctx.Trend}\",");
        sb.Append($"\"balance\":{ctx.Balance},");
        sb.Append($"\"currency\":\"{ctx.Currency}\",");
        sb.Append("\"recent_lessons\":");
        sb.Append(JsonSerializer.Serialize(ctx.Lessons));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Builds lesson strings from the trade log (newest first).</summary>
    public static IReadOnlyList<string> LessonsFrom(IReadOnlyList<Trade> trades, int count = 5)
    {
        return trades
            .Take(count)
            .Select(t =>
                $"{t.SettledAt:MM-dd HH:mm} {t.Symbol} {t.Direction} stake {t.Stake:0.##} -> " +
                $"{t.Outcome} P&L {t.Profit:+0.##;-0.##;0}")
            .ToArray();
    }

    private static string FormatNum(double v) => double.IsNaN(v) ? "null" : v.ToString("F4");
}