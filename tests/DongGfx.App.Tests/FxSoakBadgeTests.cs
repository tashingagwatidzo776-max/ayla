using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using DongGfx.App.Infrastructure;
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

    /// <summary>Drives the restore provenance directly, like SetSeen: the
    /// pill's render logic is under test here. The ledger itself is pinned by
    /// PaperSoakLedgerTests, and the restore path end to end by
    /// DemoPaperExecutionTests.PaperSoak_Survives_A_Restart_...</summary>
    private static void SetRestored(FxEngineHost host, int restored,
                                    string? droppedBuild = null)
    {
        typeof(FxEngineHost).GetProperty(nameof(FxEngineHost.PaperSoakRestored))!
            .SetValue(host, restored);
        typeof(FxEngineHost).GetProperty(nameof(FxEngineHost.PaperSoakInvalidatedBuild))!
            .SetValue(host, droppedBuild);
    }

    /// <summary>Drives the account-change provenance directly (private
    /// setter), like SetRestored: an account switch restarts the bar.</summary>
    private static void SetDroppedAccount(FxEngineHost host, string? droppedAccount) =>
        typeof(FxEngineHost).GetProperty(nameof(FxEngineHost.PaperSoakInvalidatedAccount))!
            .SetValue(host, droppedAccount);

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

    [Fact]
    public void Dashboard_Soak_Line_States_The_Progress_And_Where_It_Came_From()
    {
        // The account bar's pill is clipped first when that row is full, so
        // the dashboard line is where the bar's provenance stays visible.
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        // Brain off: nothing to report.
        Assert.Null(DashboardViewModel.SoakNoteFor(vm.FxHost));

        vm.ToggleFxBrainCommand.Execute(null);
        var hosts = vm.FxHost!.Hosts;

        // A cold bar: progress and the laggard that is holding GO LIVE back.
        Assert.Equal("paper soak 0/20 · laggard EURUSD 0/10",
            DashboardViewModel.SoakNoteFor(vm.FxHost));

        // Resumed: the line says how much was carried over, so the count is
        // not mistaken for signals earned in this session.
        SetSeen(hosts[0], 3);
        SetRestored(hosts[0], 3);
        Assert.Equal(
            "paper soak 3/20 · laggard EURUSD 0/10 · 3 carried over from the last session",
            DashboardViewModel.SoakNoteFor(vm.FxHost));

        // A build change restarts the bar on purpose: also stated.
        SetRestored(hosts[0], 0, droppedBuild: "0.9.0+deadbee");
        Assert.Equal("paper soak 3/20 · laggard EURUSD 0/10 · restarted — build changed since 0.9.0+deadbee",
            DashboardViewModel.SoakNoteFor(vm.FxHost));

        vm.ShutdownFxBrain();
    }

    [Fact]
    public void Badge_Says_When_The_Bar_Was_Carried_Over_Or_Restarted()
    {
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);
        var hosts = vm.FxHost!.Hosts;

        // A cold bar makes no claims about provenance: the standing sentence
        // only.
        Assert.Equal(TerminalViewModel.SoakTooltipBase, vm.FxSoakTooltip);
        Assert.DoesNotContain("carried over", vm.FxSoakBadge);

        // Resumed from an earlier session: the pill keeps its numbers (its row
        // is already full — a sentence there clipped), the tooltip explains
        // what the counters mean, and the status line states the provenance
        // once, where there is room.
        SetSeen(hosts[0], 3);
        SetRestored(hosts[0], 3);
        vm.RefreshFxSoakBadgeIfRunning();
        Assert.Equal("EURUSD 0/10 XAUUSDmicro 3/10", vm.FxSoakBadge);
        Assert.Contains("3 of these signals were counted in an earlier session",
            vm.FxSoakTooltip);
        Assert.Contains("resumes instead of restarting", vm.FxSoakTooltip);
        Assert.Contains("paper soak resumed — 3 signals carried over from the last session",
            vm.FxStatusText);

        // Once announced, a later refresh does not stomp the engine's own live
        // status with the same sentence again.
        vm.FxStatusText = "engine running";
        SetSeen(hosts[1], 2);
        vm.RefreshFxSoakBadgeIfRunning();
        Assert.Equal("engine running", vm.FxStatusText);

        // A build change restarts the bar on purpose — announced, not silent.
        SetSeen(hosts[0], 0);
        SetRestored(hosts[0], 0, droppedBuild: "0.9.0+deadbee");
        vm.RefreshFxSoakBadgeIfRunning();
        Assert.Contains("cleared because the build changed since 0.9.0+deadbee",
            vm.FxSoakTooltip);
        Assert.Contains("paper soak restarted — build changed since 0.9.0+deadbee",
            vm.FxStatusText);

        // Stopping the brain clears the pill AND its provenance claim.
        vm.ShutdownFxBrain();
        Assert.Equal(string.Empty, vm.FxSoakBadge);
        Assert.Equal(TerminalViewModel.SoakTooltipBase, vm.FxSoakTooltip);
    }

    [Fact]
    public void Brain_Runner_Change_Announces_Itself_So_The_Dashboard_Line_Refreshes()
    {
        // The dashboard soak line refreshes on tab change and a 60s poll, but
        // the startup auto-restore runs ~10s in — a resumed bar would stay
        // invisible for up to a minute. The VM announces each runner change so
        // the app can refresh the line the moment it matters.
        var vm = NewVm(new RouteHandler());
        var ticks = 0;
        vm.FxBrainRunnerChanged += () => ticks++;

        vm.ToggleFxBrainCommand.Execute(null);   // start (and the auto-restore path)
        Assert.Equal(1, ticks);

        vm.ToggleFxBrainCommand.Execute(null);   // stop
        Assert.Equal(2, ticks);

        vm.ShutdownFxBrain();                    // account-switch / shutdown path
        Assert.Equal(3, ticks);
    }

    [Fact]
    public void Soak_Provenance_Reports_An_Account_Switch_Too()
    {
        // The soak does not carry across an MT5 account switch: the bar is
        // the evidence base for the go-live gate on the account about to
        // trade. The switch must be stated, not read as lost progress.
        var handler = new RouteHandler();
        handler.Route("account", AccountJson);
        var vm = NewVm(handler);

        vm.ToggleFxBrainCommand.Execute(null);
        var hosts = vm.FxHost!.Hosts;

        SetSeen(hosts[0], 4);
        SetRestored(hosts[0], 0, droppedBuild: null);
        SetDroppedAccount(hosts[0], "201587365");

        // The dashboard line names the switch...
        Assert.Equal(
            "paper soak 4/20 · laggard EURUSD 0/10 · restarted — account changed since 201587365",
            DashboardViewModel.SoakNoteFor(vm.FxHost));

        // ...and so does the pill's tooltip and the one-shot status line.
        vm.RefreshFxSoakBadgeIfRunning();
        Assert.Equal("EURUSD 0/10 XAUUSDmicro 4/10", vm.FxSoakBadge);
        Assert.Contains("MT5 account changed since 201587365", vm.FxSoakTooltip);
        Assert.Contains("not carry across a switch", vm.FxSoakTooltip);
        Assert.Contains("paper soak restarted — account changed since 201587365",
            vm.FxStatusText);

        vm.ShutdownFxBrain();
    }

    [Fact]
    public void Dashboard_Soak_Timeline_Shows_The_Journaled_Provenance()
    {
        // The one-line note states where the bar came from; the timeline is
        // the audit trail behind it (every journaled restore/restart).
        var dashboard = new DashboardViewModel();
        dashboard.ConfigureFxSoakTimeline(() => new[]
        {
            new SoakTimeline.Row(System.DateTimeOffset.UtcNow,
                "paper soak restored 4/10 on EURUSD (build 0.9.0+abc123)", false),
            new SoakTimeline.Row(System.DateTimeOffset.UtcNow.AddHours(-1),
                "paper soak counters reset — account changed since 111", true),
        });

        Assert.True(dashboard.HasFxSoakTimeline);
        Assert.Equal(2, dashboard.FxSoakTimeline.Count);
        Assert.Contains("paper soak restored 4/10 on EURUSD", dashboard.FxSoakTimeline[0].Text);
        Assert.False(dashboard.FxSoakTimeline[0].IsRestart);
        Assert.True(dashboard.FxSoakTimeline[1].IsRestart);

        dashboard.ConfigureFxSoakTimeline(null);
        Assert.False(dashboard.HasFxSoakTimeline);
        Assert.Empty(dashboard.FxSoakTimeline);
    }
}
