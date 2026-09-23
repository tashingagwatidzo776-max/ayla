using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Sends Windows 10/11 toast notifications for important trading events:
/// trade settlements, growth targets, circuit breaker trips, and errors.
/// Falls back to taskbar flash on older Windows versions.
/// </summary>
public class NotificationService : IDisposable
{
    private readonly string _toastExePath;
    private bool _disposed;

    public NotificationService()
    {
        // Path to the PowerShell script that sends toasts (created on first use).
        var scriptDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tf", "notifications");
        Directory.CreateDirectory(scriptDir);
        _toastExePath = Path.Combine(scriptDir, "toast.ps1");
        EnsureScriptExists();
    }

    /// <summary>Whether notifications are enabled (can be toggled by user).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum interval between notifications to avoid spam.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(10);

    private DateTimeOffset _lastNotification = DateTimeOffset.MinValue;

    /// <summary>Notify on a trade settlement (win or loss).</summary>
    public void NotifyTradeSettled(string accountName, bool won, decimal profit, string symbol)
    {
        if (!CanNotify()) return;

        var icon = won ? "✅" : "❌";
        var title = $"{icon} Trade Settled — {accountName}";
        var body = $"{symbol} → {(won ? "WIN" : "LOSS")} | P&L: {profit:+0.##;-0.##;0}";

        SendToast(title, body, won ? "success" : "warning");
    }

    /// <summary>Notify when a growth engine hits its daily target.</summary>
    public void NotifyTargetHit(string accountName, decimal bankroll)
    {
        if (!CanNotify()) return;

        SendToast(
            "🎯 Target Reached!",
            $"{accountName} hit daily target — bankroll ${bankroll:0.##}",
            "success");
    }

    /// <summary>Notify when a growth engine hits its floor.</summary>
    public void NotifyFloorHit(string accountName, decimal bankroll)
    {
        if (!CanNotify()) return;

        SendToast(
            "🛑 Floor Hit",
            $"{accountName} hit stop-loss floor — bankroll ${bankroll:0.##}",
            "error");
    }

    /// <summary>Notify when a circuit breaker trips.</summary>
    public void NotifyCircuitBreakerTripped(string accountName, int failures)
    {
        if (!CanNotify()) return;

        SendToast(
            "⚠ Circuit Breaker Tripped",
            $"{accountName}: {failures} consecutive failures — pausing reconnection",
            "warning");
    }

    /// <summary>Notify on a connection error.</summary>
    public void NotifyConnectionError(string accountName, string error)
    {
        if (!CanNotify()) return;

        SendToast(
            "🔌 Connection Lost",
            $"{accountName}: {error}",
            "error");
    }

    /// <summary>Notify when a dashboard risk rail (governor, kill switch,
    /// circuit breaker, exhausted restarts) newly engages.</summary>
    public void NotifyRiskRailEngaged(string change, string summary)
    {
        if (!CanNotify()) return;

        SendToast(
            "⚠ Risk rail engaged",
            $"{change} — {summary}",
            "warning");
    }

    /// <summary>Notify when an update is available.</summary>
    public void NotifyUpdateAvailable(string version)
    {
        if (!CanNotify()) return;

        SendToast(
            "📦 Update Available",
            $"Version {version} is ready to install",
            "info");
    }

    private bool CanNotify()
    {
        if (_disposed) return false;
        if (!Enabled) return false;
        if (DateTimeOffset.UtcNow - _lastNotification < MinInterval) return false;
        _lastNotification = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>Virtual so tests can capture toasts without spawning
    /// PowerShell; production sends via a background powershell.exe.</summary>
    internal virtual void SendToast(string title, string body, string severity)
    {
        try
        {
            // Escape for PowerShell string embedding.
            var escTitle = title.Replace("'", "''");
            var escBody = body.Replace("'", "''");

            var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null

$template = @''
<toast launch=""action=view"" duration=""short"">
    <visual>
        <binding template=""ToastGeneric"">
            <text>{escTitle}</text>
            <text>{escBody}</text>
        </binding>
    </visual>
    <audio src=""ms-winsoundevent:Notification.Looping.Simple""/>
</toast>
''@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('DongGfx Trader').Show($toast)";

            // Run PowerShell in background to avoid blocking UI.
            _ = Task.Run(() =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit(5000);
                }
                catch
                {
                    // Toast failed — fall back to taskbar flash.
                    FlashTaskbar();
                }
            });
        }
        catch
        {
            FlashTaskbar();
        }
    }

    private void FlashTaskbar()
    {
        try
        {
            var window = Application.Current.MainWindow;
            if (window != null)
            {
                window.FlashWindow(2); // Flash until foreground
            }
        }
        catch { /* best effort */ }
    }

    private void EnsureScriptExists()
    {
        // The script is generated dynamically per notification, so no static file needed.
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

/// <summary>Win32 FlashWindowEx P/Invoke for taskbar flashing fallback.</summary>
internal static class WindowExtensions
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [System.Runtime.InteropServices.StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public int cbSize;
        public IntPtr hwnd;
        public int dwFlags;
        public int uCount;
        public int dwTimeout;
    }

    private const int FLASHW_STOP = 0;
    private const int FLASHW_CAPTION = 1;
    private const int FLASHW_TRAY = 2;
    private const int FLASHW_ALL = 3;
    private const int FLASHW_TIMER = 4;
    private const int FLASHW_TIMERNOFG = 12;

    public static void FlashWindow(this System.Windows.Window window, int count = 3)
    {
        var info = new FLASHWINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = count,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    public static void StopFlashing(this System.Windows.Window window)
    {
        var info = new FLASHWINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle,
            dwFlags = FLASHW_STOP,
            uCount = 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }
}
