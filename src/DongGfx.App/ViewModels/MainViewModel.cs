using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;
using DongGfx.App.Services;

namespace DongGfx.App.ViewModels;

/// <summary>
/// Composition root for the MT5/forex-only surface: loads persisted
/// settings, launches the pinned MT5 terminal, and starts the bridge
/// polling. No Deriv connectivity, no contract recovery, no growth hub —
/// the binary-options integration is gone.
/// </summary>
public sealed class MainViewModel
{
    private readonly SettingsService _settingsService;
    private readonly TradeJournal _journal;
    private readonly ManualRealMoneyGate? _gate;

    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel SettingsVm { get; }
    public JournalViewModel JournalVm { get; }
    public UpdateViewModel UpdateVm { get; }
    public PerformanceViewModel PerformanceVm { get; }
    public TerminalViewModel TerminalVm { get; }

    public MainViewModel(
        SettingsService settingsService,
        DashboardViewModel dashboard,
        SettingsViewModel settingsVm,
        JournalViewModel journalVm,
        UpdateViewModel updateVm,
        PerformanceViewModel performanceVm,
        TerminalViewModel terminalVm,
        TradeJournal? journal = null,
        ManualRealMoneyGate? gate = null)
    {
        _settingsService = settingsService;
        _journal = journal!;
        _gate = gate;
        Dashboard = dashboard;
        SettingsVm = settingsVm;
        JournalVm = journalVm;
        UpdateVm = updateVm;
        PerformanceVm = performanceVm;
        TerminalVm = terminalVm;
    }

    /// <summary>Loads persisted settings, launches the pinned MT5 terminal
    /// and starts the Terminal's bridge polling.</summary>
    public async Task InitializeAsync()
    {
        var settings = _settingsService.Load();
        SettingsVm.Load(settings);
        Dashboard.SetSymbol(settings.FxSymbol);

        // MT5-first startup: bring the terminal up (pinned path or first
        // known install; no-op when a terminal64 process is already
        // running), then start the Terminal's poll loops so the bridge
        // panel is live before anything else touches the network.
        try
        {
            Mt5TerminalLocator.EnsureRunning(settings.Mt5TerminalPath);
        }
        catch (Exception ex)
        {
            SettingsVm.StatusMessage = $"MT5 terminal launch failed: {ex.Message}";
        }

        TerminalVm.StartTerminalCommand.Execute(null);

        _journal?.Log(
            Guid.Empty,
            "startup",
            $"app started — terminal {settings.Mt5TerminalPath} · bridge polling active");

        await Task.CompletedTask;
    }

    /// <summary>Tray-exit and window-close teardown: stop the FX engines
    /// first so no cycle can fire while the app disposes the bridge client
    /// the engines order through. Session unlocks are session-scoped by
    /// design and reset on window close (MainWindow).</summary>
    public void Shutdown()
    {
        TerminalVm.ShutdownFxBrain();
    }

    /// <summary>Account-switch safety guards, run after every successful MT5
    /// login: (1) stop the FX brain — its open positions, exposure cap and
    /// supervisor baseline all belong to the account that was just signed
    /// out, so an engine cycle must never fire against the new one; (2)
    /// reset the manual real-money unlock, which was armed for the previous
    /// account and must not carry across a switch (session-scoped by
    /// design: a fresh account starts locked). Both best-effort: the guards
    /// must never mask the successful login.</summary>
    public void OnMt5AccountSwitched(string login, string server)
    {
        TerminalVm.ShutdownFxBrain();
        try
        {
            // A fresh account starts locked: the unlock was armed for the
            // previous account's manual surfaces.
            _gate?.Reset();
            _journal?.Log(Guid.Empty, "MT5_SESSION",
                $"account switched to {login} @ {server} — FX brain stopped, " +
                "real-money unlock reset (guards: docs/real-money-safety-audit.md)");
        }
        catch
        {
            // The switch itself already succeeded; a journal hiccup must
            // never surface as a failed login or crash the dialog close.
        }
    }
}
