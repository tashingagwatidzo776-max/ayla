using System.Globalization;

namespace Tf.Core.Brain;

/// <summary>
/// Parses the ensemble configuration free text: a comma/space/semicolon
/// separated list of brain keys with optional "key:weight" entries.
/// Weights are optional (default 1.0); unknown keys and bad weights are
/// skipped so one typo cannot disable the whole ensemble. Pure/headless so
/// it is unit-testable and reusable from the vault, runner, and UI.
/// </summary>
public static class EnsemblePlanParser
{
    /// <summary>The brain keys the registry actually offers.</summary>
    public static readonly IReadOnlyList<string> KnownBrainKeys =
        ["Growth", "TrendFollowing", "Breakout", "MeanReversion"];

    /// <summary>
    /// Parses "Growth:1.5 TrendFollowing Breakout:0.5" into (key, weight)
    /// pairs. Duplicates collapse to their first occurrence; a weight ≤ 0
    /// drops the entry (a zero-weight brain cannot vote). Empty input → an
    /// empty list, which the caller treats as "ensemble not configured".
    /// </summary>
    public static IReadOnlyList<(string Key, double Weight)> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, double)>();
        foreach (var raw in text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var colon = entry.IndexOf(':');
            var key = (colon < 0 ? entry : entry[..colon]).Trim();
            if (!KnownBrainKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue; // unknown brain: skip, don't fail
            }

            var weight = 1.0;
            if (colon >= 0)
            {
                var weightText = entry[(colon + 1)..].Trim().TrimEnd('x', 'X');
                if (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                    || w <= 0)
                {
                    continue; // bad weight: skip the entry
                }

                weight = w;
            }

            if (seen.Add(key))
            {
                result.Add((key, weight));
            }
        }

        return result;
    }

    /// <summary>Renders the pairs back to compact text ("Growth:1.5 Breakout")
    /// for round-tripping through the UI or the vault.</summary>
    public static string Render(IReadOnlyList<(string Key, double Weight)> entries) =>
        string.Join(" ", entries.Select(e => Math.Abs(e.Weight - 1.0) < 1e-9
            ? e.Key
            : $"{e.Key}:{e.Weight:0.##}"));
}
