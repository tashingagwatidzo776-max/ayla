using System.IO;
using System.Net;
using System.Text;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.App.ViewModels;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.Tests;

/// <summary>
/// Verifies the LLM-tab autonomy loop contributes telemetry to the shared
/// MetricsCollector: cycles started via StartAutonomy record latency samples
/// attributed to the "LlmTab" account, so the metrics export carries the
/// Brain tab's cycles alongside the growth runners'.
///
/// The VM news up its own LlmClient, so the LLM is simulated by a local
/// capture listener (BaseUrl points at it via the injected settings factory)
/// that answers with a valid OpenAI-style decision body — the decision
/// completes without network and the cycle's latency sample lands.
/// </summary>
[Trait("Category", "Unit")]
public class BrainViewModelTelemetryTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;
    private readonly MetricsCollector _metrics = new();

    public BrainViewModelTelemetryTests()
    {
        (_listener, _port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        _url = $"http://127.0.0.1:{_port}/v1";
        _ = Task.Run(() => CaptureAsync(_cts.Token));

        _dir = Path.Combine(Path.GetTempPath(), $"tf_bvm_telemetry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVault(), _store, _journal);
    }

    public void Dispose()
    {
        _journal.Dispose();
        _cts.Cancel();
        try { _listener.Close(); } catch { /* best effort */ }
        _cts.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task CaptureAsync(CancellationToken ct)
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

            var decision = """
                {"choices":[{"message":{"content":"{\"direction\":\"HOLD\",\"confidence\":0.5,\"stake\":0,\"reasoning\":\"telemetry drill\"}"}}]}
                """;

            var buffer = Encoding.UTF8.GetBytes(decision);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer);
            context.Response.Close();
        }
    }

    /// <summary>The factory indirection only exists to point BaseUrl at the
    /// loopback listener; the rest mirrors the app's settings.</summary>
    private AppSettings Settings() => new()
    {
        LlmBaseUrl = _url,
        LlmModel = "test-model",
        AutonomyEnabled = true,
        DecisionIntervalMinutes = 1
    };

    private BrainViewModel NewVm() => new(
        new DerivClient(),
        _store,
        new DashboardViewModel(new DerivClient(), _hub),
        Settings,
        () => new[] { new Tick("frxEURUSD", 1.1, 1.1, 1.1, 1700000000, 5) },
        () => new RiskContext(KillSwitchEngaged: false, 0, 1000m, 0m, 0, null, null),
        () => Array.Empty<string>(),
        _metrics);

    [Fact]
    public async Task AutonomyCycles_RecordLatencyUnderLlmTab()
    {
        var vm = NewVm();
        vm.Refresh(); // arms IsAutonomyEnabled from the settings factory
        vm.StartAutonomy();

        await WaitForAsync(() => vm.Metrics.Latency.Count >= 1, 10_000);

        vm.StopAutonomy();

        var sample = vm.Metrics.Latency[0];
        Assert.Equal("LlmTab", sample.Account);
        Assert.Equal("cycle_latency_ms", sample.Name);
        Assert.True(sample.Value >= 0);
    }

    [Fact]
    public void MetricsProperty_ExposesSharedCollector()
    {
        var vm = NewVm();

        Assert.Same(_metrics, vm.Metrics);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }

    private sealed class MemoryVault : IAccountVault
    {
        public IReadOnlyList<AccountConfig> Load() => [];
        public void Save(IReadOnlyList<AccountConfig> accounts) { }
    }
}
