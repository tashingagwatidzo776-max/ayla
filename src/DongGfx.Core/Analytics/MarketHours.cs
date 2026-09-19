namespace DongGfx.Core.Analytics;

/// <summary>
/// Determines whether the current time falls within active forex trading
/// sessions. Growth engines can optionally only trade during these windows
/// to avoid low-liquidity periods (weekends, holidays, dead hours).
/// </summary>
public sealed class MarketHours
{
    /// <summary>Trading session definition (UTC hours).</summary>
    public sealed record Session(string Name, int StartHourUtc, int EndHourUtc);

    /// <summary>Major forex sessions in UTC.</summary>
    public static readonly IReadOnlyList<Session> DefaultSessions = new[]
    {
        new Session("Sydney", 22, 7),      // Sun 22:00 – Mon 07:00 UTC
        new Session("Tokyo", 0, 9),        // Mon 00:00 – 09:00 UTC
        new Session("London", 8, 17),      // Mon 08:00 – 17:00 UTC
        new Session("New York", 13, 22),   // Mon 13:00 – 22:00 UTC
    };

    /// <summary>Overlap windows (highest liquidity).</summary>
    public static readonly IReadOnlyList<Session> Overlaps = new[]
    {
        new Session("London/Tokyo", 8, 9),       // 08:00–09:00 UTC
        new Session("London/New York", 13, 17),  // 13:00–17:00 UTC
    };

    /// <summary>Major forex holidays (UTC dates). Markets closed all day.</summary>
    public static readonly IReadOnlyList<DateTime> Holidays = new[]
    {
        // New Year's Day
        new DateTime(2025, 1, 1), new DateTime(2026, 1, 1), new DateTime(2027, 1, 1),
        // Good Friday (varies — add manually or compute)
        new DateTime(2025, 4, 18), new DateTime(2026, 4, 3), new DateTime(2027, 3, 26),
        // Easter Monday
        new DateTime(2025, 4, 21), new DateTime(2026, 4, 6), new DateTime(2027, 3, 29),
        // Christmas Eve (half day — treat as closed)
        new DateTime(2025, 12, 24), new DateTime(2026, 12, 24),
        // Christmas Day
        new DateTime(2025, 12, 25), new DateTime(2026, 12, 25), new DateTime(2027, 12, 25),
        // Boxing Day
        new DateTime(2025, 12, 26), new DateTime(2026, 12, 26),
        // US Independence Day
        new DateTime(2025, 7, 4), new DateTime(2026, 7, 4),
        // Thanksgiving (US — 4th Thursday in November)
        new DateTime(2025, 11, 27), new DateTime(2026, 11, 26),
        // New Year's Eve (early close)
        new DateTime(2025, 12, 31), new DateTime(2026, 12, 31),
    };

    private readonly IReadOnlyList<Session> _sessions;

    public MarketHours(IReadOnlyList<Session>? sessions = null)
    {
        _sessions = sessions ?? DefaultSessions;
    }

    /// <summary>Check if the given UTC time is within any active session.</summary>
    public bool IsOpen(DateTimeOffset utcTime)
    {
        // Holiday check
        if (IsHoliday(utcTime.Date))
            return false;

        // Weekend check — forex market is closed Sat 00:00 – Sun 22:00 UTC.
        if (utcTime.DayOfWeek == DayOfWeek.Saturday)
            return false;
        if (utcTime.DayOfWeek == DayOfWeek.Sunday && utcTime.Hour < 22)
            return false;

        var hour = utcTime.Hour;
        return _sessions.Any(s => IsInSession(hour, s));
    }

    /// <summary>Check if a date is a forex holiday.</summary>
    public static bool IsHoliday(DateTime date)
    {
        return Holidays.Any(h => h.Month == date.Month && h.Day == date.Day);
    }

    /// <summary>Get the name of the next upcoming holiday.</summary>
    public static string? GetNextHoliday(DateTime from)
    {
        var upcoming = Holidays.Where(h => h >= from.Date)
            .OrderBy(h => h)
            .FirstOrDefault();
        return upcoming == default ? null : upcoming.ToString("MMMM dd");
    }

    /// <summary>Check if we're in a high-liquidity overlap period.</summary>
    public bool IsOverlap(DateTimeOffset utcTime)
    {
        var hour = utcTime.Hour;
        return Overlaps.Any(s => IsInSession(hour, s));
    }

    /// <summary>Get the name of the currently active session(s).</summary>
    public string GetActiveSessions(DateTimeOffset utcTime)
    {
        var hour = utcTime.Hour;
        var active = _sessions.Where(s => IsInSession(hour, s)).Select(s => s.Name).ToArray();
        return active.Length == 0 ? "Closed" : string.Join(", ", active);
    }

    /// <summary>Time until the next session opens (useful for sleep scheduling).</summary>
    public TimeSpan TimeUntilNextOpen(DateTimeOffset utcTime)
    {
        if (IsOpen(utcTime)) return TimeSpan.Zero;

        // Walk forward hour by hour until we find an open session. The window
        // must span a holiday adjoining a weekend (e.g. Christmas Friday →
        // Sunday 22:00 is 60h away) — 48h silently landed the caller on a
        // closed hour, so the growth gate would resume into a closed market.
        for (var h = 1; h <= 24 * 14; h++)
        {
            var candidate = utcTime.AddHours(h);
            if (IsOpen(candidate))
                return candidate - utcTime;
        }

        return TimeSpan.FromHours(24); // fallback
    }

    private static bool IsInSession(int hour, Session session)
    {
        if (session.StartHourUtc < session.EndHourUtc)
        {
            // Normal range (e.g. 8–17)
            return hour >= session.StartHourUtc && hour < session.EndHourUtc;
        }
        else
        {
            // Wrapping range (e.g. 22–7 for Sydney)
            return hour >= session.StartHourUtc || hour < session.EndHourUtc;
        }
    }
}
