using System.Collections.ObjectModel;
using System.Text.Json;
using Tf.App.Infrastructure;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.Services;

/// <summary>
/// Owns every Deriv account connection plus its optional growth runner. One
/// <see cref="AccountConnection"/> per API token (Deriv authorizes one account
/// per WebSocket). The WPF layer binds to <see cref="Accounts"/>; the growth
/// brains are started/stopped here so the kill switch can halt everything.
/// Includes the portfolio-level daily drawdown governor: when the combined
/// net P&amp;L of all growth sessions breaches the plan's cap, every session
/// is stopped and nothing restarts until a manual start re-arms the governor.
/// </summary>
public sealed class MultiAccountHub
{
    private readonly IAccountVault _vault;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly PerformanceTracker? _tracker;
    private readonly TickHistoryCache? _tickCache;
    private readonly HeartbeatLog? _heartbeat;
    private readonly NotificationService? _notifications;
    private readonly WebhookService? _webhook;
    private readonly GrowthPlan _hubPlan;
    private readonly Dictionary<Guid, GrowthRunner> _runners = new();
    private readonly Dictionary<Guid, GrowthRunner> _observedRunners = new();
    private readonly Dictionary<Guid, int> _autoRestartCounts = new();
    private readonly Dictionary<Guid, HashSet<int>> _usedRestarts = new();
    private readonly Dictionary<Guid, GrowthPlan> _plans = new();
    private readonly Dictionary<Guid, Func<AppSettings>> _settingsFactories = new();
    private readonly Dictionary<Guid, Func<bool>> _killSwitchFactories = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _autoRestartCts = new();
    private readonly Dictionary<Guid, int> _pendingRestarts = new();
    private readonly object _runnerLock = new();

    public MultiAccountHub(IAccountVault vault, TradeStore store, TradeJournal journal,
        PerformanceTracker? tracker = null, TickHistoryCache? tickCache = null,
        HeartbeatLog? heartbeat = null, NotificationService? notifications = null,
        WebhookService? webhook = null, GrowthPlan? hubPlan = null)
    {
        _vault = vault;
        _store = store;
        _journal = journal;
        _tracker = tracker;
        _tickCache = tickCache;
        _heartbeat = heartbeat;
        _notifications = notifications;
        _webhook = webhook;
        _hubPlan = hubPlan ?? GrowthPlan.Default;

        foreach (var config in vault.Load())
        {
            Accounts.Add(CreateConnection(config));
        }

        // An overnight breach must survive an app restart: restore the
        // governor's latched trip from the journal before anything can run.
        RestoreGovernorLatchFromJournal();
    }

    /// <summary>All known accounts (bound directly by the UI).</summary>
    public ObservableCollection<AccountConnection> Accounts { get; } = new();

    /// <summary>Raised on every growth runner activity line (background thread).</summary>
    public event Action<GrowthRunner, string>? GrowthActivity;

    /// <summary>Raised after an account is added or removed.</summary>
    public event Action? AccountsChanged;

    /// <summary>
    /// Raised when an account's auto-restart state changes: an attempt is
    /// scheduled, the counter is reset by a manual start, or the bounded
    /// budget is exhausted (background thread).
    /// </summary>
    public event Action<Guid, int, bool>? RestartStateChanged;

    /// <summary>
    /// Raised when the portfolio daily drawdown governor trips: the combined
    /// net P&amp;L across all growth accounts has breached the plan's cap and
    /// every session was stopped (background thread).
    /// </summary>
    public event Action<decimal>? PortfolioGovernorTripped;

    /// <summary>
    /// Raised after a manual re-arm of the portfolio drawdown governor,
    /// clearing the latched trip state (background thread).
    /// </summary>
    public event Action? GovernorRearmed;

