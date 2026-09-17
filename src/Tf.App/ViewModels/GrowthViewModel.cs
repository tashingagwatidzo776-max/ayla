using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.App.ViewModels;

/// <summary>
/// The deterministic Growth brain fleet. Each connected demo account runs its
/// own session that aims to grow a $5-equivalent bankroll: small fixed-risk
/// stakes, a short recovery ladder, a daily profit target, and a hard floor.
/// </summary>
public sealed partial class GrowthViewModel : ObservableObject
{
    private readonly MultiAccountHub _hub;
    private readonly GrowthPlanStore _planStore;
    private readonly Func<AppSettings> _settings;
    private readonly DashboardViewModel _dashboard;
    private readonly PerformanceTracker? _tracker;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private decimal budget = 5.00m;

    [ObservableProperty]
    private int riskPercent = 20;

    [ObservableProperty]
    private int targetPercent = 100;

    [ObservableProperty]
    private int floorPercent = 40;

    [ObservableProperty]
    private int recoverySteps = 3;

    [ObservableProperty]
    private int intervalMinutes = 1;

    [ObservableProperty]
    private int failureBackoffSeconds = 5;

    [ObservableProperty]
    private int maxAutoRestarts = 3;

    [ObservableProperty]
    private double restartBaseDelaySeconds = 5.0;

    [ObservableProperty]
    private double restartBackoffFactor = 3.0;

    [ObservableProperty]
    private string portfolioDrawdownCapText = "";

    /// <summary>Combined net P&amp;L across every growth account, for the portfolio row.</summary>
    [ObservableProperty]
    private string combinedPnlText = "$0.00";

    /// <summary>True while the portfolio drawdown governor is latched after a trip.</summary>
    [ObservableProperty]
    private bool isGovernorTripped;

    /// <summary>True while the amber pre-trip warning shows (combined daily
    /// drawdown at 80% of the portfolio cap; hidden when the cap trips).</summary>
    [ObservableProperty]
    private bool isGovernorWarned;

    /// <summary>Amber banner text for the pre-trip warning (empty = hidden).</summary>
    [ObservableProperty]
    private string governorWarningText = "";

    /// <summary>Banner text shown while the governor is latched (empty = hidden).</summary>
    [ObservableProperty]
    private string governorBannerText = "";

    [ObservableProperty]
    private string statusMessage = "Growth plan loaded.";

    public ObservableCollection<GrowthRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();

    /// <summary>One banner per account whose auto-restart budget is exhausted.</summary>
    public ObservableCollection<GaveUpBanner> GaveUpBanners { get; } = new();

    /// <summary>One banner per API-verified real-money account whose session
    /// unlock is not armed (typically right after app start). Real accounts
    /// stay locked by design until the typed-phrase unlock is re-armed.</summary>
    public ObservableCollection<LockedRealAccountBanner> LockedRealAccountBanners { get; } = new();

    /// <summary>One banner per account whose session unlock is ARMED but
    /// past the staleness threshold — real trading has been possible all
    /// this time without anyone re-armming deliberately. Toasts/webhooks go
    /// out-of-band; this is the in-app mirror of the same state.</summary>
    public ObservableCollection<StaleUnlockBanner> StaleUnlockBanners { get; } = new();

    /// <summary>Accounts listed in the in-tab unlock panel (the locked real
    /// accounts the phrase will arm — possibly several in one pass).</summary>
    public ObservableCollection<LockedRealAccountBanner> UnlockableAccounts { get; } = new();

    /// <summary>True while the in-tab real-money unlock panel is open.</summary>
    [ObservableProperty]
    private bool isUnlockPanelVisible;

    /// <summary>The typed confirmation phrase for the unlock panel.</summary>
    [ObservableProperty]
    private string unlockPhrase = "";

    /// <summary>Panel notice line (why the list is empty, etc.).
    /// Null hides the line entirely (NullToVisibility).</summary>
    [ObservableProperty]
    private string? unlockPanelNotice;

