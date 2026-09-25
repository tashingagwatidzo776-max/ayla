using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

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
                FileSize = asset.Size,
                ChecksumSha256 = ExtractChecksumFromNotes(release.Body ?? "", asset.Name) ?? "",
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

        // Hardening: a truncated or resume-stitched download must never be
        // staged — a half package brick-steps the install into a rollback.
        // When the release declares a size, the byte count must match
        // exactly (0 = size unknown, skip the check). The count is read
        // AFTER the stream is closed: NTFS reports a stale (0) length for
        // a file with unflushed buffered writes.
        long written;
        using (var file = new FileStream(zipPath, mode, FileAccess.Write, FileShare.None))
        {
            await stream.CopyToAsync(file, ct);
            await file.FlushAsync(ct);
            written = file.Length;
        }

        if (info.FileSize > 0 && written != info.FileSize)
        {
            try { File.Delete(zipPath); } catch { /* best effort */ }
            Progress?.Invoke($"Incomplete download: got {written} bytes, expected {info.FileSize}");
            throw new InvalidDataException(
                $"incomplete download: {written} of {info.FileSize} bytes — retry the download");
        }

        // Hardening: an announced SHA-256 turns "right size" into "right
        // bytes" — a corrupted-but-complete download is caught here
        // instead of bricking the install into a rollback.
        if (!string.IsNullOrWhiteSpace(info.ChecksumSha256))
        {
            var actualHash = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, info.ChecksumSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zipPath); } catch { /* best effort */ }
                Progress?.Invoke($"Checksum mismatch: got {actualHash}, release announced {info.ChecksumSha256}");
                throw new InvalidDataException(
                    $"checksum mismatch — downloaded {actualHash}, release announced {info.ChecksumSha256}; download deleted");
            }
        }

        Progress?.Invoke($"Download complete: {info.Version}");
        return zipPath;
    }

    /// <summary>Extracts the announced SHA-256 for `assetName` from the
    /// release notes. Two formats are recognized: the sha256sum line
    /// ("<64-hex>  <asset-name>") and a labeled line ("sha256: <hex>",
    /// "SHA256 = <hex>"). A hex line naming a DIFFERENT file is ignored, so
    /// multi-asset releases cannot poison the verdict. No announcement →
    /// null → the download falls back to the size check alone.</summary>
    public static string? ExtractChecksumFromNotes(string releaseBody, string assetName)
    {
        if (string.IsNullOrWhiteSpace(releaseBody) || string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        foreach (var rawLine in releaseBody.Split('\n'))
        {
            var line = rawLine.Trim();
            var hex = FindHex64(line);
            if (hex is null)
            {
                continue;
            }

            var namesAsset = line.Contains(assetName, StringComparison.OrdinalIgnoreCase);
            var lineLower = line.ToLowerInvariant();
            var labeled = lineLower.Contains("sha256") || lineLower.Contains("sha-256");
            if (namesAsset || labeled)
            {
                return hex;
            }
        }

        return null;
    }

    private static string? FindHex64(string line)
    {
        var match = Regex.Match(line, @"\b[0-9a-fA-F]{64}\b");
        return match.Success ? match.Value : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>Extract and stage update files for installation. The
    /// extracted package is validated before anything can be installed:
    /// a package with no executable (a wrong-asset or junk zip) is refused
    /// and the stage is removed, so InstallUpdate never runs on it.</summary>
    public async Task<string> StageUpdateAsync(string zipPath, CancellationToken ct = default)
    {
        Progress?.Invoke("Extracting update...");

        var stageDir = Path.Combine(_updateDir, "staged");
        if (Directory.Exists(stageDir))
            Directory.Delete(stageDir, recursive: true);

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, stageDir), ct);

        // Hardening: require at least one executable somewhere in the
        // package. A zip without one is not a DON G FX update (wrong asset,
        // truncated-but-valid zip, junk) — refuse and clean up.
        if (!Directory.GetFiles(stageDir, "*.exe", SearchOption.AllDirectories)
                .Any())
        {
            try { Directory.Delete(stageDir, recursive: true); } catch { /* best effort */ }
            Progress?.Invoke("Staged package contains no executable — not an update package");
            throw new InvalidDataException(
                "staged package contains no executable — refusing to stage it");
        }

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
            // Hardening: an empty stage means "download first" reached the
            // install command, or a validated stage was wiped — refusing
            // here leaves the running app untouched instead of "installing"
            // nothing and reporting success.
            if (!Directory.Exists(stagedDir) ||
                !Directory.GetFiles(stagedDir, "*", SearchOption.AllDirectories).Any())
            {
                Progress?.Invoke("No staged update found. Download first.");
                return false;
            }

            Progress?.Invoke("Creating backup...");

            // Best-effort sweep of rename-aside leftovers from earlier hops.
            foreach (var stale in Directory.GetFiles(appDir, "*.old-*"))
            {
                try { File.Delete(stale); } catch { /* still mapped */ }
            }

            // Backup current files, recursively but never the update dir
            // itself: backups copying zips/backups into themselves grows
            // unbounded and can cycle when restoring.
            Directory.CreateDirectory(backupDir);
            var updateDirRoot = Path.GetFullPath(_updateDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (var file in Directory.GetFiles(appDir, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                if (full.StartsWith(updateDirRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(file).StartsWith("tf-update-")) continue;
                var dest = Path.Combine(backupDir,
                    Path.GetRelativePath(appDir, full));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
            }

            Progress?.Invoke("Installing update...");

            // Copy new files recursively — a staged package may carry
            // subdirectories (runtimes/, assets/, locales/), and the old
            // top-level-only walk silently half-installed those.
            foreach (var file in Directory.GetFiles(stagedDir, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(appDir, Path.GetRelativePath(stagedDir, file));
                var destDir = Path.GetDirectoryName(dest)!;
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
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
        foreach (var file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var dest = Path.Combine(appDir, Path.GetRelativePath(backupDir, file));
                var destDir = Path.GetDirectoryName(dest)!;
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(file, dest, true);
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
            "tasklist /FI \"IMAGENAME eq " + exe + "\" | \"%SystemRoot%\\System32\\find.exe\" /I \"" + exe + "\" > nul",
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

    /// <summary>SHA-256 announced in the release notes (empty = none
    /// announced; the download then relies on the size check alone).</summary>
    public string ChecksumSha256 { get; set; } = "";
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
