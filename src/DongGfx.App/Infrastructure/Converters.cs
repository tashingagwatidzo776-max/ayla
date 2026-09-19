using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DongGfx.App.Infrastructure;

/// <summary>Converts null to Collapsed, non-null to Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Converts count 0 to Collapsed, >0 to Visible.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Converts bool to Collapsed/Visible (inverted).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Maps a WebhookUrlSeverity to a warning/error brush; null
/// (no issue) collapses the bound element.</summary>
public sealed class WebhookSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is WebhookUrlSeverity severity)
        {
            var key = severity switch
            {
                WebhookUrlSeverity.Error => "DangerBrush",
                WebhookUrlSeverity.Warning => "WarningBrush",
                _ => "TextSecondaryBrush",
            };
            return Application.Current.TryFindResource(key) ?? Brushes.Gray;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
