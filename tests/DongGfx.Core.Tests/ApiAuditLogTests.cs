using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class ApiAuditLogTests : IDisposable
{
    private readonly string _dir;

    public ApiAuditLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_audit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void LogRequest_StoresEntry()
    {
        var log = new ApiAuditLog(_dir);
        log.LogRequest("ticks", "subscribe", "acct-1", "{\"ticks\":\"frxEURUSD\"}");
        log.Dispose(); // flush

        var entries = log.GetRecent();
        Assert.Single(entries);
        Assert.Equal("REQUEST", entries[0].Direction);
        Assert.Equal("ticks", entries[0].Endpoint);
        Assert.Equal("acct-1", entries[0].AccountId);
    }

    [Fact]
    public void LogResponse_StoresEntry()
    {
        var log = new ApiAuditLog(_dir);
        log.LogResponse("ticks", 200, "acct-1", "{\"tick\":{\"quote\":1.1}}", 42);
        log.Dispose();

        var entries = log.GetRecent();
        Assert.Single(entries);
        Assert.Equal("RESPONSE", entries[0].Direction);
        Assert.Equal("200", entries[0].Method);
        Assert.Equal(42, entries[0].ElapsedMs);
    }

    [Fact]
    public void LogError_StoresEntry()
    {
        var log = new ApiAuditLog(_dir);
        log.LogError("authorize", "acct-1", "Connection timeout");
        log.Dispose();

        var entries = log.GetRecent();
        Assert.Single(entries);
        Assert.Equal("ERROR", entries[0].Direction);
        Assert.Contains("timeout", entries[0].Payload);
    }

    [Fact]
    public void GetRecent_FilterByAccountId()
    {
        var log = new ApiAuditLog(_dir);
        log.LogRequest("ticks", "sub", "acct-1", "{}");
        log.LogRequest("ticks", "sub", "acct-2", "{}");
        log.Dispose();

        var entries = log.GetRecent(accountId: "acct-1");
        Assert.Single(entries);
        Assert.Equal("acct-1", entries[0].AccountId);
    }

    [Fact]
    public void GetRecent_RespectsCountLimit()
    {
        var log = new ApiAuditLog(_dir);
        for (int i = 0; i < 20; i++)
            log.LogRequest("endpoint", "GET", null, $"{{\"i\":{i}}}");
        log.Dispose();

        var entries = log.GetRecent(count: 5);
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public void Dispose_PreventsFurtherLogging()
    {
        var log = new ApiAuditLog(_dir);
        log.Dispose();
        log.LogRequest("test", "GET", null, "{}"); // should not throw

        var entries = log.GetRecent();
        Assert.Empty(entries);
    }

    [Fact]
    public void Payload_TruncatesAt2000Chars()
    {
        var log = new ApiAuditLog(_dir);
        var longPayload = new string('x', 5000);
        log.LogRequest("test", "POST", null, longPayload);
        log.Dispose();

        var entries = log.GetRecent();
        Assert.Single(entries);
        Assert.True(entries[0].Payload.Length <= 2001); // 2000 + ellipsis
    }
}

[Trait("Category", "Unit")]
public class MarketHoursEdgeCaseTests
{
    [Fact]
    public void NewYearsEve_IsHoliday()
    {
        Assert.True(MarketHours.IsHoliday(new DateTime(2025, 12, 31)));
    }

    [Fact]
    public void Thanksgiving_IsHoliday()
    {
        // Thanksgiving 2025 = November 27
        Assert.True(MarketHours.IsHoliday(new DateTime(2025, 11, 27)));
    }

    [Fact]
    public void USIndependenceDay_IsHoliday()
    {
        Assert.True(MarketHours.IsHoliday(new DateTime(2025, 7, 4)));
    }

    [Fact]
    public void GoodFriday2025_IsHoliday()
    {
        // Good Friday 2025 = April 18
        Assert.True(MarketHours.IsHoliday(new DateTime(2025, 4, 18)));
    }

    [Fact]
    public void NormalTuesday_IsNotHoliday()
    {
        Assert.False(MarketHours.IsHoliday(new DateTime(2025, 7, 8)));
    }

    [Fact]
    public void WeekdayAtLeastOneSession_IsOpen()
    {
        var hours = new MarketHours();
        // Monday 05:00 UTC — Sydney + Tokyo both active
        var time = new DateTimeOffset(2025, 7, 7, 5, 0, 0, TimeSpan.Zero);
        Assert.True(hours.IsOpen(time));
    }

    [Fact]
    public void TokyoSession_IsOpen()
    {
        var hours = new MarketHours();
        // Monday 01:00 UTC — Tokyo session
        var time = new DateTimeOffset(2025, 7, 7, 1, 0, 0, TimeSpan.Zero);
        Assert.True(hours.IsOpen(time));
    }

    [Fact]
    public void WeekdayMidnight_IsOpen()
    {
        var hours = new MarketHours();
        // Monday 00:00 UTC — Sydney (Sun 22-Mon 07) + Tokyo (Mon 00-09) both active
        var time = new DateTimeOffset(2025, 7, 7, 0, 0, 0, TimeSpan.Zero);
        Assert.True(hours.IsOpen(time));
    }
}


