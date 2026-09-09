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
    private bool respectMarketHours;

    [ObservableProperty]
    private bool overlapsOnly;

    [ObservableProperty]
    private string webhookUrl = "";

    [ObservableProperty]
    private bool isDiscordWebhook = true;

    [ObservableProperty]
    private bool webhookOnTrade = true;

    [ObservableProperty]
    private bool webhookOnMilestone = true;

    [ObservableProperty]
    private bool webhookOnCircuitBreaker = true;

    [ObservableProperty]
    private int logLevel = 1;

    public IReadOnlyList<string> LogLevels { get; } = new[] { "Debug", "Info", "Warn", "Error" };

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
        RespectMarketHours = settings.RespectMarketHours;
        OverlapsOnly = settings.OverlapsOnly;
        WebhookUrl = settings.WebhookUrl;
        IsDiscordWebhook = settings.IsDiscordWebhook;
        WebhookOnTrade = settings.WebhookOnTrade;
        WebhookOnMilestone = settings.WebhookOnMilestone;
        WebhookOnCircuitBreaker = settings.WebhookOnCircuitBreaker;
        LogLevel = settings.LogLevel;
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
        CooldownMinutesAfterLoss = 15,
        RespectMarketHours = RespectMarketHours,
        OverlapsOnly = OverlapsOnly,
        WebhookUrl = WebhookUrl?.Trim() ?? "",
        IsDiscordWebhook = IsDiscordWebhook,
        WebhookOnTrade = WebhookOnTrade,
        WebhookOnMilestone = WebhookOnMilestone,
        WebhookOnCircuitBreaker = WebhookOnCircuitBreaker,
        LogLevel = LogLevel
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // Real-money guard: require explicit confirmation.
        if (!IsDemo)
        {
            var result = System.Windows.MessageBox.Show(
                "You are about to save settings for a REAL MONEY account. " +
                "Trading with real money carries significant risk of financial loss.\n\n" +
                "Do you want to continue?",
                "⚠ Real Money Warning",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage = "Real money settings not saved — user cancelled.";
                return;
            }
        }

        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            await Task.Run(() => _settingsService.Save(settings));
            ApplyToClient(settings);

            StatusMessage = IsDemo ? "Settings saved (demo)." : "Settings saved (REAL MONEY — trade carefully).";
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