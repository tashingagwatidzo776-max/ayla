using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Per-pair calibration (go-live plan Steps 3-4): the spec grammar, its
/// clamps (a hand-edited file can never size past the caps or set a bar
/// below a pip), the tolerant parse (a bad entry degrades to that pair's
/// defaults — never a boot failure), the case-insensitive symbol lookup
/// and the session-window math. Pure: no I/O, no clock, no orders.
/// </summary>
[Trait("Category", "Unit")]
public class FxPairCalibrationTests
{
    [Fact]
    public void Parse_Reads_Every_Knob()
    {
        var table = FxPairCalibration.Parse(
            "XAUUSDmicro:conf=0.4,gwen=70,session=7-16,lots=0.5,size=2,tp=3,mae=1.3,bars=24");

        var cal = FxPairCalibration.For(table, "XAUUSDmicro");
        Assert.Equal(0.4, cal.MinConfidence);
        Assert.Equal(70, cal.GwVetoFloor);
        Assert.Equal(7, cal.SessionStartUtc);
        Assert.Equal(16, cal.SessionEndUtc);
        Assert.Equal(0.5, cal.MaxLots);
        Assert.Equal(2.0, cal.SizeMult);
        Assert.Equal(3.0, cal.RewardTargetR);
        Assert.Equal(1.3, cal.MaeEmergencyR);
        Assert.Equal(24, cal.MaxStagnantBars);
        // The record the exit brain receives carries exactly those bars.
        Assert.Equal(1.3, cal.ExitTuning.MaeEmergencyR);
        Assert.Equal(24, cal.ExitTuning.MaxStagnantBars);
    }

    [Fact]
    public void Clamps_Keep_A_Hand_Edited_Spec_Inside_The_Rails()
    {
        var cal = FxPairCalibration.For(
            FxPairCalibration.Parse(
                "X:conf=9,gwen=500,session=7-16,lots=999,size=0.001,tp=99,mae=99,bars=0"),
            "X");

        Assert.Equal(1, cal.MinConfidence);
        Assert.Equal(100, cal.GwVetoFloor);
        Assert.Equal(7, cal.SessionStartUtc);   // a valid window is untouched
        Assert.Equal(16, cal.SessionEndUtc);
        Assert.Equal(10.0, cal.MaxLots);
        Assert.Equal(0.1, cal.SizeMult);
        Assert.Equal(10.0, cal.RewardTargetR);
        Assert.Equal(5.0, cal.MaeEmergencyR);
        Assert.Equal(1, cal.MaxStagnantBars);
    }

    [Fact]
    public void Malformed_Entries_Degrade_To_Defaults_Never_An_Exception()
    {
        // No colon, empty symbol, junk values, blank chunks — every one is
        // tolerated: the desk trades on defaults instead of refusing boot.
        var table = FxPairCalibration.Parse("nocolon;:conf=0.5;X:conf=oops,bars=12,noequals; ;");

        var x = FxPairCalibration.For(table, "X");
        Assert.Null(x.MinConfidence);            // bad value skipped…
        Assert.Equal(12, x.MaxStagnantBars);     // …the good knob still lands

        // Absent symbols (and a null table) fall to the unchanged desk.
        Assert.Same(FxPairCalibration.Default, FxPairCalibration.For(table, "NOPE"));
        Assert.Same(FxPairCalibration.Default, FxPairCalibration.For(null, "XAUUSDmicro"));

        Assert.Empty(FxPairCalibration.Parse(""));
        Assert.Empty(FxPairCalibration.Parse(null));
        Assert.Empty(FxPairCalibration.Parse("   ; ;"));

        // A knob-only chunk without a symbol is skipped, not guessed at.
        Assert.Empty(FxPairCalibration.Parse(":conf=0.9"));
    }

    [Fact]
    public void Symbol_Lookup_Is_Case_Insensitive()
    {
        var table = FxPairCalibration.Parse("xauusdmicro:mae=1.3");
        Assert.Equal(1.3, FxPairCalibration.For(table, "XAUUSDmicro").MaeEmergencyR);
        Assert.Equal(1.3, FxPairCalibration.For(table, "xauusdmicro").MaeEmergencyR);
    }

    [Theory]
    // Plain window [7, 16): start inclusive, end exclusive.
    [InlineData(6, 7, 16, false)]   // before the window
    [InlineData(7, 7, 16, true)]    // exactly the open
    [InlineData(15, 7, 16, true)]   // inside
    [InlineData(16, 7, 16, false)]  // end is exclusive
    // Wrap past midnight [22, 6).
    [InlineData(23, 22, 6, true)]
    [InlineData(5, 22, 6, true)]
    [InlineData(7, 22, 6, false)]
    [InlineData(21, 22, 6, false)]
    // Degenerate = no restriction; and no window at all = always in session.
    [InlineData(3, 7, 7, true)]
    [InlineData(23, -1, -1, true)]
    public void Session_Window_Includes_The_Requested_Hour(int hour, int start, int end, bool expected)
    {
        var cal = start < 0
            ? FxPairCalibration.Default
            : FxPairCalibration.For(FxPairCalibration.Parse($"X:session={start}-{end}"), "X");

        var at = new DateTimeOffset(2026, 10, 28, hour, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, cal.InSession(at));
    }

    [Theory]
    // Out of the 0..23 range, or unparseable entirely → the knob is
    // skipped (no window) rather than a broken dispatch gate.
    [InlineData("session=25-30")]
    [InlineData("session=-1-5")]
    [InlineData("session=7")]
    [InlineData("session=seven-sixteen")]
    [InlineData("session=7-")]
    public void Invalid_Session_Knobs_Are_Skipped_Not_Guessed(string knob)
    {
        var cal = FxPairCalibration.For(FxPairCalibration.Parse($"X:{knob}"), "X");
        Assert.Null(cal.SessionStartUtc);
        Assert.Null(cal.SessionEndUtc);
        Assert.True(cal.InSession(new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_Shipped_Default_Spec_Parses_With_Exit_Bars_For_Every_Metal()
    {
        // AppSettings' default spec is what a fresh install boots with —
        // gold tightened to 1.3R, both silvers shortened to 24 bars.
        var spec = new DongGfx.Core.Models.AppSettings().FxPairCalibration;
        var table = FxPairCalibration.Parse(spec);

        Assert.Equal(1.3, FxPairCalibration.For(table, "XAUUSDmicro").MaeEmergencyR);
        Assert.Equal(24, FxPairCalibration.For(table, "XAGUSD").MaxStagnantBars);
        Assert.Equal(24, FxPairCalibration.For(table, "XAGUSDmicro").MaxStagnantBars);
    }

    [Fact]
    public void The_Shipped_Portfolio_Is_Metals_Only()
    {
        // Step 1: EURUSD/GBPUSD were vetoed daily (dead tape) and blocked
        // the all-or-nothing paper-soak badge; the desk trades metals.
        var symbols = new DongGfx.Core.Models.AppSettings().FxSymbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("XAUUSDmicro", symbols);
        Assert.Contains("XAGUSD", symbols);
        Assert.Contains("XAGUSDmicro", symbols);
        Assert.DoesNotContain("EURUSD", symbols);
        Assert.DoesNotContain("GBPUSD", symbols);
        Assert.DoesNotContain("USDJPY", symbols);
    }
}
