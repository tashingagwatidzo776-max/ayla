using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The DON G FX Terminal (MT5-only): the brain's autonomy ON/OFF switch must
/// persist through the real settings pipeline, the MT5 order card must refuse
/// through every rail in the same order the other trade paths enforce them,
/// the account bar and deal history must come from the bridge, and every
/// bridge-down path must degrade to an actionable message — all against a
/// stub handler or an in-process fake sidecar, no network.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class TerminalViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings;
    private readonly ManualRealMoneyGate _gate = new();
    private int _saves;

    public TerminalViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_term_{Guid.NewGuid():N}");
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

    private TerminalViewModel CreateVm(DashboardViewModel? dashboard = null,
        Mt5BridgeClient? mt5Client = null,
        Func<FxPortfolioHost?>? fxHostFactory = null)
    {
        return new TerminalViewModel(
            () => _settings,
            persist: () => _saves++,
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: dashboard ?? new DashboardViewModel(),
            journal: _journal,
            mt5: mt5Client ?? new Mt5BridgeClient(new StubHandler(), new Uri("http://127.0.0.1:1/")),
            setAutonomyBound: v => _settings.AutonomyEnabled = v,
            setSymbolBound: s => _settings.FxSymbol = s,
            fxHostFactory: fxHostFactory);
    }

    // ── Shutdown path ───────────────────────────────────────────────

    private FxPortfolioHost NewFxPortfolio() =>
        new(
            new Mt5BridgeClient(new StubHandler(), new Uri("http://127.0.0.1:1/")),
            _journal,
            new[] { "XAUUSDmicro" },
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            governorTripped: () => false,
            dailyLossCap: () => 5000m,
            equityFloor: () => 0m,
            portfolioMaxLots: () => 0.10m,
            webhook: null,
            newsCalendarPath: () => Path.Combine(_dir, "news-calendar.json"),
            newsWindow: () => TimeSpan.FromMinutes(15));

    [Fact]
    public void ShutdownFxBrain_StopsAndDisposesThePortfolioHost()
    {
        var host = NewFxPortfolio();
        var vm = CreateVm(fxHostFactory: () => host);

        vm.ToggleFxBrainCommand.Execute(null);
        Assert.True(host.IsRunning);

        vm.ShutdownFxBrain();

        Assert.False(host.IsRunning);
        Assert.Equal("FX BRAIN: OFF", vm.FxBadge);
        vm.ShutdownFxBrain();   // idempotent — teardown must never throw
    }

    // ── Brain switch ───────────────────────────────────────────────

    [Fact]
    public void BrainOn_PersistsAndRaisesState()
    {
        var vm = CreateVm();
        Assert.False(_settings.AutonomyEnabled);
        Assert.False(vm.BrainIsOn);
        Assert.Contains("OFF", vm.BrainStateText);

        vm.TurnBrainOnCommand.Execute(null);

        Assert.True(_settings.AutonomyEnabled);
        Assert.True(vm.BrainIsOn);
        Assert.Contains("ON", vm.BrainStateText);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void BrainSwitch_BridgesIntoSettingsUiBoundFields()
    {
        // The settings UI's bound fields are the save source of truth: the
        // bridge must keep them in sync or the next settings save clobbers
        // the flip back (the Sep-21 "brain off again" bug).
        bool? bridged = null;
        var vm = new TerminalViewModel(
            () => _settings,
            persist: () => _saves++,
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(new StubHandler(), new Uri("http://127.0.0.1:1/")),
            setAutonomyBound: v => bridged = v);

        vm.TurnBrainOnCommand.Execute(null);
        Assert.True(bridged);
        vm.TurnBrainOffCommand.Execute(null);
        Assert.False(bridged);
    }

    [Fact]
    public void BrainOff_RoundTripsAndClears()
    {
        var vm = CreateVm();
        vm.TurnBrainOnCommand.Execute(null);
        Assert.True(vm.BrainIsOn);

        vm.TurnBrainOffCommand.Execute(null);

        Assert.False(_settings.AutonomyEnabled);
        Assert.False(vm.BrainIsOn);
        Assert.Contains("OFF", vm.BrainStateText);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void SelectingSymbol_SyncsSharedSettingsAndPersists()
    {
        // The Market Watch selection drives the FX brain's fallback symbol;
        // the settings-VM bound field must be bridged so the next settings
        // save doesn't clobber it.
        string? bridged = null;
        var vm = new TerminalViewModel(
            () => _settings,
            persist: () => _saves++,
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(new StubHandler(), new Uri("http://127.0.0.1:1/")),
            setSymbolBound: s => bridged = s);

        vm.SelectedSymbol = new TerminalSymbolRow("XAUUSDmicro", "Gold Spot", true);

        Assert.Equal("XAUUSDmicro", _settings.FxSymbol);
        Assert.Equal("XAUUSDmicro", bridged);
        Assert.Equal(1, _saves);
    }

    // ── MT5 order rails (refusals never reach the bridge) ─────────

    [Fact]
    public void PlaceMt5Order_KillSwitchEngaged_IsRefused()
    {
        var dashboard = new DashboardViewModel();
        dashboard.ToggleKillSwitchCommand.Execute(null);
        var vm = CreateVm(dashboard: dashboard);

        vm.PlaceMt5OrderCommand.Execute(null);

        Assert.Contains("kill switch", vm.Mt5OrderStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmergencyStop_LatchesTheGlobalKillSwitch()
    {
        var dashboard = new DashboardViewModel();
        var vm = CreateVm(dashboard: dashboard);
        Assert.False(dashboard.IsKillSwitchEngaged);

        vm.EmergencyStopCommand.Execute(null);

        Assert.True(dashboard.IsKillSwitchEngaged);
        Assert.Contains("KILL SWITCH", vm.TicketStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaceMt5Order_ZeroLotCap_IsRefused()
    {
        _settings.Mt5MaxLots = 0m; // fail-closed: MT5 placement disabled
        var vm = CreateVm();

        vm.PlaceMt5OrderCommand.Execute(null);

        Assert.Contains("disabled", vm.Mt5OrderStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaceMt5Order_LotsOverCap_IsRefused()
    {
        _settings.Mt5MaxLots = 1m;
        var vm = CreateVm();
        vm.Mt5Lots = 5.0;

        vm.PlaceMt5OrderCommand.Execute(null);

        Assert.Contains("outside the allowed", vm.Mt5OrderStatus);
    }

    [Fact]
    public void PlaceMt5Order_BridgeDown_ShowsHint()
    {
        _settings.Mt5MaxLots = 1m;
        var vm = CreateVm();
        vm.Mt5Lots = 0.1;

        vm.PlaceMt5OrderCommand.Execute(null);

        // The client at 127.0.0.1:1 refuses instantly — the message must be
        // the actionable hint, not an unhandled crash.
        Assert.Contains("bridge unavailable", vm.Mt5OrderStatus, StringComparison.OrdinalIgnoreCase);
    }

    // ── Bridge polling / account bar / history ────────────────────

    [Fact]
    public async Task Mt5Poll_WhenSidecarUp_ReflectsAccountAndPositions()
    {
        using var http = new FakeMt5SidecarHttp();
        var vm = CreateVm(mt5Client: new Mt5BridgeClient(
            http, new Uri($"http://127.0.0.1:{http.Port}/")));

        await vm.PollMt5ForTestsAsync();

        Assert.True(vm.IsMt5Connected);
        Assert.Contains("201587365", vm.Mt5AccountText);
        Assert.Equal("MT5 bridge: connected", vm.Mt5StatusText);
    }

    [Fact]
    public async Task Mt5Poll_WhenSidecarDown_ReportsDown()
    {
        var vm = CreateVm();

        await vm.PollMt5ForTestsAsync();

        Assert.False(vm.IsMt5Connected);
        Assert.Contains("bridge down", vm.Mt5StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccountBar_ReadsLoginServerBalanceFromBridge()
    {
        using var http = new FakeMt5SidecarHttp();
        var vm = CreateVm(mt5Client: new Mt5BridgeClient(
            http, new Uri($"http://127.0.0.1:{http.Port}/")));

        await vm.RefreshAccountBarCommand.ExecuteAsync(null);

        Assert.Equal("201587365", vm.AccountText);
        Assert.Equal("Deriv-Demo", vm.ConnectionText);
        Assert.Equal("2610.55 USD", vm.BalanceText);
    }

    [Fact]
    public async Task RefreshAccountBar_BridgeDown_ReportsOffline()
    {
        var vm = CreateVm();

        await vm.RefreshAccountBarCommand.ExecuteAsync(null);

        Assert.Equal("not connected", vm.AccountText);
        Assert.Equal("bridge offline", vm.ConnectionText);
    }

    [Fact]
    public async Task HistoryPanel_FlowsFromBridgeDeals()
    {
        using var http = new FakeMt5SidecarHttp();
        var vm = CreateVm(mt5Client: new Mt5BridgeClient(
            http, new Uri($"http://127.0.0.1:{http.Port}/")));

        await vm.RefreshHistoryForTests();

        var row = Assert.Single(vm.HistoryRows);
        Assert.Equal("XAUUSDmicro", row.Symbol);
        Assert.Equal("BUY", row.Side);
        Assert.Equal("win", row.Outcome);
        Assert.Equal(12.0, row.Profit, 5);   // 12.5 profit - 0.2 commission - 0.3 swap
        Assert.Equal("FX", row.Source);
    }

    // ── Market watch ──────────────────────────────────────────────

    [Fact]
    public void SymbolFilter_NarrowsMarketWatch()
    {
        var vm = CreateVm();
        vm.Symbols.Add(new TerminalSymbolRow("XAUUSDmicro", "Gold Spot", true));
        vm.Symbols.Add(new TerminalSymbolRow("EURUSD", "Euro vs US Dollar", true));

        vm.SymbolFilter = "gold";

        Assert.Single(vm.FilteredSymbols);
        Assert.Equal("XAUUSDmicro", vm.FilteredSymbols.First().Symbol);
    }

    /// <summary>HttpMessageHandler that always fails fast (sidecar down).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"));
    }

    /// <summary>In-memory stand-in for the Python sidecar: answers /health,
    /// /account, /positions and /deals with the real payload shapes. No sockets.
    /// </summary>
    private sealed class FakeMt5SidecarHttp : HttpMessageHandler
    {
        public FakeMt5SidecarHttp()
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
        }

        public int Port { get; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            object payload = path switch
            {
                "/health" => new { ok = true, login = 201587365L, server = "Deriv-Demo", terminal_connected = true },
                "/account" => new { login = 201587365L, server = "Deriv-Demo", currency = "USD",
                                     balance = 2610.55, equity = 2610.55, margin = 0.0,
                                     margin_free = 2610.55, leverage = 1000, trade_mode = 0 },
                "/positions" => new { positions = Array.Empty<object>() },
                "/deals" => new
                {
                    deals = new[]
                    {
                        new
                        {
                            ticket = 5551212L, order = 9001L, symbol = "XAUUSDmicro",
                            side = "buy", volume = 0.10, price = 2000.0,
                            profit = 12.5, commission = -0.2, swap = -0.3,
                            time = 1758000000L,
                        },
                    },
                },
                _ => new { error = $"no route {path}" },
            };
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload),
                    Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
