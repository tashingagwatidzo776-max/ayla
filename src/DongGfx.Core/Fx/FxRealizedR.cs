namespace DongGfx.Core.Fx;

/// <summary>
/// Realized R for a settled FX trade — one honest ruler for every close
/// writer. The R unit is the stop the trade was SIZED with (the same
/// distance sizing, the placed SL and the exit brain's MAE ruler use), so
/// outcome and risk are measured identically.
///
/// Returns null when any input is unusable (missing/non-positive prices or
/// stop, unknown side) — callers journal <c>RealizedR = null,
/// OutcomeSource = "unknown"</c> rather than fabricate an outcome
/// (docs/superpowers/specs/2026-10-08-fx-win-rate-design.md §1).
/// Pure: no I/O, no clock.
/// </summary>
public static class FxRealizedR
{
    /// <summary>±R of the trade. <paramref name="side"/> must be "buy" or
    /// "sell"; anything else is unknown, not guessed.</summary>
    public static double? Compute(double entryPrice, double exitPrice, double stopDistance, string? side)
    {
        if (!Usable(entryPrice) || !Usable(exitPrice) || !Usable(stopDistance))
        {
            return null;
        }

        var dir = side switch
        {
            "buy" => 1.0,
            "sell" => -1.0,
            _ => 0.0,
        };
        if (dir == 0.0)
        {
            return null;
        }

        var r = dir * (exitPrice - entryPrice) / stopDistance;
        return double.IsNaN(r) || double.IsInfinity(r) ? null : r;
    }

    private static bool Usable(double v) => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v);
}