    public GrowthViewModel(MultiAccountHub hub, GrowthPlanStore planStore,
        Func<AppSettings> settings, DashboardViewModel dashboard,
        PerformanceTracker? tracker = null)
    {
        _hub = hub;
        _planStore = planStore;
        _settings = settings;
        _dashboard = dashboard;
        _tracker = tracker;
        _dispatcher = Dispatcher.CurrentDispatcher;

        LoadPlan();
        RebuildRows();

        _hub.AccountsChanged += OnAccountsChanged;
        _hub.GrowthActivity += OnGrowthActivity;
        _hub.GrowthActivity += OnPortfolioRefresh;
        _hub.RestartStateChanged += OnRestartStateChanged;
        _hub.PortfolioGovernorTripped += OnGovernorTripped;
        _hub.PortfolioGovernorWarning += OnGovernorWarning;
        _hub.GovernorRearmed += OnGovernorRearmed;
        _hub.RealMoneyRefused += OnRealMoneyRefused;
        _hub.UnlockStale += OnUnlockStale;
        RefreshPortfolioPnl();

        // Startup banner: session unlocks die with the process, so every app
        // start re-locks real-money accounts — surface them immediately
        // instead of leaving them to be discovered on the next start click.
        RebuildLockedBanners();

        // A session restored around an armed unlock may already be stale —
        // build the stale surface from the arm state on construction too.
        RebuildStaleBanners();

        // The hub restores a latched governor from the journal before this VM
        // exists — surface the breach immediately instead of waiting for an
        // event that already fired.
        if (_hub.IsGovernorTripped)
        {
            ShowGovernorBanner(_hub.GovernorTrippedNet ?? 0m);
            IsGovernorTripped = true;
        }
    }

    public bool AutonomyEnabled => _settings().AutonomyEnabled;

    private Controls.PnlCurveControl? _pnlChart;

    /// <summary>
    /// Feeds the Growth tab's P&amp;L curve with each account's cumulative
    /// daily P&amp;L (from the <see cref="PerformanceTracker"/>), refreshed on
    /// every new settled growth trade.
    /// </summary>
    public void AttachPnlChart(Controls.PnlCurveControl chart)
    {
        _pnlChart = chart;
        _hub.GrowthActivity -= OnActivityRefreshChart;
        _hub.GrowthActivity += OnActivityRefreshChart;
        RefreshPnlChart();
    }

    private void OnActivityRefreshChart(GrowthRunner _, string __) => OnUiThread(RefreshPnlChart);

    private void RefreshPnlChart()
    {
        var chart = _pnlChart;
        if (chart is null || _tracker is null)
        {
            return;
        }

        var series = new List<(string, IReadOnlyList<(DateTimeOffset, decimal)>)>();
        foreach (var row in Rows)
        {
            var points = _tracker.GetIntradayPnl(row.Connection.Config.Id);
            row.PnlSeries = points.Select(p => p.CumulativePnl).ToArray();
            if (points.Count > 0)
            {
                series.Add((row.Connection.DisplayName,
                    points.Select(p => (p.At, p.CumulativePnl)).ToList()));
            }
        }

        chart.SetSeries(series);
    }

    private void LoadPlan()
    {
        var plan = _planStore.Load();
        Budget = plan.StartBudget;
        RiskPercent = (int)Math.Round(plan.RiskFraction * 100);
        TargetPercent = (int)Math.Round(plan.DailyTargetFraction * 100);
        FloorPercent = (int)Math.Round(plan.FloorFraction * 100);
        RecoverySteps = plan.MaxRecoverySteps;
        IntervalMinutes = plan.IntervalMinutes;
        FailureBackoffSeconds = (int)Math.Round(plan.FailureBackoffSeconds);
        MaxAutoRestarts = plan.MaxAutoRestarts;
        RestartBaseDelaySeconds = plan.RestartBaseDelaySeconds;
        RestartBackoffFactor = plan.RestartBackoffFactor;
        PortfolioDrawdownCapText = plan.PortfolioDailyDrawdownCap?.ToString("0.##") ?? "";
    }

