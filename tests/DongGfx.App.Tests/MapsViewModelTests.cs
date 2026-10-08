using System.Collections.Generic;
using DongGfx.App.ViewModels;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Desktop-free tests for the Maps tab's colormap picker — a member the shell
/// exposes through a ComboBox that had no test. The code-behind feeds every
/// offered name to FxCmap.TryParse when the selection changes, so a choice the
/// parser does not know would silently render the default colormap instead of
/// failing.
/// </summary>
[Trait("Category", "Unit")]
public class MapsViewModelTests
{
    [Fact]
    public void Colormap_Picker_Offers_Auto_And_Only_Parsable_Names()
    {
        var vm = new MapsViewModel();
        Assert.Equal("Auto", vm.SelectedColormap);
        Assert.Equal("Auto", MapsViewModel.ColormapChoices[0]);

        // "Auto" is the per-map default (the code-behind maps it to null);
        // every other choice must parse, or the canvas ignores the pick.
        foreach (var choice in MapsViewModel.ColormapChoices)
        {
            if (choice == "Auto")
            {
                continue;
            }

            Assert.True(FxCmap.TryParse(choice, out _),
                $"colormap choice '{choice}' does not parse");
        }
    }

    [Fact]
    public void SelectedColormap_Change_Notifies()
    {
        var vm = new MapsViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedColormap = "Viridis";

        Assert.Equal("Viridis", vm.SelectedColormap);
        Assert.Contains(nameof(MapsViewModel.SelectedColormap), changed);
    }
}
