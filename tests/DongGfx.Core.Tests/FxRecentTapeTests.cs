using System.Text.Json;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// ROSTER-POLICY items 2-4 as shipped (docs/soak/ROSTER-POLICY.md):
/// default-keep below n=30, the asymmetric −0.10R bar, two-fail-to-exclude
/// hysteresis with one-pass re-entry, and strict (symbol, family)
/// isolation. Plus the one honest R ruler (FxRealizedR) and the journal
/// reseed that keeps windows alive across deploys — including its
/// never-fabricate law: unknown outcomes are counted, never guessed.
/// </summary>
[Trait("Category", "Unit")]
public class FxRecentTapeTests
{
    // ── FxRecentTape ─────────────────────────────────────────────────────

    [Fact]
    public void DefaultKeep_Thin_Window_Never_Excludes_Even_When_Catastrophic()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN - 1; i++)
        {
            tape.Record("XAUUSDmicro", "vol-breakout", -5.0);
        }

        Assert.Equal(FxRecentTape.WindowN - 1, tape.WindowCount("XAUUSDmicro", "vol-breakout"));
        Assert.False(tape.IsExcluded("XAUUSDmicro", "vol-breakout"));
        Assert.Equal(0, tape.FailStreak("XAUUSDmicro", "vol-breakout"));
    }

    [Fact]
    public void First_Qualifying_Failure_Raises_Streak_But_Keeps_The_Family()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN; i++)
        {
            tape.Record("XAUUSDmicro", "vol-breakout", -1.0);
        }

        Assert.Equal(1, tape.FailStreak("XAUUSDmicro", "vol-breakout"));
        Assert.False(tape.IsExcluded("XAUUSDmicro", "vol-breakout"));
    }

    [Fact]
    public void Second_Consecutive_Qualifying_Failure_Excludes()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN + 1; i++)
        {
            tape.Record("XAUUSDmicro", "vol-breakout", -1.0);
        }

        Assert.True(tape.IsExcluded("XAUUSDmicro", "vol-breakout"));
    }

    [Fact]
    public void Mediocre_But_Not_Negative_Family_Stays()
    {
        // −0.05R mean over a full window: above the −0.10R bar → keep.
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN + 5; i++)
        {
            tape.Record("EURUSD", "ema-slope", -0.05);
        }

        Assert.False(tape.IsExcluded("EURUSD", "ema-slope"));
        Assert.Equal(0, tape.FailStreak("EURUSD", "ema-slope"));
    }

    [Fact]
    public void One_Passing_Evaluation_Re_Enters_An_Excluded_Family()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN + 1; i++)
        {
            tape.Record("XAUUSDmicro", "vol-breakout", -1.0);
        }
        Assert.True(tape.IsExcluded("XAUUSDmicro", "vol-breakout"));

        // A single evaluation that lifts the window mean above the bar (one
        // passing window — +30R flips a 30×−1R window on its own) re-enters
        // the family immediately.
        tape.Record("XAUUSDmicro", "vol-breakout", +30.0);

        Assert.False(tape.IsExcluded("XAUUSDmicro", "vol-breakout"));
        Assert.Equal(0, tape.FailStreak("XAUUSDmicro", "vol-breakout"));
    }

    [Fact]
    public void Fail_Streak_Resets_When_A_Passing_Evaluation_Lands_Between_Failures()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN; i++)
        {
            tape.Record("XAGUSD", "bb-rev", -1.0);
        }
        Assert.Equal(1, tape.FailStreak("XAGUSD", "bb-rev"));

        // One evaluation above the bar clears the streak…
        tape.Record("XAGUSD", "bb-rev", +30.0);
        Assert.Equal(0, tape.FailStreak("XAGUSD", "bb-rev"));

        // …so the next run of failures starts over at one, not two: feed
        // −1R until the window mean crosses the bar again and stop at the
        // FIRST failing evaluation (the rolling window keeps the +30R note
        // alive for a while, so a fixed count would be brittle here).
        var guard = 0;
        while (tape.FailStreak("XAGUSD", "bb-rev") == 0 && guard++ < 100)
        {
            tape.Record("XAGUSD", "bb-rev", -1.0);
        }
        Assert.Equal(1, tape.FailStreak("XAGUSD", "bb-rev"));
        Assert.False(tape.IsExcluded("XAGUSD", "bb-rev"));
    }

    [Fact]
    public void A_Single_Passing_Evaluation_Clears_The_Fail_Streak()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN; i++)
        {
            tape.Record("XAGUSD", "bb-rev", -1.0);
        }
        Assert.Equal(1, tape.FailStreak("XAGUSD", "bb-rev"));

        // One +30R record lifts the 30×−1R window mean above the bar in a
        // single evaluation — the streak clears (and re-entry would too).
        tape.Record("XAGUSD", "bb-rev", +30.0);

        Assert.Equal(0, tape.FailStreak("XAGUSD", "bb-rev"));
        Assert.False(tape.IsExcluded("XAGUSD", "bb-rev"));
    }

    [Fact]
    public void Cells_Are_Isolated_Per_Symbol_And_Family()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN + 1; i++)
        {
            tape.Record("XAUUSDmicro", "vol-breakout", -1.0);
            tape.Record("XAUUSDmicro", "kalman-trend", +1.0);
            tape.Record("GBPUSD", "vol-breakout", -1.0);
        }

        Assert.True(tape.IsExcluded("XAUUSDmicro", "vol-breakout"));
        Assert.False(tape.IsExcluded("XAUUSDmicro", "kalman-trend"));   // winner untouched
        Assert.True(tape.IsExcluded("GBPUSD", "vol-breakout"));         // own cell, own fate
        Assert.False(tape.IsExcluded("EURUSD", "vol-breakout"));        // unknown cell: keep
    }

    [Fact]
    public void Window_Rolls_Keeping_The_Newest_Thirty()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN; i++)
        {
            tape.Record("XAUUSDmicro", "hurst-trend", -5.0);
        }
        for (var i = 0; i < FxRecentTape.WindowN; i++)
        {
            tape.Record("XAUUSDmicro", "hurst-trend", +1.0);
        }

        Assert.Equal(FxRecentTape.WindowN, tape.WindowCount("XAUUSDmicro", "hurst-trend"));
        // The evicted −5.0R samples are gone: the mean is the recent +1.0R.
        Assert.Equal(1.0, tape.WindowMeanR("XAUUSDmicro", "hurst-trend")!.Value, 3);
    }

    [Fact]
    public void Non_Finite_And_Blank_Keys_Are_Ignored()
    {
        var tape = new FxRecentTape();
        tape.Record("", "vol-breakout", 1.0);
        tape.Record("XAUUSDmicro", "", 1.0);
        tape.Record("XAUUSDmicro", "vol-breakout", double.NaN);
        tape.Record("XAUUSDmicro", "vol-breakout", double.PositiveInfinity);

        Assert.Equal(0, tape.WindowCount("XAUUSDmicro", "vol-breakout"));
        Assert.Null(tape.WindowMeanR("XAUUSDmicro", "vol-breakout"));
    }

    [Fact]
    public void Cells_Snapshot_Reports_Excluded_First()
    {
        var tape = new FxRecentTape();
        for (var i = 0; i < FxRecentTape.WindowN + 1; i++)
        {
            tape.Record("XAUUSDmicro", "vol-breakout", -1.0);
        }
        tape.Record("XAUUSDmicro", "kalman-trend", +0.5);

        var cells = tape.Cells();
        Assert.Equal(2, cells.Count);
        Assert.True(cells[0].Excluded);
        Assert.Equal("vol-breakout", cells[0].Family);
        Assert.False(cells[1].Excluded);
        Assert.Equal(0.5, cells[1].MeanR!.Value, 3);
    }

    // ── FxRealizedR ──────────────────────────────────────────────────────

    [Fact]
    public void RealizedR_Measures_The_Sized_Stop_Distance_With_Side_Sign()
    {
        Assert.Equal(1.5, FxRealizedR.Compute(100.0, 101.5, 1.0, "buy"));
        Assert.Equal(-0.5, FxRealizedR.Compute(100.0, 99.5, 1.0, "buy"));
        // Sell inverts: entry 100 → exit 99 is a WIN.
        Assert.Equal(1.0, FxRealizedR.Compute(100.0, 99.0, 1.0, "sell"));
        Assert.Equal(-2.0, FxRealizedR.Compute(100.0, 102.0, 1.0, "sell"));
    }

    [Fact]
    public void RealizedR_Is_Null_Never_Wrong_When_Inputs_Are_Unusable()
    {
        Assert.Null(FxRealizedR.Compute(100.0, 101.0, 0.0, "buy"));      // no R unit
        Assert.Null(FxRealizedR.Compute(0.0, 101.0, 1.0, "buy"));        // no entry
        Assert.Null(FxRealizedR.Compute(100.0, 0.0, 1.0, "buy"));        // no exit
        Assert.Null(FxRealizedR.Compute(100.0, 101.0, 1.0, null));       // side unknown
        Assert.Null(FxRealizedR.Compute(100.0, 101.0, 1.0, "hold"));     // side unknown
        Assert.Null(FxRealizedR.Compute(double.NaN, 101.0, 1.0, "buy"));
        Assert.Null(FxRealizedR.Compute(100.0, 101.0, double.PositiveInfinity, "buy"));
    }

    // ── FxRecentTapeReseed ───────────────────────────────────────────────

    private static string Envelope(string timestamp, string category, string details) =>
        JsonSerializer.Serialize(new { Timestamp = timestamp, Category = category, Details = details });

    private static string FillDetails(string side, string symbol, double price, double stop, long ticket, string family) =>
        $"{side} 0.1 lots {symbol} @ {price:0.#####} — ticket {ticket}: " +
        JsonSerializer.Serialize(new
        {
            Side = side,
            Lots = 0.1,
            Sl = price,
            SizedStopDistance = stop,
            PaperExec = false,
            Retcode = 10009,
            Order = ticket,
            Deal = ticket - 1,
            Price = price,
            Server = "Deriv-Demo",
            Signal = family,
        });

    private static string TempJournal(params string[] lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fx-reseed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "journal_20261007.jsonl"), lines);
        return dir;
    }

    [Fact]
    public void Reseed_Rebuilds_Windows_In_Journal_Order()
    {
        var dir = TempJournal(
            Envelope("2026-10-07T00:00:00Z", "FX_ORDER", FillDetails("buy", "XAUUSDmicro", 4100.0, 2.0, 111, "vol-breakout")),
            Envelope("2026-10-07T00:01:00Z", "FX_PROFIT",
                $"XAUUSDmicro #111: PROFIT_FORMING −0.5R: {JsonSerializer.Serialize(new { Ticket = 111L, CurrentR = -0.5 })}"),
            Envelope("2026-10-07T00:02:00Z", "FX_EXIT",
                $"XAUUSDmicro #111: closed #111 — deal 7 (profit floor): {JsonSerializer.Serialize(new { Ticket = 111L, Retcode = 10009 })}"));

        try
        {
            var tape = new FxRecentTape();
            var result = FxRecentTapeReseed.Run(dir, tape);

            Assert.Equal(1, result.FillsSeen);
            Assert.Equal(1, result.ClosesRecorded);       // tier-2 CurrentR = −0.5
            Assert.Equal(0, result.ClosesNoOutcome);
            Assert.Equal(-0.5, tape.WindowMeanR("XAUUSDmicro", "vol-breakout")!.Value, 3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reseed_Prefers_Tier1_RealizedR_Over_The_Hold_Snapshot()
    {
        var dir = TempJournal(
            Envelope("2026-10-07T00:00:00Z", "FX_ORDER", FillDetails("sell", "XAGUSD", 59.7, 0.04, 222, "kalman-trend")),
            Envelope("2026-10-07T00:01:00Z", "FX_EXIT",
                $"XAGUSD #222: hold: {JsonSerializer.Serialize(new { Ticket = 222L, ProfitR = -0.9 })}"),
            Envelope("2026-10-07T00:02:00Z", "FX_EXIT",
                $"XAGUSD #222: closed #222 — deal 8: {JsonSerializer.Serialize(new { Ticket = 222L, RealizedR = 1.25, OutcomeSource = "close-price" })}"));

        try
        {
            var tape = new FxRecentTape();
            FxRecentTapeReseed.Run(dir, tape);

            Assert.Equal(1.25, tape.WindowMeanR("XAGUSD", "kalman-trend")!.Value, 3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reseed_Never_Fabricates_An_Outcome_And_Skips_The_Unparsable()
    {
        var dir = TempJournal(
            // Fill without a Signal family → skipped, never guessed.
            Envelope("2026-10-07T00:00:00Z", "FX_ORDER",
                "buy 0.1 lots EURUSD @ 1.09 — ticket 333: {\"Side\":\"buy\"}"),
            // Fill with family, but its close has NO outcome anywhere.
            Envelope("2026-10-07T00:01:00Z", "FX_ORDER", FillDetails("buy", "EURUSD", 1.09, 0.002, 444, "ou-rev")),
            Envelope("2026-10-07T00:02:00Z", "FX_EXIT",
                $"EURUSD #444: closed #444 — broker no longer holds the ticket: {JsonSerializer.Serialize(new { Ticket = 444L, RealizedR = (double?)null, OutcomeSource = "unknown" })}"),
            // Close with no matching fill at all.
            Envelope("2026-10-07T00:03:00Z", "FX_EXIT",
                "EURUSD #999: closed #999 — stale tracking retired: {\"Ticket\":999}"),
            // Garbage line must not throw.
            "{not json at all");

        try
        {
            var tape = new FxRecentTape();
            var result = FxRecentTapeReseed.Run(dir, tape);

            Assert.Equal(1, result.FillsSeen);            // only the ou-rev fill had a family
            Assert.Equal(0, result.ClosesRecorded);       // no outcome invented for #444
            Assert.Equal(1, result.ClosesNoOutcome);
            Assert.Null(tape.WindowMeanR("EURUSD", "ou-rev"));
            Assert.True(result.Skipped >= 3);             // familyless fill, orphan close, garbage line
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reseed_On_A_Missing_Directory_Is_A_NoOp_Not_A_Crash()
    {
        var tape = new FxRecentTape();
        var result = FxRecentTapeReseed.Run(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")), tape);

        Assert.Equal(0, result.FillsSeen);
        Assert.Equal(0, result.ClosesRecorded);
        Assert.Empty(tape.Cells());
    }
}
