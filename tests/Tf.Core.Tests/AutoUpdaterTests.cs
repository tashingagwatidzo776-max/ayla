using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Tf.Core.Update;

namespace Tf.Core.Tests;

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
            ["Tf.dll"] = "fake assembly",
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
        Assert.Equal("fake assembly", File.ReadAllText(Path.Combine(stagedDir, "Tf.dll")));
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
}