    private GrowthPlan BuildPlan() => new(
        StartBudget: Math.Max(0.50m, Budget),
        RiskFraction: Math.Clamp(RiskPercent / 100.0, 0.01, 0.5),
        MaxRecoverySteps: Math.Clamp(RecoverySteps, 1, 5),
        DailyTargetFraction: Math.Clamp(TargetPercent / 100.0, 0.05, 10.0),
        FloorFraction: Math.Clamp(FloorPercent / 100.0, 0.10, 0.9),
        MinStake: 1.00m,
        IntervalMinutes: Math.Clamp(IntervalMinutes, 1, 60),
        CooldownMinutesAfterLoss: 1,
        FailureBackoffSeconds: Math.Clamp(FailureBackoffSeconds, 1, 120),
        MaxAutoRestarts: Math.Clamp(MaxAutoRestarts, 0, 10),
        RestartBaseDelaySeconds: Math.Clamp(RestartBaseDelaySeconds, 0.5, 600.0),
        RestartBackoffFactor: Math.Clamp(RestartBackoffFactor, 1.0, 10.0),
        PortfolioDailyDrawdownCap: GrowthPlan.TryCreateDrawdownCap(PortfolioDrawdownCapText));

    [RelayCommand]
    private void SavePlan()
    {
        var plan = BuildPlan();
        _planStore.Save(plan);
        StatusMessage =
            $"Plan saved: grow a ${plan.StartBudget:0.##} bankroll · {plan.RiskFraction:P0} risk · " +
            $"+{plan.DailyTargetFraction:P0} target · {(1 - plan.FloorFraction):P0} max drawdown · " +
            $"{plan.MaxRecoverySteps}-step ladder.";
    }

    [RelayCommand]
    private void StartAll()
    {
        var autonomy = AutonomyEnabled;
        var plan = BuildPlan();
        var started = 0;
        var blocked = 0;
        foreach (var connection in _hub.Accounts)
        {
            if (!connection.IsConnected)
            {
                continue;
            }

            // Real-money accounts need the explicit typed-phrase unlock for
            // this session before any engine may start on them — the in-tab
            // unlock panel is the way to arm it (modals are gone).
            if (!connection.Config.IsDemo && !_hub.IsRealMoneyUnlocked(connection.Config.Id))
            {
                blocked++;
                continue;
            }

            var runner = _hub.StartGrowth(connection, plan, _settings, () => _dashboard.IsKillSwitchEngaged);
            if (runner is not null)
            {
                var row = Rows.FirstOrDefault(r => r.Connection == connection);
                if (row is not null)
                {
                    row.Runner = runner;
                }
                started++;
            }
        }

        // The autonomy notice must survive the result line — it used to be a
        // dead store overwritten by the status below before ever being seen.
        StatusMessage = (started == 0
            ? (blocked > 0
                ? "No engines started — real-money accounts stayed locked."
                : "No connected demo accounts to run — connect accounts on this tab first.")
            : $"Growth engine started on {started} account(s). Watch the activity log below.")
            + (blocked > 0
                ? $" {blocked} real-money account(s) stayed locked — open the unlock panel (🔒) to arm this session."
                : "")
            + (autonomy
                ? ""
                : " Autonomy is OFF (Settings → Autonomy): engines run decisions only — " +
                  "no trades are placed until you enable it.");
    }

    /// <summary>Opens the in-tab unlock panel listing every locked real-
    /// money account (verified or not — the panel shows the verification
    /// state honestly; unverified accounts stay gated by the API verdict
    /// even when the phrase arms the session unlock).</summary>
    [RelayCommand]
    private void ShowUnlockPanel()
    {
        UnlockableAccounts.Clear();
        foreach (var connection in _hub.Accounts)
        {
            if (!connection.Config.IsDemo && !_hub.IsRealMoneyUnlocked(connection.Config.Id))
            {
                var apiSaysVirtual = connection.ApiVerifiedVirtual == true;
                UnlockableAccounts.Add(new LockedRealAccountBanner(
                    connection.Config.Id,
                    connection.DisplayName,
                    connection.ApiVerifiedVirtual is false
                        ? "verified REAL by the API"
                        : apiSaysVirtual
                            ? "API says virtual (demo funds) — fix the account flag"
                            : "type unverified (fails closed until connected)",
                    apiSaysVirtual));
            }
        }

        UnlockPanelNotice = UnlockableAccounts.Count == 0
            ? "No locked real-money accounts — nothing to unlock."
            : null;
        UnlockPhrase = "";
        IsUnlockPanelVisible = true;
    }

