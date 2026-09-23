using System.Drawing;
using System.Windows;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Manages the system-tray icon so the app can run headless (growth engines
/// keep trading while the window is hidden). Right-click shows a context menu
/// with Show / Exit. Double-click restores the window.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Window _window;
    private bool _disposed;

    public TrayIconService(Window window)
    {
        _window = window;

        _icon = new System.Windows.Forms.NotifyIcon();
        _icon.Text = "DON G FX — MT5 Trader";
        _icon.Visible = false;

        // Prefer the brand icon (packed as a WPF resource so it survives
        // single-file publish); fall back to a default glyph.
        try
        {
            var sri = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/assets/logo.ico"));
            _icon.Icon = sri is not null
                ? new System.Drawing.Icon(sri.Stream)
                : SystemIcons.Application;
        }
        catch
        {
            _icon.Icon = SystemIcons.Application;
        }

        // Context menu
        var showItem = new System.Windows.Forms.ToolStripMenuItem("Show DongGfx");
        showItem.Click += (_, _) => ShowWindow();

        var pauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause all engines");
        pauseItem.Click += (_, _) => PauseAllRequested?.Invoke();

        var resumeItem = new System.Windows.Forms.ToolStripMenuItem("Resume all engines");
        resumeItem.Click += (_, _) => ResumeAllRequested?.Invoke();

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _icon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
        _icon.ContextMenuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
        {
            showItem,
            new System.Windows.Forms.ToolStripSeparator(),
            pauseItem,
            resumeItem,
            new System.Windows.Forms.ToolStripSeparator(),
            exitItem
        });

        _icon.DoubleClick += (_, _) => ShowWindow();

        // Intercept window close to minimize to tray instead.
        _window.Closing += OnWindowClosing;
    }

    /// <summary>Raised when the user clicks "Show" in the tray menu.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised when the user requests exit from the tray menu.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when the user requests pausing all growth engines.</summary>
    public event Action? PauseAllRequested;

    /// <summary>Raised when the user requests resuming all growth engines.</summary>
    public event Action? ResumeAllRequested;

    /// <summary>Minimize the window to the system tray.</summary>
    public void MinimizeToTray()
    {
        _window.WindowState = WindowState.Minimized;
        _window.ShowInTaskbar = false;
        _icon.Visible = true;
        _icon.ShowBalloonTip(
            2000,
            "DON G FX — MT5 Trader",
            "Running in background. Double-click tray icon to restore.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>Restore the window from the tray.</summary>
    public void ShowWindow()
    {
        _window.WindowState = WindowState.Normal;
        _window.ShowInTaskbar = true;
        _window.Activate();
        _icon.Visible = false;
        ShowRequested?.Invoke();
    }

    /// <summary>Update the tray tooltip with current status.</summary>
    public void UpdateTooltip(string text)
    {
        if (_icon.Visible)
        {
            // NotifyIcon.Text has a 63-char limit.
            _icon.Text = text.Length > 63 ? text[..60] + "…" : text;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of exiting. The user can exit from the tray menu.
        e.Cancel = true;
        MinimizeToTray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.Closing -= OnWindowClosing;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
