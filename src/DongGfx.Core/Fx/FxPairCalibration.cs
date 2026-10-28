using System;
using System.Collections.Generic;
using System.Globalization;

namespace DongGfx.Core.Fx;

/// <summary>
/// Per-pair calibration (go-live plan steps 3-4): operator overrides keyed
/// by the engine's symbol, so each pair carries ITS OWN strategy parameters
/// instead of one global dial. Pure parse + lookup — no I/O; the engine
/// host consults it at every gate (entry floor, session window, GWEN veto
/// floor, sizing, venue TP, exit tuning).
///
/// Spec format — semicolon-separated pairs, comma-separated knobs:
/// <code>
///   "XAUUSDmicro:mae=1.3;XAGUSD:bars=24,conf=0.30"
/// </code>
/// Knobs (all optional, all clamped to sane ranges):
///   conf     [0..1]    entry-confidence floor for THIS pair (else the
///                      global FxMinEntryConfidence)
///   gwen     [0..100]  GWEN verdicts below this confidence ABSTAIN on this
///                      pair (a lower bound on veto power, never on trading
///                      — below the floor the gate passes, fail-neutral)
///   session  hh-hh     allowed dispatch window in UTC hours, inclusive
///                      start / exclusive end, wrap supported (22-6);
///                      degenerate hh-hh = no restriction
///   lots     (0..10]   per-pair lot cap, min'd with the global caps
///   size     [0.1..10] multiplier applied to the signal's suggested lots
///   tp       [0..10]   venue take-profit R for this pair (0 = no venue TP
///                      for the pair), else the global VenueRewardTargetR
///   mae      (0..5]    exit brain's MAE emergency bar in R (the drawdown
///                      override that closes at max pain)
///   bars     [1..10000] exit brain's stagnant time stop in M1 bars
///
/// Law: a missing knob = today's global/const behavior, and an absent
/// symbol falls back to <see cref="Default"/> — calibration can only ever
/// be as strict as what it says, never silently different elsewhere.
/// The parse is tolerant by design: a malformed entry or knob is SKIPPED
/// (the desk trades on its defaults) rather than refusing to boot, and
/// clamping means a hand-edited file can never size past the caps or set
/// a bar below a pip.
/// </summary>
public sealed record FxPairCalibration
{
    /// <summary>Every knob null — the historical, uncalibrated behavior.</summary>
    public static FxPairCalibration Default { get; } = new();

    /// <summary>Entry-confidence floor for this pair (null = global floor).</summary>
    public double? MinConfidence { get; init; }

    /// <summary>GWEN vetoes only bind at/above this confidence on this pair
    /// (null = the advisor's own constants, unchanged).</summary>
    public int? GwVetoFloor { get; init; }

    /// <summary>Allowed dispatch window start, UTC hour 0-23 inclusive
    /// (null = no window restriction).</summary>
    public int? SessionStartUtc { get; init; }

    /// <summary>Allowed dispatch window end, UTC hour exclusive; a value
    /// before the start wraps past midnight (null with the start = none).</summary>
    public int? SessionEndUtc { get; init; }

    /// <summary>Per-pair lot cap (null = only the global caps apply).</summary>
    public double? MaxLots { get; init; }

    /// <summary>Multiplier on the signal's suggested lots (null = 1.0).</summary>
    public double? SizeMult { get; init; }

    /// <summary>Venue take-profit R for this pair (null = the global
    /// VenueRewardTargetR; 0 = no venue TP on this pair).</summary>
    public double? RewardTargetR { get; init; }

    /// <summary>Exit brain MAE emergency bar in R (null = the brain's
    /// <see cref="FxExitBrain.MaeEmergencyR"/> constant).</summary>
    public double? MaeEmergencyR { get; init; }

    /// <summary>Exit brain stagnant time stop in M1 bars (null = the brain's
    /// <see cref="FxExitBrain.MaxStagnantBars"/> constant).</summary>
    public int? MaxStagnantBars { get; init; }

    /// <summary>The exit tuning this pair passes to the brain; null fields
    /// fall through to the brain's constants.</summary>
    public FxExitTuning ExitTuning => new(MaeEmergencyR, MaxStagnantBars);

