using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The FX account bar's soak visibility: the per-symbol badge (laggard
/// symbol first — GO LIVE is all-or-nothing, so the laggard is the number
/// that matters), its cheap poll-driven refresh, and the go-live refusal
/// naming exactly which symbols are still short of their bar. The
/// real-money unlock is stubbed closed (these tests never exercise gate
/// state; the gate rails themselves are covered elsewhere).
/// </summary>
[Trait("Category", "Unit")]
public class FxSoakBadgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tf_soak_{Guid.NewGuid():N}");
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings;

    public FxSoakBadgeTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _settings = new AppSettings { IsDemo = true, Mt5MaxLots = 1.00m };
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json)> Routes { get; } = new();

        public void Route(string path, string json) => Routes[path] = (HttpStatusCode.OK, json);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var (status, json) = Routes.TryGetValue(request.RequestUri!.AbsolutePath.TrimStart('/'), out var r)
                ? r
                : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string AccountJson =>
        "{\"login\": 201587365, \"server\": \"Deriv-Demo\", \"currency\": \"USD\", " +
        "\"balance\": 2729.34, \"equity\": 2729.34, \"margin_free\": 5000.0, \"leverage\": 1000, \"trade_mode\": 0}";

    /// <summary>The host is created lazily by ToggleFxBrain (the factory) —
    /// tests that need it grab vm.FxHost AFTER the toggle.</summary>
    private TerminalViewModel NewVm(RouteHandler handler) =>
        new(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => false,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(handler, new Uri("http://127.0.0.1:53190/")),
            fxHostFactory: () => new FxPortfolioHost(
                new Mt5BridgeClient(handler, new Uri("http://127.0.0.1:53190/")),
                _journal, new[] { "XAUUSDmicro", "EURUSD" },
                killSwitchEngaged: () => false, lotsCap: () => 1.00m, realMoneyUnlocked: () => false,
                governorTripped: () => false, dailyLossCap: () => 5000m, equityFloor: () => 0m,
                portfolioMaxLots: () => 0.10m, webhook: null,
                newsCalendarPath: () => Path.Combine(_dir, "news.json"),
                newsWindow: () => TimeSpan.FromMinutes(15)));

    /// <summary>Drives the engine's soak counter directly (private setter):
    /// the badge's render/sort/refresh logic is under test here — the
    /// counting rule itself is pinned by FxPortfolioTests.</summary>
    private static void SetSeen(FxEngineHost host, int seen) =>
        typeof(FxEngineHost).GetProperty(nameof(FxEngineHost.PaperSignalsSeen))!
            .SetValue(host, seen);

    [Fact]
    public void Badge_Is_Empty_While_The_Brain_Is_Off()
    {
        var vm = NewVm(new RouteHandler());

        Assert.Equal(string.Empty, vm.FxSoakBadge);

        vm.ShutdownFxBrain();   // no host yet: must be a safe no-op
        Assert.Equal(string.Empty, vm.FxSoakBadge);
    }

    [Fact]
    public void Badge_Lists_Every_Symbol_Laggard_First()
    {
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);   // start in paper: bar visible at zeros
        // Equal counters tie-break alphabetically: EURUSD is shown first.
        Assert.Equal("EURUSD 0/10 XAUUSDmicro 0/10", vm.FxSoakBadge);

        SetSeen(vm.FxHost!.Hosts[0], 2);         // XAUUSDmicro behind
        SetSeen(vm.FxHost.Hosts[1], 5);          // EURUSD ahead
        vm.RefreshFxSoakBadgeIfRunning();

        Assert.Equal("XAUUSDmicro 2/10 EURUSD 5/10", vm.FxSoakBadge);

        vm.ShutdownFxBrain();
    }

    [Fact]
    public void Badge_Refresh_Is_Idempotent_Until_A_Counter_Moves()
    {
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);
        SetSeen(vm.FxHost!.Hosts[0], 3);
        vm.RefreshFxSoakBadgeIfRunning();
        var first = vm.FxSoakBadge;
        Assert.Equal("EURUSD 0/10 XAUUSDmicro 3/10", first);   // EURUSD still laggard

        // No counter movement: same text, no rebuild.
        vm.RefreshFxSoakBadgeIfRunning();
        Assert.Equal(first, vm.FxSoakBadge);

        vm.ShutdownFxBrain();
    }

    [Fact]
    public void Badge_Clears_When_The_Brain_Stops()
    {
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);
        SetSeen(vm.FxHost!.Hosts[0], 1);
        vm.RefreshFxSoakBadgeIfRunning();
        Assert.NotEqual(string.Empty, vm.FxSoakBadge);

        vm.ToggleFxBrainCommand.Execute(null);   // stop
        Assert.Equal("FX BRAIN: OFF", vm.FxBadge);
        Assert.Equal(string.Empty, vm.FxSoakBadge);

        vm.ShutdownFxBrain();
    }

    [Fact]
    public void GoLive_Refusal_Names_The_Laggard_Symbols()
    {
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);
        SetSeen(vm.FxHost!.Hosts[0], 2);         // XAUUSDmicro 2/10
        SetSeen(vm.FxHost.Hosts[1], 5);          // EURUSD 5/10

        vm.FxGoLiveCommand.Execute(null);

        Assert.Contains("go-live refused — paper soak 7/20", vm.FxStatusText);
        Assert.Contains("waiting on: XAUUSDmicro 2/10, EURUSD 5/10", vm.FxStatusText);
        Assert.Equal("FX BRAIN: PAPER", vm.FxBadge);

        vm.ShutdownFxBrain();
    }

    [Fact]
    public void SoakLaggards_Excludes_Completed_Symbols()
    {
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);
        SetSeen(vm.FxHost!.Hosts[0], 10);        // XAUUSDmicro done
        SetSeen(vm.FxHost.Hosts[1], 4);          // EURUSD still short

        var laggard = Assert.Single(vm.FxHost.SoakLaggards);
        Assert.Equal("EURUSD", laggard.Symbol);

        vm.ShutdownFxBrain();
    }
}
