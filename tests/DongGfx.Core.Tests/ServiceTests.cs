using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

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