    /// <summary>Arms the session unlock for every account listed in the
    /// panel — plus the Brain/Trades tabs' shared manual unlock — after the
    /// confirmation phrase was typed exactly. A wrong phrase unlocks
    /// nothing.</summary>
    /// <summary>One-click fix for the one mismatch the app can repair: the
    /// API verified the account as virtual while the config claims real.
    /// The hub re-labels, persists, and journals; the panel refreshes so the
    /// fixed account drops out of the unlock list (it is a demo account
    /// now — the gate passes it through without any unlock).</summary>
    [RelayCommand]
    private void FixAccountFlag(Guid accountId)
    {
        var connection = _hub.Accounts.FirstOrDefault(a => a.Config.Id == accountId);
        if (!_hub.FixAccountFlagToDemo(accountId))
        {
            StatusMessage = "Could not fix the account flag — it must be API-verified as " +
                            "virtual (demo funds) and currently claim real.";
            return;
        }

        StatusMessage = $"{(connection?.DisplayName ?? "Account")} re-labelled to demo — " +
                        "the mismatch refusal is cleared.";
        ShowUnlockPanel(); // refresh in place: the fixed account drops out
    }

    [RelayCommand]
    private void ConfirmUnlock()
    {
        if (!string.Equals(UnlockPhrase?.Trim(), RealMoneyGate.ConfirmationPhrase, StringComparison.Ordinal))
        {
            StatusMessage = "Confirmation phrase did not match — nothing was unlocked.";
            UnlockPhrase = "";
            return;
        }

        // One phrase, one session: the manual surfaces (Brain tab cycles,
        // Trades tab trade) share this unlock for the primary client. Their
        // own gates still require the account to be config-real AND API-
        // verified real — arming alone trades nothing.
        //
        // Arm via the batch seam so the whole pass writes ONE summary audit
        // entry (REAL_MONEY_UNLOCK_ARMED) instead of one per account.
        var newlyArmed = new List<LockedRealAccountBanner>();
        foreach (var account in UnlockableAccounts)
        {
            if (_hub.TryArmUnlock(account.AccountId))
            {
                newlyArmed.Add(account);
            }
        }

        var manualWasLocked = !ManualRealMoneyGate.IsUnlocked;
        ManualRealMoneyGate.Arm();

        _hub.JournalUnlockArmed(
            newlyArmed.Select(b => _hub.Accounts.FirstOrDefault(a => a.Config.Id == b.AccountId)).ToList(),
            manualSurfaces: manualWasLocked);

        var scope = new List<string>();
        if (newlyArmed.Count > 0)
        {
            scope.Add($"{newlyArmed.Count} hub account(s)");
        }

        if (manualWasLocked)
        {
            scope.Add("manual surfaces");
        }

        if (scope.Count == 0)
        {
            StatusMessage = "Nothing new to unlock — everything listed was already armed this session.";
        }
        else
        {
            var joined = string.Join(" + ", scope);
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — real-money unlock armed (journalled): {joined} (this session)");
            StatusMessage = $"Real-money trading unlocked for {joined} this session.";
        }

        UnlockPhrase = "";
        IsUnlockPanelVisible = false;
        RebuildRows();
        RebuildLockedBanners();
    }

    /// <summary>Closes the unlock panel without arming anything.</summary>
    [RelayCommand]
    private void CancelUnlock()
    {
        UnlockPhrase = "";
        IsUnlockPanelVisible = false;
    }

