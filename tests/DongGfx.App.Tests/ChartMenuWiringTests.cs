using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DongGfx.App.Controls;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The chart trading menu's open must be DEFERRED off the right-click
/// input route: a synchronous ContextMenu.IsOpen inside the chart's
/// right-button-up handler is dismissed by that same input sequence (the
/// context-menu service closes what it just opened), which shipped the
/// menu unreachable in the live app — the drag-to-trendline branch of the
/// same handler worked, so only a wiring-level test catches this. The
/// assertions: the open callback runs (nothing is open synchronously
/// before it), the menu carries Buy/Sell/Add-level/Clear, the Add-level
/// item renders the horizontal level at the clicked price, Clear empties
/// the canvas, and the Buy/Sell headers carry the ticket lots and symbol.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class ChartMenuWiringTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings = new() { IsDemo = true, Mt5MaxLots = 1.00m };
    private readonly ManualRealMoneyGate _gate = new();
    private readonly List<FrameworkElement> _trash = new();

    public ChartMenuWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_menuw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        foreach (var c in _trash)
        {
            try { ((IDisposable)c).Dispose(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>HttpMessageHandler that always fails fast (sidecar down) —
    /// the wiring test never touches the bridge.</summary>
    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("bridge down"));
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

    private (TerminalViewModel Vm, CandleChartControl Chart) CreateTerminalAndChart()
    {
        var vm = new TerminalViewModel(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(new DeadHandler(), new Uri("http://127.0.0.1:1/")));
        var chart = new CandleChartControl();
        _trash.Add(chart);
        vm.CandleChart = chart;
        return (vm, chart);
    }

    /// <summary>Raises the chart's plain right-click gesture the same way
    /// the control's own gesture tests do (down+up at one point).</summary>
    private static void PlainRightClick(CandleChartControl chart, Point p)
    {
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
    }

    [Fact]
    public void Menu_Opens_Only_Via_The_Deferred_Callback_With_The_Four_Items()
    {
        RunInSta(() =>
        {
            var (vm, chart) = CreateTerminalAndChart();
            chart.SetBars(Bars(100));
            chart.Measure(new Size(800, 400));
            chart.Arrange(new Rect(0, 0, 800, 400));

            ContextMenu? opened = null;
            MainWindow.WireChartTradingCore(vm, chart, menu => opened = menu);

            PlainRightClick(chart, new Point(300, 200));

            // The wiring must hand the menu to the open callback — a
            // synchronous open inside the handler never survives the click.
            Assert.NotNull(opened);
            Assert.False(opened!.IsOpen);   // the callback owns the timing

            var headers = opened.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Assert.Contains(headers, h => h!.StartsWith("Buy 0.1 "));
            Assert.Contains(headers, h => h!.StartsWith("Sell 0.1 "));
            Assert.Single(headers, h => h!.StartsWith("Add horizontal line @ "));
            Assert.Contains("Clear drawings", headers);
        });
    }

    [Fact]
    public void Add_Level_Item_Renders_The_Horizontal_Level_At_The_Clicked_Price()
    {
        RunInSta(() =>
        {
            var (vm, chart) = CreateTerminalAndChart();
            chart.SetBars(Bars(100));
            chart.Measure(new Size(800, 400));
            chart.Arrange(new Rect(0, 0, 800, 400));

            ContextMenu? opened = null;
            MainWindow.WireChartTradingCore(vm, chart, menu => opened = menu);

            PlainRightClick(chart, new Point(300, 200));

            var hline = opened!.Items.OfType<MenuItem>()
                .First(i => (i.Header as string)!.StartsWith("Add horizontal line @ "));
            hline.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            var level = Assert.Single(chart.Drawings, d => d.Kind == "hlevel");
            var price = double.Parse(
                (hline.Header as string)!.Replace("Add horizontal line @ ", ""),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(price, level.Price1, 5);
        });
    }

    [Fact]
    public void Clear_Drawings_Item_Empties_The_Canvas()
    {
        RunInSta(() =>
        {
            var (vm, chart) = CreateTerminalAndChart();
            chart.SetBars(Bars(100));
            chart.Measure(new Size(800, 400));
            chart.Arrange(new Rect(0, 0, 800, 400));
            chart.AddDrawing(new CandleChartControl.ChartDrawing("trend", 5, 2650.0, 20, 2660.0));

            ContextMenu? opened = null;
            MainWindow.WireChartTradingCore(vm, chart, menu => opened = menu);

            PlainRightClick(chart, new Point(300, 200));

            var clear = opened!.Items.OfType<MenuItem>()
                .First(i => i.Header as string == "Clear drawings");
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Empty(chart.Drawings);
        });
    }

    [Fact]
    public void Buy_Sell_Items_Fire_The_Chart_Order_Command_With_The_Ticket_Symbol()
    {
        RunInSta(() =>
        {
            var (vm, chart) = CreateTerminalAndChart();
            chart.SetBars(Bars(100));
            chart.Measure(new Size(800, 400));
            chart.Arrange(new Rect(0, 0, 800, 400));
            vm.Mt5Symbol = "XAUUSD";

            ContextMenu? opened = null;
            MainWindow.WireChartTradingCore(vm, chart, menu => opened = menu);

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

            var headers = opened!.Items.OfType<MenuItem>()
                .Select(i => i.Header as string).ToList();
            Assert.Contains(headers, h => h!.Contains("XAUUSD"));
        });
    }
}
