using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DongGfx.App.ViewModels;
using DongGfx.Core.Update;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The Update VM's command surface, driven against a local release listener:
/// version verdicts flow into the status message, download guards and the
/// staged-install precondition are respected, and progress messages from the
/// updater surface in the UI without breaking the check. The install success
/// path is deliberately NOT tested (it launches the restart script and shuts
/// the app down) — only its safe preconditions are.
/// </summary>
[Trait("Category", "Unit")]
public class UpdateViewModelTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new();

    public UpdateViewModelTests()
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

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
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

    private void ServeRelease(string tagName)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tagName,
            ["body"] = "release notes body",
            ["assets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "tf-1.2.0-win-x64.zip",
                    ["browser_download_url"] = DownloadUrl,
                    ["size"] = 2048L,
                },
            },
        });
        Serve(Encoding.UTF8.GetBytes(json));
    }

    private static byte[] CreateZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("DongGfx.dll");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("fake assembly");
        }
        return stream.ToArray();
    }

    private (UpdateViewModel Vm, AutoUpdater Updater) NewVm()
    {
        var updater = new AutoUpdater("1.0.0");
        return (new UpdateViewModel(updater, checkUrl: Url), updater);
    }

    [Fact]
    public async Task Check_NewerRelease_Reports_Available_With_Size_And_Notes()
    {
        ServeRelease("v1.2.0");
        var (vm, _) = NewVm();

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.NotNull(vm.AvailableUpdate);
        Assert.Equal("1.2.0", vm.AvailableUpdate!.Version);
        Assert.Contains("Update available: v1.2.0", vm.StatusMessage);
        Assert.Contains("release notes body", vm.StatusMessage);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task Check_SameVersion_Says_You_Are_Latest()
    {
        ServeRelease("v1.0.0");
        var (vm, _) = NewVm();

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Null(vm.AvailableUpdate);
        Assert.Contains("latest version", vm.StatusMessage);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task Check_Failure_Surfaces_As_Update_Check_Failed()
    {
        ServeStatus(500);
        var (vm, updater) = NewVm();

        // A throwing progress handler escapes the updater's internal catch
        // (the handler runs inside it), which is the one path that reaches
        // the VM's catch instead of a clean null.
        updater.Progress += _ => throw new InvalidOperationException("check boom");

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Null(vm.AvailableUpdate);
        Assert.Contains("Update check failed", vm.StatusMessage);
        Assert.Contains("check boom", vm.StatusMessage);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task Download_Guards_When_Nothing_Is_Available()
    {
        var (vm, _) = NewVm();

        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.False(vm.IsDownloading);
        Assert.Equal("Click Check for updates", vm.StatusMessage);
    }

    [Fact]
    public async Task Download_Downloads_And_Stages_A_Real_Package()
    {
        ServeRelease("v1.2.0");
        Serve(CreateZip(), contentType: "application/zip", path: new Uri(DownloadUrl).AbsolutePath);
        var (vm, _) = NewVm();

        await vm.CheckForUpdateCommand.ExecuteAsync(null);
        Assert.NotNull(vm.AvailableUpdate);

        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Contains("Update ready", vm.StatusMessage);
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task Download_Failure_Is_Surfaced_Not_Thrown()
    {
        var (vm, _) = NewVm();
        vm.AvailableUpdate = new UpdateInfo
        {
            Version = "9.9.9",
            DownloadUrl = "http://127.0.0.1:1/tf-9.9.9-win-x64.zip", // port 1: connection refused
            FileSize = 2048,
            ReleaseNotes = "",
        };

        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Contains("Download failed", vm.StatusMessage);
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public void Install_Without_A_Staged_Update_Askes_To_Download_First()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "updates", "staged");
        if (Directory.Exists(staged))
        {
            Directory.Delete(staged, recursive: true); // make the precondition true
        }

        var (vm, _) = NewVm();
        vm.InstallUpdateCommand.Execute(null);

        Assert.Contains("No staged update found", vm.StatusMessage);
    }

    [Fact]
    public void Cleanup_Command_Is_Safe_To_Invoke()
    {
        var (vm, _) = NewVm();

        vm.CleanupOldUpdatesCommand.Execute(null); // best-effort cleanup: never throws

        Assert.NotEmpty(vm.StatusMessage);
    }
}
