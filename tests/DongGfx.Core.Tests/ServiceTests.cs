using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class MarketHoursTests
{
    [Fact]
    public void WeekdayDuringLondonSession_IsOpen()
    {
        var hours = new MarketHours();
        // Monday 10:00 UTC — London session (8-17)
        var time = new DateTimeOffset(2025, 7, 7, 10, 0, 0, TimeSpan.Zero);
        Assert.True(hours.IsOpen(time));
    }

    [Fact]
    public void Saturday_IsClosed()
    {
        var hours = new MarketHours();
        var time = new DateTimeOffset(2025, 7, 5, 10, 0, 0, TimeSpan.Zero); // Saturday
        Assert.False(hours.IsOpen(time));
    }

    [Fact]
    public void SundayBefore22_IsClosed()
    {
        var hours = new MarketHours();
        var time = new DateTimeOffset(2025, 7, 6, 12, 0, 0, TimeSpan.Zero); // Sunday 12:00
        Assert.False(hours.IsOpen(time));
    }

    [Fact]
    public void SundayAfter22_IsOpen()
    {
        var hours = new MarketHours();
        var time = new DateTimeOffset(2025, 7, 6, 23, 0, 0, TimeSpan.Zero); // Sunday 23:00 (Sydney)
        Assert.True(hours.IsOpen(time));
    }

    [Fact]
    public void ChristmasDay_IsHoliday()
    {
        Assert.True(MarketHours.IsHoliday(new DateTime(2025, 12, 25)));
    }

    [Fact]
    public void RegularWeekday_IsNotHoliday()
    {
        Assert.False(MarketHours.IsHoliday(new DateTime(2025, 7, 7)));
    }

    [Fact]
    public void GetActiveSessions_ReturnsCorrectNames()
    {
        var hours = new MarketHours();
        // Monday 14:00 UTC — London + New York overlap
        var time = new DateTimeOffset(2025, 7, 7, 14, 0, 0, TimeSpan.Zero);
        var sessions = hours.GetActiveSessions(time);
        Assert.Contains("London", sessions);
        Assert.Contains("New York", sessions);
    }

    [Fact]
    public void TimeUntilNextOpen_ReturnsPositive()
    {
        var hours = new MarketHours();
        // Saturday
        var time = new DateTimeOffset(2025, 7, 5, 10, 0, 0, TimeSpan.Zero);
        var next = hours.TimeUntilNextOpen(time);
        Assert.True(next > TimeSpan.Zero);
    }

    [Fact]
    public void IsOverlap_DuringLondonNY_ReturnsTrue()
    {
        var hours = new MarketHours();
        // Monday 14:00 UTC — overlap window (13-17)
        var time = new DateTimeOffset(2025, 7, 7, 14, 0, 0, TimeSpan.Zero);
        Assert.True(hours.IsOverlap(time));
    }

    [Fact]
    public void IsOverlap_DuringSydney_ReturnsFalse()
    {
        var hours = new MarketHours();
        // Monday 03:00 UTC — Sydney only
        var time = new DateTimeOffset(2025, 7, 7, 3, 0, 0, TimeSpan.Zero);
        Assert.False(hours.IsOverlap(time));
    }
}

[Trait("Category", "Unit")]
public class HeartbeatLogTests : IDisposable
{
    private readonly string _dir;

    public HeartbeatLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_hb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Record_StoresEntry()
    {
        var log = new HeartbeatLog(_dir);
        var id = Guid.NewGuid();

        log.Record(id, "Test", "Disconnected", "Connected");
        log.Dispose(); // flush

        var entries = log.GetRecent(id);
        Assert.Single(entries);
        Assert.Equal("Connected", entries[0].NewState);
    }

    [Fact]
    public void GetUptimePercent_AllConnected_Returns100()
    {
        var log = new HeartbeatLog(_dir);
        var id = Guid.NewGuid();

        log.Record(id, "Test", "Disconnected", "Connected");
        var uptime = log.GetUptimePercent(id, 24);

        // Should be close to 100% since we just connected
        Assert.True(uptime >= 90);
    }

    [Fact]
    public void GetRecent_FilterByAccountId()
    {
        var log = new HeartbeatLog(_dir);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        log.Record(id1, "A", "Disconnected", "Connected");
        log.Record(id2, "B", "Disconnected", "Connected");
        log.Dispose();

        var entries = log.GetRecent(id1);
        Assert.Single(entries);
        Assert.Equal("A", entries[0].AccountName);
    }
}

[Trait("Category", "Unit")]
public class AppLoggerTests : IDisposable
{
    private readonly string _dir;

