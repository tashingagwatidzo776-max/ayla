using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Finds the MT5 terminal exe the bridge attaches to and keeps it alive for
/// DON G FX: the operator can pin a specific install (Settings → FX BRAIN →
/// MT5 terminal path); otherwise the known install locations are probed in
/// order. The resolved path is written to data/mt5-bridge.json so the
/// watchdog and sidecar target the SAME terminal — with two MT5 installs on
/// one machine, "whatever initialize() finds first" is not good enough for
/// an order-routing path.
/// </summary>
public static class Mt5TerminalLocator
{
    public static readonly string[] KnownInstallDirs =
    {
        @"C:\Program Files\MetaTrader 5 Terminal",
        @"C:\Program Files\MetaTrader 5",
        @"C:\Program Files (x86)\MetaTrader 5",
    };

    /// <summary>Config file the watchdog reads (terminal path + sidecar port).</summary>
    public static string ConfigPath => Path.Combine(SettingsService.DataDir, "mt5-bridge.json");

    /// <summary>The pinned path when it exists; otherwise the install of the
    /// currently-running terminal64 process; otherwise the first known install
    /// with a terminal64.exe; null when MT5 is not installed.
    ///
    /// The running terminal wins over the known-install probe because the
    /// operator commonly runs a portable install (e.g.
    /// Desktop\...\mt5_portable) while a Program Files install also exists on
    /// disk but is not signed in. Publishing the on-disk-but-idle path makes
    /// the sidecar attach to the wrong terminal, which fails loudly with
    /// "-6 Terminal: Authorization failed" and takes the whole bridge down.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var running = RunningTerminalPath();
        if (running is not null)
        {
            return running;
        }

        foreach (var dir in KnownInstallDirs)
        {
            var exe = Path.Combine(dir, "terminal64.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>Executable path of a live terminal64 process, or null when
    /// none is running (or its image cannot be read — a terminal owned by
    /// another user/elevation level). This is the terminal the sidecar can
    /// actually attach to, so it is preferred over a merely-present install.</summary>
    public static string? RunningTerminalPath()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("terminal64"))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // access denied for this particular process — try the next
                }
            }
        }
        catch
        {
            // no process API / enumeration failure — treat as not running
        }

        return null;
    }

    /// <summary>True when a terminal64 process is already running.</summary>
    public static bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName("terminal64").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Launch the resolved terminal when it is not running
    /// (fire-and-forget; the sidecar's attach retries cover startup time).</summary>
    public static void EnsureRunning(string? configured)
    {
        var exe = Find(configured);
        if (exe is null || IsRunning())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch
        {
            // launch is best-effort: the watchdog retries every 5 min
        }
    }

    /// <summary>Publish terminalPath + port for the watchdog/sidecar
    /// (configPath injectable for tests).</summary>
    public static void WriteConfig(string? terminalPath, int port = 53190, string? configPath = null)
    {
        var target = configPath ?? ConfigPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(target,
            JsonSerializer.Serialize(new { terminalPath, port }));
    }
}
