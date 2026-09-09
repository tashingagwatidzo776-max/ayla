using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Tf.Core.Update;

/// <summary>
/// Checks for updates from a GitHub releases endpoint or custom version file,
/// downloads new versions, and optionally installs them. Thread-safe.
/// </summary>
public sealed class AutoUpdater : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly string _updateDir;
    private CancellationTokenSource? _cts;

    public AutoUpdater(string currentVersion, string? proxyUrl = null)
    {
        _currentVersion = currentVersion;
        _updateDir = Path.Combine(AppContext.BaseDirectory, "updates");
        Directory.CreateDirectory(_updateDir);

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", $"Tf-Trader/{currentVersion}");

        if (!string.IsNullOrEmpty(proxyUrl))
        {
            _http.BaseAddress = new Uri(proxyUrl);
        }
    }

    /// <summary>Raised with progress messages during download/install.</summary>
    public event Action<string>? Progress;

    /// <summary>Check if a newer version is available.</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(
        string versionUrl = "https://api.github.com/repos/user/tf/releases/latest",
        CancellationToken ct = default)
    {
        try
        {
            Progress?.Invoke("Checking for updates...");

            var response = await _http.GetAsync(versionUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                Progress?.Invoke($"Update check failed: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                Progress?.Invoke("No update available");
                return null;
            }

            var remoteVersion = release.TagName.TrimStart('v');
            if (!Version.TryParse(remoteVersion, out var remote) ||
                !Version.TryParse(_currentVersion, out var current))
            {
                Progress?.Invoke("Could not parse version numbers");
                return null;
            }

            if (remote <= current)
            {
                Progress?.Invoke($"Already on latest version ({_currentVersion})");
                return null;
            }

            // Find the win-x64 zip asset
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.Contains("win-x64") && a.Name.EndsWith(".zip"));

            if (asset == null)
            {
                Progress?.Invoke("No compatible update package found");
                return null;
            }

            Progress?.Invoke($"Update available: v{remoteVersion}");
            return new UpdateInfo
            {
                Version = remoteVersion,
                ReleaseNotes = release.Body ?? "",
                DownloadUrl = asset.BrowserDownloadUrl,
                FileSize = asset.Size
            };
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"Update check error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Download update package to temp directory.</summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        Progress?.Invoke($"Downloading v{info.Version}...");

        var zipPath = Path.Combine(_updateDir, $"tf-update-{info.Version}.zip");

        // Resume support
        var existingBytes = 0L;
        if (File.Exists(zipPath))
        {
            existingBytes = new FileInfo(zipPath).Length;
            Progress?.Invoke($"Resuming from {existingBytes / 1024}KB...");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        if (existingBytes > 0)
        {
            request.Headers.Add("Range", $"bytes={existingBytes}-");
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var mode = existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent
            ? FileMode.Append
            : FileMode.Create;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var file = new FileStream(zipPath, mode, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file, ct);

        Progress?.Invoke($"Download complete: {info.Version}");
        return zipPath;
    }

    /// <summary>Extract and stage update files for installation.</summary>
    public async Task<string> StageUpdateAsync(string zipPath, CancellationToken ct = default)
    {
        Progress?.Invoke("Extracting update...");

        var stageDir = Path.Combine(_updateDir, "staged");
        if (Directory.Exists(stageDir))
            Directory.Delete(stageDir, recursive: true);

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, stageDir), ct);

        Progress?.Invoke("Update staged successfully");
        return stageDir;
    }

    /// <summary>
    /// Install the staged update by replacing files in the current directory.
    /// Creates a backup first. Returns true if successful.
    /// </summary>
    public bool InstallUpdate(string stagedDir)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var backupDir = Path.Combine(_updateDir, $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

            Progress?.Invoke("Creating backup...");

            // Backup current files
            Directory.CreateDirectory(backupDir);
            foreach (var file in Directory.GetFiles(appDir))
            {
                if (Path.GetFileName(file).StartsWith("tf-update-")) continue;
                File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), true);
            }

            Progress?.Invoke("Installing update...");

            // Copy new files
            foreach (var file in Directory.GetFiles(stagedDir))
            {
                var dest = Path.Combine(appDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            Progress?.Invoke("Update installed! Restart to apply.");
            return true;
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"Install failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Create a batch script to restart the app after update.</summary>
    public void CreateRestartScript()
    {
        var appPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Tf.exe");
        var scriptPath = Path.Combine(_updateDir, "restart.bat");

        var script = $@"
@echo off
timeout /t 2 /nobreak > nul
start "" """"{appPath}""""
del ""%~f0""
";

        File.WriteAllText(scriptPath, script.TrimStart());

        // Launch the restart script
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    /// <summary>Clean up old update files.</summary>
    public void CleanupOldUpdates()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_updateDir, "tf-update-*.zip"))
            {
                File.Delete(file);
            }

            var backupDirs = Directory.GetDirectories(_updateDir, "backup-*");
            foreach (var dir in backupDirs.Take(backupDirs.Length - 3)) // Keep last 3 backups
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _http?.Dispose();
    }
}

public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long FileSize { get; set; }
}

internal sealed class GitHubRelease
{
    public string TagName { get; set; } = "";
    public string Body { get; set; } = "";
    public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    public string Name { get; set; } = "";
    public string BrowserDownloadUrl { get; set; } = "";
    public long Size { get; set; }
}
