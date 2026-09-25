using System.Threading;
using System.Windows;
using System.Windows.Input;
using DongGfx.App.Controls;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Chart drawing tools: trendlines anchor to (bar index, price) so they
/// survive zoom/pan; the 600-bar trim shifts anchors and drops lines whose
/// last anchor scrolled out; horizontal levels are price-anchored and
/// never drop; AddDrawing clamps indexes into the window; a plain
/// right-click (no drag) raises the context-menu event.
/// </summary>
[Trait("Category", "Unit")]
public class CandleChartDrawingTests : IDisposable
{
    private readonly List<FrameworkElement> _trash = new();

    public void Dispose()
    {
        foreach (var c in _trash)
        {
            try { ((IDisposable)c).Dispose(); } catch { /* best effort */ }
        }
    }

    private static System.Collections.Generic.IReadOnlyList<DongGfx.Core.Fx.FxBar> Bars(int n)
    {
        var list = new List<DongGfx.Core.Fx.FxBar>();
        var price = 2650.0;
        for (var i = 0; i < n; i++)
        {
            var open = price;
            price += (i % 5 - 2) * 0.4;
            var close = price;
            list.Add(new DongGfx.Core.Fx.FxBar(
                1790000000L + i * 60, open, Math.Max(open, close) + 0.8,
                Math.Min(open, close) - 0.8, close, 100 + i));
        }
        return list;
    }

    private CandleChartControl NewChart()
    {
        var chart = new CandleChartControl();
        _trash.Add(chart);
        return chart;
    }

    private static void RunInSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void Add_And_Clear_Drawings()
    {
        RunInSta(() =>
        {
            var chart = NewChart();
            chart.SetBars(Bars(50));

            chart.AddDrawing(new CandleChartControl.ChartDrawing("trend", 5, 2650.0, 20, 2660.0));
            chart.AddDrawing(new CandleChartControl.ChartDrawing("hlevel", 0, 2655.0, 0, 0));
            Assert.Equal(2, chart.Drawings.Count);

            chart.ClearDrawings();
            Assert.Empty(chart.Drawings);
        });
    }

    [Fact]
    public void AddDrawing_Clamps_Indexes_Into_The_Bar_Window()
    {
        RunInSta(() =>
        {
            var chart = NewChart();
            chart.SetBars(Bars(50));
            chart.AddDrawing(new CandleChartControl.ChartDrawing("trend", -5, 2650.0, 10_000, 2660.0));
            var d = Assert.Single(chart.Drawings);
            Assert.Equal(0, d.Index1);            // clamped to the window
            Assert.Equal(49, d.Index2);
        });
    }

    [Fact]
    public void Trim_Shifts_Trend_Anchors_And_Drops_Spent_Trends_But_Keeps_Levels()
    {
        RunInSta(() =>
        {
            var chart = NewChart();
            chart.SetBars(Bars(600));
            chart.AddDrawing(new CandleChartControl.ChartDrawing("trend", 10, 2650.0, 20, 2660.0));
            chart.AddDrawing(new CandleChartControl.ChartDrawing("hlevel", 0, 2655.0, 0, 0));

            // One new bar → front-trim → trend anchors shift back by one.
            chart.UpsertBar(Bars(1)[0] with
            {
                Time = 1790000000L + 600 * 60,
            });

            var trend = chart.Drawings.First(d => d.Kind == "trend");
            Assert.Equal(9, trend.Index1);
            Assert.Equal(19, trend.Index2);

            var level = chart.Drawings.First(d => d.Kind == "hlevel");
            Assert.Equal(2655.0, level.Price1);   // price-anchored: untouched

            // Push the trend's last anchor (now 19) out of the window.
            for (var i = 0; i < 20; i++)
            {
                chart.UpsertBar(Bars(1)[0] with { Time = 1790000000L + (601 + i) * 60 });
            }

            Assert.DoesNotContain(chart.Drawings, d => d.Kind == "trend");
            Assert.Single(chart.Drawings.Where(d => d.Kind == "hlevel"));
        });
    }

    [Fact]
    public void SetBars_NewWindow_Starts_A_Fresh_Canvas()
    {
        RunInSta(() =>
        {
            var chart = NewChart();
            chart.SetBars(Bars(50));
            chart.AddDrawing(new CandleChartControl.ChartDrawing("trend", 5, 2650.0, 20, 2660.0));

            chart.SetBars(Bars(80));   // symbol/TF switch → index anchors invalid
            Assert.Empty(chart.Drawings);
        });
    }

    [Fact]
    public void Plain_RightClick_Raises_The_Context_Menu_Event()
    {
        RunInSta(() =>
        {
            var chart = NewChart();
            chart.SetBars(Bars(100));
            chart.Measure(new Size(800, 400));
            chart.Arrange(new Rect(0, 0, 800, 400));

            (Point Pos, double Price, int Bar)? raised = null;
            chart.ChartContextMenuRequested += (pos, price, bar) => raised = (pos, price, bar);

            // A click without drag: down+up at the same point.
            var p = new Point(300, 200);
            chart.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = UIElement.MouseRightButtonDownEvent,
                Source = chart,
            });
            chart.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = UIElement.MouseRightButtonUpEvent,
                Source = chart,
            });

            Assert.NotNull(raised);
            Assert.InRange(raised.Value.Pos.X, 0, 800);
            Assert.InRange(raised.Value.Price, 2600, 2700);   // sane price under the cursor
            Assert.InRange(raised.Value.Bar, 0, 99);
        });
    }
}