    private void OnRealMoneyRefused(AccountConnection connection, string explanation)
    {
        OnUiThread(() =>
        {
            StatusMessage = explanation;
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — {connection.DisplayName}: {explanation}");
        });
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        await _hub.StopGrowthAllAsync();
        foreach (var row in Rows)
        {
            row.Runner = null;
        }

        StatusMessage = "All growth engines stopped.";
        OnUiThread(() => ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — stopped all engines"));
    }

    private void OnAccountsChanged()
    {
        OnUiThread(() =>
        {
            RebuildRows();
            RebuildLockedBanners();
            RebuildStaleBanners();
        });
    }

    private void OnGrowthActivity(GrowthRunner runner, string line)
    {
        OnUiThread(() =>
        {
            ActivityLog.Insert(0, $"{runner.Connection.DisplayName}: {line}");
            if (ActivityLog.Count > 400)
            {
                ActivityLog.RemoveAt(ActivityLog.Count - 1);
            }
        });
    }

    /// <summary>
    /// Refreshes the portfolio row from the hub's combined growth net —
    /// called on every growth activity line (settlements, stops, trips).
    /// </summary>
    private void OnPortfolioRefresh(GrowthRunner _, string __) => OnUiThread(RefreshPortfolioPnl);

    private void RefreshPortfolioPnl()
    {
        var net = _hub.CombinedGrowthNetPnl();
        CombinedPnlText = $"{(net >= 0 ? "+" : "−")}${Math.Abs(net):0.00}";
    }

    private void OnGovernorTripped(decimal net)
    {
        OnUiThread(() =>
        {
            IsGovernorTripped = true;
            // The red latch banner supersedes the amber pre-trip warning.
            IsGovernorWarned = false;
            GovernorWarningText = "";
            var signedNet = ShowGovernorBanner(net);
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — portfolio drawdown cap breached ({signedNet})");
        });
    }

    private void OnGovernorWarning(decimal used)
    {
        OnUiThread(() =>
        {
            IsGovernorWarned = true;
            // Drawdown is displayed as a negative number, like the trip banner.
            var signedUsed = $"−${Math.Abs(used):0.##}";
            GovernorWarningText = $"Portfolio drawdown warning — combined {signedUsed} is at 80% of the cap. " +
                                  "Consider stopping engines before the governor trips.";
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — portfolio drawdown at 80% of cap ({signedUsed})");
        });
    }

    /// <summary>Builds the latched-governor banner and status from the net
    /// that breached the cap (shared by live trips and the launch-time seed);
    /// returns the signed net text used in the banner.</summary>
    private string ShowGovernorBanner(decimal net)
    {
        var signedNet = $"{(net >= 0 ? "+" : "−")}${Math.Abs(net):0.##}";
        GovernorBannerText = $"Portfolio drawdown governor engaged — combined net {signedNet} " +
                             "breached the cap. All engines stopped until you re-arm.";
        StatusMessage = $"🛑 Portfolio drawdown governor: combined net {signedNet} breached " +
                        "the cap — all engines stopped. Re-arm to resume.";
        return signedNet;
    }

    private void OnGovernorRearmed()
    {
        OnUiThread(() =>
        {
            IsGovernorTripped = false;
            IsGovernorWarned = false;
            GovernorBannerText = "";
            GovernorWarningText = "";
            StatusMessage = "Portfolio governor re-armed — engines may start again.";
            ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — portfolio governor re-armed");
            RefreshPortfolioPnl();
        });
    }

    /// <summary>
    /// Manual re-arm after the portfolio drawdown governor tripped: clears
    /// the latch and re-baselines the daily drawdown so engines may start.
    /// </summary>
    [RelayCommand]
    private void RearmGovernor()
    {
        _hub.RearmGovernor();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var connection in _hub.Accounts)
        {
            var row = new GrowthRowViewModel(connection);
            var attempt = _hub.RestartAttempts.TryGetValue(connection.Config.Id, out var a) ? a : 0;
            var gaveUp = _hub.IsGivenUp(connection.Config.Id);
            row.IsGaveUp = gaveUp;
            row.RestartText = DescribeRestartState(attempt, gaveUp);
            Rows.Add(row);
        }

        GaveUpBanners.Clear();
        foreach (var row in Rows.Where(r => r.IsGaveUp))
        {
            GaveUpBanners.Add(new GaveUpBanner(row.Connection.Config.Id, row.Connection.DisplayName));
        }
    }

