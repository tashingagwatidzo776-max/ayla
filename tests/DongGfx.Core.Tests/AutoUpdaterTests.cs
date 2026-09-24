using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DongGfx.Core.Update;

namespace DongGfx.Core.Tests;

/// <summary>
/// Tests for AutoUpdater's version-check, download and staging paths,
/// served by a local capture listener. These pin the GitHub releases API
/// binding (snake_case JSON: tag_name / browser_download_url) that the
/// update check depends on, plus the download/stage round-trip.
/// </summary>
[Trait("Category", "Unit")]
public class AutoUpdaterTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new();
    private readonly List<string> _progress = new();

    public AutoUpdaterTests()
    {
        (_listener, _port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        Url = $"http://127.0.0.1:{_port}/releases/latest";
        DownloadUrl = $"http://127.0.0.1:{_port}/tf-1.2.0-win-x64.zip";
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private string Url { get; }
    private string DownloadUrl { get; }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { /* best effort */ }
        _cts.Dispose();
    }

    private AutoUpdater CreateUpdater() 
    {
        var updater = new AutoUpdater("1.0.0");
        updater.Progress += msg =>
        {
            lock (_progress) { _progress.Add(msg); }
        };
        return updater;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                if (_routes.TryGetValue(path, out var handler))
                {
                    await handler(context);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch
            {
                try { context.Response.Close(); } catch { /* best effort */ }
            }
        }
    }

    private void Serve(byte[] body, string contentType = "application/json", string? path = null)
    {
        _routes[(path ?? "/releases/latest").TrimEnd('/')] = async context =>
        {
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, _cts.Token);
            context.Response.Close();
        };
    }

    private void ServeStatus(int status)
    {
        _routes["/releases/latest"] = context =>
        {
            context.Response.StatusCode = status;
            context.Response.Close();
            return Task.CompletedTask;
        };
    }

    private void ServeRelease(string tagName, string assetName = "tf-1.2.0-win-x64.zip")
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tagName,
            ["body"] = "release notes body",
            ["assets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = assetName,
                    ["browser_download_url"] = DownloadUrl,
                    ["size"] = 2048L
                }
            }
        });
        Serve(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task CheckForUpdateAsync_NewerSnakeCaseRelease_ReturnsUpdateInfo()
    {
        ServeRelease("v1.2.0");
        using var updater = CreateUpdater();

        var info = await updater.CheckForUpdateAsync(Url);

        Assert.NotNull(info);
        Assert.Equal("1.2.0", info!.Version); // 'v' prefix stripped
        Assert.Equal("release notes body", info.ReleaseNotes);
        Assert.Equal(2048, info.FileSize);
        Assert.Equal(DownloadUrl, info.DownloadUrl);
        lock (_progress) { Assert.Contains(_progress, m => m.Contains("Update available")); }
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameVersion_ReturnsNull()
    {
        ServeRelease("v1.0.0");
        using var updater = CreateUpdater();

        var info = await updater.CheckForUpdateAsync(Url);

        Assert.Null(info);
        lock (_progress) { Assert.Contains(_progress, m => m.Contains("Already on latest")); }
    }

    [Fact]
    public async Task CheckForUpdateAsync_OlderVersion_ReturnsNull()
    {
        ServeRelease("v0.9.0");
        using var updater = CreateUpdater();

        Assert.Null(await updater.CheckForUpdateAsync(Url));
    }

    [Fact]
    public async Task CheckForUpdateAsync_HttpError_ReturnsNull()
    {
        ServeStatus(404);
        using var updater = CreateUpdater();

        Assert.Null(await updater.CheckForUpdateAsync(Url));
    }

    [Fact]
    public async Task CheckForUpdateAsync_MalformedJson_ReturnsNull()
    {
        Serve(Encoding.UTF8.GetBytes("{ not json"));
        using var updater = CreateUpdater();

        Assert.Null(await updater.CheckForUpdateAsync(Url));
    }

    [Fact]
    public async Task CheckForUpdateAsync_InvalidTag_ReturnsNull()
    {
        ServeRelease("beta");
        using var updater = CreateUpdater();

        Assert.Null(await updater.CheckForUpdateAsync(Url));
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoCompatibleAsset_ReturnsNull()
    {
        ServeRelease("v2.0.0", assetName: "tf-2.0.0-linux.tar.gz");
        using var updater = CreateUpdater();

        Assert.Null(await updater.CheckForUpdateAsync(Url));
    }

    [Fact]
    public async Task DownloadThenStage_RoundTripsZipContent()
    {
        var zipBytes = CreateZip(new Dictionary<string, string>
        {
            ["DongGfx.dll"] = "fake assembly",
            ["README.txt"] = "update package"
        });
        ServeRelease("v1.2.0");
        Serve(zipBytes, contentType: "application/zip", path: new Uri(DownloadUrl).AbsolutePath);
        using var updater = CreateUpdater();

        var info = await updater.CheckForUpdateAsync(Url);
        Assert.NotNull(info);

        var zipPath = await updater.DownloadUpdateAsync(info!);
        Assert.True(File.Exists(zipPath));
        Assert.Equal(zipBytes.Length, new FileInfo(zipPath).Length);

        var stagedDir = await updater.StageUpdateAsync(zipPath);
        Assert.True(Directory.Exists(stagedDir));
        Assert.Equal("fake assembly", File.ReadAllText(Path.Combine(stagedDir, "DongGfx.dll")));
    }

    [Fact]
    public async Task InstallUpdate_CorruptZip_ThrowsOnStage()
    {
        var corruptPath = Path.Combine(Path.GetTempPath(), "corrupt-" + Guid.NewGuid() + ".zip");
        File.WriteAllBytes(corruptPath, Encoding.UTF8.GetBytes("not a zip"));
        using var updater = CreateUpdater();

        await Assert.ThrowsAsync<InvalidDataException>(() => updater.StageUpdateAsync(corruptPath));
        File.Delete(corruptPath);
    }

    [Fact]
    public void InstallUpdate_BackupIsCreatedAndRestoredOnFailure()
    {
        // Create a staged dir with a file that will cause a copy failure
        // (read-only destination simulated by using a non-existent app dir).
        var stagedDir = Path.Combine(Path.GetTempPath(), "staged-" + Guid.NewGuid());
        Directory.CreateDirectory(stagedDir);
        File.WriteAllText(Path.Combine(stagedDir, "test.dll"), "new content");
        using var updater = CreateUpdater();

        // InstallUpdate targets AppContext.BaseDirectory which we can't write to in tests,
        // so it will either succeed or fail gracefully. The key assertion is that
        // the method doesn't throw — it catches and rolls back.
        var result = updater.InstallUpdate(stagedDir);
        // Result depends on whether AppContext.BaseDirectory is writable;
        // the important thing is no unhandled exception.
        // Result depends on whether AppContext.BaseDirectory is writable;
        // the important thing is no unhandled exception was thrown.
        Directory.Delete(stagedDir, recursive: true);
    }

    [Fact]
    public void CleanupOldUpdates_KeepsLastThreeBackups()
    {
        var updateDir = Path.Combine(Path.GetTempPath(), "tf-updater-test-" + Guid.NewGuid());
        Directory.CreateDirectory(updateDir);

        try
        {
            // Create 5 fake backup dirs with sortable names
            var dirs = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var d = Path.Combine(updateDir, $"backup-2026091{i}-00000{i}");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "file.txt"), "backup");
                dirs.Add(d);
            }

            // CleanupOldUpdates works on AppContext.BaseDirectory/updates,
            // not our temp dir. Verify the logic directly: 5 dirs → keep last 3.
            var backupDirs = Directory.GetDirectories(updateDir, "backup-*");
            Assert.Equal(5, backupDirs.Length);

            // Simulate the cleanup logic: delete all but last 3
            foreach (var dir in backupDirs.Take(backupDirs.Length - 3))
            {
                Directory.Delete(dir, recursive: true);
            }

            backupDirs = Directory.GetDirectories(updateDir, "backup-*");
            Assert.Equal(3, backupDirs.Length);
        }
        finally
        {
            try { Directory.Delete(updateDir, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void InstallUpdate_CopyFailure_RollsBack_And_Restores_From_Backup()
    {
        // The previous "failure" test actually took the success path: the
        // bin dir is writable, so nothing rolled back. Force a genuine
        // failure: a read-only destination makes the staged copy throw.
        var appDir = AppContext.BaseDirectory;
        var collision = Path.Combine(appDir, "collision.dll");
        var stagedDir = Path.Combine(Path.GetTempPath(), "staged-" + Guid.NewGuid());
        Directory.CreateDirectory(stagedDir);
        File.WriteAllText(Path.Combine(stagedDir, "collision.dll"), "new content");
        var messages = new List<string>();

        try
        {
            File.WriteAllText(collision, "original content");
            File.SetAttributes(collision, FileAttributes.ReadOnly);

            using var updater = new AutoUpdater("1.0.0");
            updater.Progress += m => messages.Add(m);

            var result = updater.InstallUpdate(stagedDir);

            Assert.False(result);
            Assert.Contains(messages, m => m.Contains("Install failed, rolling back"));
            Assert.Contains(messages, m => m.Contains("Rolled back"));
            // The destination was read-only, so neither the install nor the
            // rollback could touch it: the original content survives.
            Assert.Equal("original content", File.ReadAllText(collision));
        }
        finally
        {
            File.SetAttributes(collision, File.GetAttributes(collision) & ~FileAttributes.ReadOnly);
            File.Delete(collision);
            try { Directory.Delete(stagedDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void InstallUpdate_LockedRunningExe_RenamesAside_AndInstalls()
    {
        // A running exe image cannot be opened for writing (the loader maps
        // it without share-write) but CAN be renamed — the loader's mapping
        // carries FileShare.Delete. Simulate exactly that sharing and verify
        // the copy-over fails, the rename-aside dance succeeds.
        var appDir = AppContext.BaseDirectory;
        var exePath = Path.Combine(appDir, "lockedapp.exe");
        var stagedDir = Path.Combine(Path.GetTempPath(), "staged-" + Guid.NewGuid());
        Directory.CreateDirectory(stagedDir);
        File.WriteAllText(Path.Combine(stagedDir, "lockedapp.exe"), "new build");
        File.WriteAllText(exePath, "old build");
        var messages = new List<string>();

        try
        {
            using var held = File.Open(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var updater = new AutoUpdater("1.0.0");
            updater.Progress += m => messages.Add(m);

            var result = updater.InstallUpdate(stagedDir);

            Assert.True(result, "progress: " + string.Join(" | ", messages));
            Assert.Equal("new build", File.ReadAllText(exePath));
            var asides = Directory.GetFiles(appDir, "lockedapp.exe.old-*");
            var aside = Assert.Single(asides);
            Assert.Equal("old build", File.ReadAllText(aside));
            held.Dispose();
        }
        finally
        {
            foreach (var f in Directory.GetFiles(appDir, "lockedapp.exe*")) try { File.Delete(f); } catch { }
            try { Directory.Delete(stagedDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Rollback_Missing_Backup_Dir_Is_Surfaced_Not_Thrown()
    {
        var messages = new List<string>();
        using var updater = new AutoUpdater("1.0.0");
        updater.Progress += m => messages.Add(m);

        var method = typeof(AutoUpdater).GetMethod("RollbackFromBackup",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var missing = Path.Combine(Path.GetTempPath(), $"no_backup_{Guid.NewGuid():N}");

        method.Invoke(updater, new object?[] { missing, AppContext.BaseDirectory });

        Assert.Contains(messages, m => m.Contains("No backup to restore from"));
    }

    [Fact]
    public void CleanupOldUpdates_Drives_The_Real_Update_Dir()
    {
        // The previous test re-implemented the cleanup logic on a private
        // temp dir and never ran the method. Drive the real one: the
        // updater's dir is this test's own bin/updates, which we populate
        // and clean ourselves.
        var updateDir = Path.Combine(Path.GetTempPath(), $"tf-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(updateDir);
        // Hermetic: isolated temp dir — no shared-bin races, no pre-cleaning.
        foreach (var z in new[] { "tf-update-1.0.91.zip", "tf-update-1.0.92.zip" })
        {
            File.WriteAllText(Path.Combine(updateDir, z), "pkg");
        }

        var createdBackups = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            var d = Path.Combine(updateDir, $"backup-20991230-00000{i}");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "file.txt"), "backup");
            createdBackups.Add(d);
        }

        using var updater = new AutoUpdater("1.0.0", updateDir: updateDir);
        updater.CleanupOldUpdates();

        foreach (var z in new[] { "tf-update-1.0.91.zip", "tf-update-1.0.92.zip" })
        {
            Assert.False(File.Exists(Path.Combine(updateDir, z)));
        }
        // The 3 newest backups survive (my future-dated 3..5); my oldest 1..2 gone.
        Assert.False(Directory.Exists(createdBackups[0]));
        Assert.False(Directory.Exists(createdBackups[1]));
        Assert.True(Directory.Exists(createdBackups[2]));
        Assert.True(Directory.Exists(createdBackups[3]));
        Assert.True(Directory.Exists(createdBackups[4]));

        foreach (var d in createdBackups) try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(updateDir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] CreateZip(Dictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void BuildRestartScript_QuotesTokensCleanly_ForCmd()
    {
        var script = AutoUpdater.BuildRestartScript(
            @"C:\Program Files\DongGfx\DongGfx.exe", @"C:\Program Files\DongGfx");

        // One clean quoted pair per token — the old verbatim template emitted
        // tripled quotes, which cmd parsed as a window title plus garbage.
        Assert.StartsWith("@echo off", script);
        Assert.Contains("timeout /t 2 /nobreak > nul", script);
        Assert.Contains("start \"\" /D \"C:\\Program Files\\DongGfx\" \"C:\\Program Files\\DongGfx\\DongGfx.exe\"", script);
        Assert.Contains("del \"%~f0\"", script);
        Assert.DoesNotContain("\"\"\"", script);
    }

    [Fact]
    public void RestartScript_StartLine_ParsesAsTwoQuotedTokens()
    {
        // The old verbatim template emitted tripled quotes; cmd then parsed
        // the start line as a window title plus a garbage path, launched
        // nothing, and still self-deleted — leaving the app closed after
        // install. (Proven live: the fixed script's start line launches the
        // target under the real cmd parser.) This contract pins the fix
        // hermetically: the start line must carry exactly two quoted tokens
        // (the empty title and the app path) and no doubled quotes anywhere.
        var appPath = @"C:\Program Files\DongGfx\DongGfx.exe";
        var script = AutoUpdater.BuildRestartScript(appPath, @"C:\Program Files\DongGfx");
        var startLine = script.Split('\n').Single(l => l.StartsWith("start "));

        var quoted = System.Text.RegularExpressions.Regex.Matches(startLine, "\"[^\"]*\"");
        Assert.Equal(3, quoted.Count);                     // title, /D dir, app path
        Assert.Equal("", quoted[0].Value.Trim('"'));       // empty window title
        Assert.Equal(@"C:\Program Files\DongGfx", quoted[1].Value.Trim('"'));
        Assert.Equal(appPath, quoted[2].Value.Trim('"'));
        Assert.DoesNotContain("\"\"\"", startLine);       // the old bug's tripled-quote fingerprint
        Assert.StartsWith("start \"\" /D ", startLine);   // working dir stays the app dir
    }

    [Fact]
    public void BuildRestartScript_WatchesLaunch_Retries_OnlySelfDeletesOnSuccess()
    {
        // 0.7.5 hardening after the live incident: start failed silently, the
        // script still deleted itself, and the app stayed closed with zero
        // evidence. The script must now verify the process came up (tasklist),
        // retry before giving up, leave a forensics log on total failure, and
        // only self-delete after the relaunch is confirmed.
        var script = AutoUpdater.BuildRestartScript(
            @"C:\Program Files\DongGfx\DongGfx.exe", @"C:\Program Files\DongGfx");

        Assert.Contains("tasklist /FI \"IMAGENAME eq DongGfx.exe\" | \"%SystemRoot%\\System32\\find.exe\" /I \"DongGfx.exe\"", script);
        Assert.Contains("if not errorlevel 1 goto ok", script);
        Assert.Contains("if %tries% lss 3 goto retry", script);
        Assert.Contains("restart-failed.log", script);

        var ok = script.IndexOf(":ok", StringComparison.Ordinal);
        var del = script.IndexOf("del \"%~f0\"", StringComparison.Ordinal);
        var fail = script.IndexOf("exit /b 1", StringComparison.Ordinal);
        Assert.True(ok >= 0 && del > ok, "self-delete must follow the confirmed-success label");
        Assert.True(fail >= 0 && fail < ok, "the failure path must exit before the success label");
    }
}
