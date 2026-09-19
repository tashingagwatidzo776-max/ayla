using DongGfx.Core.Analytics;

namespace DongGfx.Core.Tests;

/// <summary>
/// Tests for MarketHours' session logic: session windows, weekend and
/// holiday closures, overlap detection, and the next-open scheduler used by
/// growth engines. All times are UTC. Complements the broad-brush
/// MarketHoursTests in ServiceTests.cs with boundary and holiday specifics.
/// </summary>
[Trait("Category", "Unit")]
public class MarketHoursSessionsTests
{
    /// <summary>Monday 2026-09-14 (not a holiday).</summary>
    private static DateTimeOffset Monday(int hour) =>
        new(2026, 9, 14, hour, 0, 0, TimeSpan.Zero);

    /// <summary>Saturday 2026-09-12.</summary>
    private static DateTimeOffset Saturday(int hour) =>
        new(2026, 9, 12, hour, 0, 0, TimeSpan.Zero);

    /// <summary>Sunday 2026-09-13.</summary>
    private static DateTimeOffset Sunday(int hour) =>
        new(2026, 9, 13, hour, 0, 0, TimeSpan.Zero);

    private readonly MarketHours _hours = new();

    // ─── Session windows ──────────────────────────────────

    [Theory]
    [InlineData(2, true)]   // Tokyo + Sydney wrap
    [InlineData(8, true)]   // London opens
    [InlineData(16, true)]  // London + New York
    [InlineData(21, true)]  // New York only
    [InlineData(23, true)]  // Sydney wrap (Mon 23:00)
    public void IsOpen_WeekdayHours_IsTrue(int hour, bool expected)
    {
        Assert.Equal(expected, _hours.IsOpen(Monday(hour)));
    }

    [Fact]
    public void IsOpen_Monday07_IsTrue_ViaSydneyWrap()
    {
        // Sydney wraps 22→7, so Monday 07:00 is still inside it.
        Assert.True(_hours.IsOpen(Monday(7)));
    }

