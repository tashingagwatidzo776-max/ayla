using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DongGfx.Core.Fx;

// The project's global usings pull in System.Drawing — disambiguate the
// WPF types this control is made of. Brushes/Point are globally aliased
// already, so their uses below are fully qualified instead.
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Size = System.Windows.Size;

namespace DongGfx.App.Controls;

/// <summary>
/// The Maps canvas: renders a <see cref="FxHeatMap"/> as a single
/// WriteableBitmap image (one pixel per cell) scaled with nearest-neighbor,
/// instead of a forest of Rectangle visual children — a 96×96 surface
/// becomes 9,216 pixels, not 9,216 objects. Colormaps come from
/// <see cref="FxCmap"/> (Turbo/Viridis/Inferno/Plasma/Magma/cool-hot);
/// diverging surfaces (anything spanning zero) render Turbo-style centered
/// at the neutral mid. A color legend strip and a hover crosshair with a
/// readout replace the old per-cell tooltips. Pure rendering — no data, no
/// timer, no I/O; the MapsViewModel feeds it via <see cref="Render"/>.
/// </summary>
public sealed class HeatMapControl : FrameworkElement
{
    private FxHeatMap? _map;
    private double[,]? _buyShare;

    /// <summary>Explicit colormap from the picker; null = Auto per map.</summary>
    private FxCmapKind? _colormap;

