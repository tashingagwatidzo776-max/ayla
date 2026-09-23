namespace DongGfx.Core.Models;

/// <summary>
/// A single market tick as delivered by the MT5 bridge quote feed.
/// Epoch is milliseconds since Unix epoch; Quote is the mid price.
/// </summary>
public sealed record Tick(
    string Symbol,
    double Quote,
    double Ask,
    double Bid,
    long Epoch,
    int PipSize)
{
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Epoch);

    public static Tick FromJson(string symbol, double quote, double ask, double bid, long epoch, int pipSize) =>
        new(symbol, quote, ask, bid, epoch, pipSize);
}