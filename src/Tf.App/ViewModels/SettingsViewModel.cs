using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Infrastructure;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly DerivClient _client;
    private readonly DashboardViewModel _dashboard;

    [ObservableProperty]
    private string apiToken = "";

    [ObservableProperty]
    private string appId = AppSettings.DefaultAppId;

    [ObservableProperty]
    private bool isDemo = true;

    [ObservableProperty]
    private string symbol = AppSettings.DefaultSymbol;

    [ObservableProperty]
    private string currency = AppSettings.DefaultCurrency;

    [ObservableProperty]
    private int durationMinutes = 5;

    [ObservableProperty]
    private decimal stake = 1.00m;

    [ObservableProperty]
    private bool autonomyEnabled;

    [ObservableProperty]
    private int decisionIntervalMinutes = 5;

    [ObservableProperty]
    private string llmBaseUrl = AppSettings.DefaultLlmBaseUrl;

    [ObservableProperty]
    private string llmModel = AppSettings.DefaultLlmModel;

    [ObservableProperty]
    private string llmApiKey = "";

    [ObservableProperty]
    private string statusMessage = "Settings load on startup; Save writes them to %APPDATA%\\tf\\data.";

    /// <summary>Human-readable trading-mode label.</summary>
    public string ModeLabel => IsDemo ? "Demo" : "Real";

    partial void OnIsDemoChanged(bool value) => OnPropertyChanged(nameof(ModeLabel));

    [ObservableProperty]
    private bool isBusy;

    public SettingsViewModel(SettingsService settingsService, DerivClient client, DashboardViewModel dashboard)
    {
        _settingsService = settingsService;
        _client = client;
        _dashboard = dashboard;
    }

    public void Load(AppSettings settings)
    {
        ApiToken = settings.ApiToken;
        AppId = settings.AppId;
        IsDemo = settings.IsDemo;
        Symbol = settings.Symbol;
        Currency = settings.Currency;
        DurationMinutes = settings.DurationMinutes;
        Stake = settings.Stake;
        AutonomyEnabled = settings.AutonomyEnabled;
        DecisionIntervalMinutes = settings.DecisionIntervalMinutes;
        LlmBaseUrl = settings.LlmBaseUrl;
        LlmModel = settings.LlmModel;
        LlmApiKey = settings.LlmApiKey;
        StatusMessage = "Settings loaded.";
    }

    /// <summary>Snapshots current editor fields into a settings object.</summary>
    public AppSettings BuildSettings() => new()
    {
        ApiToken = ApiToken.Trim(),
        AppId = string.IsNullOrWhiteSpace(AppId) ? AppSettings.DefaultAppId : AppId.Trim(),
        IsDemo = IsDemo,
        Symbol = string.IsNullOrWhiteSpace(Symbol) ? AppSettings.DefaultSymbol : Symbol.Trim(),
        Currency = string.IsNullOrWhiteSpace(Currency) ? AppSettings.DefaultCurrency : Currency.Trim(),
        DurationMinutes = Math.Max(1, DurationMinutes),
        Stake = Math.Max(0.01m, Stake),
        AutonomyEnabled = AutonomyEnabled,
        DecisionIntervalMinutes = Math.Max(1, DecisionIntervalMinutes),
        LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? AppSettings.DefaultLlmBaseUrl : LlmBaseUrl.Trim(),
        LlmModel = string.IsNullOrWhiteSpace(LlmModel) ? AppSettings.DefaultLlmModel : LlmModel.Trim(),
        LlmApiKey = LlmApiKey.Trim(),
        MaxStake = 10.00m,
        MaxConcurrentContracts = 1,
        DailyLossCap = 50.00m,
        MinConfidence = 0.60,
        CooldownMinutesAfterLoss = 15
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            await Task.Run(() => _settingsService.Save(settings));
            ApplyToClient(settings);
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            ApplyToClient(settings);

            await _client.DisconnectAsync();
            // Authorize whenever a token is present — demo tokens authorize too,
            // and that is what surfaces the account balance.
            await _client.ConnectAsync(string.IsNullOrEmpty(ApiToken) ? null : ApiToken);
            _dashboard.SetSymbol(settings.Symbol);

            // Backfill the chart quickly, then stream live ticks.
            try
            {
                var history = await _client.GetTicksHistoryAsync(settings.Symbol, 200);
                _dashboard.AddHistory(history);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Connected, but history failed: {ex.Message}";
            }

            try
            {
                await _client.SubscribeTicksAsync(settings.Symbol);
                StatusMessage = string.IsNullOrEmpty(ApiToken)
                    ? $"Connected to {settings.Symbol} (no token — live feed needs authorization)."
                    : $"Connected and authorized on {settings.Symbol}.";
            }
            catch (DerivApiException ex) when (ex.Code == "InvalidSymbol")
            {
                StatusMessage =
                    $"Connected, but live ticks for {settings.Symbol} need a token ({ex.Message}). " +
                    "Paste your Deriv demo token above and press Connect.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Live feed unavailable: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connect failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyToClient(AppSettings settings)
    {
        _client.AppId = settings.AppId;
    }
}