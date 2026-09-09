namespace Tf.Core.Models;

/// <summary>Account balance reported by Deriv (authorize/balance responses).</summary>
public sealed record AccountBalance(
    decimal Balance,
    string Currency,
    string LoginId)
{
    public static AccountBalance Empty { get; } = new(0m, "USD", "");
}