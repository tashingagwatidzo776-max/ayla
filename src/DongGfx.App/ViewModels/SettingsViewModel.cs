using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Models;

namespace DongGfx.App.ViewModels;

/// <summary>
/// Settings editor for an MT5/forex-only DON G FX: FX brain caps, the
/// webhook, cycle-telemetry monitoring, logging and the demo/real flag the
/// real-money gate reads. The Deriv surface (API token, app id, market
/// symbol, stake/duration, LLM brain, growth plan) is gone with the binary
/// options integration.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    /// <summary>True = the configured account is a demo one. The real-money
    /// gate reads this: demo passes straight through, real demands the
    /// session unlock.</summary>
    [ObservableProperty]
    private bool isDemo = true;

    [ObservableProperty]
    private bool autonomyEnabled;

    [ObservableProperty]
    private bool webhookOnTrade = true;

    [ObservableProperty]
    private bool webhookOnMilestone = true;

    [ObservableProperty]
    private bool webhookOnCircuitBreaker = true;

    [ObservableProperty]
    private string webhookUrl = "";

    [ObservableProperty]
    private bool isDiscordWebhook = true;

    [ObservableProperty]
    private bool metricsDigestEnabled = true;

    [ObservableProperty]
    private int metricsDigestIntervalHours = 6;

    /// <summary>Hours an unlock may stay armed before the staleness alert
    /// fires (0 = alert disabled). Default mirrors AppSettings.</summary>
    [ObservableProperty]
    private int armStalenessHours = 4;

    /// <summary>Maximum volume (lots) for a single MT5 bridge order.
    /// 0 disables MT5 order placement entirely (fail-closed).</summary>
    [ObservableProperty]
    private decimal mt5MaxLots = 1.00m;

    /// <summary>Fx brain daily-loss stop (currency units below session start
    /// balance). 0 disables — not recommended.</summary>
    [ObservableProperty]
    private decimal mt5DailyLossCap = 25m;

    /// <summary>Fx brain equity floor (absolute). 0 = disabled.</summary>
    [ObservableProperty]
    private decimal mt5EquityFloor = 0m;

    /// <summary>The MT5 symbol last selected in the Terminal's Market
    /// Watch (fallback when FxSymbols is empty; the Terminal's selection
    /// writes this through the quiet-save path).</summary>
    [ObservableProperty]
    private string fxSymbol = "XAUUSD";

    /// <summary>CSV of symbols the FX brain runs — one engine per entry.</summary>
    [ObservableProperty]
    private string fxSymbols = "XAUUSDmicro,EURUSD,GBPUSD,USDJPY";

    /// <summary>Portfolio cap: total open lots across all FX symbols.</summary>
    [ObservableProperty]
    private decimal fxPortfolioMaxLots = 0.10m;

    /// <summary>News blackout half-window in minutes (both sides).</summary>
    [ObservableProperty]
    private int newsBlackoutMinutes = 15;

    /// <summary>Pinned MT5 terminal64.exe (empty = auto-discover).</summary>
    [ObservableProperty]
    private string mt5TerminalPath = "";

    // Last MT5 login (login-dialog prefill; the password is never persisted).
    [ObservableProperty]
    private string mt5LastLogin = "";

    [ObservableProperty]
    private string mt5LastServer = "";

    // Second slot of the dialog's Recent picker (previous successful switch).
    [ObservableProperty]
    private string mt5PrevLogin = "";

    [ObservableProperty]
    private string mt5PrevServer = "";

    [ObservableProperty]
    private int logLevel = 1;

    public IReadOnlyList<string> LogLevels { get; } = new[] { "Debug", "Info", "Warn", "Error" };

    /// <summary>UI theme — "Dark" (modern) or "Classic" (MT5-gray). Applied
    /// live on change and persisted with the next settings save.</summary>
    [ObservableProperty]
    private string theme = Infrastructure.ThemeManager.Dark;

    public System.Collections.Generic.IReadOnlyList<string> Themes { get; } =
        new[] { Infrastructure.ThemeManager.Dark, Infrastructure.ThemeManager.Classic };

    partial void OnThemeChanged(string value) =>
        Infrastructure.ThemeManager.Apply(value);

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

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Load(AppSettings settings)
    {
        IsDemo = settings.IsDemo;
        Theme = Infrastructure.ThemeManager.Normalize(settings.Theme);
        AutonomyEnabled = settings.AutonomyEnabled;
        Mt5MaxLots = settings.Mt5MaxLots;
        Mt5DailyLossCap = settings.Mt5DailyLossCap;
        Mt5EquityFloor = settings.Mt5EquityFloor;
        FxSymbol = settings.FxSymbol;
        FxSymbols = settings.FxSymbols;
        FxPortfolioMaxLots = settings.FxPortfolioMaxLots;
        NewsBlackoutMinutes = settings.NewsBlackoutMinutes;
        Mt5TerminalPath = settings.Mt5TerminalPath;
        Mt5LastLogin = settings.Mt5LastLogin;
        Mt5LastServer = settings.Mt5LastServer;
        Mt5PrevLogin = settings.Mt5PrevLogin;
        Mt5PrevServer = settings.Mt5PrevServer;
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
        IsDemo = IsDemo,
        Theme = Theme,
        AutonomyEnabled = AutonomyEnabled,
        Mt5MaxLots = Math.Max(0m, Mt5MaxLots),
        Mt5DailyLossCap = Math.Max(0m, Mt5DailyLossCap),
        Mt5EquityFloor = Math.Max(0m, Mt5EquityFloor),
        FxSymbol = string.IsNullOrWhiteSpace(FxSymbol) ? "XAUUSD" : FxSymbol.Trim(),
        FxSymbols = string.IsNullOrWhiteSpace(FxSymbols) ? "XAUUSDmicro" : FxSymbols,
        FxPortfolioMaxLots = Math.Max(0m, FxPortfolioMaxLots),
        NewsBlackoutMinutes = Math.Clamp(NewsBlackoutMinutes, 0, 120),
        Mt5TerminalPath = (Mt5TerminalPath ?? "").Trim(),
        Mt5LastLogin = (Mt5LastLogin ?? "").Trim(),
        Mt5LastServer = (Mt5LastServer ?? "").Trim(),
        Mt5PrevLogin = (Mt5PrevLogin ?? "").Trim(),
        Mt5PrevServer = (Mt5PrevServer ?? "").Trim(),
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

    /// <summary>Records a successful account switch for the dialog's
    /// Recent picker: the previous last becomes prev (two slots), the
    /// typed account becomes last. Signing into the same account again is
    /// a no-op — repeat logins must not push the two-slot history forward
    /// (A→B→A→A would otherwise forget B). Blanks are ignored. The
    /// password is never passed here, never stored.</summary>
    public void RecordMt5Login(string account, string server)
    {
        account = (account ?? "").Trim();
        server = (server ?? "").Trim();
        if (account.Length == 0 || server.Length == 0)
        {
            return;
        }
        if (account == Mt5LastLogin && server == Mt5LastServer)
        {
            return;
        }

        Mt5PrevLogin = Mt5LastLogin;
        Mt5PrevServer = Mt5LastServer;
        Mt5LastLogin = account;
        Mt5LastServer = server;
    }

    [RelayCommand]
    /// <summary>Programmatic save used by the Terminal's switches (FX brain
    /// autonomy ON/OFF, symbol sync). Deliberately skips the real-money
    /// confirmation dialog: these are single-field flips of an already-saved
    /// configuration, never a first entry into real-money territory (the
    /// gate still applies at every trade path).</summary>
    public async Task SaveSettingsQuietAsync()
    {
        try
        {
            var settings = BuildSettings();
            await Task.Run(() => _settingsService.Save(settings));
            StatusMessage = "Settings updated from the Terminal.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

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
}
