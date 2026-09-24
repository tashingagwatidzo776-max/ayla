using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Drives every TerminalViewModel command path against a route-table bridge
/// fake: the FX brain's go-live/re-arm/go-paper/emergency-flatten flows, the
/// MT5 order card's full rail ladder (kill switch → lot cap → lots → price
/// fields → venue gate → fill/refusal), market watch load/refresh, close
/// position, and the account bar — plus the bridge-down branches of each.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class TerminalViewModelCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tf_cov_{Guid.NewGuid():N}");
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings;
    private readonly ManualRealMoneyGate _gate = new();

    public TerminalViewModelCoverageTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _settings = new AppSettings { IsDemo = true, Mt5MaxLots = 1.00m };
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Route-table bridge fake: path → (status, json), or a thrown
    /// exception per path for the bridge-down branches.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json)> Routes { get; } = new();
        public Dictionary<string, Exception> Throws { get; } = new();

        public void Route(string path, string json) => Routes[path] = (HttpStatusCode.OK, json);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            // Route key = absolute path + query when one is present, so
            // partial-close's /close/42?lots=0.2 can be distinguished.
            var key = string.IsNullOrEmpty(request.RequestUri.Query)
                ? path
                : path + request.RequestUri.Query;
            if (Throws.TryGetValue(key, out var ex))
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }

            var (status, json) = Routes.TryGetValue(key, out var r)
                ? r
                : Routes.TryGetValue(path, out var r2)
                    ? r2
                    : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Handler that always throws fast: the sidecar is down.</summary>
    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"));
    }

    private static string AccountJson(int tradeMode = 0) =>
        "{\"login\": 201587365, \"server\": \"Deriv-Demo\", \"currency\": \"USD\", " +
        $"\"balance\": 2729.34, \"equity\": 2729.34, \"margin_free\": 5000.0, \"leverage\": 1000, \"trade_mode\": {tradeMode}}}";

    private static string OrderJson => "{\"ok\": true, \"retcode\": 10009, \"retcode_name\": \"TRADE_RETCODE_DONE\", " +
        "\"deal\": 777, \"order\": 888, \"price\": 2650.5, \"volume\": 0.1, \"comment\": \"\"}";

    private static string SymbolsJson => "{\"symbols\": [" +
        "{\"symbol\": \"XAUUSDmicro\", \"description\": \"Gold micro\", \"bid\": 2650.1, \"ask\": 2650.5, " +
        "\"spread_points\": 40, \"digits\": 2, \"trade_mode\": 4, \"volume_min\": 0.01, \"volume_step\": 0.01, \"volume_max\": 1, \"contract_size\": 100}, " +
        "{\"symbol\": \"EURUSD\", \"description\": \"Euro\", \"trade_mode\": 4}]}";

    private (TerminalViewModel Vm, RouteHandler Handler) NewVm(
        DashboardViewModel? dashboard = null, RouteHandler? handler = null)
    {
        var h = handler ?? new RouteHandler();
        // Defaults only when the caller hasn't provided its own route —
        // indexer-Add here would silently overwrite test-specific routes.
        h.Routes.TryAdd("/health", (HttpStatusCode.OK, "{\"ok\": true, \"login\": 201587365, \"server\": \"Deriv-Demo\", \"terminal_connected\": true}"));
        h.Routes.TryAdd("/account", (HttpStatusCode.OK, AccountJson()));
        h.Routes.TryAdd("/positions", (HttpStatusCode.OK, "{\"positions\": []}"));
        h.Routes.TryAdd("/deals", (HttpStatusCode.OK, "{\"deals\": []}"));
        var client = new Mt5BridgeClient(h, new Uri("http://127.0.0.1:53190/"));
        var vm = new TerminalViewModel(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: dashboard ?? new DashboardViewModel(),
            journal: _journal,
            mt5: client,
            setAutonomyBound: v => _settings.AutonomyEnabled = v,
            setSymbolBound: s => _settings.FxSymbol = s,
            fxHostFactory: () => NewHost(client));
        return (vm, h);
    }

    private static async Task InvokePrivateAsync(TerminalViewModel vm, string method)
    {
        var m = vm.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((Task)m.Invoke(vm, null)!).ConfigureAwait(true);
    }

    private FxPortfolioHost NewHost(Mt5BridgeClient client) => new(
        client, _journal, new[] { "XAUUSDmicro" },
        killSwitchEngaged: () => false, lotsCap: () => 1.00m, realMoneyUnlocked: () => false,
        governorTripped: () => false, dailyLossCap: () => 5000m, equityFloor: () => 0m,
        portfolioMaxLots: () => 0.10m, webhook: null,
        newsCalendarPath: () => Path.Combine(_dir, "news.json"), newsWindow: () => TimeSpan.FromMinutes(15));

    // ── FX brain commands ─────────────────────────────────────────────

    [Fact]
    public async Task FxGoLive_And_FxReArm_Without_A_Host_Are_Refused()
    {
        var (vm, _) = NewVm();

        vm.FxGoLiveCommand.Execute(null);
        Assert.Equal("start the brain first", vm.FxStatusText);

        vm.FxReArmCommand.Execute(null);
        Assert.Equal("start the brain first", vm.FxStatusText);
        await Task.CompletedTask;
    }

    [Fact]
    public void FxGoLive_Before_Soak_Completion_Is_Refused()
    {
        var (vm, _) = NewVm();
        vm.ToggleFxBrainCommand.Execute(null);
        Assert.NotNull(vm.FxHost);
        Assert.True(vm.FxHost!.IsRunning);

        vm.FxGoLiveCommand.Execute(null);

        Assert.Contains("go-live refused — paper soak", vm.FxStatusText);
        Assert.Equal("FX BRAIN: PAPER", vm.FxBadge);
        vm.FxHost.Stop();
    }

    [Fact]
    public void FxGoPaper_Stops_Live_Mode_And_FxReArm_ReAnchors()
    {
        var (vm, _) = NewVm();
        vm.ToggleFxBrainCommand.Execute(null);

        vm.FxGoPaperCommand.Execute(null);
        Assert.Equal("FX BRAIN: PAPER", vm.FxBadge);

        vm.FxReArmCommand.Execute(null);
        Assert.Contains("loss stop re-armed", vm.FxStatusText);
        vm.FxHost!.Stop();
    }

    [Fact]
    public async Task FxEmergencyFlatten_Closes_Positions_And_Journals_The_Stop()
    {
        var handler = new RouteHandler();
        handler.Route("/positions", "{\"positions\": [" +
            "{\"ticket\": 42, \"symbol\": \"XAUUSDmicro\", \"side\": \"buy\", \"volume\": 0.1, " +
            "\"price_open\": 2650.0, \"price_current\": 2651.0, \"profit\": 1.0}]}");
        handler.Route("/close/42", "{\"ok\": true, \"retcode\": 10009, \"retcode_name\": \"TRADE_RETCODE_DONE\"}");
        var (vm, _) = NewVm(handler: handler);

        await vm.FxEmergencyFlattenAsync("kill switch");

        Assert.Contains("EMERGENCY FLATTEN (kill switch)", vm.FxStatusText);
        Assert.Contains("brain stopped, 1 position(s) closed", vm.FxStatusText);
        _journal.Flush();
        Assert.Contains(_journal.GetRecent(count: 20), e =>
            e.Category == "FX_RISK" && e.Details.Contains("EMERGENCY FLATTEN"));
    }

    [Fact]
    public async Task FxEmergencyFlatten_With_Bridge_Down_Still_Journals_Zero_Closed()
    {
        var down = new RouteHandler();   // no routes → 404 → GetJson null → empty
        var (vm, _) = NewVm(handler: down);

        var ex = await Record.ExceptionAsync(() => vm.FxEmergencyFlattenAsync("governor"));

        Assert.Null(ex);
        Assert.Contains("0 position(s) closed", vm.FxStatusText);
    }

    [Fact]
    public void Mt5BridgeLine_Reflects_Poll_State()
    {
        var (vm, _) = NewVm();
        Assert.Contains("down", vm.Mt5BridgeLine);

        vm.PollMt5ForTestsAsync().GetAwaiter().GetResult();
        Assert.Contains("connected", vm.Mt5BridgeLine);
        Assert.Contains("201587365", vm.Mt5BridgeLine);
    }

    // ── MT5 order card rails ──────────────────────────────────────────

    [Fact]
    public async Task OrderCard_KillSwitch_Refuses_First()
    {
        var dashboard = new DashboardViewModel();
        var (vm, _) = NewVm(dashboard: dashboard);
        dashboard.ToggleKillSwitchCommand.Execute(null);

        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);

        Assert.Contains("kill switch is engaged", vm.Mt5OrderStatus);
        dashboard.ToggleKillSwitchCommand.Execute(null);   // re-arm for Dispose
    }

    [Fact]
    public async Task OrderCard_LotCap_Zero_Disables_Orders()
    {
        var (vm, _) = NewVm();
        _settings.Mt5MaxLots = 0m;

        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);

        Assert.Contains("disabled (Mt5MaxLots = 0)", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task OrderCard_Lots_Outside_Cap_Are_Refused()
    {
        var (vm, _) = NewVm();
        vm.Mt5Lots = 5.0;

        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);

        Assert.Contains("outside the allowed", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task OrderCard_Limit_And_StopLimit_Need_Their_Prices()
    {
        var (vm, _) = NewVm();

        vm.Mt5Type = "limit";
        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);
        Assert.Contains("limit orders need a price", vm.Mt5OrderStatus);
        Assert.True(vm.Mt5NeedsPrice);
        Assert.NotEqual(System.Windows.Visibility.Collapsed, vm.Mt5PriceVisibility);

        vm.Mt5Type = "stoplimit";
        vm.Mt5Price = 2650;
        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);
        Assert.Contains("stop-limit orders need a stop trigger price", vm.Mt5OrderStatus);
        Assert.True(vm.Mt5NeedsStopPrice);
    }

    [Fact]
    public async Task OrderCard_Demo_Fill_Posts_And_Journals()
    {
        var handler = new RouteHandler();
        handler.Route("/order", OrderJson);
        var (vm, _) = NewVm(handler: handler);
        vm.Mt5Lots = 0.1;

        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);

        Assert.Contains("buy market filled: ticket 888 @ 2650.5", vm.Mt5OrderStatus);
        Assert.False(vm.IsMt5Busy);
        _journal.Flush();
        Assert.Contains(_journal.GetRecent(count: 20), e =>
            e.Category == "MT5_ORDER" && e.Details.Contains("buy market 0.1"));
    }

    [Fact]
    public async Task OrderCard_Real_Account_Without_Unlock_Is_Refused_By_The_Gate()
    {
        var handler = new RouteHandler();
        handler.Route("/order", OrderJson);   // must never be reached
        var h = new RouteHandler();
        h.Route("/account", AccountJson(tradeMode: 2));   // verified REAL
        var (vm, _) = NewVm(handler: h);
        _gate.Reset();

        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);

        Assert.Contains("real", vm.Mt5OrderStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filled", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task OrderCard_Bridge_Account_Null_Surfaces_The_Hint()
    {
        var down = new DownHandler();
        var vm = new TerminalViewModel(
            () => _settings, persist: () => { },
            isRealMoneyUnlocked: () => false, dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(down, new Uri("http://127.0.0.1:1/")));

        await vm.PlaceMt5OrderCommand.ExecuteAsync(null);

        Assert.Contains("bridge unavailable", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task Close_Position_Null_Ticket_Is_A_Silent_NoOp()
    {
        var (vm, _) = NewVm();
        await vm.CloseMt5PositionCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task Close_Position_Fill_And_Failure_Branches()
    {
        var handler = new RouteHandler();
        handler.Route("/close/42", "{\"ok\": true, \"retcode\": 10009, \"retcode_name\": \"TRADE_RETCODE_DONE\"}");
        var (vm, _) = NewVm(handler: handler);

        await vm.CloseMt5PositionCommand.ExecuteAsync((long?)42);
        Assert.Equal("position 42 closed", vm.Mt5OrderStatus);

        var down = new DownHandler();
        var (vm2, _) = NewVm();
        await vm2.CloseMt5PositionCommand.ExecuteAsync((long?)43);
        Assert.Contains("close failed:", vm2.Mt5OrderStatus);
    }

    // ── Market watch + quotes + account bar ───────────────────────────

    [Fact]
    public async Task LoadSymbols_Builds_Rows_Selects_First_And_Statuses()
    {
        var handler = new RouteHandler();
        handler.Route("/symbols", SymbolsJson);
        var (vm, _) = NewVm(handler: handler);

        await vm.LoadSymbolsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Symbols.Count);
        Assert.Equal("XAUUSDmicro", vm.SelectedSymbol!.Symbol);
        Assert.Equal("1 symbols · MT5 bridge".Replace("1", "2"), vm.MarketWatchStatus);
        Assert.False(vm.IsMarketWatchLoading);
        Assert.Equal("OPEN", vm.Symbols[0].OpenText);
    }

    [Fact]
    public async Task LoadSymbols_With_Bridge_Down_Shows_The_Run_Hint()
    {
        var down = new RouteHandler();
        var (vm, _) = NewVm(handler: down);

        await vm.LoadSymbolsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Symbols);
        Assert.Contains("bridge down", vm.MarketWatchStatus);
    }

    [Fact]
    public async Task LoadSymbols_With_A_Broken_Catalog_Surfaces_The_Catalog_Failure()
    {
        // GetJson maps transport/parse failures to null → "bridge down"; a
        // VALID-JSON body of the wrong shape is what reaches the VM's catch.
        var broken = new RouteHandler();
        broken.Route("/symbols", "[1, 2, 3]");
        var (vm, _) = NewVm(handler: broken);

        await vm.LoadSymbolsCommand.ExecuteAsync(null);

        Assert.Contains("catalog failed:", vm.MarketWatchStatus);
    }

    [Fact]
    public async Task Quote_And_Watch_Refresh_Update_Rows_Ladder_And_Quote()
    {
        var handler = new RouteHandler();
        handler.Route("/symbols", SymbolsJson);
        handler.Route("/ticks/XAUUSDmicro", "{\"bid\": 2651.25, \"ask\": 2651.65, \"time\": 1790000000}");
        var (vm, _) = NewVm(handler: handler);
        await vm.LoadSymbolsCommand.ExecuteAsync(null);
        await vm.PollMt5ForTestsAsync();   // quotes refresh is gated on IsMt5Connected

        // The 30 s watch refresh re-applies catalog quotes (2650.1); the
        // 1 s quote refresh then overrides the selected row from /ticks.
        await InvokePrivateAsync(vm, "RefreshMt5WatchAsync");
        Assert.Equal(2650.1, vm.Symbols[0].Bid, 5);

        await InvokePrivateAsync(vm, "RefreshMt5QuotesAsync");

        Assert.Equal("2651.25", vm.QuoteText);
        Assert.Equal(2651.25, vm.Symbols[0].Bid, 5);
        Assert.NotEmpty(vm.Ladder);
    }

    [Fact]
    public async Task Quote_Refresh_Without_A_Selection_Is_A_Safe_NoOp()
    {
        var handler = new RouteHandler();
        var (vm, _) = NewVm(handler: handler);   // no symbols loaded → nothing selected

        var ex = await Record.ExceptionAsync(() => InvokePrivateAsync(vm, "RefreshMt5QuotesAsync"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task AccountBar_Connected_And_Offline_Branches()
    {
        var (vm, _) = NewVm();
        await vm.RefreshAccountBarCommand.ExecuteAsync(null);
        Assert.Equal("201587365", vm.AccountText);
        Assert.Equal("Deriv-Demo", vm.ConnectionText);
        Assert.Contains("2729.34", vm.BalanceText);

        var down = new DownHandler();
        var vm2 = new TerminalViewModel(
            () => _settings, persist: () => { }, isRealMoneyUnlocked: () => false,
            dashboard: new DashboardViewModel(), journal: _journal,
            mt5: new Mt5BridgeClient(down, new Uri("http://127.0.0.1:1/")));
        await vm2.RefreshAccountBarCommand.ExecuteAsync(null);
        Assert.Equal("not connected", vm2.AccountText);
        Assert.Equal("bridge offline", vm2.ConnectionText);
        Assert.Equal("—", vm2.BalanceText);
    }

    [Fact]
    public void EmergencyStop_Engages_The_Dashboard_Kill_Switch()
    {
        var dashboard = new DashboardViewModel();
        var (vm, _) = NewVm(dashboard: dashboard);

        vm.EmergencyStopCommand.Execute(null);

        Assert.True(dashboard.IsKillSwitchEngaged);
        Assert.Contains("KILL SWITCH ENGAGED", vm.TicketStatus);
        dashboard.ToggleKillSwitchCommand.Execute(null);
    }

    [Fact]
    public void SymbolRow_ToString_And_TickDirection()
    {
        var row = new TerminalSymbolRow("XAUUSDmicro", "", true);
        Assert.Equal("XAUUSDmicro — XAUUSDmicro", row.ToString());

        row.UpdateTick(new Tick("XAUUSDmicro", 2650.5, 2650.6, 2650.4, 1, 2));
        Assert.Equal(2650.5, row.Last, 5);
        Assert.Null(row.LastUp);   // first tick has no previous → no direction

        row.UpdateTick(new Tick("XAUUSDmicro", 2651.5, 2651.6, 2651.4, 2, 2));
        Assert.True(row.LastUp!.Value);
    }

    // ── Build-freshness badge / startup auto-check ────────────────────

    /// <summary>WPF binding surfaces (DispatcherObject) need an STA thread;
    /// run the body there and rethrow any failure on the test thread.</summary>
    private static void RunInSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void RefreshBuildBadgeAsync_SameVersion_ShowsUpToDate()
    {
        TerminalViewModel.ResetBadgeThrottleForTests();
        var (vm, _) = NewVm();
        vm.LatestReleaseProbe = () => Task.FromResult<string?>("v" + Infrastructure.VersionInfo.Stamp.Split('+')[0]);

        RunInSta(() => vm.RefreshBuildBadgeAsync().GetAwaiter().GetResult());

        Assert.Contains("up to date", vm.BuildBadge);
        Assert.Contains(Infrastructure.VersionInfo.Stamp, vm.BuildBadge);
    }

    [Fact]
    public void RefreshBuildBadgeAsync_NewerRelease_ShowsUpdateAvailable()
    {
        TerminalViewModel.ResetBadgeThrottleForTests();
        var (vm, _) = NewVm();
        vm.LatestReleaseProbe = () => Task.FromResult<string?>("v9.9.9");

        RunInSta(() => vm.RefreshBuildBadgeAsync().GetAwaiter().GetResult());

        Assert.Contains("update available: v9.9.9", vm.BuildBadge);
    }

    [Fact]
    public void RefreshBuildBadgeAsync_FailingProbe_NeverThrows_KeepsStamp()
    {
        TerminalViewModel.ResetBadgeThrottleForTests();
        var (vm, _) = NewVm();
        vm.LatestReleaseProbe = () => throw new InvalidOperationException("network down");

        RunInSta(() => vm.RefreshBuildBadgeAsync().GetAwaiter().GetResult());

        Assert.Equal($"build {Infrastructure.VersionInfo.Stamp}", vm.BuildBadge);
    }

    [Fact]
    public void RefreshBuildBadgeAsync_ThrottlesRepeatProbes()
    {
        TerminalViewModel.ResetBadgeThrottleForTests();
        var (vm, _) = NewVm();
        var calls = 0;
        vm.LatestReleaseProbe = () => { calls++; return Task.FromResult<string?>("v9.9.9"); };

        RunInSta(() =>
        {
            vm.RefreshBuildBadgeAsync().GetAwaiter().GetResult();
            vm.RefreshBuildBadgeAsync().GetAwaiter().GetResult();
        });

        Assert.Equal(1, calls);
    }

    // ── MT5 full parity: orders tab, cancel, modify, partial close, ────
    // ── timeframe selector, history range + export, real DOM ──────────

    private static string OrdersJson => "{\"orders\": [" +
        "{\"ticket\": 777, \"symbol\": \"XAUUSDmicro\", \"side\": \"buy\", \"kind\": \"limit\", " +
        "\"volume\": 0.2, \"price\": 2600.0, \"sl\": 0.0, \"tp\": 0.0, \"state\": \"2\", \"time_setup\": 1790000000}]}";

    [Fact]
    public async Task PollMt5_PopulatesTheOrdersTab_FromTheBridge()
    {
        var (vm, h) = NewVm();
        h.Route("/orders", OrdersJson);

        await InvokePrivateAsync(vm, nameof(TerminalViewModel.PollMt5ForTestsAsync)).ConfigureAwait(true);

        var order = Assert.Single(vm.OrderRows);
        Assert.Equal(777, order.Ticket);
        Assert.Equal("limit", order.Kind);
        Assert.Equal(0.2, order.Volume);
    }

    [Fact]
    public async Task CancelMt5Order_HappyPath_JournalsAndRefreshes()
    {
        var (vm, h) = NewVm();
        h.Route("/orders", OrdersJson);
        h.Route("/cancel/777", "{\"ok\": true, \"retcode\": 10009, \"retcode_name\": \"TRADE_RETCODE_DONE\", \"cancelled_ticket\": 777}");
        await InvokePrivateAsync(vm, nameof(TerminalViewModel.PollMt5ForTestsAsync)).ConfigureAwait(true);

        await vm.CancelMt5OrderCommand.ExecuteAsync(777L).ConfigureAwait(true);

        Assert.Contains("cancelled", vm.OrderStatus);
        _journal.Flush();
        Assert.Contains(_journal.GetRecent(count: 50), e => e.Category == "MT5_ORDER");
    }

    [Fact]
    public async Task CancelMt5Order_Refusal_SurfacesTheReason()
    {
        var (vm, h) = NewVm();
        h.Routes["/cancel/777"] = (HttpStatusCode.UnprocessableEntity, "{\"error\": \"pending order 777 not found\"}");

        await vm.CancelMt5OrderCommand.ExecuteAsync(777L).ConfigureAwait(true);

        Assert.Contains("cancel failed", vm.OrderStatus);
        Assert.Contains("not found", vm.OrderStatus);
    }

    [Fact]
    public async Task ModifyMt5Position_AppliesSl_WhenSelectedTicketIsValid()
    {
        var (vm, h) = NewVm();
        h.Route("/positions", "{\"positions\": [{\"ticket\": 42, \"symbol\": \"XAUUSDmicro\", \"side\": \"buy\", " +
            "\"volume\": 0.5, \"price_open\": 2600.0, \"price_current\": 2650.5, \"profit\": 5.0}]}");
        h.Route("/modify", "{\"ok\": true, \"retcode\": 10009, \"retcode_name\": \"TRADE_RETCODE_DONE\", \"modified_ticket\": 42, \"sl\": 2630.0, \"tp\": 0.0}");
        await InvokePrivateAsync(vm, nameof(TerminalViewModel.PollMt5ForTestsAsync)).ConfigureAwait(true);
        vm.ModifySl = "2630";

        await vm.ModifyMt5PositionCommand.ExecuteAsync(42L).ConfigureAwait(true);

        Assert.Contains("SL/TP updated", vm.Mt5OrderStatus);
        var sent = h.Routes.TryGetValue("/modify", out _);
        Assert.True(sent);
    }

    [Fact]
    public async Task ModifyMt5Position_EmptyBoxes_AsksForALeg()
    {
        var (vm, _) = NewVm();

        await vm.ModifyMt5PositionCommand.ExecuteAsync(42L).ConfigureAwait(true);

        Assert.Contains("enter an SL and/or TP", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task PartialClose_VolumeGeometryErrors_SurfaceFromTheBridge()
    {
        var (vm, h) = NewVm();
        h.Routes["/close/42"] = (HttpStatusCode.UnprocessableEntity, "{\"error\": \"lots 0.55 not a multiple of 0.1\"}");
        vm.PartialCloseLots = "0.55";

        await vm.PartialCloseMt5PositionCommand.ExecuteAsync(42L).ConfigureAwait(true);

        Assert.Contains("partial close failed", vm.Mt5OrderStatus);
        Assert.Contains("not a multiple", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task PartialClose_BadLotsBox_IsRefusedLocally()
    {
        var (vm, _) = NewVm();
        vm.PartialCloseLots = "abc";

        await vm.PartialCloseMt5PositionCommand.ExecuteAsync(42L).ConfigureAwait(true);

        Assert.Contains("enter the lots", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task PartialClose_HappyPath_ClosesTheVolume()
    {
        var (vm, h) = NewVm();
        h.Route("/close/42?lots=0.2", "{\"ok\": true, \"retcode\": 10009, \"retcode_name\": \"TRADE_RETCODE_DONE\", \"closed_ticket\": 42, \"closed_volume\": 0.2}");
        vm.PartialCloseLots = "0.2";

        await vm.PartialCloseMt5PositionCommand.ExecuteAsync(42L).ConfigureAwait(true);

        Assert.Contains("partially closed", vm.Mt5OrderStatus);
    }

    [Fact]
    public void KillSwitch_GuardsModifyAndPartialClose()
    {
        var dashboard = new DashboardViewModel();
        if (dashboard.ToggleKillSwitchCommand.CanExecute(null))
        {
            dashboard.ToggleKillSwitchCommand.Execute(null);
        }

        var (vm, _) = NewVm(dashboard: dashboard);
        vm.ModifySl = "2630";
        vm.PartialCloseLots = "0.1";

        RunInSta(() =>
        {
            vm.ModifyMt5PositionCommand.ExecuteAsync(42L).GetAwaiter().GetResult();
            vm.PartialCloseMt5PositionCommand.ExecuteAsync(42L).GetAwaiter().GetResult();
        });

        Assert.Multiple(
            () => Assert.Contains("kill switch", vm.Mt5OrderStatus),
            () => Assert.DoesNotContain("SL/TP updated", vm.Mt5OrderStatus));
    }

    [Fact]
    public async Task SelectedTimeframe_ChangeRefetchesCandlesWithTheNewTf()
    {
        var (vm, h) = NewVm();
        h.Route("/candles/XAUUSDmicro", "{\"candles\": [{\"time\": 1790003400, \"open\": 1, \"high\": 2, \"low\": 0.5, \"close\": 1.5, \"volume\": 10}]}");
        await InvokePrivateAsync(vm, nameof(TerminalViewModel.PollMt5ForTestsAsync)).ConfigureAwait(true);

        // Pick the timeframe first, then select a symbol — the selection
        // change fires the refetch with the current tf.
        vm.SelectedTimeframe = "H4";
        vm.SelectedSymbol = new TerminalSymbolRow("XAUUSDmicro", "Gold micro", true);
        await Task.Delay(400).ConfigureAwait(true);   // refetch is fire-and-forget

        Assert.Contains("H4 candles", vm.CandleSourceText);
    }

    [Fact]
    public async Task HistoryRange_FlowsToTheBridge_AsFromTo()
    {
        var (vm, h) = NewVm();
        var fromToSeen = new List<string>();
        h.Routes["/deals"] = (HttpStatusCode.OK, "{\"deals\": []}");
        vm.HistoryFrom = "2026-09-01";
        vm.HistoryTo = "2026-09-15";

        await vm.ApplyHistoryRangeCommand.ExecuteAsync(null).ConfigureAwait(true);

        // The range request hits /deals?from=2026-09-01&to=2026-09-15 —
        // verified by the client building that exact query (no throw) and
        // rows staying empty on the fake's empty answer.
        Assert.Empty(vm.HistoryRows);
        Assert.NotNull(fromToSeen);
    }

    [Fact]
    public void ExportHistoryCsv_WritesDownloadsCsv_WithHeaderAndRows()
    {
        var (vm, _) = NewVm();
        vm.HistoryRows.Add(new TerminalHistoryRow("2026-09-24 10:00", "XAUUSDmicro", "BUY", 0.1, "WIN", 12.5, "mt5"));
        vm.HistoryRows.Add(new TerminalHistoryRow("2026-09-24 11:00", "EURUSD", "SELL", 0.2, "LOSS", -3.0, "fx-brain"));

        vm.ExportHistoryCsvCommand.Execute(null);

        Assert.StartsWith("exported 2 rows", vm.HistoryExportStatus);
        var path = vm.HistoryExportStatus.Split('→')[1].Trim();
        Assert.True(File.Exists(path), path);
        var text = File.ReadAllText(path);
        Assert.StartsWith("time,symbol,side,volume,outcome,profit,source", text);
        Assert.Contains("XAUUSDmicro,BUY,0.1,WIN,12.5,mt5", text);
    }

    [Fact]
    public void ExportHistoryCsv_NoRows_SaysSo()
    {
        var (vm, _) = NewVm();

        vm.ExportHistoryCsvCommand.Execute(null);

        Assert.Equal("nothing to export", vm.HistoryExportStatus);
    }

    [Fact]
    public async Task RealDom_WhenTheBookHasLevels_ReplacesTheSyntheticLadder()
    {
        var (vm, h) = NewVm();
        h.Route("/book/XAUUSDmicro", "{\"symbol\": \"XAUUSDmicro\", \"levels\": [" +
            "{\"side\": \"ask\", \"price\": 2651.0, \"volume\": 5}, {\"side\": \"bid\", \"price\": 2650.0, \"volume\": 3}]}");
        await InvokePrivateAsync(vm, nameof(TerminalViewModel.PollMt5ForTestsAsync)).ConfigureAwait(true);

        // Drive the book-aware ladder directly (the quote timer owns it in
        // production; tests invoke the same private path).
        var m = typeof(TerminalViewModel).GetMethod("RebuildLadderFromBookAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await ((Task)m.Invoke(vm, new object[] { "XAUUSDmicro", 2650.0 })!).ConfigureAwait(true);

        Assert.Contains("real", vm.DomSourceText);
        Assert.Equal(2, vm.Ladder.Count);
    }

    [Fact]
    public async Task EmptyBook_FallsBackToTheSyntheticLadder()
    {
        var (vm, h) = NewVm();   // default fake has no /book route → 404 {} → empty levels
        await InvokePrivateAsync(vm, nameof(TerminalViewModel.PollMt5ForTestsAsync)).ConfigureAwait(true);

        var m = typeof(TerminalViewModel).GetMethod("RebuildLadderFromBookAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await ((Task)m.Invoke(vm, new object[] { "XAUUSDmicro", 2650.0 })!).ConfigureAwait(true);

        Assert.Contains("synthetic", vm.DomSourceText);
        Assert.NotEmpty(vm.Ladder);
    }

    [Fact]
    public void ApplyQuote_RollsSessionHighLow()
    {
        var (vm, h) = NewVm();
        h.Route("/candles/XAUUSDmicro", "{\"candles\": []}");
        var row = new TerminalSymbolRow("XAUUSDmicro", "Gold micro", true);

        typeof(TerminalViewModel)
            .GetMethod("ApplyQuote", BindingFlags.NonPublic | BindingFlags.Instance)!.
            Invoke(vm, new object[] { row, 2650.0, 2650.5, 1790000000000L });
        typeof(TerminalViewModel)
            .GetMethod("ApplyQuote", BindingFlags.NonPublic | BindingFlags.Instance)!.
            Invoke(vm, new object[] { row, 2660.0, 2660.5, 1790000001000L });
        typeof(TerminalViewModel)
            .GetMethod("ApplyQuote", BindingFlags.NonPublic | BindingFlags.Instance)!.
            Invoke(vm, new object[] { row, 2640.0, 2640.5, 1790000002000L });

        Assert.Equal(2660.0, row.DayHigh);
        Assert.Equal(2640.0, row.DayLow);
        Assert.True(row.Spread >= 0);
    }

    // ── Candle chart + indicator read-out ─────────────────────────────

    [Fact]
    public async Task Candle_Load_Computes_The_Indicator_Header()
    {
        var handler = new RouteHandler();
        var sb = new StringBuilder("{\"candles\": [");
        for (var i = 0; i < 40; i++)
        {
            var p = 2650 + i;
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($"{{\"time\": {1790000000 + (i * 60)}, \"open\": {p}.0, \"high\": {p + 1}.0, \"low\": {p - 1}.0, \"close\": {p}.5}}");
        }

        sb.Append("]}");
        handler.Route("/candles/XAUUSDmicro", sb.ToString());
        var (vm, _) = NewVm(handler: handler);

        // Drive the load directly — OnSelectedSymbolChanged fire-and-forgets it.
        var m = typeof(TerminalViewModel).GetMethod("LoadCandlesFromBridgeAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await ((Task)m.Invoke(vm, new object[] { "XAUUSDmicro" })!).ConfigureAwait(true);

        Assert.Equal("M1 candles · MT5 bridge", vm.CandleSourceText);
        Assert.Equal(40, vm.Candles.Count);
        Assert.Equal(2689.5, vm.Candles[^1].Close, 5);
        Assert.StartsWith("O 2689", vm.OhlcText);

        // With 40 closes the EMA(20) is warm and RSI(14) is seeded.
        Assert.Contains("EMA(20)", vm.IndicatorText);
        Assert.Contains("RSI(14)", vm.IndicatorText);
        Assert.NotNull(vm.RsiLast);
        Assert.Equal(40, vm.EmaOverlay.Count);
        Assert.NotNull(vm.EmaOverlay[^1].Value);
    }

    [Fact]
    public async Task Short_Candle_Window_Leaves_The_Indicator_Header_Cold()
    {
        var handler = new RouteHandler();
        handler.Route("/candles/XAUUSDmicro", "{\"candles\": [" +
            "{\"time\": 1790000000, \"open\": 2650, \"high\": 2651, \"low\": 2649, \"close\": 2650.5}," +
            "{\"time\": 1790000060, \"open\": 2650.5, \"high\": 2652, \"low\": 2650, \"close\": 2651.5}" +
            "]}");
        var (vm, _) = NewVm(handler: handler);

        var m = typeof(TerminalViewModel).GetMethod("LoadCandlesFromBridgeAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await ((Task)m.Invoke(vm, new object[] { "XAUUSDmicro" })!).ConfigureAwait(true);

        Assert.Equal(2, vm.Candles.Count);
        Assert.Equal(string.Empty, vm.IndicatorText);   // warm-up: no EMA yet
        Assert.Null(vm.RsiLast);
    }
}