    /// <summary>Rebuilds the locked-real-account banners from current hub
    /// state: an API-verified real account (config agrees) whose session
    /// unlock is not armed gets one banner. Runs on construction (unlocks
    /// die with the process, so app start always re-locks) and whenever the
    /// account set changes.</summary>
    /// <summary>Test seam: rebuilds the locked-real-account banners from
    /// current hub state without raising hub events (mirrors the hub's
    /// TestRaise* pattern).</summary>
    internal void TestRebuildLockedBanners() => RebuildLockedBanners();

    internal void TestRebuildStaleBanners() => RebuildStaleBanners();

    /// <summary>The hub flagged an arm as stale (threshold elapsed) — the
    /// banner list mirrors it. The event fires on a background thread in
    /// production; OnUiThread marshals to the dispatcher.</summary>
    private void OnUnlockStale((Guid AccountId, string Name, TimeSpan Age, int Cycles) payload) =>
        OnUiThread(RebuildStaleBanners);

    /// <summary>Rebuilds the stale-unlock banners straight from the hub's
    /// arm state: every account whose unlock is armed for longer than the
    /// staleness threshold gets one. Runs on construction (a hub restored
    /// from a latched state may already be stale), on account changes, and
    /// on every UnlockStale flag.</summary>
    private void RebuildStaleBanners()
    {
        StaleUnlockBanners.Clear();
        var threshold = _hub.ArmStalenessThreshold;
        if (threshold is not { } limit || limit <= TimeSpan.Zero)
        {
            return; // alerting disabled — no stale surface either
        }

        var now = _hub.UtcNow; // the arm timestamps' own clock — never the wall
        foreach (var kv in _hub.UnlockArmedAtUtc.OrderBy(kv => kv.Value))
        {
            var age = now - kv.Value;
            if (age < limit)
            {
                // Not stale yet. The hub flags at age == threshold exactly,
                // so the banner uses the same boundary (>=) — otherwise an
                // event-driven rebuild at the exact due moment shows nothing.
                continue;
            }

            var connection = _hub.Accounts.FirstOrDefault(a => a.Config.Id == kv.Key);
            StaleUnlockBanners.Add(new StaleUnlockBanner(
                kv.Key,
                connection?.DisplayName ?? kv.Key.ToString()[..8],
                age.TotalHours >= 1 ? $"{(int)age.TotalHours}h{age.Minutes:00}m" : $"{age.TotalMinutes:0}m",
                $"{(int)limit.TotalHours}h"));
        }
    }

    private void RebuildLockedBanners()
    {
        LockedRealAccountBanners.Clear();
        foreach (var connection in _hub.Accounts)
        {
            // API-verified real (or never verified — fail closed) with a
            // config that agrees, and no session unlock: locked. A demo-
            // flagged config never shows here — its mismatch surfaces as a
            // refusal, not an unlock affordance.
            if (connection.Config.IsDemo)
            {
                continue;
            }

            if (_hub.IsRealMoneyUnlocked(connection.Config.Id))
            {
                continue;
            }

            LockedRealAccountBanners.Add(new LockedRealAccountBanner(
                connection.Config.Id,
                connection.DisplayName,
                connection.ApiVerifiedVirtual is null
                    ? "type unverified (fails closed)"
                    : "verified REAL by the API"));
        }
    }

