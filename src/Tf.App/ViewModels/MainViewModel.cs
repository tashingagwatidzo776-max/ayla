using Tf.App.Infrastructure;
using Tf.Core;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.ViewModels;

public sealed class MainViewModel
{
    private readonly SettingsService _settingsService;
    private readonly DerivClient _client;

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

    public MainViewModel(SettingsService settingsService, DerivClient client,
        DashboardViewModel dashboard, SettingsViewModel settingsVm, TradesViewModel trades,
        BrainViewModel brain, AccountsViewModel accountsVm, GrowthViewModel growthVm,
        JournalViewModel journalVm, UpdateViewModel updateVm, PerformanceViewModel performanceVm,
        OptimizerViewModel optimizerVm, HealthViewModel healthVm)
    {
        _settingsService = settingsService;
        _client = client;
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

        if (string.IsNullOrEmpty(settings.AppId))
        {
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
    }

    public void Shutdown()
    {
        _ = _client.DisposeAsync();
    }
}