    private readonly Grid _grid = new();
    private readonly TextBlock _title = new()
    {
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6),
    };
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Image _image = new()
    {
        Stretch = Stretch.None,
        SnapsToDevicePixels = true,
    };
    private readonly Image _buyImage = new()
    {
        Stretch = Stretch.None,
        SnapsToDevicePixels = true,
    };
    private readonly System.Windows.Shapes.Rectangle _crosshair = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 2, 2 },
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly System.Windows.Shapes.Rectangle _legendBar = new()
    {
        Height = 8,
        Margin = new Thickness(0, 6, 0, 0),
    };
    private readonly TextBlock _legendMin = new() { FontSize = 10, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _legendMid = new()
    {
        FontSize = 10,
        Foreground = System.Windows.Media.Brushes.DimGray,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly TextBlock _legendMax = new()
    {
        FontSize = 10,
        Foreground = System.Windows.Media.Brushes.DimGray,
        HorizontalAlignment = HorizontalAlignment.Right,
    };
    private readonly DockPanel _legend = new();
    private readonly TextBlock _hover = new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
        Foreground = System.Windows.Media.Brushes.DimGray,
        Margin = new Thickness(0, 4, 0, 0),
    };

    public HeatMapControl()
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _canvas.Children.Add(_image);
        _canvas.Children.Add(_buyImage);
        _canvas.Children.Add(_crosshair);
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeave += (_, _) =>
        {
            _crosshair.Visibility = Visibility.Collapsed;
            _hover.Text = string.Empty;
        };
        _canvas.SizeChanged += (_, _) => LayoutImages();

        _legend.Children.Add(_legendMin);
        _legend.Children.Add(_legendMid);
        _legend.Children.Add(_legendMax);
        DockPanel.SetDock(_legendMin, Dock.Left);
        DockPanel.SetDock(_legendMid, Dock.Left);
        DockPanel.SetDock(_legendMax, Dock.Right);
        _legend.Children.Add(_legendBar);
        DockPanel.SetDock(_legendBar, Dock.Top);

        _grid.Children.Add(_title);
        _grid.Children.Add(_canvas);
        _grid.Children.Add(_legend);
        _grid.Children.Add(_hover);
        Grid.SetRow(_title, 0);
        Grid.SetRow(_canvas, 1);
        Grid.SetRow(_legend, 2);
        Grid.SetRow(_hover, 3);
        AddVisualChild(_grid);
    }

    /// <summary>Set the colormap (picker). Null = Auto (per-map defaults).</summary>
    public void SetColormap(FxCmapKind? kind)
    {
        _colormap = kind;
        Repaint();
    }

    /// <summary>Swap in a new map and repaint. Null clears the canvas.</summary>
    public void Render(FxHeatMap? map, double[,]? buyShare = null)
    {
        _map = map;
        _buyShare = buyShare;
        _title.Text = map is null ? "no map" : map.Name;
        _hover.Text = string.Empty;
        _crosshair.Visibility = Visibility.Collapsed;
        Repaint();
        InvalidateVisual();
    }

    private FxCmapKind EffectiveColormap =>
        _colormap ?? FxCmap.DefaultFor(_map?.Name ?? string.Empty);

    private static bool IsDiverging(FxHeatMap map) =>
        // Spanning zero = signed surface: Turbo centered at neutral reads
        // it honestly (blue short/negative, red long/positive).
        map.Min < 0 && map.Max > 0;

    private double Normalize(double value, FxHeatMap map)
    {
        if (IsDiverging(map))
        {
            var scale = Math.Max(Math.Abs(map.Min), Math.Abs(map.Max));
            return scale <= 1e-12 ? 0.5 : 0.5 + 0.5 * (value / scale);
        }

        var span = map.Max - map.Min;
        return span <= 1e-12 ? 0.0 : (value - map.Min) / span;
    }

    private void Repaint()
    {
        var kind = EffectiveColormap;

        if (_map is null)
        {
            _image.Source = null;
            _buyImage.Source = null;
            _legend.Visibility = Visibility.Collapsed;
            LayoutImages();
            return;
        }

        _legend.Visibility = Visibility.Visible;
        var map = _map;
        var cols = Math.Max(map.Columns, 1);
        var rows = Math.Max(map.Rows, 1);
        var diverging = IsDiverging(map);

        // Legend gradient from the same LUT the cells use.
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0),
        };
        for (var i = 0; i <= 8; i++)
        {
            var (r, g, b) = FxCmap.Sample(kind, i / 8.0);
            brush.GradientStops.Add(new GradientStop(
                Color.FromRgb(r, g, b), i / 8.0));
        }

        _legendBar.Fill = brush;
        var midValue = diverging ? 0 : (map.Min + map.Max) / 2;
        _legendMin.Text = $"{map.Min:0.####} {map.Unit}";
        _legendMid.Text = midValue.ToString("0.####", CultureInfo.InvariantCulture);
        _legendMax.Text = $"{map.Max:0.####} {map.Unit}";

        // One bitmap: BGRA, one pixel per cell. NaN buckets stay neutral
        // dark, outside the color scale, with an honest hover readout.
        var bmp = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[cols * rows * 4];
        var buyPixels = new byte[cols * rows * 4];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var o = (r * cols + c) * 4;
                var value = map.Cells[c, r];
                if (double.IsNaN(value))
                {
                    pixels[o] = 0x18;
                    pixels[o + 1] = 0x14;
                    pixels[o + 2] = 0x14;
                    pixels[o + 3] = 0xFF;
                    continue;
                }

                var (cr, cg, cb) = FxCmap.Sample(kind, Normalize(value, map));
                pixels[o] = cb;
                pixels[o + 1] = cg;
                pixels[o + 2] = cr;
                pixels[o + 3] = 0xFF;

                // Buy-share overlay: white veil where buyers dominate
                // (split volume surface), transparent elsewhere.
                if (_buyShare is not null)
                {
                    var buy = _buyShare[c, r];
                    if (double.IsFinite(buy) && buy > 0.5)
                    {
                        var alpha = (byte)(0xB0 * Math.Clamp((buy - 0.5) * 2, 0, 1));
                        buyPixels[o] = 0xFF;
                        buyPixels[o + 1] = 0xFF;
                        buyPixels[o + 2] = 0xFF;
                        buyPixels[o + 3] = alpha;
                    }
                    else
                    {
                        buyPixels[o + 3] = 0;
                    }
                }
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, cols, rows), pixels, cols * 4, 0);
        _image.Source = bmp;

        var hasBuy = _buyShare is not null;
        if (hasBuy)
        {
            var buyBmp = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
            buyBmp.WritePixels(new Int32Rect(0, 0, cols, rows), buyPixels, cols * 4, 0);
            _buyImage.Source = buyBmp;
        }
        else
        {
            _buyImage.Source = null;
        }

        _buyImage.Visibility = hasBuy ? Visibility.Visible : Visibility.Collapsed;
        LayoutImages();
    }

    /// <summary>Scale the images to the canvas with integer cell scaling
    /// (nearest-neighbor stays crisp; cells may be slightly rectangular —
    /// honest geometry beats letterboxing a data grid).</summary>
    private void LayoutImages()
    {
        if (_image.Source is not WriteableBitmap bmp)
        {
            return;
        }

        var availW = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : bmp.PixelWidth;
        var availH = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : bmp.PixelHeight;
        var sx = Math.Max(1, (int)(availW / bmp.PixelWidth));
        var sy = Math.Max(1, (int)(availH / bmp.PixelHeight));
        var w = bmp.PixelWidth * sx;
        var h = bmp.PixelHeight * sy;
        _image.Width = w;
        _image.Height = h;
        _buyImage.Width = w;
        _buyImage.Height = h;
        Canvas.SetLeft(_image, 0);
        Canvas.SetTop(_image, 0);
        Canvas.SetLeft(_buyImage, 0);
        Canvas.SetTop(_buyImage, 0);
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_map is null || _image.Source is not WriteableBitmap bmp)
        {
            return;
        }

        var pos = e.GetPosition(_canvas);
        var cw = _image.ActualWidth > 0 ? _image.ActualWidth / bmp.PixelWidth : 0;
        var ch = _image.ActualHeight > 0 ? _image.ActualHeight / bmp.PixelHeight : 0;
        if (cw <= 0 || ch <= 0)
        {
            return;
        }

        var c = (int)(pos.X / cw);
        var r = (int)(pos.Y / ch);
        if (c < 0 || r < 0 || c >= bmp.PixelWidth || r >= bmp.PixelHeight)
        {
            _crosshair.Visibility = Visibility.Collapsed;
            return;
        }

        var value = _map.Cells[c, r];
        _hover.Text = double.IsNaN(value)
            ? $"cell [{c},{r}] — no data"
            : $"cell [{c},{r}] = {value.ToString("0.####", CultureInfo.InvariantCulture)} {_map.Unit}"
              + (_buyShare is { } share && double.IsFinite(share[c, r])
                  ? $" · buy share {share[c, r]:P0}"
                  : string.Empty);
        if (double.IsNaN(value))
        {
            _crosshair.Visibility = Visibility.Collapsed;
            return;
        }

        _crosshair.Width = Math.Max(cw, 1);
        _crosshair.Height = Math.Max(ch, 1);
        Canvas.SetLeft(_crosshair, c * cw);
        Canvas.SetTop(_crosshair, r * ch);
        _crosshair.Visibility = Visibility.Visible;
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _grid;

    protected override Size MeasureOverride(Size availableSize)
    {
        _grid.Measure(availableSize);
        return _grid.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _grid.Arrange(new Rect(default, finalSize));
        return finalSize;
    }
}
