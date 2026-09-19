using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DongGfx.Core.Tests;

/// <summary>
/// Architecture boundary test: DongGfx.Core is the headless core (brains, stores,
/// analytics, plumbing) and must never reference UI assemblies — the WPF
/// surface lives in DongGfx.App. Headless services extracted from view models
/// (PlanTextParser, GrowthPlanJsonStore's siblings, StrategyCatalog,
/// HealthSummaryBuilder) keep this boundary honest; this test fails the
/// build the moment DongGfx.Core pulls in a UI framework again.
/// </summary>
[Trait("Category", "Unit")]
public class ArchitectureTests
{
    private static readonly string[] BannedUiAssemblies =
    {
        // WPF
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Xaml",
        // WinForms / Windows-only UI helpers
        "System.Windows.Forms",
        "System.Windows.Extensions"
    };

    [Fact]
    public void Core_DoesNotReferenceUiAssemblies()
    {
        var referenced = ReadAssemblyReferences(typeof(DongGfx.Core.Brain.GrowthPlan).Assembly.Location);

        var violations = referenced
            .Intersect(BannedUiAssemblies, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "DongGfx.Core must not reference UI assemblies, but it references: " +
            string.Join(", ", violations) +
            ". Move the UI-dependent code into DongGfx.App (or extract the headless logic).");
    }

    [Fact]
    public void MetadataScan_IsLive_SeesCoreReferences()
    {
        // Guard for the guard: if the reader ever returns an empty set the
        // boundary test above would pass vacuously. DongGfx.Core always references
        // at least the base runtime assemblies.
        var referenced = ReadAssemblyReferences(typeof(DongGfx.Core.Brain.GrowthPlan).Assembly.Location);

        Assert.NotEmpty(referenced);
        Assert.Contains(referenced, r =>
            r.StartsWith("System", StringComparison.OrdinalIgnoreCase) || r == "netstandard");
    }

    /// <summary>Reads the assembly-reference table straight from the PE
    /// metadata — precise, cheap, and no assembly probing side effects.</summary>
    private static List<string> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var names = new List<string>();
        foreach (var handle in metadata.AssemblyReferences)
        {
            names.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        }

        return names;
    }
}