    /// <summary>
    /// Raised when the combined daily drawdown reaches 80% of the portfolio
    /// cap — an early warning before the trip. Fires once per arming cycle
    /// (armed on <see cref="RearmGovernor"/>, day rollover, or restore;
    /// cleared silently when the cap itself is breached) so repeated
    /// settlements inside the warning band do not spam the banner or
    /// webhook (background thread).
    /// </summary>
    public event Action<decimal>? PortfolioGovernorWarning;

    /// <summary>Arm the pre-trip warning (or clear a stale one). Callers
    /// hold <see cref="_runnerLock"/>; never notifies — arming is silent.</summary>
    private void ArmGovernorWarningLocked()
    {
        _governorWarningFired = false;
    }

    public IReadOnlyDictionary<Guid, GrowthRunner> Runners => _runners;

    /// <summary>Automatic restart attempts already made per account.
    /// Returns a snapshot — the live dictionary is mutated under the runner
    /// lock from background threads.</summary>
    public IReadOnlyDictionary<Guid, int> RestartAttempts
    {
        get { lock (_runnerLock) { return new Dictionary<Guid, int>(_autoRestartCounts); } }
    }

    /// <summary>Whether the bounded auto-restart budget is exhausted for an account.</summary>
    public bool IsGivenUp(Guid accountId)
    {
        lock (_runnerLock) { return _usedRestarts.ContainsKey(accountId); }
    }

    /// <summary>Whether the portfolio drawdown governor has tripped (not yet re-armed).</summary>
    public bool IsGovernorTripped
    {
        get { lock (_runnerLock) { return _governorTripped; } }
    }

    /// <summary>Combined net P&amp;L at the moment the governor tripped
    /// (null while the governor is clear) — used to rebuild the UI banner
    /// after the latch was restored from the journal on launch.</summary>
    /// <remarks>Set on whatever thread tripped/restored the governor, read
    /// from the UI thread — always accessed under <see cref="_runnerLock"/>.
    /// </remarks>
    public decimal? GovernorTrippedNet
    {
        get { lock (_runnerLock) { return _governorTrippedNet; } }
        private set { lock (_runnerLock) { _governorTrippedNet = value; } }
    }

    private decimal? _governorTrippedNet;

    private bool _governorTripped;

    /// <summary>Whether the pre-trip warning has fired in the current arming
    /// cycle — gates the warning to one shot per re-arm / day rollover.</summary>
    private bool _governorWarningFired;

    /// <summary>Account whose breach tripped the governor (null when the
    /// latch was restored from a journal entry without a usable id) — lets a
    /// re-added account clear its own restored latch.</summary>
    private Guid? _governorTripAccountId;

    // Drawdown is measured from this baseline (start of day = 0; a manual
    // re-arm re-baselines to the net at re-arm time, so a deeply negative
    // day does not instantly re-trip on the next settlement).
    private decimal _governorBaseline;
    private DateTime _governorBaselineDate = DateTime.UtcNow.Date;

    private decimal GovernorBaseline()
    {
        lock (_runnerLock)
        {
            return _governorBaselineDate == DateTime.UtcNow.Date ? _governorBaseline : 0m;
        }
    }

    public AccountConnection AddAccount(AccountConfig config)
    {
        var existing = Accounts.FirstOrDefault(a =>
            string.Equals(a.Config.ApiToken, config.ApiToken, StringComparison.Ordinal));
        if (existing is not null)
        {
            throw new InvalidOperationException("That API token is already in the account list.");
        }

        // A latch restored from a journal entry must not permanently block
        // configuring the very account that breached (or any account, when the
        // journal entry carried no usable id): adding that account clears the
        // restored latch. A live in-session trip of a known account stays put.
        bool clearRestoredLatch;
        lock (_runnerLock)
        {
            clearRestoredLatch = _governorTripped &&
                (_governorTripAccountId is null || _governorTripAccountId == config.Id);
            if (clearRestoredLatch)
            {
                _governorTripped = false;
                _governorTripAccountId = null;
                _governorBaseline = CombinedGrowthNetPnl();
                _governorBaselineDate = DateTime.UtcNow.Date;
                ArmGovernorWarningLocked();
            }
        }

        if (clearRestoredLatch)
        {
            GovernorTrippedNet = null;
        }

        var connection = CreateConnection(config);
        Accounts.Add(connection);
        Save();
        AccountsChanged?.Invoke();
        return connection;
    }

