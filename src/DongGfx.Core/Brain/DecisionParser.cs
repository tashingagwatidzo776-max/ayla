using System.Text.Json;

namespace DongGfx.Core.Brain;

/// <summary>
/// Parses the LLM's JSON decision. Never throws on malformed input — a bad
/// response degrades to HOLD so the risk engine can reject it gracefully.
/// </summary>
public static class DecisionParser
{
    /// <summary>
    /// Extracts the JSON object from the model's reply, tolerating markdown
    /// fences or trailing prose some models add.
    /// </summary>
    public static LlmDecision Parse(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return LlmDecision.Hold($"Unparseable LLM response: {Truncate(raw)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var directionText = GetString(root, "direction") ?? "";
            var direction = directionText.ToUpperInvariant() switch
            {
                "RISE" or "CALL" or "UP" => BrainDirection.Rise,
                "FALL" or "PUT" or "DOWN" => BrainDirection.Fall,
                _ => BrainDirection.Hold
            };

            var confidence = GetDouble(root, "confidence") ?? 0;
            confidence = Math.Clamp(confidence, 0, 1);

            var stake = GetDecimal(root, "stake") ?? 0m;
            if (stake < 0)
            {
                stake = 0;
            }

            var reasoning = GetString(root, "reasoning") ?? "";

            return new LlmDecision(direction, confidence, stake, reasoning);
        }
        catch (JsonException)
        {
            return LlmDecision.Hold($"Malformed JSON: {Truncate(raw)}");
        }
    }

    public static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.Number
            ? el.GetDouble()
            : null;

    private static decimal? GetDecimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.Number
            ? el.GetDecimal()
            : null;

    private static string Truncate(string s) => s is null ? "(null)" : s.Length > 200 ? s[..200] + "…" : s;
}