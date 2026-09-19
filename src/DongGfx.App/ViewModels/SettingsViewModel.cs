using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.ViewModels;

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

    // Primary-client new-platform identity (set by the Accounts tab's
    // "Set as primary" switch; not user-edited here). Round-tripped through
    // Load/BuildSettings so a later Settings save cannot wipe the switch.
    [ObservableProperty]
    private bool primaryNewPlatform;

    [ObservableProperty]
    private string primaryDerivAppId = "";

    [ObservableProperty]
    private string primaryDerivAccountId = "";

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
    private bool metricsDigestEnabled = true;

    [ObservableProperty]
    private int metricsDigestIntervalHours = 6;

    /// <summary>Hours an unlock may stay armed before the staleness alert
    /// fires (0 = alert disabled). Default mirrors AppSettings.</summary>
    [ObservableProperty]
    private int armStalenessHours = 4;

    /// <summary>Hard ceiling on the manual trade surfaces' stake (null/
    /// empty box = no extra limit). Editable alongside Stake; negatives are
    /// dropped on build.</summary>
    [ObservableProperty]
    private decimal? manualMaxStake;

    [ObservableProperty]
    private int logLevel = 1;

    public IReadOnlyList<string> LogLevels { get; } = new[] { "Debug", "Info", "Warn", "Error" };

    [ObservableProperty]
    private string statusMessage = "Settings load on startup; Save writes them to %APPDATA%\\tf\\data.";

    /// <summary>Human-readable trading-mode label.</summary>
    public string ModeLabel => IsDemo ? "Demo" : "Real";

    // Live webhook URL validation: recomputed whenever the URL or the
    // platform format changes, so a bad URL is flagged as it is typed —
    // before any save or test post.
    public WebhookUrlSeverity WebhookUrlSeverity =>
        WebhookUrlValidator.Validate(WebhookUrl, IsDiscordWebhook).Severity;

    public string WebhookValidationMessage =>
        WebhookUrlValidator.Validate(WebhookUrl, IsDiscordWebhook).Message;

    partial void OnWebhookUrlChanged(string value)
    {
        OnPropertyChanged(nameof(WebhookUrlSeverity));
        OnPropertyChanged(nameof(WebhookValidationMessage));
    }

    partial void OnIsDiscordWebhookChanged(bool value)
    {
        OnPropertyChanged(nameof(WebhookUrlSeverity));
        OnPropertyChanged(nameof(WebhookValidationMessage));
    }

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
        PrimaryNewPlatform = settings.PrimaryNewPlatform;
        PrimaryDerivAppId = settings.PrimaryDerivAppId;
        PrimaryDerivAccountId = settings.PrimaryDerivAccountId;
        Symbol = settings.Symbol;
        Currency = settings.Currency;
        DurationMinutes = settings.DurationMinutes;
        Stake = settings.Stake;
        ManualMaxStake = settings.ManualMaxStake > 0 ? settings.ManualMaxStake : null;
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
        MetricsDigestEnabled = settings.MetricsDigestEnabled;
        MetricsDigestIntervalHours = settings.MetricsDigestIntervalHours;
        ArmStalenessHours = settings.ArmStalenessHours;
        LogLevel = settings.LogLevel;
        StatusMessage = "Settings loaded.";
    }

    /// <summary>Snapshots current editor fields into a settings object.</summary>
    public AppSettings BuildSettings() => new()
    {
        ApiToken = ApiToken.Trim(),
        AppId = string.IsNullOrWhiteSpace(AppId) ? AppSettings.DefaultAppId : AppId.Trim(),
        IsDemo = IsDemo,
        PrimaryNewPlatform = PrimaryNewPlatform,
        PrimaryDerivAppId = PrimaryDerivAppId,
        PrimaryDerivAccountId = PrimaryDerivAccountId,
        Symbol = string.IsNullOrWhiteSpace(Symbol) ? AppSettings.DefaultSymbol : Symbol.Trim(),
        Currency = string.IsNullOrWhiteSpace(Currency) ? AppSettings.DefaultCurrency : Currency.Trim(),
        DurationMinutes = Math.Max(1, DurationMinutes),
        Stake = Math.Max(0.01m, Stake),
        ManualMaxStake = Math.Max(0m, ManualMaxStake ?? 0m),
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
        MetricsDigestEnabled = MetricsDigestEnabled,
        MetricsDigestIntervalHours = Math.Clamp(MetricsDigestIntervalHours, 1, 168),
        ArmStalenessHours = Math.Clamp(ArmStalenessHours, 0, 72),
        LogLevel = LogLevel
    };

    /// <summary>Re-points the primary client (Dashboard, Trades and Brain
    /// surfaces) at a hub account: verifies the account's type via the new
    /// platform's discovery (mandatory — the OTP socket carries no
    /// is_virtual, and an unverified verdict must never reach the gate),
    /// re-wires the primary DerivClient to the account's transport, and
    /// only then persists. The real-money gate is untouched — a real
    /// account still refuses every real trade until the session unlock is
    /// armed, on every surface.</summary>
    [RelayCommand]
    private async Task SetPrimaryAsync(AccountConnection? account)
    {
        if (account is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            account.ApplyToPrimarySettings(settings);

            await _client.DisconnectAsync();
            _client.IsVirtualOverride = null;

            // Discovery IS the demo/real verification for a PAT primary.
            // It must succeed before anything is persisted or connected:
            // without it the gate would see an unverified account.
            await PrimaryClientWiring.ApplyAsync(_client, settings);

            await _client.ConnectAsync(
                string.IsNullOrEmpty(settings.ApiToken) ? null : settings.ApiToken);
            try
            {
                await _client.SubscribeTicksAsync(settings.Symbol);
            }
            catch
            {
                // Tick subscription is best-effort on switch; history and
                // balance still confirm the account.
            }

            // Everything succeeded — persist and sync the editor.
            _settingsService.Save(settings);
            ApiToken = settings.ApiToken;
            IsDemo = settings.IsDemo;
            AppId = settings.AppId;
            PrimaryNewPlatform = settings.PrimaryNewPlatform;
            PrimaryDerivAppId = settings.PrimaryDerivAppId;
            PrimaryDerivAccountId = settings.PrimaryDerivAccountId;

            StatusMessage =
                $"Primary account switched to {account.DisplayName}. " +
                "Real accounts still require the session unlock before any real trade.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Switch failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

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

    /// <summary>Posts a test message to the webhook URL currently typed in
    /// the editor (not the last-saved settings) so a misconfigured URL is
    /// caught here instead of silently failing on a real trade event.</summary>
    [RelayCommand]
    private async Task TestWebhookAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var webhook = new WebhookService
            {
                WebhookUrl = WebhookUrl?.Trim(),
                IsDiscord = IsDiscordWebhook
            };
            var (ok, message) = await webhook.TestConnectionAsync();
            StatusMessage = ok ? $"✅ {message}" : $"❌ {message}";
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