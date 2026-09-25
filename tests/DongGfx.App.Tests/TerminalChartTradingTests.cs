using System.IO;
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
/// Chart trading: the chart's buy/sell goes through the SAME guarded
/// order path as the ticket (kill switch, lot cap, real-money gate) — a
/// shortcut, never a bypass. A dragged SL/TP line maps onto
/// ModifyPositionAsync for that leg only, and price lines rebuild from
/// the selected symbol's positions/orders on every poll.
/// </summary>
[Trait("Category", "Unit")]
public class TerminalChartTradingTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings = new() { IsDemo = true, Mt5MaxLots = 1.00m };
    private readonly ManualRealMoneyGate _gate = new();

    public TerminalChartTradingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_chart_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Fake bridge: /account = Deriv-Demo demo verdict, /order
    /// returns a fill, /positions + /orders return the configured rows.</summary>
    private class FakeBridge : HttpMessageHandler
    {
        public int OrderCalls;
        public List<(long Ticket, double? Sl, double? Tp)> Modifies { get; } = new();
        public List<Mt5Position> Positions { get; set; } = new();
        public List<Mt5PendingOrder> Orders { get; set; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            string json;
            if (path == "account")
            {
                json = "{\"login\":201587365,\"server\":\"Deriv-Demo\",\"currency\":\"USD\"," +
                       "\"balance\":1000,\"equity\":1000,\"margin_free\":1000,\"leverage\":500,\"trade_mode\":0}";
            }
            else if (path == "order")
            {
                OrderCalls++;
                json = "{\"ok\":true,\"retcode\":10009,\"retcode_name\":\"done\",\"order\":777," +
                       "\"deal\":555,\"price\":4347.9,\"volume\":0.1,\"comment\":\"done\"}";
            }
            else if (path == "positions")
            {
                json = JsonSerializer.Serialize(new
                {
                    positions = Positions.Select(p => new Dictionary<string, object>
                    {
                        ["ticket"] = p.Ticket,
                        ["symbol"] = p.Symbol,
                        ["side"] = p.Side,
                        ["volume"] = p.Volume,
                        ["price_open"] = p.PriceOpen,
                        ["price_current"] = p.PriceCurrent,
                        ["profit"] = p.Profit,
                        ["sl"] = p.Sl,
                        ["tp"] = p.Tp,
                    }),
                });
            }
            else if (path == "orders")
            {
                json = JsonSerializer.Serialize(new
                {
                    orders = Orders.Select(o => new Dictionary<string, object>
                    {
                        ["ticket"] = o.Ticket,
                        ["symbol"] = o.Symbol,
                        ["side"] = o.Side,
                        ["kind"] = o.Kind,
                        ["volume"] = o.Volume,
                        ["price"] = o.Price,
                        ["sl"] = o.Sl,
                        ["tp"] = o.Tp,
                        ["time_setup"] = o.TimeSetup,
                    }),
                });
            }
            else if (path == "modify")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var sl = doc.RootElement.TryGetProperty("sl", out var s) && s.ValueKind == JsonValueKind.Number ? (double?)s.GetDouble() : null;
                var tp = doc.RootElement.TryGetProperty("tp", out var t) && t.ValueKind == JsonValueKind.Number ? (double?)t.GetDouble() : null;
                Modifies.Add((doc.RootElement.GetProperty("ticket").GetInt64(), sl, tp));
                json = "{\"ok\":true,\"retcode\":10009,\"retcode_name\":\"done\"}";
            }
            else if (path == "health")
            {
                json = "{\"ok\":true,\"login\":201587365,\"server\":\"Deriv-Demo\"}";
            }
            else
            {
                json = "{}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private TerminalViewModel CreateVm(FakeBridge bridge)
    {
        var vm = new TerminalViewModel(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(bridge, new Uri("http://127.0.0.1:53190/")));
        vm.Symbols.Add(new TerminalSymbolRow("XAUUSDmicro", "Gold micro", true));
        vm.SelectedSymbol = vm.Symbols[0];
        return vm;
    }

    [Fact]
    public async Task ChartOrder_DemoAccount_Fills_Through_The_Shared_Path()
    {
        var bridge = new FakeBridge();
        var vm = CreateVm(bridge);

        await vm.PlaceChartOrderCommand.ExecuteAsync("buy");

        Assert.Equal(1, bridge.OrderCalls);
        Assert.Contains("filled", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task ChartOrder_Obeys_The_Lot_Cap()
    {
        _settings.Mt5MaxLots = 0.05m;
        var bridge = new FakeBridge();
        var vm = CreateVm(bridge);
        vm.Mt5Lots = 0.5;   // over the cap

        await vm.PlaceChartOrderCommand.ExecuteAsync("buy");

        Assert.Equal(0, bridge.OrderCalls);
        Assert.Contains("outside the allowed", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task ChartOrder_Kill_Switch_Blocks_Before_Any_Send()
    {
        var bridge = new FakeBridge();
        var dashboard = new DashboardViewModel();
        var vm = new TerminalViewModel(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: dashboard,
            journal: _journal,
            mt5: new Mt5BridgeClient(bridge, new Uri("http://127.0.0.1:53190/")));
        vm.Symbols.Add(new TerminalSymbolRow("XAUUSDmicro", "Gold micro", true));
        vm.SelectedSymbol = vm.Symbols[0];
        dashboard.ToggleKillSwitchCommand.Execute(null);
        Assert.True(dashboard.IsKillSwitchEngaged);

        await vm.PlaceChartOrderCommand.ExecuteAsync("sell");

        Assert.Equal(0, bridge.OrderCalls);
        Assert.Contains("kill switch", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task ChartOrder_Real_Account_Without_Unlock_Fails_Closed()
    {
        // Real venue verdict (trade_mode=2): the gate demands the session
        // unlock even from the chart — fail-closed without it.
        var bridge = new FakeBridge
        {
            Positions = Array.Empty<Mt5Position>().ToList(),
        };
        var vm = CreateVm(bridge);
        var realAccountJson = "{\"login\":201587365,\"server\":\"Deriv-Real\",\"currency\":\"USD\"," +
            "\"balance\":1000,\"equity\":1000,\"margin_free\":1000,\"leverage\":500,\"trade_mode\":2}";
        // Swap the account route by wrapping: simplest is a subclass hook —
        // instead flip via a second handler instance bound to real mode.
        var realBridge = new RealModeBridge();
        var vm2 = new TerminalViewModel(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(realBridge, new Uri("http://127.0.0.1:53190/")));
        vm2.Symbols.Add(new TerminalSymbolRow("XAUUSDmicro", "Gold micro", true));
        vm2.SelectedSymbol = vm2.Symbols[0];

        await vm2.PlaceChartOrderCommand.ExecuteAsync("buy");

        Assert.Equal(0, realBridge.OrderCalls);
        Assert.Contains("unlock", vm2.Mt5OrderStatus, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RealModeBridge : FakeBridge
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path == "account")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"login\":201587365,\"server\":\"Deriv-Real\",\"currency\":\"USD\"," +
                        "\"balance\":1000,\"equity\":1000,\"margin_free\":1000,\"leverage\":500,\"trade_mode\":2}",
                        Encoding.UTF8, "application/json"),
                };
            }

            return await base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task Dragged_Sl_Line_Modifies_Only_That_Leg()
    {
        var bridge = new FakeBridge();
        var vm = CreateVm(bridge);

        await vm.ChartLineDraggedCommand.ExecuteAsync("111|sl|4330.5");

        var mod = Assert.Single(bridge.Modifies);
        Assert.Equal(111, mod.Ticket);
        Assert.Equal(4330.5, mod.Sl);
        Assert.Null(mod.Tp);
        Assert.Contains("dragged to 4330.5", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task Dragged_Tp_Line_Modifies_Only_That_Leg()
    {
        var bridge = new FakeBridge();
        var vm = CreateVm(bridge);

        await vm.ChartLineDraggedCommand.ExecuteAsync("111|tp|4400");

        var mod = Assert.Single(bridge.Modifies);
        Assert.Equal(4400, mod.Tp);
        Assert.Null(mod.Sl);
    }

    [Fact]
    public async Task Dragged_Line_Garbage_Spec_Is_A_NoOp()
    {
        var bridge = new FakeBridge();
        var vm = CreateVm(bridge);

        await vm.ChartLineDraggedCommand.ExecuteAsync("not-a-spec");
        await vm.ChartLineDraggedCommand.ExecuteAsync(null);

        Assert.Empty(bridge.Modifies);
    }

    [Fact]
    public void PriceLines_Rebuild_For_The_Selected_Symbol_With_Draggable_Legs()
    {
        // The chart control needs STA; the rebuild itself is synchronous,
        // so run the whole body on an STA thread without async plumbing.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var bridge = new FakeBridge();
                var vm = CreateVm(bridge);
                var chart = new Controls.CandleChartControl();
                vm.CandleChart = chart;

                var positions = new List<Mt5Position>
                {
                    new(111, "XAUUSDmicro", "buy", 0.5, 4300, 4347, 23.8, Sl: 4280, Tp: 4400),
                    new(222, "EURUSD", "sell", 0.1, 1.0850, 1.0840, 1.0),   // other symbol: filtered out
                };
                var orders = new List<Mt5PendingOrder>
                {
                    new(777, "XAUUSDmicro", "buy", "limit", 0.2, 4200, 4190, 4260, 1790000000),
                };
                vm.RebuildChartPriceLines(positions, orders);

                var lines = chart.PriceLines;
                Assert.Contains(lines, l => l.Kind == "entry" && Math.Abs(l.Price - 4300) < 1e-9 && l.Ticket is null);
                Assert.Contains(lines, l => l.Kind == "sl" && l.Ticket == 111 && Math.Abs(l.Price - 4280) < 1e-9);
                Assert.Contains(lines, l => l.Kind == "tp" && l.Ticket == 111 && Math.Abs(l.Price - 4400) < 1e-9);
                Assert.Contains(lines, l => l.Kind == "entry" && Math.Abs(l.Price - 4200) < 1e-9);   // pending order
                Assert.DoesNotContain(lines, l => l.Ticket == 222);                                   // other symbol gone
                Assert.All(lines.Where(l => l.Kind != "entry"), l => Assert.True(l.Ticket.HasValue));

                // No chart attached → the poll-time rebuild is a safe no-op.
                vm.CandleChart = null;
                vm.RebuildChartPriceLines(positions, orders);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
