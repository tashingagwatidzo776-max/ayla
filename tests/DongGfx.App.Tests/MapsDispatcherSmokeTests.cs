using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DongGfx.App.Controls;
using DongGfx.App.ViewModels;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// UI-thread smoke harness for the Maps refresh path — the regression test
/// for the 2026-09-27 dispatcher deadlock (a sync-over-async provider froze
/// the UI while all unit tests stayed green: xunit has no WPF
/// SynchronizationContext, so a plain test cannot catch it — but running
/// the real chain on a real STA Dispatcher with a DispatcherSynchronization
/// Context, and PUMPING it with a hard watchdog, turns a deadlock into a
/// red test: the queued watchdog throw unwinds PushFrame instead of the
/// run hanging).
///
/// The chain under test mirrors MainWindow.WireMaps + its timer tick
/// exactly: a tick that starts on the UI thread, hops off for "I/O"
/// (ConfigureAwait(false) — as TerminalViewModel.BridgeCandles does),
/// fills the ConcurrentDictionary caches, then awaits RefreshAsync back on
/// the dispatcher.
/// </summary>
public class MapsDispatcherSmokeTests
{
    /// <summary>Runs `action` on a fresh STA dispatcher thread with a
    /// DispatcherSynchronizationContext installed. The watchdog BeginInvokes
    /// a throw after `watchdogTimeout`: if the pumped chain is deadlocked
    /// while the dispatcher STILL PUMPS (awaited chain never completes),
    /// the throw unwinds PushFrame and surfaces here. If the bug instead
    /// BLOCKS the dispatcher outright (the classic .GetAwaiter().GetResult()
    /// shape — the queue stops, the throw can never run), the outer
    /// `outerLimit` bound fires and the test still goes red. Either way a
    /// deadlock is a failed test, never a hung run.</summary>
    private static void OnUiDispatcher(
        Action<Dispatcher> action,
        TimeSpan? watchdogTimeout = null,
        TimeSpan? outerLimit = null)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim(false);
        var limit = watchdogTimeout ?? TimeSpan.FromSeconds(15);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            using var watchdog = new System.Threading.Timer(_ =>
                dispatcher.BeginInvoke(new Action(() =>
                    throw new TimeoutException(
                        "UI chain did not complete in time — sync-over-async deadlock?"))),
                null, limit, Timeout.InfiniteTimeSpan);
            try
            {
                action(dispatcher);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;   // a deliberately-deadlocked detector run must not hold the process open
        thread.Start();

        Assert.True(finished.Wait(outerLimit ?? TimeSpan.FromSeconds(45)),
            "dispatcher thread hung — sync-over-async deadlock detected (blocked dispatcher shape)");
        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>Pumps `dispatcher` (must be the calling thread's) until
    /// `task` completes — the non-hanging equivalent of task.Wait() on a
    /// UI thread, and the property a deadlock detector needs.</summary>
    private static void PumpUntil(Dispatcher dispatcher, Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(
            _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
    }

    private static IReadOnlyList<FxBar> FakeBars(int n = 60)
    {
        var bars = new List<FxBar>();
        for (var i = 0; i < n; i++)
        {
            var c = 2400 + i * 0.5;
            bars.Add(new FxBar(1790000000L + 60 * i, c - 0.3, c + 0.6, c - 0.6, c, 100));
        }
        return bars;
    }

    private static string? TitleOf(HeatMapControl control) =>
        control.GetType().GetField("_title", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(control) is TextBlock title ? title.Text : null;

    [Fact]
    public void Harness_Goes_Red_On_The_Classic_SyncOverAsync_Deadlock()
    {
        // The detector's own regression test: the verbatim 2026-09-27 bug
        // (blocking an awaited continuation on the UI thread) must turn
        // into a bounded FAILURE here — never a hung CI run.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => OnUiDispatcher(dispatcher =>
        {
            var blocked = dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(50).ConfigureAwait(true);   // needs the (about-to-block) dispatcher
            }).Task;
            blocked.GetAwaiter().GetResult();   // ← the bug, verbatim
        }, watchdogTimeout: TimeSpan.FromSeconds(2), outerLimit: TimeSpan.FromSeconds(10)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"deadlock detection took too long: {sw.Elapsed}");
    }

    [Fact]
    public void Maps_Refresh_Chain_Completes_On_A_Real_Dispatcher()
    {
        OnUiDispatcher(dispatcher =>
        {
            var vm = new MapsViewModel();
            var canvas = new HeatMapControl();

            // The app's wiring contract: providers read caches ONLY — all
            // "network" work happens off the UI thread. A .GetAwaiter()
            // .GetResult() anywhere in the awaited chain deadlocks this
            // exact setup (2026-09-27) and now fails via the watchdog.
            vm.ConfluenceProvider = () =>
            {
                var frames = new List<(string, IReadOnlyList<FxBar>)>();
                foreach (var tf in new[] { "M1", "M5", "M15" })
                {
                    if (vm.CachedCandles.TryGetValue(tf, out var bars) && bars.Count > 0)
                    {
                        frames.Add((tf, bars));
                    }
                }
                return frames;
            };
            vm.BarsProvider = () =>
                vm.CachedCandles.TryGetValue("M1", out var b) ? b : [];

            // The MainWindow timer tick shape, verbatim: starts on the UI
            // thread, awaits the "bridge call" (the I/O hop — BridgeCandles
            // is ConfigureAwait(false) INSIDE, which Task.Run stands in
            // for), resumes on the dispatcher (ConfigureAwait(true)), fills
            // the cache, then awaits RefreshAsync. A .GetAwaiter().GetResult()
            // anywhere in that awaited chain deadlocks this exact setup and
            // now trips the watchdog instead of hanging the run.
            async Task TickAsync()
            {
                var bars = await Task.Run(async () =>
                {
                    await Task.Delay(20).ConfigureAwait(false);
                    return FakeBars();
                }).ConfigureAwait(true);
                vm.CachedCandles["M1"] = bars;
                var bars5 = await Task.Run(async () =>
                {
                    await Task.Delay(20).ConfigureAwait(false);
                    return FakeBars();
                }).ConfigureAwait(true);
                vm.CachedCandles["M5"] = bars5;
                await vm.RefreshAsync().ConfigureAwait(true);
            }

            var tick = TickAsync();
            PumpUntil(dispatcher, tick);
            Assert.True(tick.IsCompletedSuccessfully,
                $"tick failed: {tick.Exception?.GetBaseException().Message}");

            // Map switch + refresh on the dispatcher — the path that froze
            // the real app. Same property flow: SelectedMap setter →
            // OnSelectedMapChanged → fire-and-forget RefreshAsync, plus the
            // explicitly awaited refresh the WireMaps timer performs.
            vm.SelectedMap = "MTF confluence";
            var refresh = vm.RefreshAsync();
            PumpUntil(dispatcher, refresh);
            Assert.True(refresh.IsCompletedSuccessfully);

            Assert.NotNull(vm.CurrentMap);
            // FxMaps decorates the base name with the frames it got.
            Assert.StartsWith("MTF confluence", vm.CurrentMap!.Name);
            Assert.Contains("M1/M5", vm.CurrentMap.Name);
            Assert.Contains("MTF confluence", vm.StatusText);
            Assert.Equal(2, vm.CurrentMap.Rows);   // one row per cached frame

            // And the real control renders it without throwing.
            canvas.Render(vm.CurrentMap);
            Assert.StartsWith("MTF confluence", TitleOf(canvas));
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Map_Switch_Via_Property_Fires_A_Completing_Refresh()
    {
        OnUiDispatcher(dispatcher =>
        {
            var vm = new MapsViewModel { SelectedMap = "Volume surface" };
            vm.TicksProvider = () =>
            {
                var ticks = new List<(double Price, double Vol, long TimeMs, int Direction)>();
                for (var i = 0; i < 40; i++)
                {
                    ticks.Add((2400 + i * 0.1, 1 + i % 5, 1790000000000L + i, i % 2));
                }
                return ticks;
            };

            // OnSelectedMapChanged fires `_ = RefreshAsync()`; pump the
            // explicit refresh so a deadlocked continuation (the original
            // bug) fails the test instead of hanging it.
            vm.SelectedMap = "Order flow";
            var refresh = vm.RefreshAsync();
            PumpUntil(dispatcher, refresh);

            Assert.NotNull(vm.CurrentMap);
            Assert.StartsWith("Order flow", vm.CurrentMap!.Name);
        });
    }

    [Fact]
    public void Render_Survives_Null_And_Diverging_Maps()
    {
        OnUiDispatcher(_ =>
        {
            var canvas = new HeatMapControl();
            canvas.Render(null);
            Assert.Equal("no map", TitleOf(canvas));

            var cells = new double[4, 2];
            for (var c = 0; c < 4; c++)
            {
                for (var r = 0; r < 2; r++)
                {
                    cells[c, r] = (c + r) % 2 == 0 ? -0.5 : 0.5;
                }
            }
            cells[0, 0] = double.NaN;   // never-touched bucket
            var diverging = new FxHeatMap("MTF confluence", "tf", "price", 4, 2, cells, -1, 1, "trend");
            canvas.Render(diverging);
            Assert.Contains("MTF confluence", TitleOf(canvas));
        });
    }
}
