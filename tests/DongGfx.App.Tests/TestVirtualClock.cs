namespace DongGfx.App.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose time stands still until <see cref="Advance"/>
/// is called. One-shot timers created through it fire, in due-time order, while
/// the clock is advanced — code under test waits on fake time that moves only
/// when the test decides, so tests can assert exact timestamps and interval
/// boundaries with no wall-clock bound anywhere (nothing can flake under CI load).
/// Mirrors the <see cref="TestVirtualClock"/> in DongGfx.Core.Tests (each suite keeps
/// its own copy, like WaitForAsync) so the App-layer E2E tests can drive the
/// scheduler's backoffs and the hub's restart ladders on the virtual clock.
///
/// Timer callbacks run outside the internal lock so completions can propagate;
/// tasks completed by <see cref="Advance"/> may still have their continuations
/// queued (xUnit's sync context defers them) — await the task (or pump with a
/// few <see cref="Task.Yield"/>) before synchronously observing it.
/// </summary>
internal sealed class TestVirtualClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<FakeTimer> _timers = new();
    private long _nowMs;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync) return DateTimeOffset.UnixEpoch.AddMilliseconds(_nowMs);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_sync)
        {
            timer.DueMs = dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero
                ? -1
                : _nowMs + (long)dueTime.TotalMilliseconds;
            _timers.Add(timer);
        }
        return timer;
    }

    /// <summary>Advance fake time by <paramref name="ms"/> milliseconds,
    /// firing every timer that comes due along the way (earliest first,
    /// registration order on ties). Time never moves backwards even if a
    /// callback advances the clock further.</summary>
    public void Advance(long ms)
    {
        lock (_sync) _nowMs = Math.Max(_nowMs, 0);
        var target = CurrentMs() + ms;
        while (true)
        {
            FakeTimer? due;
            lock (_sync)
            {
                due = null;
                foreach (var t in _timers)
                {
                    if (!t.Enabled || t.DueMs < 0 || t.DueMs > target) continue;
                    if (due is null || t.DueMs < due.DueMs) due = t;
                }
                if (due is null)
                {
                    _nowMs = Math.Max(_nowMs, target);
                    return;
                }
                _nowMs = Math.Max(_nowMs, due.DueMs);
            }
            due.Fire(); // outside the lock: callbacks may schedule new timers
        }
    }

    public long CurrentMs()
    {
        lock (_sync) return _nowMs;
    }

    /// <summary>Number of registered timers that have neither fired nor been
    /// disposed. Tests gate <see cref="Advance"/> on this: advancing only once
    /// the code under test has (re)armed its wait keeps virtual time pinned to
    /// real deadlines even when await continuations drain lazily — a blind
    /// advance loop can run whole batches with nothing due and smear the very
    /// timestamps the test is trying to assert exactly.</summary>
    public int PendingTimerCount
    {
        get
        {
            lock (_sync)
            {
                var pending = 0;
                foreach (var t in _timers)
                {
                    if (t.IsPending()) pending++;
                }
                return pending;
            }
        }
    }

    /// <summary>A one-shot timer that fires only when the clock advances past
    /// its due time. <see cref="Change"/> is implemented honestly even though
    /// current consumers never re-arm or period their timers.</summary>
    private sealed class FakeTimer : ITimer
    {
        private readonly TestVirtualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _sync = new();

        public long DueMs = -1;
        private bool _cancelled;

        public FakeTimer(TestVirtualClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public bool Enabled
        {
            get { lock (_sync) return !_cancelled && DueMs >= 0; }
        }

        public void Fire()
        {
            lock (_sync)
            {
                if (_cancelled || DueMs < 0) return;
                DueMs = -1; // one-shot: firing consumes it
            }
            _callback(_state);
        }

        /// <summary>Called with the clock's lock held (lock order is always
        /// clock → timer), so it must not itself take the clock lock.</summary>
        public bool IsPending()
        {
            lock (_sync) return !_cancelled && DueMs >= 0;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // Compute the due time before taking the timer lock: CurrentMs
            // takes the clock's lock, and PendingTimerCount/Advance hold the
            // clock lock while taking this one — nesting the other way round
            // would be a lock-order inversion.
            var due = dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero
                ? -1
                : _clock.CurrentMs() + (long)dueTime.TotalMilliseconds;
            lock (_sync)
            {
                if (_cancelled) return false;
                DueMs = due;
                return true;
            }
        }

        public void Dispose() { lock (_sync) _cancelled = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
