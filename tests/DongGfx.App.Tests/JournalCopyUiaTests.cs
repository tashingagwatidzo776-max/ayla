using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using DongGfx.App.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace DongGfx.App.Tests;

/// <summary>
/// End-to-end for the journal row copy flow: a seeded data dir puts two
/// APP_FAULT rows carrying unique probe tokens in the grid, the dashboard
/// fault notice navigates to the Journal filtered to APP_FAULT, and the two
/// user gestures are driven for real — a right-click → "Copy details" from the
/// row context menu, then a left-click on the second row followed by Ctrl+C.
/// Each gesture's clipboard is asserted against the seeded details, so the
/// test proves the copied text is the SELECTED row's, not a stale leftover.
///
/// Real mouse/keyboard input is required: UI Automation has no click
/// primitive, and the right-click handler hit-tests the actual mouse event's
/// source. Windows-only and interactive-desktop oriented, so it runs in CI's
/// dedicated uia-smoke job (Category=Uia), not the unit gate. Vacuous pass
/// when an instance is already running — the smoke must never kill a trader's
/// live app. Serial with the boot smoke via the "Uia" collection (one shared
/// clipboard).
///
/// RUNNER: the job must stay on GitHub's <c>windows-latest</c> (x64) image,
/// which has an interactive desktop session; the <c>windows-11-arm</c> image
/// is headless (actions/runner-images#14049) and cannot drive synthetic input
/// or activate a window. A preflight below fails fast with that reason if the
/// session ever stops being interactive, instead of surfacing as a menu
/// timeout.
/// </summary>
[Trait("Category", "Uia")]
[Collection("Uia")]
public class JournalCopyUiaTests
{
    private readonly ITestOutputHelper _output;