    private void OnRestartStateChanged(Guid accountId, int attempt, bool gaveUp)
    {
        OnUiThread(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Connection.Config.Id == accountId);
            if (row is not null)
            {
                row.IsGaveUp = gaveUp;
                row.RestartText = DescribeRestartState(attempt, gaveUp);
            }

            var existing = GaveUpBanners.FirstOrDefault(b => b.AccountId == accountId);
            if (gaveUp && existing is null && row is not null)
            {
                GaveUpBanners.Add(new GaveUpBanner(accountId, row.Connection.DisplayName));
            }
            else if (!gaveUp && existing is not null)
            {
                GaveUpBanners.Remove(existing);
            }
        });
    }

    private static string DescribeRestartState(int attempt, bool gaveUp) => gaveUp
        ? $"gave up — {attempt} restarts used"
        : attempt > 0 ? $"restarts {attempt}" : "";

    /// <summary>
    /// Manually restarts one engine after the auto-restart budget was
    /// exhausted; also clears the gave-up state via the hub. Real-money
    /// accounts must pass the same typed-phrase unlock as StartAll first.
    /// </summary>
    [RelayCommand]
    private void RestartAccount(Guid accountId)
    {
        var connection = _hub.Accounts.FirstOrDefault(a => a.Config.Id == accountId);
        if (connection is null || !connection.IsConnected)
        {
            StatusMessage = "Cannot restart — the account is not connected.";
            return;
        }

        if (!connection.Config.IsDemo && !_hub.IsRealMoneyUnlocked(connection.Config.Id))
        {
            // No modal: route to the in-tab unlock panel (Start All does the
            // same) so the refusal and the affordance live in one place.
            StatusMessage = $"{connection.DisplayName} is locked — open the unlock panel to arm this session first.";
            ShowUnlockPanel();
            return;
        }

        var runner = _hub.StartGrowth(connection, BuildPlan(), _settings, () => _dashboard.IsKillSwitchEngaged);
        var row = Rows.FirstOrDefault(r => r.Connection == connection);
        if (row is not null && runner is not null)
        {
            row.Runner = runner;
        }

        StatusMessage = runner is null
            ? "Cannot restart — the account must be connected, and real-money starts " +
              "must pass the unlock (see the activity log for the exact refusal)."
            : $"Restarted the growth engine on {connection.DisplayName}.";
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}    /// <summary>One row in the growth grid; nests the account and its live runner.</summary>
public sealed partial class GrowthRowViewModel : ObservableObject
{
    /// <summary>Intraday cumulative P&amp;L series for the row's sparkline
    /// (one point per settled trade, from the PerformanceTracker).</summary>
    [ObservableProperty]
    private IReadOnlyList<decimal> pnlSeries = Array.Empty<decimal>();

    [ObservableProperty]
    private GrowthRunner? runner;

    [ObservableProperty]
    private string restartText = "";

    [ObservableProperty]
    private bool isGaveUp;

    public GrowthRowViewModel(AccountConnection connection)
    {
        Connection = connection;
    }

    public AccountConnection Connection { get; }
    public string DisplayName => Connection.DisplayName;
    public string LoginIdText => Connection.LoginIdText;
    public string StatusText => Connection.StatusText;
    public string BalanceText => Connection.BalanceText;
    public string SymbolText => Connection.Config.Symbol;
    public string BudgetText => Connection.Config.StartBudget.ToString("0.##");
    public bool IsConnected => Connection.IsConnected;
}

/// <summary>One gave-up banner in the growth tab; carries the restart target.</summary>
public sealed record GaveUpBanner(Guid AccountId, string AccountName);

/// <summary>One banner per API-verified real-money account whose session
/// unlock is not armed — the visible reminder that app restarts re-lock
/// real trading until the phrase is typed again. CanFixFlag marks panel
/// rows where the API has verified the account as VIRTUAL while the config
/// claims real — the one mismatch the app can fix with one click.</summary>
public sealed record LockedRealAccountBanner(Guid AccountId, string AccountName, string VerificationText, bool CanFixFlag = false);

/// <summary>One banner per account whose session unlock is armed but past
/// the staleness threshold — the in-app mirror of the hub's out-of-band
/// stale alert, so dismissing the toast cannot hide the state.</summary>
public sealed record StaleUnlockBanner(Guid AccountId, string AccountName, string ArmedForText, string ThresholdText);
