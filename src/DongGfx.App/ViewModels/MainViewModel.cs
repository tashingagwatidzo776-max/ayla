using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;

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
        TradeJournal? journal = null)
    {
        _settingsService = settingsService;
        _journal = journal!;
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

    public void Shutdown()
    {
        // Session unlocks are session-scoped by design and reset on window
        // close; the bridge client's HttpClient lifetime is DI-managed.
    }
}
