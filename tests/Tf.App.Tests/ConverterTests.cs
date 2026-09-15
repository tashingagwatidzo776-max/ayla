using System.Globalization;
using System.Windows;
using Tf.App.Infrastructure;

namespace Tf.App.Tests;

/// <summary>
/// Unit tests for the WPF value converters' Convert logic (all one-way;
/// ConvertBack is not supported by design and must throw).
/// </summary>
[Trait("Category", "Unit")]
public class ConverterTests
{
    [Fact]
    public void NullToVisibility_Null_IsCollapsed()
    {
        var converter = new NullToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed,
            converter.Convert((object?)null, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullToVisibility_NonNull_IsVisible()
    {
        var converter = new NullToVisibilityConverter();
        Assert.Equal(Visibility.Visible,
            converter.Convert("", typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible,
            converter.Convert(0, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible,
            converter.Convert(false, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullToVisibility_ConvertBack_Throws()
    {
        var converter = new NullToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Visibility.Visible, typeof(object), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CountToVisibility_Positive_IsVisible()
    {
        var converter = new CountToVisibilityConverter();
        Assert.Equal(Visibility.Visible,
            converter.Convert(1, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible,
            converter.Convert(42, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CountToVisibility_Zero_IsCollapsed()
    {
        var converter = new CountToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed,
            converter.Convert(0, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CountToVisibility_NonInt_IsCollapsed()
    {
        var converter = new CountToVisibilityConverter();
        // A wrong-typed value must collapse, not throw — the XAML binding
        // may hand over anything during a transient state.
        Assert.Equal(Visibility.Collapsed,
            converter.Convert((object?)null, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed,
            converter.Convert("3", typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CountToVisibility_ConvertBack_Throws()
    {
        var converter = new CountToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Visibility.Collapsed, typeof(int), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseBoolToVisibility_True_IsCollapsed()
    {
        var converter = new InverseBoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed,
            converter.Convert(true, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseBoolToVisibility_False_IsVisible()
    {
        var converter = new InverseBoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible,
            converter.Convert(false, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseBoolToVisibility_NonBool_IsVisible()
    {
        var converter = new InverseBoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible,
            converter.Convert((object?)null, typeof(Visibility), (object?)null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseBoolToVisibility_ConvertBack_Throws()
    {
        var converter = new InverseBoolToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Visibility.Visible, typeof(bool), (object?)null, CultureInfo.InvariantCulture));
    }
}
