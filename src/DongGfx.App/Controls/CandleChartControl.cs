using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DongGfx.Core.Fx;

namespace DongGfx.App.Controls;

/// <summary>
/// MT5-style candlestick chart drawn entirely in OnRender — wicks + bodies,
/// right-hand price axis with a last-price tag, bottom time axis, horizontal
/// grid, crosshair read-out, wheel zoom and drag pan. Overlays (EMA/Bollinger)
/// computed via <see cref="FxIndicators"/> render in the same price transform.
/// No charting dependency; fed from the UI thread.
/// </summary>
public sealed class CandleChartControl : FrameworkElement
{
    private readonly List<FxBar> _bars = new();

    // View state: bar width in px, pan offset (0 = latest bar at right edge).
    private double _barWidth = 7;
    private int _panBarsOffset;
    private bool _followLatest = true;

    // Crosshair position (null = hidden).
    private Point? _mouse;

    private static readonly Brush UpBrush = MakeBrush(0x00, 0xC8, 0x96);
    private static readonly Brush DownBrush = MakeBrush(0xFF, 0x4D, 0x4D);
    private static readonly Brush GridBrush = MakeBrush(0x2A, 0x30, 0x3A);
    private static readonly Brush AxisBrush = MakeBrush(0x8A, 0x93, 0xA2);
    private static readonly Brush CrossBrush = MakeBrush(0x9A, 0xA3, 0xB2);
    private static readonly Brush EmaBrush = MakeBrush(0xFF, 0xA5, 0x00);
    private static readonly Brush BbUpBrush = MakeBrush(0xFF, 0x6B, 0x6B);
    private static readonly Brush BbLoBrush = MakeBrush(0x6B, 0xB6, 0xFF);

    public CandleChartControl()
    {
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.Cross;
    }

    /// <summary>Replace the whole bar window (bridge candle load).</summary>
    public void SetBars(IEnumerable<FxBar> bars)
    {
        _bars.Clear();
        _bars.AddRange(bars);
        if (_bars.Count > 600)
        {
            _bars.RemoveRange(0, _bars.Count - 600);
        }

        InvalidateVisual();
    }

    /// <summary>Append/roll the newest bar (live tick aggregation).</summary>
    public void UpsertBar(FxBar bar)
    {
        if (_bars.Count > 0 && _bars[^1].Time == bar.Time)
        {
            _bars[^1] = bar;
        }
        else
        {
            _bars.Add(bar);
            if (_bars.Count > 600)
            {
                _bars.RemoveAt(0);
            }
        }

        InvalidateVisual();
    }

    public IReadOnlyList<FxBar> Bars => _bars;

    /// <summary>Overwrites the newest bar (same-second tick roll-up).
    /// No-op when the chart is empty.</summary>
    public void UpdateLast(FxBar bar)
    {
        if (_bars.Count > 0)
        {
            _bars[^1] = bar;
            InvalidateVisual();
        }
    }
    public double BarWidth => _barWidth;
    public bool IsFollowing => _followLatest;
    public int PanOffset => _panBarsOffset;

    // ── Transforms (exposed for tests and the crosshair) ──────────────

    /// <summary>The slice of bars currently visible after zoom/pan.
    /// Defensive against any offset (the pan handler clamps, but this
    /// method must stay sane on its own).</summary>
    public (int Start, int End) VisibleRange(int plotWidth)
    {
        var count = Math.Max(1, (int)(plotWidth / Math.Max(2, _barWidth)));
        var end = Math.Clamp(_bars.Count - _panBarsOffset, 1, _bars.Count);
        var start = Math.Max(0, Math.Min(end - count, end - 1));
        return (start, end);
    }

    private double PriceToY(double price, double top, double plotHeight, double min, double max) =>
        top + plotHeight * (1d - (price - min) / (max - min));

    private double YToPrice(double y, double top, double plotHeight, double min, double max) =>
        min + (1d - (y - top) / plotHeight) * (max - min);

