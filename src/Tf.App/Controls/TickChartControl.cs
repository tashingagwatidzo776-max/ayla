using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tf.Core.Models;

namespace Tf.App.Controls;

/// <summary>
/// Lightweight tick chart — a single polyline drawn in OnRender, with
/// min/max/last-price labels. No charting dependency, just WPF primitives.
/// Must be fed from the UI thread (the view models marshal for us).
/// </summary>
public sealed class TickChartControl : FrameworkElement
{
    private readonly List<Tick> _ticks = new();
    private int _maxPoints = 400;
    private bool _renderQueued;
    private readonly System.Windows.Threading.DispatcherTimer _renderTimer;
    private const int TargetFps = 20;
    private const int RenderIntervalMs = 1000 / TargetFps;

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(System.Windows.Media.Brush),
        typeof(TickChartControl),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(System.Windows.Media.Brush),
        typeof(TickChartControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Brush LineBrush
    {
        get => (System.Windows.Media.Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public System.Windows.Media.Brush LabelBrush
    {
        get => (System.Windows.Media.Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public TickChartControl()
    {
        _renderTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RenderIntervalMs)
        };
        _renderTimer.Tick += (_, _) =>
        {
            _renderQueued = false;
            _renderTimer.Stop();
            InvalidateVisual();
        };
    }

    public int MaxPoints
    {
        get => _maxPoints;
        set
        {
            _maxPoints = Math.Max(10, value);
            Trim();
            InvalidateVisual();
        }
    }

    public IReadOnlyList<Tick> Ticks => _ticks;

    public void AddTicks(IEnumerable<Tick> ticks)
    {
        _ticks.AddRange(ticks);
        Trim();

        // Rate-limit redraws to ~20fps to avoid choking the UI thread.
        if (!_renderQueued)
        {
            _renderQueued = true;
            _renderTimer.Start();
        }
    }

    public void Clear()
    {
        _ticks.Clear();
        InvalidateVisual();
    }

    private void Trim()
    {
        if (_ticks.Count > _maxPoints)
        {
            _ticks.RemoveRange(0, _ticks.Count - _maxPoints);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2 || _ticks.Count == 0)
        {
            return;
        }

        double min = _ticks[0].Quote, max = _ticks[0].Quote;
        foreach (var t in _ticks)
        {
            min = Math.Min(min, t.Quote);
            max = Math.Max(max, t.Quote);
        }

        if (Math.Abs(max - min) < 1e-12)
        {
            var pad = Math.Max(Math.Abs(max) * 0.001, 1e-9);
            min -= pad;
            max += pad;
        }
        else
        {
            var range = max - min;
            min -= range * 0.08;
            max += range * 0.08;
        }

        var left = 8d;
        var top = 6d;
        var right = width - 8d;
        var bottom = height - 6d;
        var plotWidth = right - left;
        var plotHeight = bottom - top;

        var n = _ticks.Count;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var first = true;
            for (var i = 0; i < n; i++)
            {
                var x = left + plotWidth * (i / (double)Math.Max(1, n - 1));
                var y = top + plotHeight * (1d - (_ticks[i].Quote - min) / (max - min));
                if (first)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                    first = false;
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 1.5), geometry);

        // Last price dashed marker line
        var last = _ticks[^1];
        var lastY = top + plotHeight * (1d - (last.Quote - min) / (max - min));
        var dashed = new Pen(LabelBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        dc.DrawLine(dashed, new Point(left, lastY), new Point(right, lastY));

        // Labels: max / min / last
        dc.DrawText(MakeLabel(max.ToString("0.00000", CultureInfo.InvariantCulture)), new Point(right - 70, top));
        dc.DrawText(MakeLabel(min.ToString("0.00000", CultureInfo.InvariantCulture)), new Point(right - 70, bottom - 14));
        dc.DrawText(MakeLabel($"last {last.Quote:0.00000}"), new Point(right - 130, lastY - 14));
    }

    private FormattedText MakeLabel(string text) => new(
        text,
        CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight,
        new Typeface("Consolas"),
        11,
        LabelBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}