    public AppLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_log_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Info_LogsMessage()
    {
        var logger = new AppLogger(_dir);
        logger.Info("test message", "test-category");
        logger.Dispose();

        var entries = logger.GetRecent();
        Assert.Single(entries);
        Assert.Equal("test message", entries[0].Message);
        Assert.Equal("test-category", entries[0].Category);
        Assert.Equal(LogLevel.Info, entries[0].Level);
    }

    [Fact]
    public void Debug_RespectsMinLevel()
    {
        var logger = new AppLogger(_dir) { MinLevel = LogLevel.Info };
        logger.Debug("debug message");
        logger.Dispose();

        var entries = logger.GetRecent();
        Assert.Empty(entries);
    }

    [Fact]
    public void Error_IncludesExceptionInfo()
    {
        var logger = new AppLogger(_dir);
        logger.Error("something broke", new InvalidOperationException("test error"));
        logger.Dispose();

        var entries = logger.GetRecent();
        Assert.Single(entries);
        Assert.Equal(LogLevel.Error, entries[0].Level);
        Assert.Contains("InvalidOperationException", entries[0].Properties["Exception"].ToString());
    }

    [Fact]
    public void GetRecent_RespectsMinLevelFilter()
    {
        var logger = new AppLogger(_dir);
        logger.Debug("d1");
        logger.Info("i1");
        logger.Warn("w1");
        logger.Error("e1");
        logger.Dispose();

        var errorsOnly = logger.GetRecent(minLevel: LogLevel.Error);
        Assert.Single(errorsOnly);
        Assert.Equal("e1", errorsOnly[0].Message);
    }
}

[Trait("Category", "Unit")]
public class TickHistoryCacheTests : IDisposable
{
    private readonly string _dir;

    public TickHistoryCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_tick_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static Tick MakeTick(int epoch, double price = 1.10000) =>
        new("frxEURUSD", price, price + 0.00001, price - 0.00001, epoch, 5);

    [Fact]
    public void AddTicks_StoresAndRetrieves()
    {
        var cache = new TickHistoryCache(_dir);
        var ticks = new[] { MakeTick(1000, 1.10001), MakeTick(2000, 1.10002) };

        cache.AddTicks("frxEURUSD", ticks);

        var result = cache.GetTicks("frxEURUSD");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void AddTicks_DeduplicatesByEpoch()
    {
        var cache = new TickHistoryCache(_dir);
        var tick = MakeTick(1000);

        cache.AddTicks("frxEURUSD", new[] { tick });
        cache.AddTicks("frxEURUSD", new[] { tick }); // duplicate

        var result = cache.GetTicks("frxEURUSD");
        Assert.Single(result);
    }

    [Fact]
    public void AddTicks_SortsByEpoch()
    {
        var cache = new TickHistoryCache(_dir);
        cache.AddTicks("frxEURUSD", new[] { MakeTick(3000), MakeTick(1000), MakeTick(2000) });

        var result = cache.GetTicks("frxEURUSD");
        Assert.Equal(1000, result[0].Epoch);
        Assert.Equal(2000, result[1].Epoch);
        Assert.Equal(3000, result[2].Epoch);
    }

    [Fact]
    public void GetSymbols_ReturnsAllSymbols()
    {
        var cache = new TickHistoryCache(_dir);
        cache.AddTicks("frxEURUSD", new[] { MakeTick(1000) });
        cache.AddTicks("frxGBPUSD", new[] { MakeTick(1000) });

        var symbols = cache.GetSymbols();
        Assert.Equal(2, symbols.Count);
        Assert.Contains("frxEURUSD", symbols);
        Assert.Contains("frxGBPUSD", symbols);
    }

    [Fact]
    public void GetTickCount_ReturnsCorrectCount()
    {
        var cache = new TickHistoryCache(_dir);
        cache.AddTicks("frxEURUSD", new[] { MakeTick(1000), MakeTick(2000) });

        Assert.Equal(2, cache.GetTickCount("frxEURUSD"));
        Assert.Equal(0, cache.GetTickCount("nonexistent"));
    }

    [Fact]
    public void Persistence_LoadsFromDisk()
    {
        var cache1 = new TickHistoryCache(_dir);
        cache1.AddTicks("frxEURUSD", new[] { MakeTick(1000), MakeTick(2000) });

        // Create new cache instance — should load from disk
        var cache2 = new TickHistoryCache(_dir);
        var result = cache2.GetTicks("frxEURUSD");
        Assert.Equal(2, result.Count);
    }
}
