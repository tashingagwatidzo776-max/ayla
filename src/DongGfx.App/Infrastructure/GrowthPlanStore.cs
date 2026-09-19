using System.IO;
using System.Text.Json;
using DongGfx.Core.Brain;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Persists the Growth brain plan as JSON under %APPDATA%\tf\data\
/// growth-plan.json — no secrets here, so no encryption needed. The file
/// I/O lives here; (de)serialization and validation live in the headless
/// <see cref="GrowthPlanJsonStore"/> so they stay unit-testable.
/// </summary>
public sealed class GrowthPlanStore
{
    private static readonly string PlanPath =
        Path.Combine(SettingsService.DataDir, "growth-plan.json");

    public GrowthPlan Load()
    {
        try
        {
            if (!File.Exists(PlanPath))
            {
                return GrowthPlan.Default;
            }

            return GrowthPlanJsonStore.Load(File.ReadAllBytes(PlanPath));
        }
        catch
        {
            return GrowthPlan.Default;
        }
    }

    public void Save(GrowthPlan plan)
    {
        Directory.CreateDirectory(SettingsService.DataDir);
        File.WriteAllBytes(PlanPath, GrowthPlanJsonStore.Save(plan));
    }
}
