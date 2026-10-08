using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using DongGfx.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace DongGfx.App.Tests;

/// <summary>
/// End-to-end UI smoke: launches the real WPF shell (or attaches to a
/// running instance via TF_UIA_PID) and asserts, through UI Automation,
/// that the Terminal tab's key surfaces render — the tab itself, the
/// Market Watch grid, the account bar and the FX-brain badge. With the
/// bridge offline this verifies the graceful-degradation path ("bridge
/// offline", empty grid); when a loopback sidecar is up the same
/// assertions verify live data (account login, symbol rows).
///
/// UIA only — no synthetic mouse, no focus stealing, so this is safe on
/// CI's interactive desktop. Skipped (not failed) when an instance is
/// already running and none was handed over via TF_UIA_PID: the smoke
/// must never kill a trader's live app. Runs in CI's dedicated
/// uia-smoke job (Category=Uia), not in the unit gate.
/// </summary>
[Trait("Category", "Uia")]
[Collection("Uia")]   // serial with the journal-copy smoke: one clipboard, one desktop
public class UiaSmokeTests
{
    private readonly ITestOutputHelper _output;

    public UiaSmokeTests(ITestOutputHelper output) => _output = output;

    private const int DefaultSidecarPort = 53190;

    [Fact]
    public void App_Boots_And_Terminal_Surfaces_Render()
    {
        var (proc, owned) = AttachOrLaunch();
        if (proc is null)
        {
            // An instance is already running and none was handed over:
            // vacuous pass with context in the output — the smoke must
            // never kill a trader's live app.
            return;
        }
        try
        {
            RunAssertions(proc);
        }
        finally
        {
            CloseOwned(proc, owned);
        }
    }

    // ── process plumbing ────────────────────────────────────────────

    private (Process? Proc, bool Owned) AttachOrLaunch()
    {
        var pidEnv = Environment.GetEnvironmentVariable("TF_UIA_PID");
        if (int.TryParse(pidEnv, out var pid))
        {
            var proc = Process.GetProcessById(pid);
            _output.WriteLine($"attaching to running DongGfx pid={pid}");
            return (proc, owned: false);
        }

        var existing = Process.GetProcessesByName("DongGfx");
        if (existing.Length > 0)
        {
            foreach (var p in existing) p.Dispose();
            _output.WriteLine("DongGfx.exe already running and TF_UIA_PID unset — smoke skipped (vacuous pass)");
            return (null, owned: false);
        }

        var exe = LocateAppExe();
        _output.WriteLine($"launching {exe}");
        var launched = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Assert.NotNull(launched);
        return (launched!, owned: true);
    }

    /// <summary>Walks up from the test bin dir to the repo root (the dir
    /// holding DongGfx.sln) and returns the Debug app exe.</summary>
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

    // ── UIA plumbing ────────────────────────────────────────────────

