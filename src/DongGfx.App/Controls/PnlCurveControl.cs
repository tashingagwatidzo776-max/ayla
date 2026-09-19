using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DongGfx.App.Controls;

/// <summary>
/// Lightweight multi-series P&amp;L curve — one polyline per account's
/// cumulative daily P&amp;L, with a zero line and a color legend. Hand-drawn
/// in OnRender like <see cref="TickChartControl"/>; no charting dependency.
/// Must be fed from the UI thread (the view model marshals for us).
/// </summary>
public sealed class PnlCurveControl : FrameworkElement
{
    private IReadOnlyList<(string Name, IReadOnlyList<(DateTimeOffset At, decimal Value)> Points)> _series
        = Array.Empty<(string, IReadOnlyList<(DateTimeOffset, decimal)>)>();

    private static readonly string[] Palette =
    {
        "#FF00D4AA", "#FFFFC857", "#FF7EB3FF", "#FFFF5C5C", "#FFB48CFF", "#FF8CE99A"
    };

    public void SetSeries(
        IReadOnlyList<(string Name, IReadOnlyList<(DateTimeOffset At, decimal Value)> Points)> series)
    {
        _series = series;
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

        var left = 8d;
        var top = 6d;
        var right = width - 8d;
        var bottom = height - 6d;
        var plotWidth = right - left;
        var plotHeight = bottom - top;

        if (_series.Count == 0 || _series.All(s => s.Points.Count == 0))
        {
            dc.DrawText(
                MakeLabel("no settled growth trades yet today", TextSecondary),
                new Point(left + 4, top + plotHeight / 2 - 8));
            return;
        }

        var min = 0m;
        var max = 0m;
        foreach (var (_, points) in _series)
        {
            foreach (var (_, value) in points)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        if (min == max)
        {
            var pad = Math.Max(Math.Abs(max) * 0.1m, 0.1m);
            min -= pad;
            max += pad;
        }

        double Y(decimal v) => top + plotHeight * (double)(1m - (v - min) / (max - min));

        // Zero line (breakeven) — dashed.
        var zeroY = (double)Y(0m);
        if (zeroY >= top && zeroY <= bottom)
        {
            var zeroPen = new Pen(TextSecondary, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(zeroPen, new Point(left, zeroY), new Point(right, zeroY));
        }

        for (var s = 0; s < _series.Count; s++)
        {
            var (name, points) = _series[s];
            if (points.Count == 0)
            {
                continue;
            }

            var brush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Palette[s % Palette.Length]));
            brush.Freeze();
            var pen = new Pen(brush, 1.5);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var n = points.Count;
                var first = true;
                for (var i = 0; i < n; i++)
                {
                    // X by trade index within the series — intraday trades are
                    // sparse and unevenly spaced, so an index axis reads better.
                    var x = left + plotWidth * (i / (double)Math.Max(1, n - 1));
                    var y = (double)Y(points[i].Value);
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
            dc.DrawGeometry(null, pen, geometry);

            // Legend swatch + account name in fixed slots along the top.
            var slotX = left + 4 + s * 110;
            dc.DrawRectangle(brush, null, new Rect(slotX, top + 2, 8, 8));
            dc.DrawText(
                MakeLabel($"{name} {points[^1].Value:+0.##;-0.##;0}", TextSecondary),
                new Point(slotX + 12, top - 2));
        }

        // Min/max labels of the combined range.
        dc.DrawText(MakeLabel($"+{max:0.##}", TextSecondary), new Point(right - 54, top));
        dc.DrawText(MakeLabel($"{min:0.##}", TextSecondary), new Point(right - 54, bottom - 14));
    }

    private static readonly Brush TextSecondary = MakeSecondaryBrush();

    private static Brush MakeSecondaryBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA3, 0xB2));
        brush.Freeze();
        return brush;
    }

    private FormattedText MakeLabel(string text, Brush brush) => new(
        text,
        CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight,
        new Typeface("Consolas"),
        11,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
