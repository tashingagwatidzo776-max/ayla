using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Core.Update;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Account-switch safety guards: after every successful MT5 login the FX
/// brain must be stopped (its open positions, exposure cap and supervisor
/// baseline belong to the account that was just signed out) and the manual
/// real-money unlock must reset (a fresh account starts locked — the
/// unlock was armed for the previous account). Runs
/// <see cref="MainViewModel.OnMt5AccountSwitched"/> — the exact
/// continuation the login dialog invokes — against a real journal and a
/// real portfolio host, no network.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
public class AccountSwitchGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeJournal _journal;
    private readonly AppSettings _settings;
    private readonly ManualRealMoneyGate _gate = new();
    private int _saves;

    public AccountSwitchGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_switch_{Guid.NewGuid():N}");
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

    private TerminalViewModel CreateTerminal(Func<FxPortfolioHost?>? fxHostFactory = null) =>
        new(
            () => _settings,
            persist: () => _saves++,
            isRealMoneyUnlocked: () => _gate.IsUnlocked,
            dashboard: new DashboardViewModel(),
            journal: _journal,
            mt5: new Mt5BridgeClient(new DeadHandler(), new Uri("http://127.0.0.1:1/")),
            setAutonomyBound: v => _settings.AutonomyEnabled = v,
            setSymbolBound: s => _settings.FxSymbol = s,
            fxHostFactory: fxHostFactory);

    private MainViewModel CreateMain(TerminalViewModel terminal) => new(
        new SettingsService(),
        new DashboardViewModel(),
        new SettingsViewModel(new SettingsService()),
        new JournalViewModel(_journal, killSwitch: () => false),
        new UpdateViewModel(new AutoUpdater("0.0.0-test", updateDir: Path.Combine(_dir, "updates"))),
        new PerformanceViewModel(new PerformanceTracker(Path.Combine(_dir, "perf"))),
        terminal,
        _journal,
        _gate);

    private FxPortfolioHost NewFxPortfolio() => new(
        new Mt5BridgeClient(new DeadHandler(), new Uri("http://127.0.0.1:1/")),
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

    /// <summary>HttpMessageHandler that always fails fast (sidecar down) —
    /// the guard tests never touch the bridge.</summary>
    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("bridge down"));
    }

    [Fact]
    public void AccountSwitch_StopsTheFxBrain()
    {
        var host = NewFxPortfolio();
        var terminal = CreateTerminal(fxHostFactory: () => host);
        terminal.ToggleFxBrainCommand.Execute(null);
        Assert.True(host.IsRunning);
        Assert.Equal("FX BRAIN: PAPER", terminal.FxBadge);

        CreateMain(terminal).OnMt5AccountSwitched("12345", "Demo-Server");

        Assert.False(host.IsRunning);
        Assert.Equal("FX BRAIN: OFF", terminal.FxBadge);
    }

    [Fact]
    public void AccountSwitch_ResetsTheRealMoneyUnlock()
    {
        var terminal = CreateTerminal();
        _gate.Arm();
        Assert.True(_gate.IsUnlocked);
        Assert.NotNull(_gate.ArmedAt);

        CreateMain(terminal).OnMt5AccountSwitched("12345", "Demo-Server");

        Assert.False(_gate.IsUnlocked);
        Assert.Null(_gate.ArmedAt);
    }

    [Fact]
    public void AccountSwitch_JournalsTheGuardAction()
    {
        var main = CreateMain(CreateTerminal());

        main.OnMt5AccountSwitched("98765", "Demo-Server");
        _journal.Flush();

        var entry = _journal.GetRecent(count: 50)
            .FirstOrDefault(e => e.Category == "MT5_SESSION");
        Assert.NotNull(entry);
        Assert.Contains("98765", entry!.Details);
        Assert.Contains("Demo-Server", entry.Details);
        Assert.Contains("FX brain stopped", entry.Details);
        Assert.Contains("real-money unlock reset", entry.Details);
    }

    [Fact]
    public void AccountSwitch_WithoutRunningBrain_IsSafeAndIdempotent()
    {
        var main = CreateMain(CreateTerminal());

        main.OnMt5AccountSwitched("1", "A");
        main.OnMt5AccountSwitched("1", "A");
        _journal.Flush();

        Assert.Equal("FX BRAIN: OFF", main.TerminalVm.FxBadge);
        Assert.Equal(2, _journal.GetRecent(count: 50)
            .Count(e => e.Category == "MT5_SESSION"));
    }
}
