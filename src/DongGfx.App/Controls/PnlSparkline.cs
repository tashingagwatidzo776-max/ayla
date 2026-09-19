using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DongGfx.App.Controls;

/// <summary>
/// Tiny hand-drawn sparkline for one runner-grid row: the account's
/// intraday cumulative P&amp;L (from the <see cref="DongGfx.Core.Analytics.PerformanceTracker"/>
/// series the P&amp;L curve chart uses) as a thin amber polyline over a
/// dashed zero line. Draws a subtle placeholder when there is no data yet.
/// Bind <see cref="Points"/> in XAML or call <see cref="SetPoints"/>;
/// must be fed from the UI thread (the view model marshals for us).
/// </summary>
public sealed class PnlSparkline : FrameworkElement
{
    /// <summary>Bindable series: cumulative P&amp;L, one point per settled trade.</summary>
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<decimal>), typeof(PnlSparkline),
        new PropertyMetadata(null, OnPointsChanged));

    public IReadOnlyList<decimal>? Points
    {
        get => (IReadOnlyList<decimal>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PnlSparkline)d).SetPoints(e.NewValue as IReadOnlyList<decimal> ?? Array.Empty<decimal>());
    private IReadOnlyList<decimal> _points = Array.Empty<decimal>();

    private static readonly Brush SeriesBrush = MakeBrush(0xFF, 0xC8, 0x57); // amber
    private static readonly Brush EmptyBrush = MakeBrush(0x51, 0x5A, 0x66);  // dim grey
    private static readonly Pen ZeroPen;

    static PnlSparkline()
    {
        var pen = new Pen(MakeBrush(0x6B, 0x74, 0x82), 0.75)
        {
            DashStyle = new DashStyle(new double[] { 2, 2 }, 0)
        };
        pen.Freeze();
        ZeroPen = pen;
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Replaces the series (cumulative P&amp;L, one point per settled trade).</summary>
    public void SetPoints(IReadOnlyList<decimal> points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2)
        {
            return;
        }

        if (_points.Count == 0)
        {
            dc.DrawRoundedRectangle(EmptyBrush, null, new Rect(2, height / 2 - 0.75, width - 4, 1.5), 1, 1);
            return;
        }

        var left = 2d;
        var top = 2d;
        var right = width - 2d;
        var bottom = height - 2d;
        var plotWidth = right - left;
        var plotHeight = bottom - top;

        var min = 0m;
        var max = 0m;
        foreach (var value in _points)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (min == max)
        {
            var pad = Math.Max(Math.Abs(max) * 0.1m, 0.01m);
            min -= pad;
            max += pad;
        }

        double Y(decimal v) => top + plotHeight * (double)(1m - (v - min) / (max - min));

        // Zero line (breakeven) so a flat or all-negative curve still reads.
        var zeroY = Y(0m);
        if (zeroY >= top && zeroY <= bottom)
        {
            dc.DrawLine(ZeroPen, new Point(left, zeroY), new Point(right, zeroY));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var n = _points.Count;
            var first = true;
            for (var i = 0; i < n; i++)
            {
                var x = left + plotWidth * (i / (double)Math.Max(1, n - 1));
                var y = Y(_points[i]);
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
        dc.DrawGeometry(null, new Pen(SeriesBrush, 1.25), geometry);

        // Endpoint dot — the account's current net for the day.
        var lastX = left + plotWidth;
        var lastY = Y(_points[^1]);
        dc.DrawEllipse(SeriesBrush, null, new Point(lastX, lastY), 1.75, 1.75);
    }
}
