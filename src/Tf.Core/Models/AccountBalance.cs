using System.Text.Json;

namespace Tf.Core.Models;

/// <summary>Account balance reported by Deriv (authorize/balance responses).
/// <see cref="IsVirtual"/> comes from Deriv's own <c>is_virtual</c> flag —
/// the API-level truth about whether the authorized account plays with
/// virtual funds. Unknown defaults to virtual: safety treats an
/// unverified account as demo, never as real.</summary>
public sealed record AccountBalance(
    decimal Balance,
    string Currency,
    string LoginId,
    bool IsVirtual = true)
{
    public static AccountBalance Empty { get; } = new(0m, "USD", "", IsVirtual: true);

    /// <summary>Extracts the <c>is_virtual</c> flag from a Deriv
    /// authorize/balance payload. Deriv sends it as a numeric flag (1/0) in
    /// practice, but a JSON true/false must also parse. Missing or malformed
    /// → true (unverified = treated as virtual; never assume real).</summary>
    public static bool ParseIsVirtual(JsonElement balance)
    {
        if (!balance.TryGetProperty("is_virtual", out var v))
        {
            return true;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return v.TryGetInt32(out var flag) && flag != 0;
            case JsonValueKind.String:
                var text = v.GetString();
                if (bool.TryParse(text, out var parsed))
                {
                    return parsed;
                }
                if (int.TryParse(text, out var numeric))
                {
                    return numeric != 0;
                }
                break;
        }

        return true;
    }
}