    public JournalCopyUiaTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Seeded_Journal_Row_Copies_By_RightClick_And_CtrlC()
    {
        var seed = SeedJournal();
        var (proc, owned) = LaunchSeeded(seed.DataDir);
        if (proc is null)
        {
            // A live instance is running and none was handed over: vacuous
            // pass with context — never disturb a trader's session.
            return;
        }

        // Fail fast and clearly on a headless session: the assertions below
        // depend on synthetic mouse/keyboard input and the real clipboard, so
        // without an interactive input desktop the only honest outcome is a
        // precise failure naming the runner constraint — never a mystery
        // "context menu did not open" 40 seconds later.
        Assert.True(InteractiveDesktopAvailable(),
            "no interactive input desktop: this smoke drives the app with real mouse/keyboard "
            + "input and the OLE clipboard, which need an interactive session. Run it on GitHub's "
            + "windows-latest image (interactive); the windows-11-arm image is headless "
            + "(actions/runner-images#14049). An RDP-disconnected or service session also fails here.");

        try
        {
            var win = WindowOf(proc.Id, TimeSpan.FromSeconds(45));
            Assert.True(win is not null, $"no main window appeared for pid={proc.Id}");

            // 1) Bring the seeded APP_FAULT rows up. Primary path is the
            // dashboard fault notice → "View faults in the journal" (which also
            // exercises ShowCategory selecting the newest fault row); fall back
            // to the Journal tab's Refresh button.
            var viewFaults = Named(win!, "View faults in the journal", TimeSpan.FromSeconds(25));
            if (viewFaults is not null)
            {
                ((InvokePattern)viewFaults.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                _output.WriteLine("journal: opened via the dashboard fault notice (APP_FAULT filter)");
            }
            else
            {
                var tab = Named(win!, "Journal", TimeSpan.FromSeconds(20));
                Assert.NotNull(tab);
                ((SelectionItemPattern)tab!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                var refresh = Named(win!, "Refresh", TimeSpan.FromSeconds(15));
                Assert.NotNull(refresh);
                ((InvokePattern)refresh!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                _output.WriteLine("journal: opened via the Journal tab Refresh button");
            }
            Thread.Sleep(800);

            // 2) The two seeded probe rows render, found by their unique tokens.
            var rowA = RowContaining(win!, seed.TokenA, TimeSpan.FromSeconds(25));
            var rowB = RowContaining(win!, seed.TokenB, TimeSpan.FromSeconds(15));
            Assert.True(rowA is not null, $"right-click probe row did not render (token {seed.TokenA})");
            Assert.True(rowB is not null, $"Ctrl+C probe row did not render (token {seed.TokenB})");

            // 3) Select row A with a REAL left click (user-like: focuses the
            // grid and shows the selection highlight).
            var pointA = RowPoint(rowA!);
            BringToForeground(proc);
            Click(pointA.X, pointA.Y, right: false);
            Thread.Sleep(300);

            // 4) Right-click row A → context menu → "Copy details" → clipboard.
            // A loaded CI runner can drop the first synthetic click before the
            // window is fully settled, so retry the right-click a couple of
            // times before declaring the menu unwired.
            AutomationElement? menuItem = null;
            for (var attempt = 1; attempt <= 3 && menuItem is null; attempt++)
            {
                BringToForeground(proc);
                Click(pointA.X, pointA.Y, right: true);
                menuItem = MenuItemNamed("Copy details", TimeSpan.FromSeconds(5));
                if (menuItem is null)
                {
                    _output.WriteLine($"retry: context menu did not open on attempt {attempt}");
                    Thread.Sleep(400);
                }
            }
            Assert.True(menuItem is not null,
                "the row context menu did not open (no 'Copy details' MenuItem found)");
            ((InvokePattern)menuItem!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            var copiedA = WaitForClipboard(seed.DetailsA, TimeSpan.FromSeconds(5));
            Assert.Equal(seed.DetailsA, copiedA);
            _output.WriteLine($"smoke: right-click copy OK ({copiedA.Length} chars)");

            // 5) Select row B with a REAL left click, then send Ctrl+C with a
            // real key event: proves the keyboard path copies the NEWLY
            // selected row, not the right-click leftover.
            var pointB = RowPoint(rowB!);
            Click(pointB.X, pointB.Y, right: false);
            BringToForeground(proc);
            Thread.Sleep(200);
            SendCtrlC();

            var copiedB = WaitForClipboard(seed.DetailsB, TimeSpan.FromSeconds(5));
            Assert.Equal(seed.DetailsB, copiedB);
            _output.WriteLine($"smoke: Ctrl+C copy OK ({copiedB.Length} chars)");
        }
        finally
        {
            CloseOwned(proc, owned);
            try { if (Directory.Exists(seed.DataDir)) Directory.Delete(seed.DataDir, recursive: true); }
            catch { /* the launched instance may still be flushing its journal */ }
        }
    }

    // ── seed ────────────────────────────────────────────────────────

    private sealed record JournalRow(DateTimeOffset Timestamp, string AccountId, string Category, string Details);

    private sealed record JournalSeed(string DataDir, string TokenA, string TokenB, string DetailsA, string DetailsB);

    /// <summary>Builds a scratch data dir with four journal rows: two
    /// APP_FAULT probe rows carrying unique tokens (one for the right-click
    /// copy, one for Ctrl+C) plus older context rows, and settings that keep
    /// the engine off. Mirrors the capture script's seed exactly.</summary>
    private static JournalSeed SeedJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-data-uia-journal-copy");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* a previous smoke may still be winding down */ }

        var journalDir = Path.Combine(dir, "journal");
        Directory.CreateDirectory(journalDir);

        const string account = "11111111-1111-1111-1111-111111111111";
        var tokenA = Guid.NewGuid().ToString("N");
        var tokenB = Guid.NewGuid().ToString("N");
        var detailsA = $"contained fault in fx-cycle: probe {tokenA}";
        var detailsB = $"contained fault in deal-feed: probe {tokenB}";

        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new JournalRow(now.AddMinutes(-9), account, "APP_FAULT", "contained fault in fx-supervisor: older context row"),
            new JournalRow(now.AddMinutes(-7), account, "FX_MODE", "paper soak restored — seeded context row"),
            new JournalRow(now.AddMinutes(-3), account, "APP_FAULT", detailsB),
            new JournalRow(now.AddMinutes(-1), account, "APP_FAULT", detailsA),
        };

        // UTF-8 WITHOUT a BOM: a BOM would corrupt the first JSONL line for the
        // app's per-line deserializer.
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var file = Path.Combine(journalDir, $"journal_{now:yyyyMMdd}.jsonl");
        // Each row on its own line, file TERMINATED by a newline: the app
        // appends its own entries to this same file, and without the trailing
        // newline its first append would concatenate onto our last line and
        // lose both rows to the per-line parser.
        File.WriteAllText(file,
            string.Join("\n", rows.Select(r => JsonSerializer.Serialize(r))) + "\n", utf8NoBom);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{\"FxBrainRunning\":false}", utf8NoBom);

        return new JournalSeed(dir, tokenA, tokenB, detailsA, detailsB);
    }

    // ── process plumbing (mirrors UiaSmokeTests) ────────────────────

    /// <summary>Walks up from the test bin dir to the repo root and returns
    /// the Debug app exe.</summary>
    private static string LocateAppExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DongGfx.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var exe = Path.Combine(
            dir!.FullName, "src", "DongGfx.App", "bin", "Debug", "net8.0-windows", "DongGfx.exe");
        Assert.True(File.Exists(exe), $"app exe not found: {exe} — build Debug first");
        return exe;
    }

    /// <summary>Launches the Debug exe with TF_DATA_DIR pointed at the seeded
    /// dir. Never attaches to a running instance — a live app makes this a
    /// vacuous pass, exactly like the boot smoke.</summary>
    private (Process? Proc, bool Owned) LaunchSeeded(string dataDir)
    {
        var existing = Process.GetProcessesByName("DongGfx");
        if (existing.Length > 0)
        {
            foreach (var p in existing) p.Dispose();
            _output.WriteLine("DongGfx.exe already running — journal-copy smoke skipped (vacuous pass)");
            return (null, false);
        }

        var exe = LocateAppExe();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment[SettingsService.DataDirEnvVar] = dataDir;
        _output.WriteLine($"launching {exe} with TF_DATA_DIR={dataDir}");
        var launched = Process.Start(psi);
        Assert.NotNull(launched);
        return (launched!, true);
    }

    private static void CloseOwned(Process? proc, bool owned)
    {
        if (!owned || proc is null)
        {
            return;
        }

        try
        {
            // CloseMainWindow first so the app's own teardown runs; force-kill
            // as fallback.
            proc.Refresh();
            if (!proc.HasExited && proc.CloseMainWindow())
            {
                proc.WaitForExit(10_000);
            }
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5_000);
            }
        }
        catch { /* best effort — the smoke already asserted */ }
    }

    // ── UIA plumbing ────────────────────────────────────────────────

    private static AutomationElement? WindowOf(int pid, TimeSpan wait)
    {
        var root = AutomationElement.RootElement;
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var win = root.FindFirst(TreeScope.Children, cond);
            if (win is not null)
            {
                return win;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    private static AutomationElement? Named(AutomationElement scope, string name, TimeSpan wait)
    {
        var cond = new PropertyCondition(AutomationElement.NameProperty, name);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var el = scope.FindFirst(TreeScope.Descendants, cond);
            if (el is not null)
            {
                return el;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
            Thread.Sleep(400);
        }
    }

    /// <summary>A journal DataGrid row whose own name or a descendant Text
    /// contains the probe token.</summary>
    private static AutomationElement? RowContaining(AutomationElement scope, string token, TimeSpan wait)
    {
        var rowCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem);
        var textCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var rows = scope.FindAll(TreeScope.Descendants, rowCond);
            foreach (AutomationElement row in rows)
            {
                if (row.Current.Name?.Contains(token) == true)
                {
                    return row;
                }
                var texts = row.FindAll(TreeScope.Descendants, textCond);
                foreach (AutomationElement t in texts)
                {
                    if (t.Current.Name?.Contains(token) == true)
                    {
                        return row;
                    }
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
            Thread.Sleep(400);
        }
    }

    /// <summary>The "Copy details" entry of the row context menu — a top-level
    /// popup, so it is searched from the desktop root and pinned to
    /// ControlType.MenuItem (the toolbar's own "Copy details" is a Button and
    /// must not match).</summary>
    private static AutomationElement? MenuItemNamed(string name, TimeSpan wait)
    {
        var root = AutomationElement.RootElement;
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var el = root.FindFirst(TreeScope.Descendants, cond);
            if (el is not null)
            {
                return el;
            }
            Thread.Sleep(300);
        }
        return null;
    }

    private static (int X, int Y) RowPoint(AutomationElement row)
    {
        var r = row.Current.BoundingRectangle;
        return ((int)Math.Round(r.X + 60), (int)Math.Round(r.Y + r.Height / 2));
    }

    private static void BringToForeground(Process proc)
    {
        try
        {
            proc.Refresh();
            var hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                NativeInput.SetForegroundWindow(hwnd);
            }
        }
        catch { /* focus is best-effort */ }
    }

    // ── real input ──────────────────────────────────────────────────

    private static void Click(int x, int y, bool right)
    {
        NativeInput.SetCursorPos(x, y);
        Thread.Sleep(250);
        if (right)
        {
            NativeInput.mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);   // RIGHTDOWN
            NativeInput.mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);   // RIGHTUP
        }
        else
        {
            NativeInput.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFT DOWN
            NativeInput.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFT UP
        }
        Thread.Sleep(350);
    }

    private static void SendCtrlC()
    {
        NativeInput.keybd_event(0x11, 0, 0x0000, UIntPtr.Zero);   // Ctrl down
        NativeInput.keybd_event(0x43, 0, 0x0000, UIntPtr.Zero);   // C down
        NativeInput.keybd_event(0x43, 0, 0x0002, UIntPtr.Zero);   // C up
        NativeInput.keybd_event(0x11, 0, 0x0002, UIntPtr.Zero);   // Ctrl up
    }

    private static class NativeInput
    {
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
    }

    /// <summary>True when this session has an interactive input desktop — the
    /// precondition for synthetic mouse/keyboard input and the OLE clipboard.
    /// GitHub's windows-latest image has one; the windows-11-arm image does
    /// not (actions/runner-images#14049), so this probe turns a headless
    /// runner into one clear, actionable failure instead of a menu timeout.</summary>
    private static bool InteractiveDesktopAvailable()
    {
        // DESKTOP_READOBJECTS — enough to open the input desktop without
        // asking for rights this process has no business holding.
        var desktop = NativeInput.OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }
        NativeInput.CloseDesktop(desktop);
        return true;
    }

    // ── clipboard (STA) ─────────────────────────────────────────────

    /// <summary>Polls the clipboard until it holds the expected text (the app
    /// writes it asynchronously on the Copy command) or the wait elapses, then
    /// returns the trimmed last read — so the caller's assert reports the real
    /// mismatch.</summary>
    private static string WaitForClipboard(string expected, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        var last = string.Empty;
        while (true)
        {
            last = ReadClipboardText();
            if (last == expected || DateTime.UtcNow >= deadline)
            {
                return last;
            }
            Thread.Sleep(150);
        }
    }

    /// <summary>Reads the clipboard text on a dedicated STA thread (WPF's
    /// Clipboard needs STA; the xUnit worker is MTA).</summary>
    private static string ReadClipboardText()
    {
        var text = string.Empty;
        var thread = new Thread(() =>
        {
            try { text = System.Windows.Clipboard.GetText() ?? string.Empty; }
            catch { text = string.Empty; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(3));
        return text.TrimEnd('\r', '\n');
    }
}
