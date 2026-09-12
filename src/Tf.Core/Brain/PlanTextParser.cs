using System.Globalization;

namespace Tf.Core.Brain;

/// <summary>
/// Headless parsing of the free-text fields on the Growth plan editor.
/// Extracted from the WPF view model so the rules are unit-testable
/// without a dispatcher; production behavior is unchanged.
/// </summary>
public static class PlanTextParser
{
    /// <summary>
    /// Parses a portfolio daily-drawdown cap from free text ("$150", "150",
    /// "1,000"). Blank/whitespace → null (governor disabled); anything that
    /// is not a positive number also disables it rather than throwing.
    /// </summary>
    public static decimal? TryCreateDrawdownCap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim().TrimStart('$').Replace(",", "");
        return decimal.TryParse(cleaned, CultureInfo.InvariantCulture, out var cap)
            && cap > 0 ? cap : null;
    }
}
