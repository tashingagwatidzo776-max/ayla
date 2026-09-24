using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DongGfx.Core.Update;

/// <summary>
/// Checks for updates from a GitHub releases endpoint or custom version file,
/// downloads new versions, and optionally installs them. Thread-safe.
/// </summary>
public sealed class AutoUpdater : IDisposable
{
    /// <summary>The real releases endpoint for this product (the placeholder
    /// default above points at user/tf and can never resolve).</summary>
    public const string GitHubReleasesUrl =
        "https://api.github.com/repos/tashingagwatidzo776-max/ayla/releases/latest";

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly string _updateDir;
    private CancellationTokenSource _cts = new();

    /// <param name="updateDir">Optional override of the update/staging
    /// directory (tests use an isolated temp dir; production defaults to
    /// <c>AppContext.BaseDirectory/updates</c>).</param>
    public AutoUpdater(string currentVersion, string? proxyUrl = null, string? updateDir = null)
    {
        _currentVersion = currentVersion;
        _updateDir = updateDir ?? Path.Combine(AppContext.BaseDirectory, "updates");
        Directory.CreateDirectory(_updateDir);

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", $"DongGfx-Trader/{currentVersion}");

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
    /// Creates a backup first. On partial failure, restores the backup so the
    /// app is left in a consistent state. Returns true if successful.
    /// </summary>
    public bool InstallUpdate(string stagedDir)
    {
        var appDir = AppContext.BaseDirectory;
        var backupDir = Path.Combine(_updateDir, $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

        try
        {
            Progress?.Invoke("Creating backup...");

            // Best-effort sweep of rename-aside leftovers from earlier hops.
            foreach (var stale in Directory.GetFiles(appDir, "*.old-*"))
            {
                try { File.Delete(stale); } catch { /* still mapped */ }
            }

            // Backup current files
            Directory.CreateDirectory(backupDir);
            foreach (var file in Directory.GetFiles(appDir))
            {
                if (Path.GetFileName(file).StartsWith("tf-update-")) continue;
                File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), true);
            }

            Progress?.Invoke("Installing update...");

            // Copy new files. A running exe image cannot be overwritten in
            // place (the loader maps it without share-write), so an exe that
            // refuses the copy is renamed aside first — the rename is
            // permitted while the old process keeps running from it.
            foreach (var file in Directory.GetFiles(stagedDir))
            {
                var dest = Path.Combine(appDir, Path.GetFileName(file));
                CopyOverRunningImage(file, dest);
            }

            Progress?.Invoke("Update installed! Restart to apply.");
            return true;
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"Install failed, rolling back: {ex.Message}");
            RollbackFromBackup(backupDir, appDir);
            return false;
        }
    }

    /// <summary>
    /// Copies a staged file over its destination, falling back to the
    /// rename-aside dance when the destination is a running image that
    /// cannot be opened for writing.
    /// </summary>
    private static void CopyOverRunningImage(string sourceFile, string dest)
    {
        try
        {
            File.Copy(sourceFile, dest, true);
        }
        catch (IOException) when (dest.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Timestamped so successive install hops never collide on the
            // aside name, even while an earlier generation still runs.
            var aside = dest + ".old-" + DateTime.UtcNow.ToString("HHmmss");
            File.Move(dest, aside);
            File.Copy(sourceFile, dest, true);
        }
    }

    /// <summary>
    /// Restores files from the backup directory so a partial install does not
    /// leave the app in a broken state. Best-effort: individual file restore
    /// failures are swallowed so the rollback completes as far as possible.
    /// </summary>
    private void RollbackFromBackup(string backupDir, string appDir)
    {
        if (!Directory.Exists(backupDir))
        {
            Progress?.Invoke("No backup to restore from");
            return;
        }
        var restored = 0;
        foreach (var file in Directory.GetFiles(backupDir))
        {
            try
            {
                File.Copy(file, Path.Combine(appDir, Path.GetFileName(file)), true);
                restored++;
            }
            catch
            {
                // Best-effort: continue restoring remaining files.
            }
        }

        Progress?.Invoke($"Rolled back {restored} file(s) from backup");
    }

    /// <summary>Create a batch script to restart the app after update.</summary>
    public void CreateRestartScript()
    {
        var appPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DongGfx.exe");
        var appDir = Path.GetDirectoryName(appPath)!;
        var scriptPath = Path.Combine(_updateDir, "restart.bat");

        File.WriteAllText(scriptPath, BuildRestartScript(appPath, appDir));

        // Launch the restart script
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    /// <summary>
    /// Renders the restart script. One clean quoted pair per token: the
    /// previous verbatim-escaped template emitted tripled quotes, which cmd
    /// parsed as a window title plus a garbage path — start failed silently
    /// and the script still self-deleted, leaving the app closed. The script
    /// now watches its own launch: it confirms the process actually came up
    /// (tasklist), retries twice, only self-deletes on confirmed success, and
    /// on total failure appends to restart-failed.log and keeps itself for
    /// forensics instead of vanishing with the evidence.
    /// </summary>
    public static string BuildRestartScript(string appPath, string appDir)
    {
        var exe = Path.GetFileName(appPath);
        return string.Join("\r\n",
            "@echo off",
            "timeout /t 2 /nobreak > nul",
            "set /a tries=0",
            ":retry",
            "start \"\" /D \"" + appDir + "\" \"" + appPath + "\"",
            "timeout /t 3 /nobreak > nul",
            "tasklist /FI \"IMAGENAME eq " + exe + "\" | find /I \"" + exe + "\" > nul",
            "if not errorlevel 1 goto ok",
            "set /a tries+=1",
            "if %tries% lss 3 goto retry",
            "echo %date% %time% restart failed after 3 tries: " + appPath + " >> \"%~dp0restart-failed.log\"",
            "exit /b 1",
            ":ok",
            "del \"%~f0\"",
            "exit /b 0",
            "");
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
    // GitHub's releases API returns snake_case; without these mappings the
    // properties never bind and every update check reports "no update".
    [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("size")]
    public long Size { get; set; }
}
