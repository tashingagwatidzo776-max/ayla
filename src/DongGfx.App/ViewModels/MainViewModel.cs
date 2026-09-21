using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.ViewModels;

public sealed class MainViewModel
{
    private readonly SettingsService _settingsService;
    private readonly DerivClient _client;
    private readonly TradeStore _store;
    private readonly DongGfx.Core.Logging.TradeJournal _journal;

    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel SettingsVm { get; }
    public TradesViewModel Trades { get; }
    public BrainViewModel Brain { get; }
    public AccountsViewModel AccountsVm { get; }
    public GrowthViewModel GrowthVm { get; }
    public JournalViewModel JournalVm { get; }
    public UpdateViewModel UpdateVm { get; }
    public PerformanceViewModel PerformanceVm { get; }
    public OptimizerViewModel OptimizerVm { get; }
    public HealthViewModel HealthVm { get; }
    public TerminalViewModel TerminalVm { get; }

    public MainViewModel(SettingsService settingsService, DerivClient client,
        TradeStore store,
        DashboardViewModel dashboard, SettingsViewModel settingsVm, TradesViewModel trades,
        BrainViewModel brain, AccountsViewModel accountsVm, GrowthViewModel growthVm,
        JournalViewModel journalVm, UpdateViewModel updateVm, PerformanceViewModel performanceVm,
        OptimizerViewModel optimizerVm, HealthViewModel healthVm,
        TerminalViewModel terminalVm,
        DongGfx.Core.Logging.TradeJournal? journal = null)
    {
        _settingsService = settingsService;
        _client = client;
        _store = store;
        _journal = journal!;
        Dashboard = dashboard;
        SettingsVm = settingsVm;
        Trades = trades;
        Brain = brain;
        AccountsVm = accountsVm;
        GrowthVm = growthVm;
        JournalVm = journalVm;
        UpdateVm = updateVm;
        PerformanceVm = performanceVm;
        OptimizerVm = optimizerVm;
        HealthVm = healthVm;
        TerminalVm = terminalVm;
    }