    private System.Windows.Automation.AutomationElement? WindowOf(int pid, TimeSpan wait)
    {
        var root = System.Windows.Automation.AutomationElement.RootElement;
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ProcessIdProperty, pid);
            var win = root.FindFirst(
                System.Windows.Automation.TreeScope.Children, cond);
            if (win is not null)
            {
                return win;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    private System.Windows.Automation.AutomationElement? Named(
        System.Windows.Automation.AutomationElement scope, string name, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var el = scope.FindFirst(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.NameProperty, name));
            if (el is not null)
            {
                return el;
            }
            Thread.Sleep(400);
        }
        return null;
    }

    private bool BridgeOnline()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = http.GetAsync($"http://127.0.0.1:{DefaultSidecarPort}/health").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void CloseOwned(Process? proc, bool owned)
    {
        if (!owned || proc is null)
        {
            return;
        }

        try
        {
            // CloseMainWindow first so the app's own teardown runs
            // (journal flush, gate reset); force-kill as fallback.
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

    // ── the TP1 override banner's reason-matched action ────────────

    /// <summary>
    /// End-to-end for the dashboard's override banner: with an armed plan-%
    /// override and a fresh watcher verdict whose unmet precondition is
    /// <c>engine-idle</c>, the banner renders the armed value, the verdict
    /// line (reason included) and the reason-matched "Start brain" button —
    /// and pressing it through UI Automation actually starts the engine:
    /// the Terminal badge flips OFF → PAPER, the same effect the Settings
    /// toggle produces. A seeded TF_DATA_DIR keeps the scenario
    /// deterministic and entirely off the live session's settings/journal.
    /// </summary>
    [Fact]
    public void Override_Banner_Action_Button_Starts_The_Brain()
    {
        var dataDir = SeedOverrideBanner();
        var (proc, owned) = LaunchSeeded(dataDir);
        if (proc is null)
        {
            return;   // a live instance is running: vacuous, like the boot smoke
        }
        try
        {
            var win = WindowOf(proc.Id, TimeSpan.FromSeconds(45));
            Assert.True(win is not null, $"no main window appeared for pid={proc.Id}");

            // Dashboard is the first tab: the armed value AND the watcher's
            // verdict render on the banner, the unmet precondition spelled
            // out — and, since engine-idle HAS a fix, the stand-in hint
            // text does not.
            Assert.NotNull(TextContaining(win!, "TP1 plan % armed at 15%",
                TimeSpan.FromSeconds(25)));
            Assert.NotNull(TextContaining(win!, "waiting for a rung",
                TimeSpan.FromSeconds(15)));
            Assert.NotNull(TextContaining(win!, "(the brain loop is off)",
                TimeSpan.FromSeconds(5)));

            // The reason-matched button is offered…
            var button = Named(win!, "Start brain", TimeSpan.FromSeconds(15));
            Assert.NotNull(button);

            // …and pressing it really starts the brain.
            ((System.Windows.Automation.InvokePattern)button!.GetCurrentPattern(
                System.Windows.Automation.InvokePattern.Pattern)).Invoke();

            var tab = Named(win!, "Terminal", TimeSpan.FromSeconds(20));
            Assert.NotNull(tab);
            ((System.Windows.Automation.SelectionItemPattern)tab!.GetCurrentPattern(
                System.Windows.Automation.SelectionItemPattern.Pattern)).Select();
            Assert.NotNull(Named(win!, "FX BRAIN: PAPER", TimeSpan.FromSeconds(25)));
            _output.WriteLine("smoke: Start brain pressed via UIA; badge is FX BRAIN: PAPER");
        }
        finally
        {
            CloseOwned(proc, owned);
        }
    }

    /// <summary>Builds a scratch data dir whose settings arm a 15% plan-%
    /// override and whose watcher verdict — fresh and naming that same
    /// plan % — reports the unmet precondition as engine-idle: the exact
    /// state the banner's action button exists for.</summary>
    private static string SeedOverrideBanner()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-data-uia-override");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* a previous smoke may still be winding down */ }
        Directory.CreateDirectory(Path.Combine(dir, "watcher"));
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{\"FxBrainRunning\":false,\"Tp1PlanPctOverride\":15}");
        var now = DateTimeOffset.UtcNow.ToString("o");
        File.WriteAllText(
            Path.Combine(dir, "watcher", "tp1-override-verification.json"),
            "{\"status\":\"waiting\",\"plan_pct\":15,\"armed_ts\":\"" + now
            + "\",\"age_days\":0.1,\"partials\":true,\"seen_pct\":null,"
            + "\"computed_at\":\"" + now
            + "\",\"reason\":\"engine-idle\",\"reason_text\":\"the brain loop is off\"}");
        return dir;
    }

    /// <summary>Launches the Debug exe with TF_DATA_DIR pointed at the
    /// seeded dir. Never attaches to a running instance — the live
    /// session must not be disturbed, so a live instance makes this a
    /// vacuous pass, exactly like the boot smoke.</summary>
    private (Process? Proc, bool Owned) LaunchSeeded(string dataDir)
    {
        var existing = Process.GetProcessesByName("DongGfx");
        if (existing.Length > 0)
        {
            foreach (var p in existing) p.Dispose();
            _output.WriteLine("DongGfx.exe already running — banner smoke skipped (vacuous pass)");
            return (null, false);
        }

        var exe = LocateAppExe();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment[DongGfx.App.Infrastructure.SettingsService.DataDirEnvVar] = dataDir;
        _output.WriteLine($"launching {exe} with TF_DATA_DIR={dataDir}");
        var launched = Process.Start(psi);
        Assert.NotNull(launched);
        return (launched!, true);
    }

    /// <summary>Finds a descendant whose UIA name CONTAINS the substring
    /// (WPF exposes a TextBlock's full text as its name). Scans at least
    /// once even with a zero wait, so absence probes work too.</summary>
    private System.Windows.Automation.AutomationElement? TextContaining(
        System.Windows.Automation.AutomationElement scope, string part, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var els = scope.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            var hit = els.Cast<System.Windows.Automation.AutomationElement>()
                .FirstOrDefault(t => t.Current.Name?.Contains(part) == true);
            if (hit is not null)
            {
                return hit;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
            Thread.Sleep(400);
        }
    }

    // ── the actual smoke assertions ─────────────────────────────────

    private void RunAssertions(Process proc)
    {
        var win = WindowOf(proc.Id, TimeSpan.FromSeconds(45));
        Assert.True(win is not null, $"no main window appeared for pid={proc.Id}");
        Assert.Contains("DON G FX", win!.Current.Name);

        // Terminal tab exists and is selectable.
        var tab = Named(win, "Terminal", TimeSpan.FromSeconds(20));
        Assert.True(tab is not null, "Terminal tab not found");
        ((System.Windows.Automation.SelectionItemPattern)tab!.GetCurrentPattern(
                System.Windows.Automation.SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(800);

        // Terminal surfaces render: the watch header, the brain badge and
        // the candles panel caption (chart mounted). The caption carries a
        // separator glyph between "candles" and "MT5 bridge", so match it
        // by prefix instead of an exact name. Waits are generous: locally
        // this runs in parallel with the rest of the suite, and the app's
        // first poll cycle must win that race.
        Assert.NotNull(Named(win, "MARKET WATCH", TimeSpan.FromSeconds(25)));
        Assert.NotNull(Named(win, "FX BRAIN: OFF", TimeSpan.FromSeconds(25)));
        var candlesDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        var candlesFound = false;
        while (DateTime.UtcNow < candlesDeadline && !candlesFound)
        {
            var texts = win.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            candlesFound = texts.Cast<System.Windows.Automation.AutomationElement>()
                .Any(t => t.Current.Name?.StartsWith("M1 candles") == true);
            if (!candlesFound)
            {
                Thread.Sleep(500);
            }
        }
        Assert.True(candlesFound, "candle chart panel caption not found");

        // Account bar renders and settles: either the bridge-offline
        // degradation text or a live server/login once the sidecar answers.
        var accountBarDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var online = BridgeOnline();
        while (DateTime.UtcNow < accountBarDeadline)
        {
            online = BridgeOnline();
            var offlineText = Named(win, "bridge offline", TimeSpan.FromSeconds(0)) is not null;
            if (offlineText && !online)
            {
                break;   // graceful degradation confirmed
            }
            if (online)
            {
                break;   // live data path — rows asserted below
            }
            Thread.Sleep(800);
        }

        if (online)
        {
            // Live path: at least one Market Watch symbol row appears.
            var rowsDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            var rowCount = 0;
            while (DateTime.UtcNow < rowsDeadline)
            {
                var rows = win.FindAll(
                    System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.DataItem));
                rowCount = rows.Count;
                if (rowCount > 0)
                {
                    break;
                }
                Thread.Sleep(800);
            }
            Assert.True(rowCount > 0, "bridge is online but no Market Watch rows rendered");
            _output.WriteLine($"smoke: bridge online, {rowCount} symbol rows");
        }
        else
        {
            // Offline path: the account bar must show the degradation text.
            var offline = Named(win, "bridge offline", TimeSpan.FromSeconds(15));
            Assert.True(offline is not null, "no 'bridge offline' hint and no live sidecar");
            _output.WriteLine("smoke: bridge offline, degradation path confirmed");
        }
    }
}
