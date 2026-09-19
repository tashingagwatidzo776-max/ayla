using System.Windows;
using System.Windows.Controls;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Minimal two-way binding support for <see cref="PasswordBox"/>
/// (WPF deliberately does not allow binding the SecureString).
/// Usage: local:PasswordBoxHelper.BoundPassword="{Binding ApiToken, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxHelper),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        box.PasswordChanged -= PasswordChanged;
        if (!Equals(box.Password, e.NewValue))
        {
            box.Password = e.NewValue as string ?? string.Empty;
        }

        box.PasswordChanged += PasswordChanged;
    }

    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            SetBoundPassword(box, box.Password);
        }
    }
}