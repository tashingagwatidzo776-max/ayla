using System.Globalization;
using System.Text.RegularExpressions;

namespace DongGfx.Core.Brain;

/// <summary>Parsed form of Deriv's "market is closed" rejection. The API
/// answers a proposal for a closed market with code <c>MarketIsClosed</c>
/// and a message like "This market is presently closed. Market will open at
/// 2026-09-21 00:00:00." — an expected weekend/holiday state, not a
/// failure. This type extracts the reopen instant so engines can idle
/// until the market actually reopens instead of burning failure budget.</summary>
public static partial class MarketClosedNotice
{
    [GeneratedRegex(@"Market will open at (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})")]
    private static partial Regex ReopenPattern();

    /// <summary>Upper bound on how far ahead an extracted reopen time may
    /// schedule. Deriv states reopen in server time; a misread or exotic
    /// value must never idle an engine for days.</summary>
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(30);

    /// <summary>Fallback when the message carries no parseable timestamp:
    /// retry reasonably soon rather than spinning hot.</summary>
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromMinutes(5);

    /// <summary>Returns when the engine may ask for a proposal again, or
    /// null when the exception is not a market-closed rejection.</summary>
    public static DateTimeOffset? ParseReopenUtc(string? message, DateTimeOffset nowUtc)
    {
        if (message is null || !message.Contains("MarketIsClosed", StringComparison.Ordinal))
        {
            // The caller normally gates on the error code; the message may
            // still carry the code when wrapped. No match → not ours.
            if (message is null || !message.Contains("market is presently closed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var m = ReopenPattern().Match(message);
        var reopen = DateTimeOffset.UtcNow; // replaced below; kept for clarity
        if (m.Success
            && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            reopen = new DateTimeOffset(parsed, TimeSpan.Zero);
        }
        else
        {
            reopen = nowUtc + DefaultRetry;
        }

        var idle = reopen - nowUtc;
        if (idle <= TimeSpan.Zero)
        {
            return nowUtc + DefaultRetry; // already reopened per the clock — retry soon
        }
        if (idle > MaxIdle)
        {
            // Deriv quotes reopen in a possibly non-UTC clock; never trust it
            // for more than MaxIdle. Checking back periodically is cheap and
            // self-correcting: until the market truly opens, it just says so.
            return nowUtc + MaxIdle;
        }
        return reopen;
    }
}
