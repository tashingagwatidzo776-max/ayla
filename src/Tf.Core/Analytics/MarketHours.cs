namespace Tf.Core.Analytics;

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

    private readonly IReadOnlyList<Session> _sessions;

    public MarketHours(IReadOnlyList<Session>? sessions = null)
    {
        _sessions = sessions ?? DefaultSessions;
    }

    /// <summary>Check if the given UTC time is within any active session.</summary>
    public bool IsOpen(DateTimeOffset utcTime)
    {
        // Weekend check — forex market is closed Sat 00:00 – Sun 22:00 UTC.
        if (utcTime.DayOfWeek == DayOfWeek.Saturday)
            return false;
        if (utcTime.DayOfWeek == DayOfWeek.Sunday && utcTime.Hour < 22)
            return false;

        var hour = utcTime.Hour;
        return _sessions.Any(s => IsInSession(hour, s));
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

        // Walk forward hour by hour until we find an open session (max 48 hours).
        for (var h = 1; h <= 48; h++)
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