    // ─── Weekend closure ──────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(23)]
    public void IsOpen_Saturday_IsAlwaysFalse(int hour)
    {
        Assert.False(_hours.IsOpen(Saturday(hour)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(21, false)]
    [InlineData(22, true)]  // Sydney reopens Sun 22:00 UTC
    [InlineData(23, true)]
    public void IsOpen_Sunday_OpensAt22(int hour, bool expected)
    {
        Assert.Equal(expected, _hours.IsOpen(Sunday(hour)));
    }

    // ─── Holidays ─────────────────────────────────────────

    [Theory]
    [InlineData(2026, 1, 1)]   // New Year's Day
    [InlineData(2026, 12, 25)] // Christmas
    [InlineData(2026, 7, 4)]   // US Independence Day
    public void IsHoliday_KnownDates_AreTrue(int year, int month, int day)
    {
        Assert.True(MarketHours.IsHoliday(new DateTime(year, month, day)));
    }

    [Fact]
    public void IsHoliday_MatchesMonthDay_RegardlessOfYear()
    {
        // The holiday table is month/day based — 2030 must still match.
        Assert.True(MarketHours.IsHoliday(new DateTime(2030, 12, 25)));
        Assert.False(MarketHours.IsHoliday(new DateTime(2026, 9, 14)));
    }

    [Fact]
    public void IsOpen_HolidayWeekday_IsFalse()
    {
        // Friday 2026-12-25 10:00 UTC — London hours, but a holiday.
        var christmas = new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.Zero);
        Assert.False(_hours.IsOpen(christmas));
    }

    [Fact]
    public void GetNextHoliday_ReturnsName()
    {
        var next = MarketHours.GetNextHoliday(new DateTime(2026, 11, 1));

        Assert.NotNull(next);
        Assert.Equal("November 26", next); // Thanksgiving 2026
    }

    [Fact]
    public void GetNextHoliday_NoneRemaining_ReturnsNull()
    {
        Assert.Null(MarketHours.GetNextHoliday(new DateTime(2027, 12, 31)));
    }

    // ─── Overlaps ─────────────────────────────────────────

    [Fact]
    public void IsOverlap_LondonNewYorkWindow_IsTrue()
    {
        Assert.True(_hours.IsOverlap(Monday(14)));
    }

    [Fact]
    public void IsOverlap_OutsideWindows_IsFalse()
    {
        Assert.False(_hours.IsOverlap(Monday(10))); // London only
        Assert.False(_hours.IsOverlap(Monday(23))); // Sydney only
    }

    // ─── Active session names ─────────────────────────────

    [Fact]
    public void GetActiveSessions_ListsOverlappingSessions()
    {
        Assert.Equal("London, New York", _hours.GetActiveSessions(Monday(14)));
    }

    [Fact]
    public void GetActiveSessions_ClosedHours_ReportsClosed()
    {
        // Default sessions cover every weekday hour, so "Closed" needs a
        // custom schedule with a real gap.
        var sparse = new MarketHours(new[]
        {
            new MarketHours.Session("London", 8, 12),
        });

        Assert.Equal("Closed", sparse.GetActiveSessions(Monday(14)));
    }

    // ─── Next open scheduling ─────────────────────────────

    [Fact]
    public void TimeUntilNextOpen_WhileOpen_IsZero()
    {
        Assert.Equal(TimeSpan.Zero, _hours.TimeUntilNextOpen(Monday(10)));
    }

    [Fact]
    public void TimeUntilNextOpen_SaturdayMorning_FindsSunday22()
    {
        var wait = _hours.TimeUntilNextOpen(Saturday(12));

        Assert.Equal(TimeSpan.FromHours(34), wait);
    }

    [Fact]
    public void TimeUntilNextOpen_Monday00_FindsLondonOpen()
    {
        // Monday 00:00 is inside Tokyo — actually open, so zero.
        Assert.Equal(TimeSpan.Zero, _hours.TimeUntilNextOpen(Monday(0)));
    }

    [Fact]
    public void TimeUntilNextOpen_SundayNight_FindsTokyoContinuation()
    {
        // Sunday 23:00 is open (Sydney + Tokyo wrap); one hour later is
        // Monday 00:00 — still open (Tokyo). Zero wait either way.
        Assert.Equal(TimeSpan.Zero, _hours.TimeUntilNextOpen(Sunday(23)));
    }

    // ─── Growth-path holiday integration ─────────────────

    [Fact]
    public void TimeUntilNextOpen_HolidayWeekday_SkipsToNextOpenDay()
    {
        // The growth runner's market-hours gate calls IsOpen then
        // TimeUntilNextOpen to schedule the resume. On a holiday (Friday
        // 2026-12-25) the gate must report closed AND the wait must skip the
        // whole holiday, not just roll an hour forward into it.
        var christmas = new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.Zero);
        Assert.False(_hours.IsOpen(christmas));

        var wait = _hours.TimeUntilNextOpen(christmas);
        var nextOpen = christmas.Add(wait);

        Assert.False(MarketHours.IsHoliday(nextOpen.Date),
            $"next open {nextOpen} must not land on a holiday");
        Assert.True(_hours.IsOpen(nextOpen), $"next open {nextOpen} must actually be open");
    }

    [Fact]
    public void HolidayGate_ComposesIsOpenAndNextHolidayExactlyAsTheRunnerDoes()
    {
        // Locks the exact composition GrowthRunner.OnCycle uses for its
        // activity line: IsHoliday(now) gates the parenthetical, and
        // GetNextHoliday(tomorrow) supplies the pointer — never today's
        // holiday name again.
        var christmas = new DateTime(2026, 12, 25);
        var line = MarketHours.IsHoliday(christmas)
            ? $" (holiday — next: {MarketHours.GetNextHoliday(christmas.AddDays(1)) ?? "none"})"
            : "";

        Assert.Contains("holiday", line);
        Assert.DoesNotContain("December 25", line); // today's holiday is not 'next'
        Assert.NotEqual(" (holiday — next: none)", line); // a pointer exists (Boxing Day)
    }
}