    /// <summary>Loads persisted settings, applies them, and starts the feed.</summary>
    public async Task InitializeAsync()
    {
        var settings = _settingsService.Load();
        SettingsVm.Load(settings);

        _client.AppId = settings.AppId;
        Dashboard.SetSymbol(settings.Symbol);
        Trades.Load();
        Brain.Refresh();

        // Crash recovery: check for unsettled contracts from a previous session.
        await RecoverUnsettledContractsAsync(settings);

        if (string.IsNullOrEmpty(settings.AppId))
        {
            return;
        }

        // A "Set as primary" switch (Accounts tab) may have pointed the
        // primary client at a new-platform PAT: re-apply that wiring on
        // every startup, with discovery re-verified. Classic primaries are
        // untouched by this (no-op wiring).
        try
        {
            await PrimaryClientWiring.ApplyAsync(_client, settings);
        }
        catch (Exception ex)
        {
            SettingsVm.StatusMessage = $"Primary account wiring failed: {ex.Message}";
            return;
        }

        try
        {
            await _client.ConnectAsync(string.IsNullOrEmpty(settings.ApiToken) ? null : settings.ApiToken);

            // History backfill works without a token and renders the chart
            // immediately.
            try
            {
                var history = await _client.GetTicksHistoryAsync(settings.Symbol, 200);
                Dashboard.AddHistory(history);
            }
            catch (Exception ex)
            {
                SettingsVm.StatusMessage = $"History unavailable: {ex.Message}";
            }

            // Live ticks now require an authorized session on Deriv.
            try
            {
                await _client.SubscribeTicksAsync(settings.Symbol);
                SettingsVm.StatusMessage = "Auto-connected — live feed streaming.";
            }
            catch (DerivApiException ex) when (ex.Code == "InvalidSymbol")
            {
                SettingsVm.StatusMessage =
                    $"Connected, but live ticks for {settings.Symbol} need a token " +
                    $"({ex.Message}). Add your Deriv demo token in Settings and press Connect.";
            }
            catch (Exception ex)
            {
                SettingsVm.StatusMessage = $"Live feed unavailable: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            SettingsVm.StatusMessage = $"Auto-connect failed: {ex.Message}";
        }

        await RunStartupProfileAsync(settings);
    }

    /// <summary>
    /// One-click session start (Settings → Startup profile): auto-connect the
    /// first demo row, arm the brain through the settings-VM bridge, select
    /// R_100, and start the engines. Demo automation only — real accounts
    /// still require the manual session unlock; the gate is never bypassed.
    /// </summary>
    private async Task RunStartupProfileAsync(AppSettings settings)
    {
        if (!settings.StartupProfile)
        {
            return;
        }

        try
        {
            var demoRow = AccountsVm.Hub.Accounts.FirstOrDefault(a => a.Config.IsDemo);
            if (demoRow is not null && !demoRow.IsConnected)
            {
                await demoRow.ConnectAsync();
            }

            // Arm the brain through the same bound-fields path the terminal's
            // switch uses, so the flip survives the next settings save.
            if (!settings.AutonomyEnabled)
            {
                settings.AutonomyEnabled = true;
                SettingsVm.AutonomyEnabled = true;
                _settingsService.Save(settings);
            }

            if (!string.Equals(settings.Symbol, "R_100", StringComparison.Ordinal))
            {
                settings.Symbol = "R_100";
                SettingsVm.Symbol = "R_100";
                _settingsService.Save(settings);
                Dashboard.SetSymbol("R_100");
            }

            GrowthVm.StartAllCommand.Execute(null);
            TerminalVm.StartTerminalCommand.Execute(null);
            _journal?.Log(Guid.Empty, "startup-profile",
                "one-click session start executed (demo row, autonomy ON, R_100, engines started)");
        }
        catch (Exception ex)
        {
            SettingsVm.StatusMessage = $"Startup profile failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Crash recovery: if the app closed while a contract was still open on
    /// Deriv, poll it until it settles so the trade log stays accurate.
    /// </summary>
    private async Task RecoverUnsettledContractsAsync(AppSettings settings)
    {
        var unsettled = _store.Trades
            .Where(t => t.Outcome == ContractStatus.Open &&
                        t.SettledAt > DateTimeOffset.UtcNow.AddHours(-24))
            .ToList();

        if (unsettled.Count == 0) return;

        Trades.StatusMessage = $"Recovering {unsettled.Count} unsettled contract(s) from previous session...";

        try
        {
            await _client.ConnectAsync(string.IsNullOrEmpty(settings.ApiToken) ? null : settings.ApiToken);
        }
        catch (Exception ex)
        {
            Trades.StatusMessage = $"Recovery failed — cannot connect: {ex.Message}";
            return;
        }

        foreach (var trade in unsettled)
        {
            try
            {
                Trades.StatusMessage = $"Checking contract {trade.ContractId}...";
                var info = await _client.WaitForSettlementAsync(
                    trade.ContractId, TimeSpan.FromSeconds(30));

                _store.Add(trade with
                {
                    Outcome = info.Status,
                    Profit = info.Profit,
                    ExitSpot = info.ExitSpot > 0 ? info.ExitSpot : null,
                    ExitEpoch = info.ExitTime > 0 ? info.ExitTime : null,
                    SettledAt = DateTimeOffset.UtcNow
                });
            }
            catch (TimeoutException)
            {
                Trades.StatusMessage = $"Contract {trade.ContractId} still open — will retry on next startup.";
            }
            catch (Exception ex)
            {
                Trades.StatusMessage = $"Recovery error for {trade.ContractId}: {ex.Message}";
            }
        }

        Trades.StatusMessage = "Recovery complete.";
    }

    public void Shutdown()
    {
        _ = _client.DisposeAsync();
    }
}