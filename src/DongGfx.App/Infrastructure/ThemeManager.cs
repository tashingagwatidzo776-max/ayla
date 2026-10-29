using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

        ApplyTitleBar(themeName);
    }

    // ── Title bar ────────────────────────────────────────────────
    // The DWM frame follows the OS, not the app: on a light Windows it
    // renders a WHITE title bar over the dark palette (observed in the
    // 2026-10-26 deploy screenshot). Ask DWM for the immersive mode that
    // matches the applied theme; attribute 20 is Win10 20H1+, 19 the
    // 1809 fallback, and an unsupported value fails harmlessly (the bar
    // just keeps the OS default).
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Aligns every open window's DWM title bar with the theme.
    /// Called again from a window's Loaded when its handle first exists —
    /// the startup window is not yet in Application.Current.Windows when
    /// Apply first runs at boot.</summary>
    public static void ApplyTitleBar(string themeName)
    {
        if (Application.Current is null)
        {
            return;
        }

        var dark = !string.Equals(themeName, Classic, StringComparison.OrdinalIgnoreCase);
        foreach (Window window in Application.Current.Windows)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            var on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref on, sizeof(int));
            }
        }
    }

    /// <summary>Maps any unknown/corrupt value to Dark.</summary>
    public static string Normalize(string? theme) =>
        string.Equals(theme, Classic, StringComparison.OrdinalIgnoreCase) ? Classic : Dark;
}