    // ── Input: zoom, pan, crosshair ───────────────────────────────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.25 : 0.8;
        _barWidth = Math.Clamp(_barWidth * factor, 2, 24);
        InvalidateVisual();
        e.Handled = true;
    }

    private Point _dragOrigin;
    private int _dragStartOffset;
    private bool _dragging;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _panBarsOffset = 0;
            _followLatest = true;
            InvalidateVisual();
            return;
        }

        _dragOrigin = e.GetPosition(this);
        _dragStartOffset = _panBarsOffset;
        _dragging = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_dragging)
        {
            var dx = _mouse.Value.X - _dragOrigin.X;
            var barsMoved = (int)Math.Round(dx / Math.Max(2, _barWidth));
            var newOffset = Math.Clamp(_dragStartOffset + barsMoved, 0, Math.Max(0, _bars.Count - 10));
            if (newOffset != _panBarsOffset)
            {
                _panBarsOffset = newOffset;
                _followLatest = newOffset == 0;
            }
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    // Double-click returns to following the latest bar (handled alongside
    // the drag logic: FrameworkElement has no OnMouseDoubleClick override).

    // ── Render ─────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        var axisW = 62d;
        var axisH = 20d;
        var left = 6d;
        var top = 4d;
        var right = width - axisW;
        var bottom = height - axisH;
        if (width < 80 || height < 60 || right - left < 20 || bottom - top < 20)
        {
            return;
        }

        var plotW = right - left;
        var plotH = bottom - top;

        var (start, end) = VisibleRange((int)plotW);
        if (end <= start)
        {
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        for (var i = start; i < end; i++)
        {
            min = Math.Min(min, _bars[i].Low);
            max = Math.Max(max, _bars[i].High);
        }

        if (min > max)
        {
            return;
        }

        // Overlay values participate in the scale so bands never clip.
        var closes = _bars.Select(b => b.Close).ToArray();
        var ema = FxIndicators.Ema(closes, 20);
        var bb = FxIndicators.Bollinger(closes, 20, 2.0);
        foreach (var b in bb)
        {
            if (b.Index < start || b.Index >= end)
            {
                continue;
            }

            if (b.Upper is { } u)
            {
                max = Math.Max(max, u);
            }

            if (b.Lower is { } l)
            {
                min = Math.Min(min, l);
            }
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

        // Grid + price axis labels on a 1-2-5 ladder.
        var step = NiceStep((max - min) / 6);
        var gridPen = new Pen(GridBrush, 1);
        var first = Math.Ceiling(min / step) * step;
        for (var p = first; p <= max; p += step)
        {
            var y = PriceToY(p, top, plotH, min, max);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            dc.DrawText(MakeLabel(p.ToString("0.#####", CultureInfo.InvariantCulture), AxisBrush),
                new Point(right + 4, y - 7));
        }

        // Candles.
        var bodyW = Math.Max(1d, _barWidth * 0.68);
        var wickPenUp = new Pen(UpBrush, 1);
        var wickPenDown = new Pen(DownBrush, 1);
        for (var i = start; i < end; i++)
        {
            var b = _bars[i];
            var xCenter = left + ((i - start) * _barWidth) + (_barWidth / 2);
            var up = b.Close >= b.Open;
            var wick = up ? wickPenUp : wickPenDown;
            dc.DrawLine(wick,
                new Point(xCenter, PriceToY(b.High, top, plotH, min, max)),
                new Point(xCenter, PriceToY(b.Low, top, plotH, min, max)));

            var yOpen = PriceToY(b.Open, top, plotH, min, max);
            var yClose = PriceToY(b.Close, top, plotH, min, max);
            var bodyTop = Math.Min(yOpen, yClose);
            var bodyH = Math.Max(1d, Math.Abs(yClose - yOpen));
            var brush = up ? UpBrush : DownBrush;
            dc.DrawRectangle(brush, null, new Rect(xCenter - bodyW / 2, bodyTop, bodyW, bodyH));
        }

        // Overlays (EMA + Bollinger) over the visible window.
        DrawSeries(dc, ema.Select(p => (p.Index, p.Value)), start, end, left, top, plotW, plotH, min, max, EmaBrush, 1.6);
        DrawSeries(dc, bb.Select(b => (b.Index, b.Upper)), start, end, left, top, plotW, plotH, min, max, BbUpBrush, 1.2);
        DrawSeries(dc, bb.Select(b => (b.Index, b.Lower)), start, end, left, top, plotW, plotH, min, max, BbLoBrush, 1.2);

        // Time axis: a label every ~90px at the bar nearest that pixel.
        var labelEveryBars = Math.Max(1, (int)(90 / Math.Max(2, _barWidth)));
        for (var i = start; i < end; i += labelEveryBars)
        {
            var x = left + ((i - start) * _barWidth) + (_barWidth / 2);
            var t = DateTimeOffset.FromUnixTimeSeconds(_bars[i].Time).LocalDateTime;
            dc.DrawText(MakeLabel(t.ToString("HH:mm"), AxisBrush), new Point(x - 16, bottom + 3));
        }

        // Last-price tag on the axis.
        var last = _bars[end - 1];
        var lastY = PriceToY(last.Close, top, plotH, min, max);
        var tagBrush = last.Close >= last.Open ? UpBrush : DownBrush;
        var tagText = MakeLabel(last.Close.ToString("0.#####", CultureInfo.InvariantCulture), Brushes.Black);
        dc.DrawRoundedRectangle(tagBrush, null, new Rect(right + 1, lastY - 9, axisW - 3, 18), 2, 2);
        dc.DrawText(tagText, new Point(right + 5, lastY - 7));

        // Legend.
        var legend = $"EMA(20) · BB(20,2)";
        var rsi = FxIndicators.Rsi(closes, 14);
        if (rsi is { Count: > 0 } && rsi[^1].Value is { } rv)
        {
            legend += $" · RSI(14) {rv:0.0}";
        }

        dc.DrawText(MakeLabel(legend, AxisBrush), new Point(left + 4, top + 2));

        // Crosshair + read-outs.
        if (_mouse is { } m && m.X <= right && m.Y <= bottom)
        {
            var crossPen = new Pen(CrossBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(crossPen, new Point(left, m.Y), new Point(right, m.Y));
            dc.DrawLine(crossPen, new Point(m.X, top), new Point(m.X, bottom));

            var price = YToPrice(m.Y, top, plotH, min, max);
            dc.DrawText(MakeLabel(price.ToString("0.#####", CultureInfo.InvariantCulture), CrossBrush),
                new Point(right + 4, m.Y - 7));

            var idx = start + (int)((m.X - left) / Math.Max(2, _barWidth));
            if (idx >= start && idx < end)
            {
                var bt = DateTimeOffset.FromUnixTimeSeconds(_bars[idx].Time).LocalDateTime;
                dc.DrawText(MakeLabel(bt.ToString("HH:mm:ss"), CrossBrush), new Point(m.X - 26, bottom + 3));
            }
        }
    }

    private void DrawSeries(
        DrawingContext dc, IEnumerable<(int Index, double? Value)> points,
        int start, int end, double left, double top, double plotW, double plotH,
        double min, double max, Brush brush, double thickness)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var pen = false;
            foreach (var (index, value) in points)
            {
                if (index < start || index >= end)
                {
                    if (pen)
                    {
                        pen = false;   // gap at window edges keeps segments honest
                    }

                    continue;
                }

                if (value is not { } v)
                {
                    pen = false;
                    continue;
                }

                var x = left + ((index - start) * _barWidth) + (_barWidth / 2);
                var y = PriceToY(v, top, plotH, min, max);
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
        dc.DrawGeometry(null, new Pen(brush, thickness), geometry);
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw))
        {
            return 1;
        }

        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1d, 2d, 2.5d, 5d, 10d })
        {
            if (raw <= m * mag)
            {
                return m * mag;
            }
        }

        return 10 * mag;
    }

    private FormattedText MakeLabel(string text, Brush brush) => new(
        text,
        CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight,
        new Typeface("Consolas"),
        10.5,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
