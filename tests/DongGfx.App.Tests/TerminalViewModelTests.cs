using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;
using InfrastructureVault = DongGfx.App.Infrastructure.IAccountVault;
using MemoryVaultImpl = DongGfx.App.Tests.VaultStubs.MemoryVault;

namespace DongGfx.App.Tests;

/// <summary>
/// The DON G FX Terminal: the brain's autonomy ON/OFF switch must persist
/// through the real settings pipeline (one switch governs every surface),
/// the order ticket must refuse through every rail in the same order the
/// other trade paths enforce them, position rows must track live deltas and
/// settle into the shared TradeStore, and a real order must flow end-to-end
/// through a local fake Deriv WebSocket — proposal → buy → settlement —
/// with no network and no client subclassing.
/// </summary>
[Trait("Category", "RealMoney")]
public class TerminalViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly MultiAccountHub _hub;
    private readonly AppSettings _settings;
    private readonly ManualRealMoneyGate _gate;
    private int _saves;

    public TerminalViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_term_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _hub = new MultiAccountHub(new MemoryVaultImpl(), _store, _journal);
        _gate = _hub.ManualGate;
        _settings = new AppSettings { IsDemo = true, Stake = 1.00m, DurationMinutes = 5 };
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TerminalViewModel CreateVm(DerivClient? client = null, DashboardViewModel? dashboard = null)
    {
        return new TerminalViewModel(
            () => client ?? new DerivClient(),
            _hub,
            _store,
            () => _settings,
            persist: () => _saves++,
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard ?? new DashboardViewModel(new DerivClient(), _hub),
            new PublicMarketDataClient("ws://127.0.0.1:1/ws"), // dead endpoint: never connects
            setAutonomyBound: v => _settings.AutonomyEnabled = v,
            setSymbolBound: s => _settings.Symbol = s);
    }

    /// <summary>Marks a hub row connected (same reflection seam the
    /// reconnect tests use) so the terminal takes the per-account path.</summary>
    private static void ForceConnected(AccountConnection c) =>
        typeof(AccountConnection).GetProperty(nameof(AccountConnection.IsConnected))!
            .SetValue(c, true);

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
            () => new DerivClient(), _hub, _store, () => _settings,
            persist: () => _saves++, isRealMoneyUnlocked: () => _gate.IsUnlocked,
            new DashboardViewModel(new DerivClient(), _hub),
            new PublicMarketDataClient("ws://127.0.0.1:1/ws"),
            setAutonomyBound: v => bridged = v, setSymbolBound: null);

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

    // ── Ticket rails (refusals never touch any client) ─────────────

    [Fact]
    public void PlaceOrder_KillSwitchEngaged_IsRefused()
    {
        var dashboard = new DashboardViewModel(new DerivClient(), _hub);
        dashboard.ToggleKillSwitchCommand.Execute(null);
        var vm = CreateVm(dashboard: dashboard);

        vm.PlaceOrderCommand.Execute(null);

        Assert.Contains("kill switch", vm.TicketStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.Positions);
    }

    [Fact]
    public void PlaceOrder_StakeOverManualCap_IsRefused()
    {
        _settings.ManualMaxStake = 10m;
        var vm = CreateVm();
        vm.Stake = 25m;

        vm.PlaceOrderCommand.Execute(null);

        Assert.Contains("manual max", vm.TicketStatus);
        Assert.Empty(vm.Positions);
    }

    [Fact]
    public void PlaceOrder_RealAccountLocked_IsRefusedByGate()
    {
        var connection = _hub.AddAccount(new AccountConfig
        {
            Label = "RealRow", ApiToken = "tok", IsDemo = false, BrainKey = "Terminal"
        });
        connection.ApiVerifiedVirtual = false;
        typeof(DerivClient).GetProperty(nameof(DerivClient.LoginId))!
            .SetValue(connection.Client, "CR0001");

        _settings.IsDemo = false;
        ForceConnected(connection);
        var vm = CreateVm(client: connection.Client);

        vm.PlaceOrderCommand.Execute(null);

        Assert.Contains("unlock", vm.TicketStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.Positions);
        Assert.False(_hub.IsRealMoneyUnlocked(connection.Config.Id));
    }

    [Fact]
    public void PlaceOrder_UnlockedRealAccount_PassesGateToConnectivityCheck()
    {
        var connection = _hub.AddAccount(new AccountConfig
        {
            Label = "RealRow", ApiToken = "tok", IsDemo = false, BrainKey = "Terminal"
        });
        connection.ApiVerifiedVirtual = false;
        typeof(DerivClient).GetProperty(nameof(DerivClient.LoginId))!
            .SetValue(connection.Client, "CR0001");

        _settings.IsDemo = false;
        ForceConnected(connection);
        _hub.UnlockRealMoney(connection.Config.Id);
        Assert.True(_hub.IsRealMoneyUnlocked(connection.Config.Id));

        var vm = CreateVm(client: connection.Client);
        vm.PlaceOrderCommand.Execute(null);

        // Past the per-account gate; refused only because the fake client
        // never connected.
        Assert.Contains("connect", vm.TicketStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaceOrder_NotConnected_IsRefused()
    {
        var vm = CreateVm();

        vm.PlaceOrderCommand.Execute(null);

        Assert.Contains("not connected", vm.TicketStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.Positions);
    }

    // ── Position rows ──────────────────────────────────────────────

    [Fact]
    public void PositionRow_TracksDeltaAndSettles()
    {
        var row = new TerminalPositionRow("c-1", "R_100", Direction.Rise, 1m, 100, 0)
        {
            CurrentSpot = 101
        };
        Assert.Equal("+1", row.DeltaText);

        row.UpdateSpot(99.5);
        Assert.Equal("-0.5", row.DeltaText);

        row.Settle(new ContractInfo("c-1", ContractStatus.Won, 100, 100.8, 0, 0,
            1m, 0.85m, "USD", IsSold: true));
        Assert.Equal("won", row.Status);
        Assert.Equal(0.85m, row.Profit);
        Assert.Equal("100.8", row.ExitSpotText);
        Assert.Equal("", row.DeltaText); // realized — the delta column retires
    }

    [Fact]
    public void PositionRow_FallDirection_DeltasInvert()
    {
        var row = new TerminalPositionRow("c-2", "R_100", Direction.Fall, 1m, 100, 0)
        {
            CurrentSpot = 101
        };
        Assert.Equal("-1", row.DeltaText); // a fall wins when price drops
    }

    // ── Live order through a local fake Deriv socket ───────────────

    [Fact]
    public async Task PlaceOrder_ConnectedClient_BuysAndFeedsSharedStore()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new FakeTerminalServer();
        _ = server.RunAsync(cts.Token);
        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await client.ConnectAsync(ct: cts.Token);
        await using var __client = client;
        await using var __server = server;

        var connection = _hub.AddAccount(new AccountConfig
        {
            Label = "DemoRow", ApiToken = "tok", IsDemo = true, BrainKey = "Terminal"
        });
        // The terminal routes orders through the CONNECTED row's own client —
        // point it at the fake server so the flow is real end-to-end.
        typeof(AccountConnection).GetField("_client",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(connection, client);
        typeof(DerivClient).GetProperty(nameof(DerivClient.LoginId))!
            .SetValue(connection.Client, "DOT92951338");
        connection.ApiVerifiedVirtual = true;
        ForceConnected(connection);

        var vm = CreateVm(client: client);
        vm.RefreshAccountBarCommand.Execute(null);
        // The bar is fed from the (force-)connected hub row.
        Assert.Equal(connection.DisplayName, vm.AccountText);
        Assert.Equal(connection.StatusText, vm.ConnectionText);

        // Selecting R_100 syncs the shared settings symbol — the brain and
        // growth engines trade what the terminal trades.
        vm.SelectedSymbol = new TerminalSymbolRow("R_100", "Volatility 100 Index", true);
        vm.SelectedDirection = Direction.Rise;
        vm.Stake = 1m;
        vm.DurationMinutes = 5;
        await vm.PlaceOrderCommand.ExecuteAsync(null);

        Assert.Contains("open @ 1.05", vm.TicketStatus);
        var row = Assert.Single(vm.Positions);
        Assert.Equal("R_100", row.Symbol);
        Assert.Equal(Direction.Rise, row.Direction);
        Assert.Equal("TERMINAL-77", row.ContractId);

        // Settlement (the server answers sold immediately) lands in the store.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_store.Trades.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }

        var trade = Assert.Single(_store.ForAccount(connection.Config.Id, source: "Manual"));
        Assert.Equal("R_100", trade.Symbol);
        Assert.Equal(ContractStatus.Won, trade.Outcome);
        Assert.Equal(0.85m, trade.Profit);
        Assert.Equal(connection.Config.Id, trade.AccountId);

        // The ticket's symbol was synced into shared settings — the brain
        // and growth engines now trade what the terminal traded.
        Assert.Equal("R_100", _settings.Symbol);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task PlaceOrder_ServerRejectsProposal_ShowsApiError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = new FakeTerminalServer(rejectProposals: true);
        _ = server.RunAsync(cts.Token);
        var client = new DerivClient { AppId = "1089", Endpoint = server.WsUrl };
        await client.ConnectAsync(ct: cts.Token);
        await using var __client = client;
        await using var __server = server;

        var vm = CreateVm(client: client);
        vm.Stake = 1m;
        await vm.PlaceOrderCommand.ExecuteAsync(null);

        Assert.Contains("order failed", vm.TicketStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.Positions);
        Assert.Empty(_store.Trades);
    }
}

/// <summary>
/// Shared in-memory vault stubs for VM tests (each test file aliases its own
/// private copy today; a shared stub class avoids another duplicate).
/// </summary>
public static class VaultStubs
{
    public sealed class MemoryVault : InfrastructureVault
    {
        private readonly List<AccountConfig> _accounts = new();
        public IReadOnlyList<AccountConfig> Load() => _accounts;
        public void Save(IReadOnlyList<AccountConfig> accounts)
        {
            _accounts.Clear();
            _accounts.AddRange(accounts);
        }
    }
}

/// <summary>
/// Tiny in-process Deriv stand-in for the terminal tests: answers proposal,
/// buy and contract snapshots, echoing the request's req_id the way the real
/// new platform does. Independent copy so Core test refactors never reach in.
/// </summary>
internal sealed class FakeTerminalServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly bool _rejectProposals;
    private readonly List<string> _received = new();
    private readonly object _sync = new();
    private Task? _runTask;

    public FakeTerminalServer(bool rejectProposals = false)
    {
        _rejectProposals = rejectProposals;
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
    }

    public int Port { get; }
    public string WsUrl => $"ws://127.0.0.1:{Port}";

    public IReadOnlyList<string> Received
    {
        get { lock (_sync) { return _received.ToArray(); } }
    }

    public Task RunAsync(CancellationToken ct) => _runTask ??= AcceptLoopAsync(ct);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync().ConfigureAwait(false);
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var ws = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
            _ = ServeAsync(ws, ct);
        }
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct)
                        .ConfigureAwait(false);
                    return;
                }

                var request = JsonDocument.Parse(
                    Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;
                lock (_sync) { _received.Add(request.GetRawText()); }

                string response;
                if (request.TryGetProperty("proposal", out _))
                {
                    response = _rejectProposals
                        ? Wrap(request, "error",
                            "{\"code\":\"ContractBuyValidationError\"," +
                            "\"message\":\"Trading is not offered for this duration\"}")
                        : Wrap(request, "proposal",
                            "{\"id\":\"PROP-T\",\"spot\":100,\"longcode\":\"Rise 5m\"," +
                            "\"payout\":1.85}");
                }
                else if (request.TryGetProperty("buy", out _))
                {
                    response = Wrap(request, "buy",
                        "{\"contract_id\":\"TERMINAL-77\",\"buy_price\":1.05," +
                        "\"balance_after\":999.95,\"longcode\":\"Rise 5m\"}");
                }
                else if (request.TryGetProperty("proposal_open_contract", out _))
                {
                    response = Wrap(request, "proposal_open_contract",
                        "{\"contract_id\":\"" + request.GetProperty("contract_id").GetString() +
                        "\",\"status\":\"won\",\"is_sold\":1,\"entry_spot\":100," +
                        "\"exit_spot\":100.85,\"entry_spot_time\":1,\"exit_spot_time\":2," +
                        "\"buy_price\":\"1.05\",\"profit\":\"0.85\",\"currency\":\"USD\"}");
                }
                else
                {
                    response = Wrap(request, "error",
                        "{\"code\":\"Unhandled\",\"message\":\"no script for this request\"}");
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                await socket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Client teardown — expected at test end.
        }
    }

    private static string Wrap(JsonElement req, string msgType, string body) =>
        $"{{\"msg_type\":\"{msgType}\",\"req_id\":{req.GetProperty("req_id").GetInt32()},\"{msgType}\":{body}}}";

    public ValueTask DisposeAsync()
    {
        try { _listener.Stop(); } catch { /* already down */ }
        return ValueTask.CompletedTask;
    }
}
