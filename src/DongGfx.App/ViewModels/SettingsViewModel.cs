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

    /// <summary>Pass-through mirror of the brain engine loop's persisted
    /// running state (TerminalViewModel owns it; the settings editor never
    /// shows it). BuildSettings copies it back so a settings save can never
    /// silently stop the restored loop.</summary>
    [ObservableProperty]
    private bool fxBrainRunning;

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
    private bool analystEnabled = true;

    [ObservableProperty]
    private bool analystMemoryEnabled = true;

    [ObservableProperty]
    private bool riskNarratorEnabled = true;

    /// <summary>Nightly genetic lab (journal-only walk-forward evidence).
    /// Default mirrors AppSettings.</summary>
    [ObservableProperty]
    private bool fxLabEnabled = true;

    /// <summary>TP1 partial prototype: when armed, the Profit Brain's first
    /// rung EXECUTES through the Exit Brain's close path. Off by default;
    /// arming requires the confirm dialog (RequestTp1ToggleAsync) and is
    /// journaled as an explicit operator act.</summary>
    [ObservableProperty]
    private bool fxExecuteTp1Partials;

    [ObservableProperty]
    private string tp1ToggleHint = "TP1 partials: OFF — the allocation plan is advisory only";

    /// <summary>True while the TP1 rung is armed but HELD by the overdue
    /// plan-% review circuit breaker — the hint line spells out why.</summary>
    [ObservableProperty]
    private bool tp1BreakerHold;

    /// <summary>The open (unreviewed) plan-% recommendations, newest first —
    /// what the operator can act on or clear in-app when the breaker holds.
    /// Refilled by RefreshTp1Hint from the same ledger the breaker reads.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Tp1PlanReviewItem> OpenTp1Reviews { get; } = new();

    /// <summary>One actionable plan-% review row (label pre-formatted for
    /// the list, so the XAML binds plain text).</summary>
    public sealed record Tp1PlanReviewItem(string Id, string Label, bool IsStale);

    /// <summary>The shared gate/breaker tunables, editable in-app. Rebuilt by
    /// LoadTp1Tunables; written back to the SAME file the watcher reads by
    /// SaveTp1TunablesCommand, so retuning never needs hand-edited JSON and
    /// cannot leave the two sides inconsistent.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Tp1TunableRow> Tp1Tunables { get; } = new();

    /// <summary>Save/validation feedback for the tunables editor.</summary>
    [ObservableProperty]
    private string tp1TunablesStatus = "";

    /// <summary>Key -> (label, hint) for the tunables editor. The labels are
    /// what a human sees; the keys are what the shared file carries.</summary>
    private static readonly (string Key, string Label, string Hint)[] TunableLabels =
    {
        ("min_graded", "Min graded rungs", "settled rungs before the gate fires"),
        ("net_r", "Down band (R)", "net R at or below this recommends LOWER"),
        ("up_net_r", "Up band (R)", "net R at or above this recommends HIGHER"),
        ("step_r", "Step sensitivity (R)", "R of mean per full step"),
        ("step_pct", "Base step (%)", "plan points per step"),
        ("min_pct", "Plan % floor", "lowest candidate plan %"),
        ("max_pct", "Plan % ceiling", "highest candidate plan %"),
        ("score_min_graded", "Score min graded", "post-close rungs needed to score"),
        ("step_feedback_min", "Feedback min sample", "scored reviews before the step adapts"),
        ("step_feedback_pct", "Feedback step (%)", "step change when acting helps/fails"),
        ("step_min", "Step min (%)", "narrowest allowed step"),
        ("step_max", "Step max (%)", "widest allowed step"),
        ("step_max_drift", "Step drift bound (%)", "max drift from the base step"),
        ("stale_days", "Stale review (days)", "open review age before the hold"),
    };

    /// <summary>Refills the tunables editor from the shared config (rebuilt
    /// rather than mutated, so the TextBoxes always show what is on disk).</summary>
    public void LoadTp1Tunables()
    {
        var current = Infrastructure.Tp1PlanGateConfig.Loaded;
        Tp1Tunables.Clear();
        foreach (var (key, label, hint) in TunableLabels)
        {
            var value = current.TryGetValue(key, out var v) ? v : DefaultTunable(key);
            Tp1Tunables.Add(new Tp1TunableRow(key, label, hint, value));
        }

        Tp1TunablesStatus = "";
    }

    private static double DefaultTunable(string key) => key switch
    {
        "min_graded" => 3,
        "net_r" => -1.0,
        "up_net_r" => 1.0,
        "step_r" => 1.0,
        "step_pct" => 5.0,
        "min_pct" => 0.0,
        "max_pct" => 60.0,
        "score_min_graded" => 1,
        "step_feedback_min" => 3,
        "step_feedback_pct" => 2.0,
        "step_min" => 1.0,
        "step_max" => 15.0,
        "step_max_drift" => 3.0,
        "stale_days" => 7,
        _ => 0.0,
    };

    /// <summary>Parses, validates and writes the edited tunables back to the
    /// shared config file — consuming every value (the bands, step and drift
    /// bound included) so an incoherent gate is rejected before it lands.</summary>
    [RelayCommand]
    private void SaveTp1Tunables()
    {
        var values = new System.Collections.Generic.Dictionary<string, double>();
        var problems = new System.Collections.Generic.List<string>();
        foreach (var row in Tp1Tunables)
        {
            if (double.TryParse(row.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                values[row.Key] = v;
            }
            else
            {
                problems.Add($"{row.Label}: '{row.Value}' is not a number");
            }
        }

        if (problems.Count == 0)
        {
            problems.AddRange(Infrastructure.Tp1PlanGateConfig.Validate(values));
        }

        if (problems.Count > 0)
        {
            Tp1TunablesStatus = "⚠ " + string.Join("; ", problems);
            StatusMessage = "Tunables NOT saved — fix the flagged values.";
            return;
        }

        if (Infrastructure.Tp1PlanGateConfig.TrySave(values, out var error))
        {
            Infrastructure.Tp1PlanBreaker.Invalidate();
            LoadTp1Tunables();   // re-read to confirm what actually landed
            Tp1TunablesStatus =
                "Saved to the shared config — the watcher and the app now read these values.";
            StatusMessage = Tp1TunablesStatus;
            RefreshTp1Hint();
        }
        else
        {
            Tp1TunablesStatus = $"⚠ could not write the shared config: {error}";
            StatusMessage = Tp1TunablesStatus;
        }
    }

    /// <summary>Discards unsaved edits by re-reading the shared config.</summary>
    [RelayCommand]
    private void RevertTp1Tunables()
    {
        Infrastructure.Tp1PlanGateConfig.Refresh();
        LoadTp1Tunables();
        StatusMessage = "Tunables reverted to the shared config.";
    }

    /// <summary>Maps colormap picker: "Auto" or a colormap name.</summary>
    [ObservableProperty]
    private string mapsColormap = "Auto";

    [ObservableProperty]
    private int metricsDigestIntervalHours = 6;

    /// <summary>Hours an unlock may stay armed before the staleness alert
    /// fires (0 = alert disabled). Default mirrors AppSettings.</summary>
    [ObservableProperty]
    private int armStalenessHours = 4;

    /// <summary>Minimum entry confidence for dispatch (0 = off): signals
    /// below it are journaled and counted toward the soak but not traded.
    /// Applied live by ConfigureFromSettings and on save.</summary>
    [ObservableProperty]
    private double fxMinEntryConfidence = 0.0;

    /// <summary>Applies the floor to the live engine the moment the editor
    /// changes it (and on Load) — the same apply-without-a-restart pattern
    /// the TP1 arm uses. Clamped to [0,1] so a hand-typed value cannot
    /// demand confidence above 1.0 (which would block every entry).</summary>
    partial void OnFxMinEntryConfidenceChanged(double value)
    {
        Services.FxEngineHost.MinEntryConfidence = Math.Clamp(value, 0, 1);
    }

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

    /// <summary>Injected confirmation hook (App layer supplies the dialog).
    /// Returns true when the operator confirmed arming TP1 execution.
    /// Null (tests, headless) = refuse to arm — fail-closed.</summary>
    public Func<string, bool>? ConfirmTp1Arm { get; set; }

    /// <summary>True while Load is restoring persisted state. A restore is
    /// not a new operator act: the confirmation gate must not re-prompt for
    /// a decision the operator already made (it blocked startup with a modal
    /// on every launch — 2026-10-07). Genuine UI toggles still prompt.
    /// The restore's audit row is written by ConfigureFromSettings.</summary>
    private bool _loading;

    /// <summary>Injected confirmation hook (App layer supplies the dialog).
    /// Returns true when the operator confirmed saving settings for a
    /// real-money account. Null (tests, headless) = refuse — fail-closed, so
    /// nothing can write real-money settings without an explicit prompt.</summary>
    public Func<string, bool>? ConfirmRealMoneySave { get; set; }

    /// <summary>The arming gate. Flipping TP1 execution ON demands an
    /// explicit confirmation; flipping OFF is always allowed (and clears
    /// the static arm immediately — fail-safe direction). The change is
    /// journaled as an operator act and reflected in the hint line.</summary>
    partial void OnFxExecuteTp1PartialsChanged(bool value)
    {
        // Restore (Load) applies the persisted arm silently — only a live
        // operator toggle earns the confirmation dialog.
        if (value && !_loading)
        {
            var summary = "Execute the Profit Brain's TP1 rung live: once per trade, " +
                "when price crosses the armed target, the plan's percentage of the " +
                "position is closed through the Exit Brain's path. This places REAL " +
                "demo orders beyond the advisory boundary.";
            var confirmed = ConfirmTp1Arm?.Invoke(summary) ?? false;
            if (!confirmed)
            {
                // Revert the toggle; the property-changed recursion is guarded
                // by the value check (false != the pending true).
                FxExecuteTp1Partials = false;
                Tp1BreakerHold = false;
                Tp1ToggleHint = "TP1 partials: OFF — arming was not confirmed";
                StatusMessage = "TP1 execution NOT armed (confirmation declined).";
                return;
            }
        }

        // Apply immediately (the App config pass re-applies from settings
        // on save; this makes the toggle live without a restart).
        Services.FxEngineHost.ExecuteTp1Partials = value;
        RefreshTp1Hint();
    }

    /// <summary>Recomputes the arming hint, including the overdue plan-%
    /// review circuit-breaker state so an operator sees that a rung is not
    /// just armed but HELD. Called on load, on toggle, and whenever the
    /// settings surface is shown; the ledger read is cheap.</summary>
    public void RefreshTp1Hint()
    {
        var held = FxExecuteTp1Partials
            && Infrastructure.Tp1PlanBreaker.IsHeld(
                Infrastructure.SettingsService.DataDir,
                System.DateTimeOffset.UtcNow);
        Tp1BreakerHold = held;
        Tp1ToggleHint = BuildTp1Hint(held);
        RefreshOpenTp1Reviews();
    }

    /// <summary>Refills the actionable plan-% review list from the watcher's
    /// ledger (the same file the breaker reads).</summary>
    private void RefreshOpenTp1Reviews()
    {
        var path = Infrastructure.Tp1PlanReviewLedger.PathFor(
            Infrastructure.SettingsService.DataDir);
        var open = Infrastructure.Tp1PlanReviewLedger.ReadOpen(
            path, System.DateTimeOffset.UtcNow);
        OpenTp1Reviews.Clear();
        foreach (var r in open)
        {
            OpenTp1Reviews.Add(new Tp1PlanReviewItem(r.Id, r.Label, r.IsStale));
        }
    }

    /// <summary>Act on an open plan-% review in-app: appends the same `acted`
    /// ledger event the CLI runbook writes, then drops the breaker cache so
    /// the rung releases on the next engine cycle. The human step the runbook
    /// leaves open, now reachable without a terminal.</summary>
    [RelayCommand]
    private void ActTp1Review(Tp1PlanReviewItem? review)
    {
        if (review is null)
        {
            return;
        }

        var path = Infrastructure.Tp1PlanReviewLedger.PathFor(
            Infrastructure.SettingsService.DataDir);
        var wrote = Infrastructure.Tp1PlanReviewLedger.MarkActed(
            path, review.Id, "acted in-app (Settings)");
        Infrastructure.Tp1PlanBreaker.Invalidate();
        StatusMessage = wrote
            ? $"TP1 plan-% review {review.Id} marked acted — the rung releases next cycle."
            : $"TP1 plan-% review {review.Id}: the ledger could not be written.";
        RefreshTp1Hint();
    }

    /// <summary>Clear an open plan-% review in-app (the condition resolved
    /// without a change) — the same `cleared` event the watcher folds, then
    /// release the breaker cache.</summary>
    [RelayCommand]
    private void ClearTp1Review(Tp1PlanReviewItem? review)
    {
        if (review is null)
        {
            return;
        }

        var path = Infrastructure.Tp1PlanReviewLedger.PathFor(
            Infrastructure.SettingsService.DataDir);
        var wrote = Infrastructure.Tp1PlanReviewLedger.MarkCleared(path, review.Id);
        Infrastructure.Tp1PlanBreaker.Invalidate();
        StatusMessage = wrote
            ? $"TP1 plan-% review {review.Id} cleared — the rung releases next cycle."
            : $"TP1 plan-% review {review.Id}: the ledger could not be written.";
        RefreshTp1Hint();
    }

    private string BuildTp1Hint(bool held)
    {
        if (!FxExecuteTp1Partials)
        {
            return "TP1 partials: OFF — the allocation plan is advisory only";
        }

        if (held)
        {
            return "\u26a0 TP1 partials: ARMED but HELD — an overdue plan-% review is "
                + "open, so no rung arms until it is acted on or cleared "
                + "(python scripts/watch_tp1_first_arm.py --plan-reviews)";
        }

        return "TP1 partials: ARMED — the first rung executes on the cross (once per trade)";
    }

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
        _loading = true;
        try
        {
            LoadCore(settings);
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadCore(AppSettings settings)
    {
        IsDemo = settings.IsDemo;
        Theme = Infrastructure.ThemeManager.Normalize(settings.Theme);
        AutonomyEnabled = settings.AutonomyEnabled;
        FxBrainRunning = settings.FxBrainRunning;
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
        AnalystEnabled = settings.AnalystEnabled;
        AnalystMemoryEnabled = settings.AnalystMemoryEnabled;
        RiskNarratorEnabled = settings.RiskNarratorEnabled;
        FxLabEnabled = settings.FxLabEnabled;
        FxExecuteTp1Partials = settings.FxExecuteTp1Partials;
        RefreshTp1Hint();   // reflect any overdue-review breaker hold at load
        LoadTp1Tunables();  // the shared config, editable in-app
        MapsColormap = settings.MapsColormap;
        ArmStalenessHours = settings.ArmStalenessHours;
        FxMinEntryConfidence = Math.Clamp(settings.FxMinEntryConfidence, 0, 1);
        LogLevel = settings.LogLevel;
        StatusMessage = "Settings loaded.";
    }

    /// <summary>Snapshots current editor fields into a settings object.</summary>
    public AppSettings BuildSettings() => new()
    {
        IsDemo = IsDemo,
        Theme = Theme,
        AutonomyEnabled = AutonomyEnabled,
        FxBrainRunning = FxBrainRunning,
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
        AnalystEnabled = AnalystEnabled,
        AnalystMemoryEnabled = AnalystMemoryEnabled,
        RiskNarratorEnabled = RiskNarratorEnabled,
        FxLabEnabled = FxLabEnabled,
        FxExecuteTp1Partials = FxExecuteTp1Partials,
        MapsColormap = MapsColormap,
        ArmStalenessHours = Math.Clamp(ArmStalenessHours, 0, 72),
        FxMinEntryConfidence = Math.Clamp(FxMinEntryConfidence, 0, 1),
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

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // Real-money guard: the confirmation dialog is injected (App supplies
        // it), so this stays dialog-free and testable. No hook (headless,
        // tests) = refuse — fail-closed, never a silent real-money save.
        if (!IsDemo)
        {
            var summary = "You are about to save settings for a REAL MONEY account. "
                + "Trading with real money carries significant risk of financial loss.";
            if (ConfirmRealMoneySave?.Invoke(summary) != true)
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

/// <summary>One editable row of the shared gate/breaker tunables: the key the
/// config file carries, a human label and hint, and the current value as text
/// (parsed and validated on save). A plain settable property is enough — the
/// collection is rebuilt from disk whenever the config is (re)loaded.</summary>
public sealed class Tp1TunableRow
{
    public Tp1TunableRow(string key, string label, string hint, double value)
    {
        Key = key;
        Label = label;
        Hint = hint;
        Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string Key { get; }

    public string Label { get; }

    public string Hint { get; }

    /// <summary>The current value as text; two-way bound to the editor's
    /// TextBox and parsed on save.</summary>
    public string Value { get; set; }
}
