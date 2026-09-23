namespace DongGfx.Core.Models;

/// <summary>
/// A settled trade, venue-neutral: an MT5/FX deal (or any future venue).
/// The binary-options contract shape (direction, contract id, payout,
/// won/lost) is gone with the Deriv integration; what performance and
/// metrics care about is stake, realised P/L, when it settled, and which
/// account/strategy produced it.
/// </summary>
/// <remarks>
/// <paramref name="AccountId"/>/<paramref name="AccountName"/> tag trades
/// per account (null = the default account) and <paramref name="Source"/>
/// says which engine placed it (see <see cref="TradeSource"/>). All three
/// are optional so previously persisted analytics keep deserializing.
/// </remarks>
public sealed record Trade(
    Guid Id,
    string Symbol,
    decimal Stake,
    decimal Profit,
    DateTimeOffset SettledAt,
    string? Source = null,
    Guid? AccountId = null,
    string? AccountName = null,
    string? Currency = null)
{
    /// <summary>A settled trade counts as a win when it closed green.</summary>
    public bool IsWin => Profit > 0;
}

/// <summary>Identifies which part of the system placed a trade.</summary>
public static class TradeSource
{
    public const string Manual = "Manual";
    public const string Fx = "FX";
}

/// <summary>Aggregated stats for a single day.</summary>
public sealed record DailySummary(int Count, int Wins, int Losses, decimal NetProfit)
{
    public double WinRate => Count == 0 ? 0 : (double)Wins / Count;
}