    public async Task RemoveAccountAsync(AccountConnection connection)
    {
        if (connection is null)
        {
            return;
        }

        GrowthRunner? runner;
        lock (_runnerLock)
        {
            _runners.TryGetValue(connection.Config.Id, out runner);
            if (runner is not null)
            {
                runner.Connection.StateChanged -= OnConnectionStateChanged;
                runner.Exited -= OnRunnerExited;
                runner.Settled -= OnRunnerSettled;
                _runners.Remove(connection.Config.Id);
            }

            _autoRestartCounts.Remove(connection.Config.Id);
            _usedRestarts.Remove(connection.Config.Id);
            _plans.Remove(connection.Config.Id);
            _settingsFactories.Remove(connection.Config.Id);
            _killSwitchFactories.Remove(connection.Config.Id);

            if (_autoRestartCts.Remove(connection.Config.Id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            _pendingRestarts.Remove(connection.Config.Id);
        }

        if (runner is not null)
        {
            await runner.DisposeAsync();
        }

        Accounts.Remove(connection);
        await connection.DisposeAsync();
        Save();
        AccountsChanged?.Invoke();
    }

    public async Task ConnectAllAsync()
    {
        foreach (var account in Accounts.ToArray())
        {
            if (!account.IsConnected && !account.IsBusy)
            {
                try
                {
                    await account.ConnectAsync();
                }
                catch
                {
                    // Row surfaces the error; keep connecting the others.
                }
            }
        }
    }

    public async Task DisconnectAllAsync()
    {
        StopAllRunners();

        foreach (var account in Accounts.ToArray())
        {
            await account.DisconnectAsync();
        }
    }

    /// <summary>
    /// Starts the deterministic growth brain on one connected demo account.
    /// Returns the runner, or null when the account cannot run — including
    /// while the portfolio drawdown governor is tripped (see
    /// <see cref="RearmGovernor"/>). A manual start resets the bounded
    /// auto-restart counter and cancels any pending restart.
    /// </summary>
    public GrowthRunner? StartGrowth(AccountConnection connection, GrowthPlan plan,
        Func<AppSettings> settings, Func<bool> killSwitch)
    {
        lock (_runnerLock)
        {
            if (_governorTripped)
            {
                return null; // latched: re-arm explicitly before starting again
            }
        }

        CancelPendingRestart(connection.Config.Id);
        lock (_runnerLock)
        {
            _autoRestartCounts.Remove(connection.Config.Id);
            _usedRestarts.Remove(connection.Config.Id);
        }

        var runner = StartGrowthCore(connection, plan, settings, killSwitch);
        if (runner is not null)
        {
            RestartStateChanged?.Invoke(connection.Config.Id, 0, false);
        }

        return runner;
    }

    /// <summary>
    /// Re-arms the portfolio drawdown governor after a trip, re-baselining
    /// the daily drawdown to the current combined net, so sessions can start
    /// again. Nothing is started here — call StartGrowth as usual.
    /// </summary>
    public void RearmGovernor()
    {
        bool wasTripped;
        decimal baseline;
        lock (_runnerLock)
        {
            wasTripped = _governorTripped;
            _governorTripped = false;
            _governorTripAccountId = null;
            _governorBaseline = CombinedGrowthNetPnl();
            _governorBaselineDate = DateTime.UtcNow.Date;
            ArmGovernorWarningLocked();
            baseline = _governorBaseline;
        }

        if (wasTripped)
        {
            GovernorTrippedNet = null;
            _journal.LogGrowthState(Guid.Empty, "portfolio-governor", baseline, 0,
                $"re-armed manually — drawdown now measured from {baseline:0.##}");
            GovernorRearmed?.Invoke();
        }
    }

    /// <summary>
    /// Restores the portfolio governor's tripped latch from the journal on
    /// launch. The newest journal entry for the governor decides: if it is a
    /// trip from today and no later re-arm follows, the breach is still in
    /// effect — the latch comes back up (and the UI banner reappears) so an
    /// overnight breach is not silently forgotten. Trips from earlier days
    /// are ignored: the governor measures per-day drawdown and a new day
    /// already resets its baseline.
    /// </summary>
    public void RestoreGovernorLatchFromJournal()
    {
        // The journal buffers entries for a few seconds; force any pending
        // trip/re-arm lines to disk so the scan below cannot miss them.
        try { _journal.Flush(); }
        catch { /* best effort — scan whatever is already persisted */ }

        // The newest governor entry decides the latch. GetRecent is newest-
        // first overall, but interleaved entries from other categories are
        // skipped below, so keep scanning until a governor entry turns up.
        GovernorEvent? newest = null;
        foreach (var entry in _journal.GetRecent(count: 200))
        {
            if (entry.Category != "GROWTH_STATE")
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(entry.Details);
                var root = doc.RootElement;
                if (!root.TryGetProperty("State", out var state) ||
                    state.GetString() != "portfolio-governor")
                {
                    continue;
                }

                var reason = root.TryGetProperty("Reason", out var reasonEl)
                    ? reasonEl.GetString() ?? "" : "";
                decimal net = 0m;
                if (root.TryGetProperty("Bankroll", out var netEl) &&
                    netEl.TryGetDecimal(out var parsed))
                {
                    net = parsed;
                }

                newest = new GovernorEvent(entry.Timestamp, entry.AccountId, reason, net);
                break;
            }
            catch
            {
                // Malformed entry — skip it and keep looking at older ones.
            }
        }

        // Nothing journaled, a stale day (a new day already resets the
        // baseline), or already re-armed → the governor is clear.
        if (newest is null ||
            newest.Timestamp.UtcDateTime.Date != DateTime.UtcNow.Date ||
            newest.Reason.Contains("re-armed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_runnerLock)
        {
            if (_governorTripped)
            {
                return;
            }

            _governorTripped = true;
            _governorTripAccountId =
                newest.AccountId is { } id && id != Guid.Empty ? id : null;
        }

        GovernorTrippedNet = newest.Net;
        PortfolioGovernorTripped?.Invoke(newest.Net);
    }

    /// <summary>One journaled governor event (trip or re-arm).</summary>
    private sealed record GovernorEvent(
        DateTimeOffset Timestamp, Guid AccountId, string Reason, decimal Net);

    /// <summary>Test seam: trips the governor through the real path — latch,
    /// stop, journal — so a raised trip behaves like a genuine settlement
    /// breach and <see cref="RearmGovernor"/> clears it.</summary>
    internal void TestRaiseGovernorTripped(decimal net)
    {
        var limit = Math.Abs(net);
        if (limit == 0)
        {
            limit = 1;
        }

        TripGovernor(net, limit, 0m, Guid.Empty, "test");
    }

    /// <summary>Test seam: raises <see cref="GovernorRearmed"/> directly.</summary>
    internal void TestRaiseGovernorRearmed() => GovernorRearmed?.Invoke();

    /// <summary>Test seam: raises <see cref="PortfolioGovernorWarning"/> with
    /// the drawdown in use (the UI reaction is unit-tested here; the band
    /// crossing itself is covered by the integration tests).</summary>
    internal void TestRaiseGovernorWarning(decimal used) => PortfolioGovernorWarning?.Invoke(used);

    /// <summary>Cancels a scheduled automatic restart, if one is pending.</summary>
    private void CancelPendingRestart(Guid accountId)
    {
        lock (_runnerLock)
        {
            if (_autoRestartCts.Remove(accountId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            _pendingRestarts.Remove(accountId);
        }
    }

    /// <summary>
    /// Hooks an externally managed runner into the hub's monitoring (activity
    /// feed and the portfolio drawdown governor) without transferring its
    /// lifecycle. Use when the caller starts/stops the runner directly but
    /// the hub must still see its settlements.
    /// </summary>
    /// <param name="plan">The plan governing this runner. Its
    /// <see cref="GrowthPlan.PortfolioDailyDrawdownCap"/> feeds the governor —
    /// observed runners never pass through <see cref="StartGrowth"/>, so
    /// without it the hub would fall back to the hub-level plan.</param>
    public void ObserveRunner(GrowthRunner runner, GrowthPlan? plan = null)
    {
        lock (_runnerLock)
        {
            if (plan is not null)
            {
                _plans[runner.Connection.Config.Id] = plan;
            }

            // Track the runner (without owning its lifecycle) so a portfolio
            // governor stop can reach it too.
            _observedRunners[runner.Connection.Config.Id] = runner;
        }

        runner.Activity += line => GrowthActivity?.Invoke(runner, line);
        runner.Settled += OnRunnerSettled;
    }

    /// <summary>Starts a runner and remembers its plan for auto-restarts.</summary>
    private GrowthRunner? StartGrowthCore(AccountConnection connection, GrowthPlan plan,
        Func<AppSettings> settings, Func<bool> killSwitch)
    {
        if (connection is null || !connection.IsConnected || !connection.Config.IsDemo)
        {
            return null;
        }

        lock (_runnerLock)
        {
            if (_runners.TryGetValue(connection.Config.Id, out var running))
            {
                return running;
            }

            var runner = new GrowthRunner(connection, _store, settings, killSwitch, _journal, _tracker, _notifications, _webhook);
            runner.Activity += line => GrowthActivity?.Invoke(runner, line);
            runner.Connection.StateChanged += OnConnectionStateChanged;
            runner.Exited += OnRunnerExited;
            runner.Settled += OnRunnerSettled;

            _runners[connection.Config.Id] = runner;
            _plans[connection.Config.Id] = plan;
            _settingsFactories[connection.Config.Id] = settings;
            _killSwitchFactories[connection.Config.Id] = killSwitch;
            _ = runner.StartAsync(plan);
            return runner;
        }
    }

    public Task StopGrowthAllAsync()
    {
        StopAllRunners();
        return Task.CompletedTask;
    }

    private IReadOnlyList<GrowthRunner> StopAllRunners()
    {
        GrowthRunner[] runners;
        lock (_runnerLock)
        {
            runners = _runners.Values.Concat(_observedRunners.Values).ToArray();
            _runners.Clear();
            _observedRunners.Clear();

            foreach (var id in runners.Select(r => r.Connection.Config.Id))
            {
                if (_autoRestartCts.Remove(id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                _pendingRestarts.Remove(id);
            }
        }

        foreach (var runner in runners)
        {
            runner.Connection.StateChanged -= OnConnectionStateChanged;
            runner.Exited -= OnRunnerExited;
            runner.Settled -= OnRunnerSettled;
            runner.Stop();
        }

        return runners;
    }

    /// <summary>
    /// Combined net P&amp;L of every settled growth trade across all accounts
    /// — the number the drawdown governor watches.
    /// </summary>
    public decimal CombinedGrowthNetPnl() =>
        _store.Trades
            .Where(t => t.Source == TradeSource.Growth)
            .Sum(t => t.Profit);

    /// <summary>
    /// A growth trade settled. If the combined net P&amp;L across all growth
    /// accounts has breached the plan's portfolio daily-drawdown cap, stop
    /// every session: journal it, alert via webhook/toast, raise
    /// <see cref="PortfolioGovernorTripped"/>, and latch — nothing restarts
    /// (kill-switch, failure-restart, or otherwise) until a manual start
    /// re-arms the governor.
    /// </summary>
    private void OnRunnerSettled(GrowthRunner runner, Trade trade)
    {
        GrowthPlan plan;
        decimal net, baseline;
        lock (_runnerLock)
        {
            if (_governorTripped)
            {
                return;
            }

            plan = _plans.TryGetValue(runner.Connection.Config.Id, out var p) ? p : _hubPlan;

            // One consistent settlement view decides BOTH the pre-trip
            // warning and the trip: the combined net and baseline are read
            // in the same lock window as the latch check, so a concurrent
            // settlement can no longer trip the governor on a breach this
            // check never saw (which silently skipped the owed warning).
            // Every settlement's handler still evaluates the warning first
            // on its own view, so the one-shot fires on exactly one view.
            net = CombinedGrowthNetPnl();
            baseline = GovernorBaseline();
        }

        var cap = plan.PortfolioDailyDrawdownCap;
        if (cap is not { } limit || limit <= 0)
        {
            return; // governor disabled
        }

        // Warn at 80% of the cap before tripping — a silent glide to the cap
        // gives no chance to pause engines manually. No-op while latched or
        // once fired for this arming cycle.
        CheckGovernorWarning(net, limit, baseline);

        // TripGovernor re-checks the breach under the latch; the view passed
        // here is the same one the warning saw, so a trip is never decided
        // on a different settlement than the warning was.
        TripGovernor(net, limit, baseline,
            runner.Connection.Config.Id, runner.Connection.DisplayName);
    }

    /// <summary>
    /// Warns when the combined daily drawdown reaches 80% of the portfolio
    /// cap. One shot per arming cycle (re-arm, day rollover, or latch
    /// restore): a settlement that slides from 81% to 95% must not spam the
    /// banner and webhook; re-arming re-arms the warning too.
    /// </summary>
    private void CheckGovernorWarning(decimal net, decimal limit, decimal baseline)
    {
        bool fire;
        lock (_runnerLock)
        {
            // The latch always wins: nothing to warn about once the cap is
            // already breached, and the fired flag stays false so a re-arm
            // (or the next day's baseline rollover) can warn again.
            if (_governorTripped)
            {
                return;
            }

            // Arm the one-shot only when actually crossing the band: a
            // below-band settlement that happens to run first must not
            // consume the warning before the band is ever reached.
            fire = !_governorWarningFired && baseline - net >= 0.80m * limit;
            _governorWarningFired = fire;
        }

        if (!fire)
        {
            return;
        }

        var used = baseline - net;
        _journal.LogGrowthState(Guid.Empty, "portfolio-governor-warning", net, 0,
            $"combined drawdown {used:0.##} reached 80% of the -{limit:0.##} portfolio cap " +
            $"({used / limit:P0}) — trip ahead if losses continue");

        _notifications?.NotifyRiskRailEngaged("Portfolio drawdown warning",
            $"{used:0.##} of the -{limit:0.##} portfolio cap used");
        _webhook?.PostRiskRail("⚠ Portfolio drawdown warning",
            $"combined daily drawdown {used:0.##} reached 80% of the -{limit:0.##} portfolio cap");

        PortfolioGovernorWarning?.Invoke(used);
    }

    /// <summary>
    /// Latches the portfolio governor (caller must already hold a consistent
    /// view — the latch is set here under the lock, re-checked), stops every
    /// session, journals the trip, raises the toast/webhook, and notifies UI
    /// observers. Shared by the settlement path and the test seam.
    /// </summary>
    private void TripGovernor(decimal net, decimal limit, decimal baseline,
        Guid accountId, string displayName)
    {
        lock (_runnerLock)
        {
            if (_governorTripped)
            {
                return;
            }

            // Re-check the breach under the latch so a settlement racing this
            // one (already handled below) cannot be lost between the caller's
            // cap read and here. The test seam may pass a net that does not
            // breach an implied cap, so only the real settlement path — which
            // measured the breach before calling — sets a baseline of 0.
            if (accountId != Guid.Empty && net - baseline > -limit)
            {
                return;
            }

            _governorTripped = true;
            _governorTripAccountId = accountId == Guid.Empty ? null : accountId;
            ArmGovernorWarningLocked();
        }

        GovernorTrippedNet = net;
        var stopped = StopAllRunners();

        var line = $"{DateTime.Now:HH:mm:ss} Portfolio drawdown cap breached " +
                   $"(combined {net:0.##} ≤ -{limit:0.##}) — stopped {stopped.Count} session(s)";
        foreach (var stoppedRunner in stopped)
        {
            stoppedRunner.LastActivity = "Stopped — portfolio drawdown cap breached.";
            GrowthActivity?.Invoke(stoppedRunner, line);
        }

        _journal.LogGrowthState(accountId, "portfolio-governor", net, 0,
            $"combined net P&L {net:0.##} drew down past the -{limit:0.##} portfolio cap " +
            $"(baseline {baseline:0.##}) — stopped {stopped.Count} session(s)");

        _notifications?.NotifyFloorHit(displayName, net);
        _webhook?.PostStatus("🛑 Portfolio drawdown governor",
            $"{displayName}: combined net P&L {net:0.##} breached the -{limit:0.##} cap — " +
            $"{stopped.Count} session(s) stopped");

        PortfolioGovernorTripped?.Invoke(net);
    }

    /// <summary>
    /// A scheduler self-exited. Repeated broker/LLM failures get a bounded
    /// number of automatic restarts — each journaled, announced, and fired as
    /// a toast/webhook on a growing exponential delay — before the hub gives
    /// up until the user starts the session again (which resets the counter).
    /// The budget, base delay, and growth factor come from the account's plan.
    /// A tripped portfolio governor suppresses restarts entirely.
    /// </summary>
    private void OnRunnerExited(GrowthRunner runner, GrowthExitReason reason)
    {
        if (reason != GrowthExitReason.RepeatedFailures)
        {
            return;
        }

        var id = runner.Connection.Config.Id;
        GrowthPlan plan;
        Func<AppSettings> settings;
        Func<bool> killSwitch;
        int restarts;
        bool governorTripped;

        lock (_runnerLock)
        {
            // Only react while this runner is still the tracked one.
            if (!_runners.TryGetValue(id, out var current) || !ReferenceEquals(current, runner))
            {
                return;
            }

            _runners.Remove(id);
            plan = _plans[id];
            settings = _settingsFactories[id];
            killSwitch = _killSwitchFactories[id];
            restarts = _autoRestartCounts.TryGetValue(id, out var count) ? count : 0;
            governorTripped = _governorTripped;
        }

        runner.Exited -= OnRunnerExited;
        runner.Connection.StateChanged -= OnConnectionStateChanged;

        var name = runner.Connection.DisplayName;
        var budget = plan.MaxAutoRestarts;

        if (governorTripped || restarts >= budget)
        {
            lock (_runnerLock)
            {
                var used = _usedRestarts.TryGetValue(id, out var set) ? set : new HashSet<int>();
                for (var i = 1; i <= budget; i++)
                {
                    used.Add(i);
                }
                _usedRestarts[id] = used;
            }

            if (!governorTripped)
            {
                _journal.LogGrowthState(id, "autorestart", runner.Engine?.Bankroll ?? 0,
                    runner.Engine?.LossStreak ?? 0, $"gave up after {budget} restarts");
                runner.LastActivity =
                    $"{DateTime.Now:HH:mm:ss} Stopped after repeated broker failures — " +
                    $"{budget} automatic restarts used. Start the engine manually to try again.";
                GrowthActivity?.Invoke(runner, runner.LastActivity);

                _notifications?.NotifyCircuitBreakerTripped(name, budget);
                _webhook?.PostCircuitBreaker(name, budget);
            }

            RestartStateChanged?.Invoke(id, restarts, true);
            return;
        }

        restarts++;
        lock (_runnerLock)
        {
            _autoRestartCounts[id] = restarts;
        }

        var delay = TimeSpan.FromSeconds(
            plan.RestartBaseDelaySeconds * Math.Pow(plan.RestartBackoffFactor, restarts - 1));
        _journal.LogGrowthState(id, "autorestart", runner.Engine?.Bankroll ?? 0,
            runner.Engine?.LossStreak ?? 0,
            $"restart {restarts}/{budget} after repeated failures (delay {delay.TotalSeconds:0.#} s)");
        runner.LastActivity =
            $"{DateTime.Now:HH:mm:ss} Restarting after repeated broker failures " +
            $"(attempt {restarts}/{budget}) in {delay.TotalSeconds:0.#} s…";
        GrowthActivity?.Invoke(runner, runner.LastActivity);
        RestartStateChanged?.Invoke(id, restarts, false);

        _notifications?.NotifyCircuitBreakerTripped(name, restarts);
        _webhook?.PostStatus("🔁 Growth engine restarting",
            $"{name}: restart {restarts}/{budget} in {delay.TotalSeconds:0.#} s after repeated failures");

        CancellationToken ct;
        lock (_runnerLock)
        {
            var cts = new CancellationTokenSource();
            _autoRestartCts[id] = cts;
            _pendingRestarts[id] = restarts;
            ct = cts.Token;
        }

        _ = RestartAfterDelayAsync(runner, id, plan, settings, killSwitch, restarts, delay, ct);
    }

    /// <summary>
    /// Waits out one restart delay and starts the session again — unless the
    /// restart was superseded by a manual start/stop or a disconnect, which
    /// cancel the token. Fire-and-forget: failures are journaled, never thrown.
    /// </summary>
    private async Task RestartAfterDelayAsync(GrowthRunner oldRunner, Guid id,
        GrowthPlan plan, Func<AppSettings> settings, Func<bool> killSwitch,
        int attempt, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);

            bool stillPending;
            lock (_runnerLock)
            {
                stillPending = _pendingRestarts.TryGetValue(id, out var pending) && pending == attempt;
                if (stillPending)
                {
                    _pendingRestarts.Remove(id);
                    if (_autoRestartCts.Remove(id, out var cts))
                    {
                        cts.Dispose();
                    }
                }
            }

            if (stillPending)
            {
                StartGrowthCore(oldRunner.Connection, plan, settings, killSwitch);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a manual start/stop or a disconnect — expected.
        }
        catch (Exception ex)
        {
            _journal.LogGrowthState(id, "autorestart", 0, 0, $"restart attempt {attempt} failed: {ex.Message}");
        }
    }

    public void StopGrowth(Guid accountId)
    {
        CancelPendingRestart(accountId);

        GrowthRunner? runner;
        lock (_runnerLock)
        {
            _runners.Remove(accountId, out runner);
        }

        if (runner is not null)
        {
            runner.Connection.StateChanged -= OnConnectionStateChanged;
            runner.Exited -= OnRunnerExited;
            runner.Settled -= OnRunnerSettled;
            runner.Stop();
        }
    }

    private AccountConnection CreateConnection(AccountConfig config) =>
        new(config, _tickCache, _heartbeat);

    private void OnConnectionStateChanged(AccountConnection connection)
    {
        // A lost connection ends its runner (the scheduler cannot trade anyway).
        GrowthRunner? runner;
        lock (_runnerLock)
        {
            if (!connection.IsConnected &&
                _runners.TryGetValue(connection.Config.Id, out var r))
            {
                _runners.Remove(connection.Config.Id);
                runner = r;
            }
            else
            {
                runner = null;
            }
        }

        if (runner is not null)
        {
            CancelPendingRestart(connection.Config.Id);
            runner.Connection.StateChanged -= OnConnectionStateChanged;
            runner.Exited -= OnRunnerExited;
            runner.Settled -= OnRunnerSettled;
            runner.Stop();
            runner.LastActivity = "Stopped — account disconnected.";
        }
    }

    private void Save()
    {
        try
        {
            _vault.Save(Accounts.Select(a => a.Config).ToArray());
        }
        catch
        {
            // Persistence is best-effort; the in-memory list still works.
        }
    }
}
