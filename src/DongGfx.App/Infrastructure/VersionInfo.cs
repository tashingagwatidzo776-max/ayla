using System.Reflection;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// The binary's self-identification: InformationalVersion as stamped at
/// publish time (-p:InformationalVersion="vX.Y.Z+&lt;sha&gt;"), with the
/// csproj <c>Version</c> as the dev fallback. Read once, reused by the
/// window title and the About dialog.
/// </summary>
public static class VersionInfo
{
    /// <summary>The product's display name (rebrand, formerly "DongGfx").</summary>
    public const string ProductName = "DON G FX";

    /// <summary>The full branding title used on the window and About box.</summary>
    public const string FullTitle = "DON G FX — Deriv Binary-Options Trader";
    private static readonly string Full = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "dev";

    /// <summary>The raw informational version as stamped.</summary>
    public static string FullVersion => Full;

    /// <summary>"v0.0.1+a1b2c3d" (or the bare informational version when the
    /// stamp carries no commit part).</summary>
    public static string Stamp
    {
        get
        {
            var plus = Full.IndexOf('+');
            return plus < 0 ? Full : Full[..plus] + "+" + Full[(plus + 1)..][..Math.Min(7, Full.Length - plus - 1)];
        }
    }

    /// <summary>The window-title suffix: empty for a bare version, or
    /// " · vX.Y.Z+a1b2c3d" when stamped.</summary>
    public static string TitleSuffix => string.IsNullOrEmpty(Full) || Full == "dev"
        ? ""
        : $" · {Stamp}";
}
