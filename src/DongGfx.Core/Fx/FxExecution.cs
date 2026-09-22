using System;
using System.Collections.Generic;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>One child order of an execution schedule.</summary>
public sealed record FxSlice(int Index, double Lots, DateTimeOffset? NotBefore = null);

/// <summary>
/// Family 12 — execution algos: pure schedule generators. The engine can
/// route any FxDecision through one of these instead of a single market
/// slice; rails still apply per child order at the bridge ticket path.
/// </summary>
public static class FxExecution
{
    /// <summary>TWAP: split total lots into n equal time-spaced slices.</summary>
    public static IReadOnlyList<FxSlice> Twap(double totalLots, int slices, DateTimeOffset start, TimeSpan interval)
    {
        if (totalLots <= 0 || slices <= 0)
        {
            return Array.Empty<FxSlice>();
        }

        var per = System.Math.Round(totalLots / slices, 2);
        var list = new List<FxSlice>(slices);
        for (var i = 0; i < slices; i++)
        {
            list.Add(new FxSlice(i, per, start + TimeSpan.FromTicks(interval.Ticks * i)));
        }

        return list;
    }

    /// <summary>VWAP-ish participation: slices sized by that period's relative
    /// volume weights (normalized), so execution follows the day's volume shape.
    /// </summary>
    public static IReadOnlyList<FxSlice> Vwap(double totalLots, IReadOnlyList<double> periodVolumes, DateTimeOffset start, TimeSpan interval)
    {
        if (totalLots <= 0 || periodVolumes is not { Count: > 0 } || periodVolumes.All(v => v <= 0))
        {
            return Array.Empty<FxSlice>();
        }

        var total = periodVolumes.Sum();
        var list = new List<FxSlice>(periodVolumes.Count);
        double placed = 0;
        for (var i = 0; i < periodVolumes.Count; i++)
        {
            var lots = i == periodVolumes.Count - 1
                ? System.Math.Round(totalLots - placed, 2)                       // last slice absorbs rounding
                : System.Math.Round(totalLots * periodVolumes[i] / total, 2);
            placed += lots;
            list.Add(new FxSlice(i, lots, start + TimeSpan.FromTicks(interval.Ticks * i)));
        }

        return list;
    }

    /// <summary>Iceberg: show only clipSize at a time; the schedule emits clips
    /// until the total is exhausted (UI/bridge advances on each child fill).
    /// </summary>
    public static IReadOnlyList<FxSlice> Iceberg(double totalLots, double clipSize)
    {
        if (totalLots <= 0 || clipSize <= 0)
        {
            return Array.Empty<FxSlice>();
        }

        var list = new List<FxSlice>();
        double remaining = totalLots;
        for (var i = 0; remaining > 1e-9; i++)
        {
            var clip = System.Math.Min(clipSize, remaining);
            list.Add(new FxSlice(i, System.Math.Round(clip, 2)));
            remaining -= clip;
        }

        return list;
    }
}

/// <summary>
/// Family 13 — market-making skeleton (needs real depth from the bridge to
/// go beyond quoting logic): two-sided quotes around a fair value with an
/// inventory-skewed reservation price. Pure math only — no order path.
/// </summary>
public static class FxMarketMaking
{
    /// <param name="fair">fair value (e.g. mid or Kalman level)</param>
    /// <param name="gamma">inventory risk aversion (0 = symmetric)</param>
    /// <param name="inventory">current net position (lots, +long)</param>
    /// <param name="maxInventory">hard inventory cap</param>
    /// <returns>(bid, ask) or null when the inventory cap blocks new quotes</returns>
    public static (double Bid, double Ask)? Quote(double fair, double halfSpread, double gamma,
        double inventory, double maxInventory)
    {
        if (halfSpread <= 0 || maxInventory <= 0 || fair <= 0)
        {
            return null;
        }

        if (System.Math.Abs(inventory) >= maxInventory &&
            System.Math.Sign(inventory) == (inventory > 0 ? 1 : -1))
        {
            // at the cap: quote only the reducing side by skewing fully
            var reduce = inventory > 0 ? fair - halfSpread : fair + halfSpread;
            return inventory > 0 ? (reduce, double.NaN) : (double.NaN, reduce);
        }

        // Avellaneda–Stoikov-lite reservation price: fair minus gamma * inventory
        var reservation = fair - gamma * inventory;
        return (reservation - halfSpread, reservation + halfSpread);
    }
}
