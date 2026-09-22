using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The tick-staleness watchdog: a Connected socket whose live ticks stop
/// (the silent failure mode that froze the signal window on Sep 20) must
/// raise exactly one stall alert per episode, never fire while disconnected
/// or fresh, and re-arm after a recovery so a second stall is caught too.
/// All timing is driven through TestLastTickUtc — no waiting.
/// </summary>
[Trait("Category", "Unit")]
public class TickStalenessWatchdogTests
{
    private static AccountConnection MakeConnected()
    {
        var config = new AccountConfig { Label = "Demo (watchdog)", IsDemo = true, NewPlatform = true };
        var connection = new AccountConnection(config);
        // Simulate a connected session without any network.
        typeof(AccountConnection).GetProperty("IsConnected")!
            .SetValue(connection, true);
        return connection;
    }

    [Fact]
    public void FreshTick_NeverStalls()
    {
        var c = MakeConnected();
        c.TestLastTickUtc = DateTimeOffset.UtcNow;

        var (_, age, stalled) = c.EvaluateTickStaleness();

        Assert.False(stalled);
        Assert.True(age < AccountConnection.TickStalenessThreshold);
    }

    [Fact]
    public void StaleTickWhileConnected_FiresExactlyOneAlert()
    {
        var c = MakeConnected();
        c.TestLastTickUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);

        var fired = 0;
        c.TickFeedStalled += (_, _) => fired++;

        var (_, age, stalled) = c.EvaluateTickStaleness();
        Assert.True(stalled);
        Assert.Equal(1, fired);

        // Subsequent evaluations in the same episode stay silent.
        c.EvaluateTickStaleness();
        c.EvaluateTickStaleness();
        Assert.Equal(1, fired);
        Assert.True(age >= AccountConnection.TickStalenessThreshold);
    }

    [Fact]
    public void StaleTickWhileDisconnected_NeverFires()
    {
        var config = new AccountConfig { Label = "Demo (offline)", IsDemo = true };
        var c = new AccountConnection(config);
        c.TestLastTickUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);

        var fired = 0;
        c.TickFeedStalled += (_, _) => fired++;

        var (connected, _, stalled) = c.EvaluateTickStaleness();

        Assert.False(connected);
        Assert.False(stalled);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Recovery_RearmsWatchForNextEpisode()
    {
        var c = MakeConnected();
        c.TestLastTickUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        c.EvaluateTickStaleness(); // episode 1 fires

        var fired = 0;
        c.TickFeedStalled += (_, _) => fired++;

        // A live tick arrives (recovery), then the feed stalls again.
        c.TestLastTickUtc = DateTimeOffset.UtcNow;
        var (_, _, stalledAfterRecovery) = c.EvaluateTickStaleness();
        Assert.False(stalledAfterRecovery);

        c.TestLastTickUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(9);
        var (_, _, stalledAgain) = c.EvaluateTickStaleness();
        Assert.True(stalledAgain);
        Assert.Equal(1, fired); // second episode raised exactly once
    }

    [Fact]
    public void TickAge_NoTicksEver_IsInfinite()
    {
        var config = new AccountConfig { Label = "Demo (silent)", IsDemo = true };
        var c = new AccountConnection(config);

        Assert.Equal(Timeout.InfiniteTimeSpan, c.TickAge);
    }
}
