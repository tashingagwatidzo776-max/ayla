namespace DongGfx.Core.Models;

/// <summary>Trade direction mapped to Deriv contract types (CALL / PUT).</summary>
public enum Direction
{
    Rise,
    Fall
}

public static class DirectionExtensions
{
    public static string ToContractType(this Direction direction) =>
        direction == Direction.Rise ? "CALL" : "PUT";
}

/// <summary>A Deriv proposal (quote) for a binary contract, ready to buy.</summary>
public sealed record Proposal(
    string Id,
    string Symbol,
    Direction Direction,
    decimal Amount,
    string Currency,
    int DurationMinutes,
    double Spot,
    string Longcode,
    decimal Payout);

/// <summary>Result of buying a proposal.</summary>
public sealed record BuyResult(
    string ContractId,
    decimal BuyPrice,
    decimal BalanceAfter,
    string Longcode);

/// <summary>Lifecycle state of an open contract.</summary>
public enum ContractStatus
{
    Open,
    Won,
    Lost,
    Sold,
    Cancelled,
    Unknown
}

/// <summary>Snapshot of an open/settled contract from proposal_open_contract.</summary>
public sealed record ContractInfo(
    string ContractId,
    ContractStatus Status,
    double EntrySpot,
    double ExitSpot,
    long EntryTime,
    long ExitTime,
    decimal BuyPrice,
    decimal Profit,
    string Currency,
    bool IsSold);

/// <summary>Identifies which part of the system placed a trade.</summary>
public static class TradeSource
{
    public const string Manual = "Manual";
    public const string Llm = "LLM";
    public const string Growth = "Growth";
}

/// <summary>
/// A completed trade as recorded in the trade log. Only settled contracts are
/// stored; in-flight contracts live in the UI until they resolve.
/// </summary>
/// <remarks>
/// <paramref name="AccountId"/>/<paramref name="AccountName"/> tag multi-account
/// trades (null = the primary account), <paramref name="Source"/> says which
/// brain placed it. All three are optional so previously persisted logs keep
/// deserializing.
/// </remarks>
public sealed record Trade(
    Guid Id,
    string Symbol,
    Direction Direction,
    decimal Stake,
    string Currency,
    double EntrySpot,
    long EntryEpoch,
    string ContractId,
    ContractStatus Outcome,
    decimal Profit,
    double? ExitSpot,
    long? ExitEpoch,
    DateTimeOffset SettledAt,
    Guid? AccountId = null,
    string? AccountName = null,
    string? Source = null)
{
    public bool IsWin => Outcome == ContractStatus.Won;
}

/// <summary>Aggregated stats for a single day.</summary>
public sealed record DailySummary(int Count, int Wins, int Losses, decimal NetProfit)
{
    public double WinRate => Count == 0 ? 0 : (double)Wins / Count;
}