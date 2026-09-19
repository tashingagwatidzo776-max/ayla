using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.Core.Update;

namespace DongGfx.App.ViewModels;

/// <summary>
/// ViewModel for the auto-update feature. Checks for updates,
/// downloads them, and installs with user confirmation.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly AutoUpdater _updater;

    [ObservableProperty]
    private string currentVersion = "1.0.0";

    [ObservableProperty]
    private string statusMessage = "Click Check for updates";

    [ObservableProperty]
    private bool isChecking;

    [ObservableProperty]
    private bool isDownloading;

    [ObservableProperty]
    private UpdateInfo? availableUpdate;

    [ObservableProperty]
    private double downloadProgress;

    public UpdateViewModel(AutoUpdater updater)
    {
        _updater = updater;
        _updater.Progress += msg => StatusMessage = msg;
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsChecking) return;

        try
        {
            IsChecking = true;
            StatusMessage = "Checking for updates...";

            var update = await _updater.CheckForUpdateAsync();
            AvailableUpdate = update;

            if (update != null)
            {
                StatusMessage = $"Update available: v{update.Version}\n" +
                              $"Size: {update.FileSize / 1024 / 1024:F1} MB\n" +
                              $"Release notes:\n{update.ReleaseNotes}";
            }
            else
            {
                StatusMessage = "You're running the latest version!";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (AvailableUpdate == null || IsDownloading) return;

        try
        {
            IsDownloading = true;
            StatusMessage = "Downloading update...";

            var zipPath = await _updater.DownloadUpdateAsync(AvailableUpdate);
            StatusMessage = "Extracting update...";

            var stagedDir = await _updater.StageUpdateAsync(zipPath);
            StatusMessage = "Update ready! Click Install to apply.\n" +
                          "The app will restart after installation.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        try
        {
            StatusMessage = "Installing update...";

            var stagedDir = Path.Combine(AppContext.BaseDirectory, "updates", "staged");
            if (!Directory.Exists(stagedDir))
            {
                StatusMessage = "No staged update found. Download first.";
                return;
            }

            var success = _updater.InstallUpdate(stagedDir);
            if (success)
            {
                StatusMessage = "Update installed! Restarting...";
                _updater.CreateRestartScript();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                StatusMessage = "Install failed. Check logs for details.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Install error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CleanupOldUpdates()
    {
        _updater.CleanupOldUpdates();
        StatusMessage = "Old updates cleaned up.";
    }
}
