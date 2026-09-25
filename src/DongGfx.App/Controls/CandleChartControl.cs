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
    private static readonly Brush TrendBrush = MakeBrush(0xFF, 0xD1, 0x66);
    private static readonly Brush LevelBrush = MakeBrush(0x6B, 0xD6, 0xFF);

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
        ResetDrawingsForNewWindow();
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
                ShiftDrawingsAfterTrim();
            }
        }

        InvalidateVisual();
    }

    /// <summary>One bar scrolled out of the front of the window shifts
    /// trend anchors back by one; a trendline whose LAST anchor has left
    /// the window is dropped entirely (h-levels are price-anchored and
    /// never drop).</summary>
    private void ShiftDrawingsAfterTrim()
    {
        for (var i = _drawings.Count - 1; i >= 0; i--)
        {
            var d = _drawings[i];
            if (d.Kind == "hlevel")
            {
                continue;
            }

            var i1 = d.Index1 - 1;
            var i2 = d.Index2 - 1;
            if (i2 < i1)
            {
                (i1, i2) = (i2, i1);
            }

            if (i2 < 0)
            {
                _drawings.RemoveAt(i);   // both anchors scrolled out
                continue;
            }

            _drawings[i] = d with { Index1 = Math.Max(0, i1), Index2 = Math.Max(0, i2) };
        }
    }

    public IReadOnlyList<FxBar> Bars => _bars;

    // ── User drawings (trendlines, horizontal levels) ────────────────

    /// <summary>One user drawing anchored to (bar index, price) pairs so
    /// it stays glued to its bars through zoom and pan. "trend" renders a
    /// segment between the anchors; "hlevel" renders a full-width
    /// horizontal line at Price1 (Index2/Price2 ignored) and is immune to
    /// bar-window trims because it carries no index.</summary>
    public sealed record ChartDrawing(string Kind, int Index1, double Price1, int Index2, double Price2);

    private readonly List<ChartDrawing> _drawings = new();

    /// <summary>User drawings in insertion order.</summary>
    public IReadOnlyList<ChartDrawing> Drawings => _drawings;

    /// <summary>Right-click on the plot (no drag) — the Terminal opens the
    /// chart context menu at this position with the price/bar under it.</summary>
    public event Action<Point, double, int>? ChartContextMenuRequested;

    // Right-drag gesture state: origin point + live trendline preview.
    private Point? _rightOrigin;
    private ChartDrawing? _previewTrend;

    /// <summary>Commits a drawing. Anchors clamp into the current bar
    /// window (a chart with no bars clamps to 0).</summary>
    public void AddDrawing(ChartDrawing drawing)
    {
        var last = Math.Max(0, _bars.Count - 1);
        _drawings.Add(drawing with
        {
            Index1 = Math.Clamp(drawing.Index1, 0, last),
            Index2 = Math.Clamp(drawing.Index2, 0, last),
        });
        InvalidateVisual();
    }

    public void ClearDrawings()
    {
        _drawings.Clear();
        InvalidateVisual();
    }

    /// <summary>A replaced bar window (bridge candle load, usually a
    /// symbol or timeframe switch) invalidates index anchors — start with
    /// a fresh canvas rather than draw lines glued to the wrong bars.</summary>
    private void ResetDrawingsForNewWindow() => _drawings.Clear();

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
        if (_bars.Count == 0)
        {
            return (0, 0);
        }

        var count = Math.Max(1, (int)(plotWidth / Math.Max(2, _barWidth)));
        var end = Math.Clamp(_bars.Count - _panBarsOffset, 1, _bars.Count);
        var start = Math.Max(0, Math.Min(end - count, end - 1));
        return (start, end);
    }

    private double PriceToY(double price, double top, double plotHeight, double min, double max) =>
        top + plotHeight * (1d - (price - min) / (max - min));

    private double YToPrice(double y, double top, double plotHeight, double min, double max) =>
        min + (1d - (y - top) / plotHeight) * (max - min);

    /// <summary>The plot rectangle (excludes price/time axes), or null
    /// when the control is too small to draw. Shared by OnRender and the
    /// input handlers so gestures and rendering agree on geometry.</summary>
    private (double L, double T, double R, double B)? PlotRect()
    {
        var left = 6d;
        var top = 4d;
        var right = ActualWidth - 62d;
        var bottom = ActualHeight - 20d;
        if (ActualWidth < 80 || ActualHeight < 60 || right - left < 20 || bottom - top < 20)
        {
            return null;
        }

        return (left, top, right, bottom);
    }

    /// <summary>The price scale over the visible window (candles +
    /// Bollinger participation + padding). Shared by OnRender and the
    /// input handlers so the cursor price and the drawn scale agree.</summary>
    private (double Min, double Max) ComputeScale(int start, int end)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (var i = start; i < end && i < _bars.Count; i++)
        {
            min = Math.Min(min, _bars[i].Low);
            max = Math.Max(max, _bars[i].High);
        }

        if (min > max)
        {
            return (0, 1);
        }

        var bb = FxIndicators.Bollinger(_bars.Select(b => b.Close).ToArray(), 20, 2.0);
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

        return (min, max);
    }

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

        if (_rightOrigin is not null)
        {
            UpdateTrendPreview(_mouse.Value);
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
        _rightOrigin = null;
        _previewTrend = null;
        InvalidateVisual();
    }

    // ── Right-button: drag = trendline, click = context menu ─────────

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        _rightOrigin = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var origin = _rightOrigin;
        _rightOrigin = null;
        _previewTrend = null;
        ReleaseMouseCapture();
        e.Handled = true;
        if (origin is null || PlotRect() is not { } rect)
        {
            return;   // gesture started outside the plot (or too small)
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - origin.Value.X) + Math.Abs(pos.Y - origin.Value.Y) < 4)
        {
            // A click, not a drag: raise the context menu at the cursor.
            // Scale/window match OnRender exactly — the clicked price is
            // the price the user sees under the cursor.
            var (start, end) = VisibleRange((int)(rect.R - rect.L));
            var (min, max) = ComputeScale(start, end);
            var price = YToPrice(pos.Y, rect.T, rect.B - rect.T, min, max);
            var idx = Math.Clamp(start + (int)((pos.X - rect.L) / Math.Max(2, _barWidth)), start, Math.Max(start, end - 1));
            ChartContextMenuRequested?.Invoke(pos, price, idx);
            return;
        }

        // A drag: commit a trendline between the two anchors.
        CommitTrend(origin.Value, pos, rect);
    }

    /// <summary>Converts two plot points into an index/price-anchored
    /// trendline using the visible-window scale (what the user sees).</summary>
    private void CommitTrend(Point from, Point to, (double L, double T, double R, double B) rect)
    {
        var plotH = rect.B - rect.T;
        var plotW = rect.R - rect.L;
        var (start, end) = VisibleRange((int)plotW);
        var (min, max) = ComputeScale(start, end);
        int Idx(Point p) => Math.Clamp(start + (int)((p.X - rect.L) / Math.Max(2, _barWidth)), start, Math.Max(start, end - 1));
        double Price(Point p) => YToPrice(p.Y, rect.T, plotH, min, max);

        AddDrawing(new ChartDrawing("trend", Idx(from), Price(from), Idx(to), Price(to)));
    }

    /// <summary>Live trendline preview while the right button is down.
    /// The drag moves through OnMouseMove; the preview renders dashed.</summary>
    private void UpdateTrendPreview(Point pos)
    {
        if (_rightOrigin is not { } origin || PlotRect() is not { } rect)
        {
            _previewTrend = null;
            return;
        }

        if (Math.Abs(pos.X - origin.X) + Math.Abs(pos.Y - origin.Y) < 4)
        {
            _previewTrend = null;
            return;
        }

        var plotH = rect.B - rect.T;
        var plotW = rect.R - rect.L;
        var (start, end) = VisibleRange((int)plotW);
        var (min, max) = ComputeScale(start, end);
        int Idx(Point p) => Math.Clamp(start + (int)((p.X - rect.L) / Math.Max(2, _barWidth)), start, Math.Max(start, end - 1));
        double Price(Point p) => YToPrice(p.Y, rect.T, plotH, min, max);

        _previewTrend = new ChartDrawing("trend", Idx(origin), Price(origin), Idx(pos), Price(pos));
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

        // User drawings + the live right-drag preview, above the overlays.
        foreach (var d in _drawings)
        {
            DrawDrawing(dc, d, left, top, plotW, plotH, min, max, start);
        }

        if (_previewTrend is { } preview)
        {
            var ghostBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xD1, 0x66));
            ghostBrush.Freeze();
            var ghost = new Pen(ghostBrush, 1.2) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
            double Xp(int index) => left + ((index - start) * _barWidth) + (_barWidth / 2);
            dc.DrawLine(ghost,
                new Point(Xp(preview.Index1), PriceToY(preview.Price1, top, plotH, min, max)),
                new Point(Xp(preview.Index2), PriceToY(preview.Price2, top, plotH, min, max)));
        }

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

    /// <summary>One committed drawing: dashed gold trend segment between
    /// its bar-anchored endpoints, or dashed cyan full-width horizontal
    /// level with a price tag.</summary>
    private void DrawDrawing(
        DrawingContext dc, ChartDrawing d, double left, double top,
        double plotW, double plotH, double min, double max, int start)
    {
        var pen = new Pen(d.Kind == "hlevel" ? LevelBrush : TrendBrush, 1.4)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };

        if (d.Kind == "hlevel")
        {
            var y = PriceToY(d.Price1, top, plotH, min, max);
            dc.DrawLine(pen, new Point(left, y), new Point(left + plotW, y));
            dc.DrawText(MakeLabel(d.Price1.ToString("0.#####", CultureInfo.InvariantCulture), LevelBrush),
                new Point(left + 4, y - 13));
            return;
        }

        double X(int index) => left + ((index - start) * _barWidth) + (_barWidth / 2);
        dc.DrawLine(pen,
            new Point(X(d.Index1), PriceToY(d.Price1, top, plotH, min, max)),
            new Point(X(d.Index2), PriceToY(d.Price2, top, plotH, min, max)));
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
