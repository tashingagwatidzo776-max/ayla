using System.Windows;
using DongGfx.Core.Models;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Runtime theme switcher. Both theme dictionaries expose the same resource
/// keys, so swapping the merged dictionary re-skins every window with no
/// XAML changes. Unknown theme names fall back to Dark rather than throwing
/// (a corrupt settings file must never brick the UI).
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Classic = "Classic";

    /// <summary>Applies the theme named in settings (fallback Dark) and
    /// returns the name actually applied.</summary>
    public static string Apply(AppSettings settings)
    {
        var name = Normalize(settings.Theme);
        Apply(name);
        return name;
    }

    /// <summary>Swaps the merged theme dictionary at runtime. Safe on the UI
    /// thread only (callers own the marshalling — settings saves already do).
    /// No-op outside a running WPF Application (unit tests construct the
    /// settings view model directly).</summary>
    public static void Apply(string themeName)
    {
        if (Application.Current is null)
        {
            return;
        }

        var uri = new Uri(
            themeName.Equals(Classic, StringComparison.OrdinalIgnoreCase)
                ? "Theme/Classic.xaml"
                : "Theme/Dark.xaml",
            UriKind.Relative);

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>Maps any unknown/corrupt value to Dark.</summary>
    public static string Normalize(string? theme) =>
        string.Equals(theme, Classic, StringComparison.OrdinalIgnoreCase) ? Classic : Dark;
}
