using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Market Watch right-click commands: New Order pre-selects the ticket
/// symbol without sending anything, Create Alert arms above/below alerts
/// around the quote (dedupe via the engine), Open Chart switches the
/// selected symbol, and every command tolerates a null row.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class TerminalContextCommandsTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings = new() { IsDemo = true, Mt5MaxLots = 1.00m };
    private readonly ManualRealMoneyGate _gate = new();

    public TerminalContextCommandsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_termctx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        _gate.Reset();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Dead-end handler: the context commands under test never
    /// touch the bridge; anything that does fails fast instead of hanging.</summary>
    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

    private TerminalViewModel CreateVm(PriceAlertEngine? alerts = null) =>
        new(
            () => _settings,
            persist: () => { },
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(new DeadHandler(), new Uri("http://127.0.0.1:1/")),
            alerts: alerts);

    [Fact]
    public async Task NewOrder_Preselects_The_Ticket_Symbol_Without_Sending()
    {
        var vm = CreateVm();
        var row = new TerminalSymbolRow("EURUSD", "Euro vs US Dollar", true);

        await vm.SymbolNewOrderCommand.ExecuteAsync(row);

        Assert.Equal("EURUSD", vm.Mt5Symbol);
        Assert.Contains("EURUSD", vm.Mt5OrderStatus);
    }

    [Fact]
    public async Task NewOrder_NullRow_Is_A_NoOp()
    {
        var vm = CreateVm();
        await vm.SymbolNewOrderCommand.ExecuteAsync(null);
        Assert.Equal("XAUUSDmicro", vm.Mt5Symbol);   // unchanged default
    }

    [Fact]
    public async Task CreateAlert_Arms_Above_And_Below_Around_The_Quote()
    {
        var alerts = new PriceAlertEngine(new NotificationService());
        var vm = CreateVm(alerts);
        var row = new TerminalSymbolRow("XAUUSDmicro", "Gold micro", true);
        row.Last = 4347.0;
        row.Spread = 0.3;

        await vm.SymbolCreateAlertCommand.ExecuteAsync(row);

        Assert.Equal(2, alerts.Alerts.Count);
        Assert.Contains(alerts.Alerts, a => a.Direction == "above" && a.Trigger > 4347.0);
        Assert.Contains(alerts.Alerts, a => a.Direction == "below" && a.Trigger < 4347.0);
        Assert.Contains("alerts armed", vm.MarketWatchStatus);
    }

    [Fact]
    public async Task CreateAlert_Dedupes_Repeat_Clicks_And_Falls_Back_To_Bid()
    {
        var alerts = new PriceAlertEngine(new NotificationService());
        var vm = CreateVm(alerts);
        var row = new TerminalSymbolRow("EURUSD", "Euro", true);
        row.Bid = 1.0850;   // no Last: falls back to the bid

        await vm.SymbolCreateAlertCommand.ExecuteAsync(row);
        await vm.SymbolCreateAlertCommand.ExecuteAsync(row);

        Assert.Equal(2, alerts.Alerts.Count);   // engine collapses duplicates
    }

    [Fact]
    public async Task CreateAlert_Without_Any_Quote_Is_Refused_With_A_Hint()
    {
        var alerts = new PriceAlertEngine(new NotificationService());
        var vm = CreateVm(alerts);
        var row = new TerminalSymbolRow("NOQUOTE", "No tick yet", true);   // Last/Bid 0

        await vm.SymbolCreateAlertCommand.ExecuteAsync(row);

        Assert.Empty(alerts.Alerts);
        Assert.Contains("no quote yet", vm.MarketWatchStatus);
    }

    [Fact]
    public async Task OpenChart_Selects_The_Row_From_The_Catalog()
    {
        var vm = CreateVm();
        var row = new TerminalSymbolRow("GBPUSD", "Pound", true);
        vm.Symbols.Add(new TerminalSymbolRow("EURUSD", "Euro", true));
        vm.Symbols.Add(row);

        await vm.SymbolOpenChartCommand.ExecuteAsync(row);

        Assert.Equal("GBPUSD", vm.SelectedSymbol?.Symbol);
    }

    [Fact]
    public async Task OpenChart_NullRow_Keeps_The_Selection()
    {
        var vm = CreateVm();
        await vm.SymbolOpenChartCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedSymbol);
    }
}
