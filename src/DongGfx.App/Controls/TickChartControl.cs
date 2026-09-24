using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DongGfx.Core.Fx;
using DongGfx.Core.Models;

namespace DongGfx.App.Controls;

/// <summary>One indicator polyline drawn over the tick series: index-aligned
/// points (null = warm-up gap), its own brush, and a legend label.</summary>
public sealed record ChartOverlay(string Name, Brush Brush, IReadOnlyList<IndicatorPoint> Points);

/// <summary>
/// Lightweight tick chart — a single polyline drawn in OnRender, with
/// min/max/last-price labels and indicator overlay polylines (EMA/Bollinger).
/// No charting dependency, just WPF primitives.
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

    public static readonly DependencyProperty OverlaysProperty = DependencyProperty.Register(
        nameof(Overlays),
        typeof(IReadOnlyList<ChartOverlay>),
        typeof(TickChartControl),
        new FrameworkPropertyMetadata(Array.Empty<ChartOverlay>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowIndicatorsProperty = DependencyProperty.Register(
        nameof(ShowIndicators),
        typeof(bool),
        typeof(TickChartControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

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

    /// <summary>Indicator polylines drawn over the price line (each on the
    /// same min/max scale; null warm-up values render as gaps).</summary>
    public IReadOnlyList<ChartOverlay> Overlays
    {
        get => (IReadOnlyList<ChartOverlay>)GetValue(OverlaysProperty);
        set => SetValue(OverlaysProperty, value);
    }

    /// <summary>When true (default), the control computes and draws its own
    /// indicator overlays from the tick series it already holds: EMA(20),
    /// Bollinger(20,2) upper/lower, and an RSI(14) legend read-out.</summary>
    public bool ShowIndicators
    {
        get => (bool)GetValue(ShowIndicatorsProperty);
        set => SetValue(ShowIndicatorsProperty, value);
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

        var legendText = (string?)null;
        if (ShowIndicators)
        {
            var self = BuildIndicators(n);
            DrawOverlays(dc, left, top, plotWidth, plotHeight, n, min, max, self);
            legendText = _legend;
        }

        var external = Overlays;
        if (external is { Count: > 0 })
        {
            DrawOverlays(dc, left, top, plotWidth, plotHeight, n, min, max, external);
        }

        // Last price dashed marker line
        var last = _ticks[^1];
        var lastY = top + plotHeight * (1d - (last.Quote - min) / (max - min));
        var dashed = new Pen(LabelBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        dc.DrawLine(dashed, new Point(left, lastY), new Point(right, lastY));

        // Labels: max / min / last (+ indicator legend bottom-left)
        dc.DrawText(MakeLabel(max.ToString("0.00000", CultureInfo.InvariantCulture)), new Point(right - 70, top));
        dc.DrawText(MakeLabel(min.ToString("0.00000", CultureInfo.InvariantCulture)), new Point(right - 70, bottom - 14));
        dc.DrawText(MakeLabel($"last {last.Quote:0.00000}"), new Point(right - 130, lastY - 14));
        if (legendText is { } legend)
        {
            dc.DrawText(MakeLabel(legend), new Point(left + 2, bottom - 14));
        }
    }

    private string? _legend;

    /// <summary>Builds the self-rendered overlay set from the current tick
    /// window via <see cref="FxIndicators"/>, and the legend line that goes
    /// with it. Warm-up periods (fewer ticks than the indicator period)
    /// simply omit that overlay.</summary>
    private IReadOnlyList<ChartOverlay> BuildIndicators(int n)
    {
        _legend = null;
        if (n < 20)
        {
            return Array.Empty<ChartOverlay>();
        }

        var closes = new double[n];
        for (var i = 0; i < n; i++)
        {
            closes[i] = _ticks[i].Quote;
        }

        var list = new List<ChartOverlay>(3);

        var ema = FxIndicators.Ema(closes, 20);
        if (ema[^1].Value is not null)
        {
            list.Add(new ChartOverlay("EMA(20)", MakeBrush(0xFF, 0xA5, 0x00), ema));
        }

        var bb = FxIndicators.Bollinger(closes, 20, 2.0);
        if (bb[^1].Upper is not null)
        {
            list.Add(new ChartOverlay("BB upper", MakeBrush(0xFF, 0x6B, 0x6B),
                bb.Select(b => new IndicatorPoint(b.Index, b.Upper)).ToArray()));
            list.Add(new ChartOverlay("BB lower", MakeBrush(0x6B, 0xB6, 0xFF),
                bb.Select(b => new IndicatorPoint(b.Index, b.Lower)).ToArray()));
        }

        if (list.Count > 0)
        {
            _legend = "EMA(20) · BB(20,2)";
            var rsi = FxIndicators.Rsi(closes, 14);
            if (rsi[^1].Value is { } rsiValue)
            {
                _legend += $" · RSI(14) {rsiValue:0.0}";
            }
        }

        return list;
    }

    /// <summary>Draws each overlay polyline on the same scale as the price
    /// line. Null warm-up values break the line (a gap, not a zero).</summary>
    private void DrawOverlays(
        DrawingContext dc, double left, double top, double plotWidth, double plotHeight,
        int n, double min, double max, IReadOnlyList<ChartOverlay> overlays)
    {
        var scale = max - min;
        if (scale < 1e-12)
        {
            return;
        }

        foreach (var overlay in overlays)
        {
            var pts = overlay.Points;
            if (pts is not { Count: > 0 })
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var pen = false;
                for (var i = 0; i < pts.Count && i < n; i++)
                {
                    var v = pts[i].Value;
                    if (v is null)
                    {
                        pen = false;
                        continue;
                    }

                    var x = left + plotWidth * (i / (double)Math.Max(1, n - 1));
                    var y = top + plotHeight * (1d - (v.Value - min) / scale);
                    if (!pen)
                    {
                        ctx.BeginFigure(new Point(x, y), false, false);
                        pen = true;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y), true, false);
                    }
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(overlay.Brush, 1.4), geometry);
        }
    }

    private FormattedText MakeLabel(string text) => new(
        text,
        CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight,
        new Typeface("Consolas"),
        11,
        LabelBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}