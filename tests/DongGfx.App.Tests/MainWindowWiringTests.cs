using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using System.Xml;
using System.Xml.Linq;
using DongGfx.App.ViewModels;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Desktop-free contract tests for the shell's data-bound view surfaces. The
/// end-to-end smokes (Category=Uia) prove the flows on an interactive desktop,
/// but they cannot run in the ordinary unit gate — so a rename that silently
/// unwires a command, a key binding or a visibility gate would otherwise slip
/// through until the uia-smoke job. These parse MainWindow.xaml (compiled into
/// the app's BAML) and assert both the bindings and that each named target is a
/// real member.
///
/// Structural XML parse, not a string scan: the relationships (which DataGrid,
/// which ContextMenu, which ItemsControl) are asserted as a tree, so reordering
/// attributes or reformatting the XAML does not break them.
/// </summary>
[Trait("Category", "Unit")]
public class MainWindowWiringTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ── journal grid copy surface ───────────────────────────────────

    private const string CopyCommand = "{Binding JournalVm.CopySelectedDetailsCommand}";
    private const string PlacementTargetBinding =
        "{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}";

    [Fact]
    public void Journal_Grid_Copy_Wiring_Is_Intact()
    {
        if (!TryLoadMainWindow(out var doc))
        {
            return;   // not running from a checkout (published artifacts) — skip
        }

        // Exactly one journal grid, identified by name — the tree must not have
        // drifted to a second grid or a renamed one.
        var grid = doc.Descendants(P + "DataGrid")
            .SingleOrDefault(g => (string?)g.Attribute(X + "Name") == "JournalEntriesGrid");
        Assert.NotNull(grid);

        // The grid replaces the DataGrid's own cell copy with the whole-row
        // "copy details" command, and its right-click selects the row under the
        // cursor first.
        Assert.Equal("None", (string?)grid!.Attribute("ClipboardCopyMode"));
        Assert.Equal("OnJournalRowRightClick", (string?)grid.Attribute("PreviewMouseRightButtonDown"));

        // Ctrl+C: a DataGrid key binding bound to the same command.
        var keyBinding = grid.Element(P + "DataGrid.InputBindings")
            ?.Elements(P + "KeyBinding").SingleOrDefault();
        Assert.NotNull(keyBinding);
        Assert.Equal("C", (string?)keyBinding!.Attribute("Key"));
        Assert.Equal("Control", (string?)keyBinding.Attribute("Modifiers"));
        Assert.Equal(CopyCommand, (string?)keyBinding.Attribute("Command"));

        // Right-click: the context menu's "Copy details" item, bound to the same
        // command. The placement-target DataContext is what lets the popup (which
        // lives outside the visual tree) resolve JournalVm — without it the item
        // would be inert, so it is part of the contract.
        var contextMenu = grid.Element(P + "DataGrid.ContextMenu")
            ?.Elements(P + "ContextMenu").SingleOrDefault();
        Assert.NotNull(contextMenu);
        Assert.Equal(PlacementTargetBinding, (string?)contextMenu!.Attribute("DataContext"));

        var copyItem = contextMenu.Elements(P + "MenuItem").SingleOrDefault();
        Assert.NotNull(copyItem);
        Assert.Equal("Copy details", (string?)copyItem!.Attribute("Header"));
        Assert.Equal(CopyCommand, (string?)copyItem.Attribute("Command"));

        // The XAML's named handler and both bindings must resolve to real
        // members: a rename in the code-behind or a dropped [RelayCommand]
        // generator would leave the markup wired to nothing.
        var handler = typeof(DongGfx.App.MainWindow).GetMethod(
            "OnJournalRowRightClick", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handler);
        var parameters = handler!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(object), parameters[0].ParameterType);
        Assert.Equal(typeof(System.Windows.Input.MouseButtonEventArgs), parameters[1].ParameterType);

        AssertCommand<JournalViewModel>("CopySelectedDetailsCommand");
    }

    // ── dashboard: contained-fault notice + paper-soak timeline ─────

    [Fact]
    public void Fault_Notice_And_Soak_Timeline_Wiring_Is_Intact()
    {
        if (!TryLoadMainWindow(out var doc))
        {
            return;
        }

        // Contained-fault notice: hidden until the dashboard reports a recent
        // fault, and its triage button opens the journal.
        var viewFaults = SingleButton(doc, "View faults in the journal");
        Assert.Equal("{Binding Dashboard.ViewFaultsCommand}", (string?)viewFaults.Attribute("Command"));

        var noticeBorder = viewFaults.Ancestors(P + "Border").First();
        AssertVisibilityGated(noticeBorder, "{Binding Dashboard.HasFaultNotice}");
        Assert.Contains(noticeBorder.Descendants(P + "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding Dashboard.FaultNoticeText}");

        // The resume button appears only in safe mode and reports its outcome
        // beside itself. It is the only way back from a held-back engine loop,
        // so its command and gate are part of the contract.
        var resume = SingleButton(doc, "Resume engine loop anyway");
        Assert.Equal("{Binding Dashboard.ResumeFromSafeModeCommand}", (string?)resume.Attribute("Command"));
        AssertVisibilityGated(resume.Parent!, "{Binding Dashboard.IsSafeMode}");
        Assert.Contains(resume.Parent!.Elements(P + "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding Dashboard.FaultStatus}");

        // Paper-soak timeline: an ItemsControl under the soak note, gated on
        // there being a note AND at least one restore/restart row.
        var timeline = doc.Descendants(P + "ItemsControl").SingleOrDefault(
            i => (string?)i.Attribute("ItemsSource") == "{Binding Dashboard.FxSoakTimeline}");
        Assert.NotNull(timeline);
        AssertVisibilityGated(timeline!, "{Binding Dashboard.HasFxSoakTimeline}");

        var soakBorder = timeline!.Ancestors(P + "Border").First();
        AssertVisibilityGated(soakBorder, "{Binding Dashboard.HasFxSoakNote}");
        Assert.Contains(soakBorder.Descendants(P + "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding Dashboard.FxSoakNote}");

        // The row template renders the row view's two fields.
        var rowTemplate = timeline.Element(P + "ItemsControl.ItemTemplate")
            ?.Element(P + "DataTemplate");
        Assert.NotNull(rowTemplate);
        Assert.Contains(rowTemplate!.Descendants(P + "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding When}");
        Assert.Contains(rowTemplate.Descendants(P + "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding Text}");

        // Every binding above names a real member.
        var vm = typeof(DashboardViewModel);
        foreach (var property in new[]
        {
            "ViewFaultsCommand", "ResumeFromSafeModeCommand",
            "FaultNoticeText", "HasFaultNotice", "IsSafeMode", "FaultStatus",
            "FxSoakTimeline", "HasFxSoakTimeline", "FxSoakNote", "HasFxSoakNote",
        })
        {
            Assert.True(vm.GetProperty(property) is not null, $"DashboardViewModel.{property} missing");
        }
        AssertCommand<DashboardViewModel>("ViewFaultsCommand");
        AssertCommand<DashboardViewModel>("ResumeFromSafeModeCommand");

        var rowType = vm.GetNestedType("SoakTimelineRowView");
        Assert.True(rowType is not null, "DashboardViewModel.SoakTimelineRowView missing");
        Assert.NotNull(rowType!.GetProperty("When"));
        Assert.NotNull(rowType.GetProperty("Text"));
    }

    // ── whole-shell binding guard ───────────────────────────────────

    /// <summary>
    /// Every Command and DataTrigger binding in every XAML surface the app
    /// ships (the shell, the login dialog and the theme dictionaries) must
    /// name a member that exists on the view model in scope. These are the
    /// bindings that make a control DO something or gate a surface: an
    /// unwired one is inert rather than merely blank, which is the failure a
    /// rename leaves behind. A DataTemplate's rows are typed from the enclosing
    /// ItemsControl's ItemsSource, and a {RelativeSource AncestorType=Window}
    /// binding resolves against the surface's own view model — so template
    /// members are checked too. Only a row type or DataContext the walk cannot
    /// infer is skipped, so it has no false positives: it reports only a
    /// binding that provably resolves to nothing.
    /// </summary>
    [Fact]
    public void Every_Command_And_DataTrigger_Binding_Resolves()
    {
        if (!TryFindRepoRoot(out var root))
        {
            return;   // not running from a checkout (published artifacts) — skip
        }

        // Every XAML file the app ships must be accounted for: a new view or
        // dictionary whose DataContext this guard has never been taught would
        // otherwise drop out of coverage silently. The discovery also keeps the
        // map honest when a file is renamed or removed.
        var discovered = Directory
            .EnumerateFiles(Path.Combine(root, "src", "DongGfx.App"),
                "*.xaml", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/bin/", StringComparison.Ordinal)
                     && !p.Contains("/obj/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            XamlSurfaces.Select(s => s.Path).OrderBy(p => p, StringComparer.Ordinal),
            discovered);

        var failures = new List<string>();
        var resolved = 0;
        var skipped = 0;

        foreach (var surface in XamlSurfaces)
        {
            var doc = XDocument.Load(Path.Combine(root, surface.Path), LoadOptions.SetLineInfo);
            var (surfaceFailures, surfaceResolved, surfaceSkipped) =
                ResolveBindings(doc, surface.DefaultScope);
            failures.AddRange(surfaceFailures.Select(f => $"{surface.Path}: {f}"));
            resolved += surfaceResolved;
            skipped += surfaceSkipped;
        }

        // Make the coverage visible to CI before asserting: the count goes to
        // the file named by XAML_BINDING_GUARD_REPORT (the unit job folds it
        // into the run summary), so a creeping drop shows up run over run
        // instead of only when the floor below finally trips.
        ReportCoverage(resolved, skipped);

        Assert.True(failures.Count == 0,
            $"{failures.Count} binding(s) resolve to nothing on the bound view model:\n  "
            + string.Join("\n  ", failures));

        // Coverage floor: if the walker silently stops finding bindings (a
        // namespace change, a new markup form) this fails instead of the guard
        // passing vacuously over nothing.
        Assert.True(resolved >= 40,
            $"resolved only {resolved} bindings and skipped {skipped} — the guard has lost its coverage");
    }

    /// <summary>
    /// The guard's own mutation test: it proves the walker actually REPORTS a
    /// binding that names nothing, rather than passing vacuously. It parses a
    /// small shell slice — not the real MainWindow.xaml, so it stays valid as
    /// the app grows — whose two bindings (a Command and a DataTrigger
    /// condition) both resolve, then points them at members that do not exist
    /// and requires both failures. Without this, a future regression that
    /// silently stops walking bindings would leave the whole guard green.
    /// </summary>
    [Fact]
    public void Binding_Guard_Reports_A_Mutated_Binding()
    {
        const string fixture = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Button Content="Save settings" Command="{Binding SettingsVm.SaveCommand}" />
              <Border>
                <Border.Style>
                  <Style TargetType="Border">
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding Dashboard.HasFaultNotice}" Value="True">
                        <Setter Property="Visibility" Value="Visible" />
                      </DataTrigger>
                    </Style.Triggers>
                  </Style>
                </Border.Style>
              </Border>
            </Window>
            """;

        // Baseline: every binding names a real member, so nothing is reported.
        var clean = ResolveBindings(XDocument.Parse(fixture), typeof(MainViewModel));
        Assert.Empty(clean.Failures);
        Assert.Equal(2, clean.Resolved);

        // Break both — the Command and the DataTrigger — and require the guard
        // to name each of them (and their kind) rather than stay silent.
        var mutated = XDocument.Parse(fixture);
        foreach (var attribute in mutated.Descendants().Attributes())
        {
            attribute.Value = attribute.Value switch
            {
                "{Binding SettingsVm.SaveCommand}" => "{Binding SettingsVm.SaveCommandz}",
                "{Binding Dashboard.HasFaultNotice}" => "{Binding Dashboard.HasFaultNoticez}",
                _ => attribute.Value,
            };
        }

        var broken = ResolveBindings(mutated, typeof(MainViewModel));
        Assert.Equal(2, broken.Failures.Count);
        Assert.Contains(broken.Failures,
            f => f.Contains("Command", StringComparison.Ordinal)
              && f.Contains("SaveCommandz", StringComparison.Ordinal));
        Assert.Contains(broken.Failures,
            f => f.Contains("DataTrigger", StringComparison.Ordinal)
              && f.Contains("HasFaultNoticez", StringComparison.Ordinal));
    }

    /// <summary>A XAML surface the guard walks, with the view model its
    /// unqualified bindings resolve against (the window's own DataContext).
    /// Untyped resources (theme dictionaries) carry only TemplateBindings,
    /// which the walk ignores, so their scope is never consulted.</summary>
    private sealed record XamlSurface(string Path, Type DefaultScope);

    private static readonly XamlSurface[] XamlSurfaces =
    {
        new("src/DongGfx.App/MainWindow.xaml", typeof(MainViewModel)),
        new("src/DongGfx.App/LoginDialog.xaml", typeof(LoginViewModel)),
        new("src/DongGfx.App/App.xaml", typeof(MainViewModel)),
        new("src/DongGfx.App/Theme/Classic.xaml", typeof(MainViewModel)),
        new("src/DongGfx.App/Theme/Dark.xaml", typeof(MainViewModel)),
    };

    // ── helpers ─────────────────────────────────────────────────────

    /// <summary>Writes the guard's coverage to the file named by
    /// XAML_BINDING_GUARD_REPORT when the environment asks for it (CI folds
    /// the file into the run summary); a normal local run sets no variable and
    /// writes nothing. Best effort: reporting never fails the guard.</summary>
    private static void ReportCoverage(int resolved, int skipped)
    {
        var path = Environment.GetEnvironmentVariable("XAML_BINDING_GUARD_REPORT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path,
                $"resolved={resolved}\nskipped={skipped}\nsurfaces={XamlSurfaces.Length}\n");
        }
        catch
        {
            // best effort: never fail the guard on reporting
        }
    }

    /// <summary>Walks one parsed surface, resolving every Command and
    /// DataTrigger binding against <paramref name="defaultScope"/> (or the
    /// nearest DataContext override the walker recognises). Returns the
    /// human-readable failures plus how many bindings resolved or were
    /// deliberately skipped.</summary>
    private static (List<string> Failures, int Resolved, int Skipped) ResolveBindings(
        XDocument doc, Type defaultScope)
    {
        var failures = new List<string>();
        var resolved = 0;
        var skipped = 0;

        // Recursive so a DataTemplate can switch the scope to its ROW type:
        // the enclosing ItemsControl's ItemsSource property type says what each
        // row is, which is what the template's bindings actually resolve
        // against.
        Walk(doc.Root!, defaultScope, defaultScope, failures, ref resolved, ref skipped);
        return (failures, resolved, skipped);
    }

    /// <summary>Walks one element and its subtree. The scope is the view model
    /// the subtree binds against: the surface default, the nearest DataContext
    /// override we recognise, or — inside a template — the inferred row type.
    /// An unmodelled override or untypable row makes it null, and bindings
    /// under a null scope are skipped rather than guessed at.</summary>
    private static void Walk(
        XElement element, Type? scope, Type defaultScope,
        List<string> failures, ref int resolved, ref int skipped)
    {
        var name = element.Name.LocalName;
        if (name is "DataTemplate" or "DataGridTemplateColumn")
        {
            scope = ItemTypeOf(element, scope);
        }
        else if (element.Attribute("DataContext") is { } dataContext)
        {
            // A placement-target override is transparent — the popup shares
            // its target's context — so it leaves the scope alone.
            var value = dataContext.Value;
            if (!value.Contains("PlacementTarget.DataContext", StringComparison.Ordinal))
            {
                scope = value switch
                {
                    "{Binding TerminalVm}" => typeof(TerminalViewModel),
                    "{Binding MapsVm}" => typeof(MapsViewModel),
                    _ => null,
                };
            }
        }

        foreach (var (kind, path, windowRelative) in BindingsOf(element))
        {
            if (path is null)
            {
                skipped++;
                continue;
            }

            // A {RelativeSource AncestorType=Window} binding resolves against
            // the surface's own DataContext wherever it sits; its leading
            // 'DataContext.' names that context, not a member, so drop it.
            var effective = windowRelative ? defaultScope : scope;
            if (effective is null)
            {
                skipped++;
                continue;
            }

            var memberPath = windowRelative && path.StartsWith("DataContext.", StringComparison.Ordinal)
                ? path["DataContext.".Length..]
                : path;

            if (TryResolve(effective, memberPath, out var missing))
            {
                resolved++;
            }
            else
            {
                failures.Add($"{kind} on <{element.Name.LocalName}> (line {Line(element)}): "
                    + $"{{Binding {path}}} — {missing}");
            }
        }

        foreach (var child in element.Elements())
        {
            Walk(child, scope, defaultScope, failures, ref resolved, ref skipped);
        }
    }

    /// <summary>The Command binding and, for a DataTrigger, the condition
    /// binding — the two surfaces that decide whether a control acts. The
    /// third field marks a binding that resolves against the Window's own
    /// DataContext, not the surrounding scope.</summary>
    private static IEnumerable<(string Kind, string? Path, bool WindowRelative)> BindingsOf(
        XElement element)
    {
        if (element.Attribute("Command") is { } command)
        {
            yield return ("Command", BindingPath(command.Value), IsWindowRelative(command.Value));
        }

        if (element.Name == P + "DataTrigger" && element.Attribute("Binding") is { } binding)
        {
            yield return ("DataTrigger", BindingPath(binding.Value),
                IsWindowRelative(binding.Value));
        }
    }

    /// <summary>True when the markup resolves against the Window's DataContext
    /// (its own view model) regardless of where in the tree it sits.</summary>
    private static bool IsWindowRelative(string markup) =>
        markup.Contains("AncestorType=Window", StringComparison.Ordinal);

    /// <summary>The simple property path inside a <c>{Binding ...}</c> markup
    /// extension, or null when it is not a plain binding (no path, nested
    /// markup, non-binding command value).</summary>
    private static string? BindingPath(string markup)
    {
        var value = markup.Trim();
        if (!value.StartsWith("{Binding", StringComparison.Ordinal))
        {
            return null;
        }

        var inner = value.Substring("{Binding".Length).TrimStart();
        if (inner.StartsWith("}", StringComparison.Ordinal))
        {
            return null;   // "{Binding}" — the whole DataContext
        }

        var end = inner.IndexOf('}');
        if (end >= 0)
        {
            inner = inner[..end];
        }
        var comma = inner.IndexOf(',');
        if (comma >= 0)
        {
            inner = inner[..comma];
        }

        inner = inner.Trim();
        if (inner.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
        {
            inner = inner["Path=".Length..].Trim();
        }

        return inner.Length == 0 || inner.Contains('{') ? null : inner;
    }

    /// <summary>The element type of the nearest enclosing ItemsControl's
    /// ItemsSource, or null when it cannot be inferred — then the template's
    /// bindings stay skipped, exactly as before.</summary>
    private static Type? ItemTypeOf(XElement template, Type? outerScope)
    {
        if (outerScope is null)
        {
            return null;
        }

        // Ancestors() yields nearest-first, so this is the template's own
        // ItemsControl (or DataGrid), not an outer one.
        var itemsSource = template.Ancestors()
            .Select(a => a.Attribute("ItemsSource"))
            .FirstOrDefault(a => a is not null);
        if (itemsSource is null)
        {
            return null;
        }

        var path = BindingPath(itemsSource.Value);
        if (path is null || !TryResolveType(outerScope, path, out var collection, out _))
        {
            return null;
        }

        return ElementType(collection);
    }

    /// <summary>The T of IEnumerable&lt;T&gt; — directly, as an array, or via
    /// an implemented interface — or null when the collection is not generic.</summary>
    private static Type? ElementType(Type collection)
    {
        if (collection.IsArray)
        {
            return collection.GetElementType();
        }

        var enumerable = collection.GetInterfaces()
            .Prepend(collection)
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    /// <summary>Walks a dotted path through public instance properties,
    /// reporting the first segment that does not exist.</summary>
    private static bool TryResolve(Type root, string path, out string missing) =>
        TryResolveType(root, path, out _, out missing);

    /// <summary>The same walk, also returning the final property type (used to
    /// type an ItemsControl's rows).</summary>
    private static bool TryResolveType(Type root, string path, out Type type, out string missing)
    {
        type = root;
        foreach (var raw in path.Split('.'))
        {
            var segment = raw;
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                segment = segment[..bracket];
            }

            var property = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                missing = $"{type.Name} has no member '{segment}'";
                return false;
            }
            type = property.PropertyType;
        }

        missing = "";
        return true;
    }

    private static int Line(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    /// <summary>The single button carrying this exact Content — Single enforces
    /// uniqueness, so a duplicated control fails the contract.</summary>
    private static XElement SingleButton(XDocument doc, string content)
    {
        var button = doc.Descendants(P + "Button")
            .SingleOrDefault(b => (string?)b.Attribute("Content") == content);
        Assert.True(button is not null, $"no Button with Content=\"{content}\"");
        return button!;
    }

    /// <summary>Asserts that <paramref name="owner"/> (or a descendant style)
    /// hides itself by default and reveals on a True DataTrigger for
    /// <paramref name="binding"/>.</summary>
    private static void AssertVisibilityGated(XElement owner, string binding)
    {
        var trigger = owner.Descendants(P + "DataTrigger").FirstOrDefault(
            t => (string?)t.Attribute("Binding") == binding
              && (string?)t.Attribute("Value") == "True");
        Assert.True(trigger is not null, $"no DataTrigger on {binding}");
        Assert.Contains(trigger!.Elements(P + "Setter"),
            s => (string?)s.Attribute("Property") == "Visibility"
              && (string?)s.Attribute("Value") == "Visible");
    }

    private static void AssertCommand<TViewModel>(string property)
    {
        var prop = typeof(TViewModel).GetProperty(property);
        Assert.True(prop is not null, $"{typeof(TViewModel).Name}.{property} missing");
        Assert.True(typeof(ICommand).IsAssignableFrom(prop!.PropertyType),
            $"{typeof(TViewModel).Name}.{property} must be an ICommand");
    }

    /// <summary>The parsed MainWindow markup from the source checkout; false
    /// when the tests run outside a repo (pack URIs don't resolve in the test
    /// host, so this mirrors the theme-dictionary contract test's source read).</summary>
    private static bool TryLoadMainWindow(out XDocument doc)
    {
        doc = null!;
        if (!TryFindRepoRoot(out var root))
        {
            return false;
        }

        var xaml = Path.Combine(root, "src", "DongGfx.App", "MainWindow.xaml");
        if (!File.Exists(xaml))
        {
            return false;
        }

        doc = XDocument.Load(xaml, LoadOptions.SetLineInfo);
        return true;
    }

    /// <summary>Walks up from the test host to the checkout that contains
    /// DongGfx.sln — false outside a repo, where there is no XAML source to
    /// read.</summary>
    private static bool TryFindRepoRoot(out string root)
    {
        root = "";
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            if (File.Exists(Path.Combine(probe, "DongGfx.sln")))
            {
                root = probe;
                return true;
            }

            probe = Path.GetDirectoryName(probe);
        }

        return false;
    }
}