    /// <summary>True when <paramref name="utc"/> falls inside this pair's
    /// dispatch window (no window configured, or a degenerate one = always).</summary>
    public bool InSession(DateTimeOffset utc)
    {
        if (SessionStartUtc is not int start || SessionEndUtc is not int end || start == end)
        {
            return true;
        }
        var hour = utc.UtcDateTime.Hour;
        return start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end;   // wrap past midnight
    }

    /// <summary>Parse the settings spec into a case-insensitive symbol
    /// table. Never throws: malformed entries are skipped so a hand-edited
    /// spec degrades to defaults for the affected pair only.</summary>
    public static IReadOnlyDictionary<string, FxPairCalibration> Parse(string? spec)
    {
        var table = new Dictionary<string, FxPairCalibration>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(spec))
        {
            return table;
        }

        foreach (var chunk in spec.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = chunk.IndexOf(':');
            if (colon <= 0)
            {
                continue;   // "XAUUSDmicro" or ":..." — no symbol, skip
            }
            var symbol = chunk[..colon].Trim();
            if (symbol.Length == 0)
            {
                continue;
            }
            table[symbol] = ParseEntry(chunk[(colon + 1)..]);
        }
        return table;
    }

    /// <summary>The calibrated entry for <paramref name="symbol"/>, or
    /// <see cref="Default"/> when the table has no opinion on it.</summary>
    public static FxPairCalibration For(
        IReadOnlyDictionary<string, FxPairCalibration>? table, string symbol)
        => table is not null && !string.IsNullOrWhiteSpace(symbol)
           && table.TryGetValue(symbol, out var cal)
            ? cal
            : Default;

    private static FxPairCalibration ParseEntry(string knobs)
    {
        var cal = new FxPairCalibration();
        foreach (var part in knobs.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part[..eq].Trim().ToLowerInvariant();
            var raw = part[(eq + 1)..].Trim();

            switch (key)
            {
                case "conf":
                    if (AsDouble(raw) is { } conf)
                    {
                        cal = cal with { MinConfidence = Math.Clamp(conf, 0, 1) };
                    }
                    break;
                case "gwen":
                    if (AsDouble(raw) is { } gwen)
                    {
                        cal = cal with { GwVetoFloor = (int)Math.Clamp(gwen, 0, 100) };
                    }
                    break;
                case "session":
                    if (ParseSession(raw, out var s, out var e))
                    {
                        cal = cal with { SessionStartUtc = s, SessionEndUtc = e };
                    }
                    break;
                case "lots":
                    if (AsDouble(raw) is { } lots)
                    {
                        cal = cal with { MaxLots = Math.Clamp(lots, 0.01, 10) };
                    }
                    break;
                case "size":
                    if (AsDouble(raw) is { } size)
                    {
                        cal = cal with { SizeMult = Math.Clamp(size, 0.1, 10) };
                    }
                    break;
                case "tp":
                    if (AsDouble(raw) is { } tp)
                    {
                        cal = cal with { RewardTargetR = Math.Clamp(tp, 0, 10) };
                    }
                    break;
                case "mae":
                    if (AsDouble(raw) is { } mae)
                    {
                        cal = cal with { MaeEmergencyR = Math.Clamp(mae, 0.1, 5) };
                    }
                    break;
                case "bars":
                    if (AsDouble(raw) is { } bars)
                    {
                        cal = cal with
                        {
                            MaxStagnantBars = (int)Math.Clamp(bars, 1, 10000),
                        };
                    }
                    break;
                // unknown knob: skipped on purpose — forward-compatible specs
            }
        }
        return cal;
    }

    private static double? AsDouble(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && double.IsFinite(v)
            ? v
            : null;

    private static bool ParseSession(string raw, out int start, out int end)
    {
        start = 0;
        end = 0;
        var dash = raw.IndexOf('-');
        if (dash <= 0 || dash >= raw.Length - 1)
        {
            return false;
        }
        if (!int.TryParse(raw[..dash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
            !int.TryParse(raw[(dash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }
        if (start is < 0 or > 23 || end is < 0 or > 23)
        {
            return false;
        }
        return true;
    }
}
