using System.Collections.ObjectModel;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.Core;
using DongGfx.Core.Analytics;
using DongGfx.Core.Brain;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

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
    private readonly TimeProvider _timeProvider;
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

    /// <summary>When each account's session unlock was armed (UTC) — feeds
    /// the digest's arm-state leg. entries removed with the account.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _unlockArmedAtUtc = new();

    /// <summary>One timer per armed account: fires when the arm reaches the
    /// staleness threshold and flags it. Cancelled on drop/remove.</summary>
    private readonly Dictionary<Guid, ITimer> _stalenessTimers = new();

    /// <summary>Per-session real-money unlock: accounts whose growth engine
    /// may run on a real-money (API-verified non-virtual) account. Armed by
    /// the UI's typed-phrase unlock dialog, never persisted, cleared on
    /// app exit. Demo accounts never consult this set (passthrough).</summary>
    private readonly HashSet<Guid> _realMoneyUnlocked = new();

    /// <summary>Growth-trade activity per unlock window: when the window
    /// opened, how many growth trades settled inside it, and their combined
    /// net P&L. Cleared when the arm state is; feeds the digest's arm leg
    /// so monitoring sees not just that real mode was enabled but whether
    /// it was actually used.</summary>
    private readonly Dictionary<Guid, (DateTimeOffset ArmedAt, int Trades, decimal Net)> _unlockWindows = new();

    /// <summary>Raised when an account's session unlock is flagged as stale
    /// — armed longer than <see cref="ArmStalenessThreshold"/> (background
    /// thread). Payload: account id, display name, arm age, threshold cycles.</summary>
    public event Action<(Guid AccountId, string Name, TimeSpan Age, int Cycles)>? UnlockStale;

    private readonly object _runnerLock = new();

    /// <summary>The manual surfaces' shared session gate. Defaults to a
    /// private instance when not injected: unlock state is scoped to this
    /// hub (or, via DI, to the one singleton the whole app shares), never
    /// to the process.</summary>
    public ManualRealMoneyGate ManualGate => _manualGate;
    private readonly ManualRealMoneyGate _manualGate;

    public MultiAccountHub(IAccountVault vault, TradeStore store, TradeJournal journal,
        PerformanceTracker? tracker = null, TickHistoryCache? tickCache = null,
        HeartbeatLog? heartbeat = null, NotificationService? notifications = null,
        WebhookService? webhook = null, GrowthPlan? hubPlan = null,
        TimeProvider? timeProvider = null, ManualRealMoneyGate? manualGate = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _manualGate = manualGate ?? new ManualRealMoneyGate();

        foreach (var config in vault.Load())
        {
            Accounts.Add(CreateConnection(config));
        }

        // An overnight breach must survive an app restart: restore the
        // governor's latched trip from the journal before anything can run.
        RestoreGovernorLatchFromJournal();
    }

    /// <summary>All known accounts (bound directly by the UI).</summary>
    /// <summary>Aggregated operational telemetry (cycle latencies, errors)
    /// across every runner the hub manages — feeds the metrics export.</summary>
    public MetricsCollector Metrics { get; } = new();

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

    /// <summary>
    /// All growth runners the hub knows about — accounts started here
    /// (<see cref="Runners"/>) plus externally-run runners registered via
    /// <see cref="ObserveRunner"/>. Snapshot taken under the runner lock;
    /// when a connection appears in both, the started runner wins.
    /// </summary>
    public IReadOnlyDictionary<Guid, GrowthRunner> AllRunners
    {
        get
        {
            lock (_runnerLock)
            {
                var merged = new Dictionary<Guid, GrowthRunner>(_runners);
                foreach (var (id, runner) in _observedRunners)
                {
                    merged.TryAdd(id, runner);
                }
                return merged;
            }
        }
    }

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

            // Drop the arm state with the account: the session unlock
            // itself is revoked, the staleness watch cancelled (no late flag
            // for a removed account), and the digest window closed with it.
            CancelUnlockStalenessCheck(connection.Config.Id);
            _realMoneyUnlocked.Remove(connection.Config.Id);

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

    /// <summary>Test seam: raises <see cref="RestartStateChanged"/> so the
    /// restart banners on the Growth tab can be exercised headlessly (the
    /// real ladder needs broker failures and is covered by the E2E tests).</summary>
    internal void TestRaiseRestartStateChanged(Guid accountId, int attempt, bool gaveUp) =>
        RestartStateChanged?.Invoke(accountId, attempt, gaveUp);

    /// <summary>Test seam: raises <see cref="GrowthActivity"/> so the
    /// dashboard's brain-status line can be exercised headlessly (a real
    /// activity line needs a live trading session).</summary>
    internal void TestRaiseGrowthActivity(GrowthRunner runner, string line) =>
        GrowthActivity?.Invoke(runner, line);

    /// <summary>Test seam: replays the settlement hook exactly as a live
    /// runner would — updates the unlock window counters (item of the
    /// digest's arm leg) and runs the drawdown governor on the same code
    /// path. Tests use fake-broker trades; no Deriv network involved.</summary>
    internal void TestRaiseSettled(GrowthRunner runner, Trade trade) =>
        OnRunnerSettled(runner, trade);

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

    /// <summary>Raised when a growth start was refused by the real-money
    /// gate: the account config claims real, and the API-verified account
    /// type or the session unlock refused it (background thread).</summary>
    public event Action<AccountConnection, string>? RealMoneyRefused;

    /// <summary>How long a session unlock may stay armed before the hub
    /// flags it as stale (toast + webhook + journal, repeating every
    /// threshold while still armed). Real-money unlocks are meant to be
    /// armed for a deliberate trading window, not for the life of the
    /// process — an all-day arm is exactly the state monitoring must see.
    /// Null disables the alert. Read from the user's settings live via
    /// <see cref="SetThresholdSource"/>; tests set the property directly.</summary>
    public TimeSpan? ArmStalenessThreshold { get; set; } = TimeSpan.FromHours(4);

    private Func<int>? _armStalenessHoursSource;

    /// <summary>Wires the threshold to the user's settings (read live, so a
    /// save takes effect on the next scheduled fire without a restart). The
    /// mapping: hours &lt;= 0 disables alerting; anything else clamps to at
    /// least one hour below. Tests without settings keep the 4h default.</summary>
    public void SetThresholdSource(Func<int>? hours)
    {
        _armStalenessHoursSource = hours;
        RefreshArmStalenessThreshold();
    }

    /// <summary>Re-reads the threshold from the wired source (settings) and
    /// reschedules any live watches so a lowered threshold catches an arm
    /// that is already past the new limit sooner, and a raised one delays
    /// the next flag. Safe to call repeatedly.</summary>
    internal void RefreshArmStalenessThreshold()
    {
        var hours = _armStalenessHoursSource?.Invoke() ?? 0;
        ArmStalenessThreshold = hours <= 0
            ? null
            : TimeSpan.FromHours(Math.Min(hours, 72));

        // Reschedule live watches against the new threshold.
        Dictionary<Guid, DateTimeOffset> armed;
        lock (_runnerLock)
        {
            armed = _unlockArmedAtUtc.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        foreach (var kv in armed)
        {
            ScheduleUnlockStalenessCheck(kv.Key, kv.Value);
        }
    }

    /// <summary>Arm the per-session real-money unlock for one account.
    /// The unlock lives only as long as this process; the gate re-checks
    /// the API verdict on every start regardless. Arming is journalled as a
    /// REAL_MONEY_UNLOCK_ARMED audit entry (with the API-verified account
    /// state) and announced out-of-band, so the moment real trading became
    /// possible is as visible as every refusal.</summary>
    public void UnlockRealMoney(Guid accountId)
    {
        if (!TryArmUnlock(accountId))
        {
            return; // re-arming the same account is a no-op, not a new audit event
        }

        JournalUnlockArmed(new[] { Accounts.FirstOrDefault(a => a.Config.Id == accountId) },
            manualSurfaces: false);
    }

    /// <summary>Arms the unlock without journaling or announcing. The unlock
    /// panel uses this to arm a batch of accounts and write ONE summary
    /// audit entry via <see cref="JournalUnlockArmed"/> afterwards. True
    /// when the account was newly armed.</summary>
    internal bool TryArmUnlock(Guid accountId)
    {
        DateTimeOffset armedAt;
        lock (_runnerLock)
        {
            if (!_realMoneyUnlocked.Add(accountId))
            {
                return false;
            }

            armedAt = _timeProvider.GetUtcNow();
            _unlockArmedAtUtc[accountId] = armedAt;
            _unlockWindows[accountId] = (armedAt, Trades: 0, Net: 0m); // open the digest window
        }

        // The unlock is now on a clock: if it is still armed when the
        // staleness threshold elapses, the hub flags it (toast + webhook +
        // journal) instead of letting a forgotten arm sit silently for the
        // rest of the process lifetime.
        ScheduleUnlockStalenessCheck(accountId, armedAt);
        return true;
    }

    /// <summary>The hub's clock: every arm timestamp is taken from this
    /// source, so anything measuring arm ages (digest line, banner surface,
    /// staleness timers) must read the SAME clock rather than the wall —
    /// tests inject virtual time here.</summary>
    internal DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    /// <summary>When each account's session unlock was armed (UTC), for the
    /// digest's arm-state leg. Only accounts currently unlocked appear.</summary>
    internal IReadOnlyDictionary<Guid, DateTimeOffset> UnlockArmedAtUtc
    {
        get
        {
            lock (_runnerLock)
            {
                return _unlockArmedAtUtc.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
    }

    /// <summary>One-line summary of the current session unlock state for
    /// the webhook digest: armed accounts (with arm age) and whether the
    /// manual surfaces' shared unlock is on. Null while nothing is armed —
    /// silence means no real trading is possible, which is not news.</summary>
    public string? DescribeUnlockState()
    {
        Dictionary<Guid, DateTimeOffset> armed;
        lock (_runnerLock)
        {
            armed = _unlockArmedAtUtc.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        if (armed.Count == 0 && !_manualGate.IsUnlocked)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var parts = new List<string>();
        foreach (var kv in armed.OrderBy(kv => kv.Value))
        {
            var connection = Accounts.FirstOrDefault(a => a.Config.Id == kv.Key);
            var name = connection?.Config.Label ?? kv.Key.ToString()[..8];
            (var armedAt, var trades, var net) = _unlockWindows.TryGetValue(kv.Key, out var window)
                ? window
                : (kv.Value, 0, 0m);
            parts.Add(trades > 0
                ? $"{name} ({(now - armedAt).TotalMinutes:0}m ago, {trades} growth trade{(trades == 1 ? "" : "s")} · net {net:+0.##;-0.##;0})"
                : $"{name} ({(now - armedAt).TotalMinutes:0}m ago, no growth trades)");
        }

        if (_manualGate.IsUnlocked)
        {
            parts.Add("manual surfaces");
        }

        return $"🔒 real-money unlock ARMED this session: {string.Join(", ", parts)}";
    }

    /// <summary>Flags an arm that has been live for more than
    /// <see cref="ArmStalenessThreshold"/>: journalled, toasted, and posted
    /// out-of-band on the webhook, repeating with every full threshold the
    /// arm survives (4h, 8h, 12h…) so an all-day arm keeps resurfacing
    /// instead of being dismissed once. Never throws — the alert must not
    /// be able to take the hub down.</summary>
    private void FlagStaleUnlock(Guid accountId, DateTimeOffset scheduledFor)
    {
        try
        {
            DateTimeOffset armedAt;
            lock (_runnerLock)
            {
                // The stored arm time only ever moves forward (a re-arm) or
                // disappears (drop/clear). Equal to this watch's scheduled
                // generation → the flag is ours; newer → a re-arm superseded
                // this timer and its (late) fire must stay silent.
                if (!_unlockArmedAtUtc.TryGetValue(accountId, out armedAt) ||
                    armedAt > scheduledFor)
                {
                    return; // re-armed (newer watch scheduled) or dropped meanwhile
                }
            }

            var threshold = ArmStalenessThreshold;
            if (threshold is not { } limit || limit <= TimeSpan.Zero)
            {
                return; // alerting disabled
            }

            var now = _timeProvider.GetUtcNow();
            var age = now - armedAt;
            var cycles = Math.Max(1, (int)Math.Floor(age / limit));
            var connection = Accounts.FirstOrDefault(a => a.Config.Id == accountId);
            var name = connection?.Config.Label ?? accountId.ToString()[..8];
            var ageText = age.TotalHours >= 1
                ? $"{(int)age.TotalHours}h{age.Minutes:00}m"
                : $"{age.TotalMinutes:0}m";

            _journal.Log(Guid.Empty, "REAL_MONEY_UNLOCK_STALE",
                $"real-money unlock for '{name}' armed {ageText} ago " +
                $"(threshold {limit.TotalHours:0.#}h × {cycles}) — " +
                "still live; re-arm or let the session end to clear it");

            var summary = $"{name}: unlock armed {ageText} ago (threshold {limit.TotalHours:0.#}h)";
            _notifications?.NotifyRiskRailEngaged("🔓 Unlock left armed", summary);
            _webhook?.PostRiskRail("🔓 Real-money unlock left armed",
                $"'{name}' has been armed for {ageText} this session " +
                $"(staleness threshold {limit.TotalHours:0.#}h, x{cycles}). " +
                "Re-arm deliberately or let the session end.");

            UnlockStale?.Invoke((accountId, name, age, cycles));

            // Still armed → the next full threshold re-flags it. Scheduled
            // from NOW, not armedAt + limit: a fire exactly on the threshold
            // would otherwise reschedule with a zero due time and spin.
            ScheduleUnlockStalenessCheck(accountId, _timeProvider.GetUtcNow());
        }
        catch (Exception)
        {
            // Journalling the failure keeps the audit trail honest even when
            // the alert itself could not be delivered.
            try
            {
                _journal.Log(accountId, "REAL_MONEY_UNLOCK_STALE",
                    "staleness alert delivery failed");
            }
            catch
            {
                // Nothing left to do — never throw from a background alert.
            }
        }
    }

    /// <summary>Arms (or re-arms) the one-shot timer that flags an account's
    /// unlock once it has been live for <see cref="ArmStalenessThreshold"/>.
    /// Called from every arming path and again after each staleness flag so
    /// an all-day arm keeps resurfacing on every threshold.</summary>
    private void ScheduleUnlockStalenessCheck(Guid accountId, DateTimeOffset armedAt)
    {
        var threshold = ArmStalenessThreshold;
        if (threshold is not { } limit || limit <= TimeSpan.Zero)
        {
            return; // alerting disabled
        }

        var now = _timeProvider.GetUtcNow();
        var due = armedAt + limit - now;
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero; // already stale (test clock, huge threshold change)
        }

        ITimer timer;
        lock (_runnerLock)
        {
            if (_stalenessTimers.Remove(accountId, out var previous))
            {
                previous.Dispose(); // a newer arming supersedes the old watch
            }

            timer = _timeProvider.CreateTimer(
                _ => FlagStaleUnlock(accountId, armedAt), null, due, Timeout.InfiniteTimeSpan);
            _stalenessTimers[accountId] = timer;
        }
    }

    /// <summary>One-click fix for a config/API mismatch: the Deriv API
    /// verified the account as VIRTUAL (demo funds) but the config claims
    /// real, so the gate refuses every start with a mismatch the user must
    /// fix by hand. This re-labels the config to demo — the honest state —
    /// persisting the change and journaling it as an account event. False
    /// when the account does not exist, has not been API-verified as
    /// virtual, or already claims demo (the fix must never run on an
    /// unverified or genuinely real account — that stays a human edit).</summary>
    public bool FixAccountFlagToDemo(Guid accountId)
    {
        lock (_runnerLock)
        {
            var connection = Accounts.FirstOrDefault(a => a.Config.Id == accountId);
            if (connection is null || connection.ApiVerifiedVirtual != true ||
                connection.Config.IsDemo)
            {
                return false;
            }

            connection.Config.IsDemo = true;
            Save();
        }

        _journal.Log(accountId, "ACCOUNT_EVENT",
            "real-money flag fixed: the Deriv API verified this account as virtual " +
            "(demo funds), so the config was re-labelled to demo — the mismatch " +
            "refusal is cleared");

        return true;
    }

    /// <summary>Cancels an account's staleness watch and clears its arm
    /// window (the arm itself is cleared by the callers under the lock).</summary>
    private void CancelUnlockStalenessCheck(Guid accountId)
    {
        lock (_runnerLock)
        {
            if (_stalenessTimers.Remove(accountId, out var timer))
            {
                timer.Dispose();
            }

            _unlockWindows.Remove(accountId);
            _unlockArmedAtUtc.Remove(accountId);
        }
    }

    /// <summary>Journals + announces the arming of one or more accounts (and
    /// optionally the manual surfaces). Called from UnlockRealMoney for
    /// single-account arming and from the UI's unlock panel for the
    /// one-phrase-arms-all flow. Never throws.</summary>
    internal void JournalUnlockArmed(IReadOnlyList<AccountConnection?> connections, bool manualSurfaces)
    {
        try
        {
            var entries = connections
                .Where(c => c is not null)
                .Select(c => c!)
                .DistinctBy(c => c.Config.Id)
                .ToList();

            _journal.LogRealMoneyUnlockArmed(
                entries.Select(c => (c.Config.Id, c.Config.Label, VerifiedReal: c.ApiVerifiedVirtual == false))
                       .ToList(),
                manualSurfaces);

            var names = entries.Select(c => c.Config.Label).ToList();
            if (manualSurfaces)
            {
                names.Add("manual surfaces");
            }

            if (names.Count > 0)
            {
                var verified = entries.Count(c => c.ApiVerifiedVirtual == false);
                var suffix = entries.Count > 0
                    ? $" — {verified}/{entries.Count} API-verified real"
                    : "";
                _webhook?.PostStatus("🔓 Real-money unlock armed",
                    $"Session unlock armed for {string.Join(", ", names)}{suffix}. " +
                    "Expires with this process.");
            }
        }
        catch (Exception ex)
        {
            _journal.Log(Guid.Empty, "REAL_MONEY_UNLOCK",
                "unlock journal/announce failed", ex.Message);
        }
    }

    /// <summary>Thread-safe read of the session unlock set (the runner's
    /// per-cycle and per-settlement gate re-checks call this from background
    /// threads while the UI may arm it concurrently).</summary>
    internal bool IsRealMoneyUnlocked(Guid accountId)
    {
        lock (_runnerLock)
        {
            return _realMoneyUnlocked.Contains(accountId);
        }
    }

    /// <summary>Starts a runner and remembers its plan for auto-restarts.</summary>
    private GrowthRunner? StartGrowthCore(AccountConnection connection, GrowthPlan plan,
        Func<AppSettings> settings, Func<bool> killSwitch)
    {
        if (connection is null || !connection.IsConnected)
        {
            return null;
        }

        // Real-money gate: every condition must pass before an engine may
        // run on an account the config marks real. Demo accounts pass
        // through untouched (with a loud mismatch surface if the API says
        // the config's demo flag is wrong). This check runs on EVERY start,
        // including the automatic restart ladder — a deferred restart after
        // a session unlock was never armed cannot slip through.
        var decision = RealMoneyGate.Evaluate(
            connection.Config.IsDemo,
            connection.ApiVerifiedVirtual,
            IsRealMoneyUnlocked(connection.Config.Id));
        if (decision is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            var explanation = RealMoneyGate.Explain(decision);
            _journal.LogGrowthState(connection.Config.Id, "real-money-gate", 0, 0,
                $"start refused ({decision}): {explanation}");
            _notifications?.NotifyRiskRailEngaged("Real-money gate",
                $"{connection.DisplayName} — start refused");
            _webhook?.PostRiskRail("🛑 Real-money gate refused an engine start",
                $"{connection.DisplayName}: {explanation}");
            RealMoneyRefused?.Invoke(connection, explanation);
            return null;
        }

        lock (_runnerLock)
        {
            if (_runners.TryGetValue(connection.Config.Id, out var running))
            {
                return running;
            }

            var runner = new GrowthRunner(connection, _store, settings, killSwitch, _journal,
                _tracker, _notifications, _webhook, _timeProvider, Metrics,
                realMoneyUnlocked: () => IsRealMoneyUnlocked(connection.Config.Id));
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

            // A growth trade inside an open unlock window counts toward the
            // window's digest line: real mode was not just armed but used.
            if (_unlockWindows.TryGetValue(runner.Connection.Config.Id, out var window))
            {
                _unlockWindows[runner.Connection.Config.Id] =
                    (window.ArmedAt, window.Trades + 1, window.Net + trade.Profit);
            }

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
        if (reason == GrowthExitReason.RealMoneyGate)
        {
            // The gate stopped the engine mid-session: the runner must be
            // dropped here (StartGrowthCore otherwise keeps returning it),
            // and NO restart ladder may fire — the account state failed the
            // gate, so retrying without re-evaluation would loop. The next
            // manual start re-runs the gate from scratch.
            DropRunner(runner);
            return;
        }

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

        if (governorTripped || restarts >= plan.MaxAutoRestarts)
        {
            GiveUpRestart(runner, id, plan, restarts, governorTripped);
            return;
        }

        restarts++;
        lock (_runnerLock)
        {
            _autoRestartCounts[id] = restarts;
        }

        ScheduleNextRestart(runner, id, plan, settings, killSwitch, restarts,
            journalReason: "after repeated failures",
            activityReason: "after repeated broker failures",
            webhookReason: "after repeated failures");
    }

    /// <summary>Removes a gate-stopped runner from the hub's tracking and
    /// detaches its event handlers, mirroring the disconnect path. The runner
    /// is already stopped (it stopped itself); the next start creates a fresh
    /// one through the gate.</summary>
    private void DropRunner(GrowthRunner runner)
    {
        var id = runner.Connection.Config.Id;
        lock (_runnerLock)
        {
            if (_runners.TryGetValue(id, out var current) && ReferenceEquals(current, runner))
            {
                _runners.Remove(id);
            }
            _observedRunners.Remove(id);
        }

        runner.Connection.StateChanged -= OnConnectionStateChanged;
        runner.Exited -= OnRunnerExited;
        runner.Settled -= OnRunnerSettled;
        CancelPendingRestart(id);
    }

    /// <summary>
    /// Marks the bounded restart budget exhausted: fills the used set so
    /// <see cref="IsGivenUp"/> reports true, journals the give-up, announces
    /// it (toast + webhook), and raises <see cref="RestartStateChanged"/> with
    /// gaveUp set. A tripped portfolio governor suppresses the journal and
    /// notification noise — the trip already announced the stop — but still
    /// marks the budget used. Shared by the runner-exit path and the deferred
    /// (could-not-start) restart path so both give up identically.
    /// </summary>
    private void GiveUpRestart(GrowthRunner runner, Guid id, GrowthPlan plan,
        int restarts, bool governorTripped)
    {
        var budget = plan.MaxAutoRestarts;
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

            _notifications?.NotifyCircuitBreakerTripped(runner.Connection.DisplayName, budget);
            _webhook?.PostCircuitBreaker(runner.Connection.DisplayName, budget);
        }

        RestartStateChanged?.Invoke(id, restarts, true);
    }

    /// <summary>
    /// Announces and schedules restart <paramref name="attempt"/> on the
    /// exponential ladder: journal line, activity line,
    /// <see cref="RestartStateChanged"/>, toast + webhook, then arms the
    /// cancellation source and fires the delayed restart. The exit path passes
    /// the original wording byte-for-byte; the deferred path (see
    /// <see cref="HandleDeferredRestart"/>) passes its own reason phrases so
    /// the audit trail says why an attempt fired early or late.
    /// </summary>
    private void ScheduleNextRestart(GrowthRunner oldRunner, Guid id, GrowthPlan plan,
        Func<AppSettings> settings, Func<bool> killSwitch, int attempt,
        string journalReason, string activityReason, string webhookReason)
    {
        var budget = plan.MaxAutoRestarts;
        var delay = TimeSpan.FromSeconds(
            plan.RestartBaseDelaySeconds * Math.Pow(plan.RestartBackoffFactor, attempt - 1));
        _journal.LogGrowthState(id, "autorestart", oldRunner.Engine?.Bankroll ?? 0,
            oldRunner.Engine?.LossStreak ?? 0,
            $"restart {attempt}/{budget} {journalReason} (delay {delay.TotalSeconds:0.#} s)");
        oldRunner.LastActivity =
            $"{DateTime.Now:HH:mm:ss} Restarting {activityReason} " +
            $"(attempt {attempt}/{budget}) in {delay.TotalSeconds:0.#} s…";
        GrowthActivity?.Invoke(oldRunner, oldRunner.LastActivity);
        RestartStateChanged?.Invoke(id, attempt, false);

        _notifications?.NotifyCircuitBreakerTripped(oldRunner.Connection.DisplayName, attempt);
        _webhook?.PostStatus("🔁 Growth engine restarting",
            $"{oldRunner.Connection.DisplayName}: restart {attempt}/{budget} in {delay.TotalSeconds:0.#} s {webhookReason}");

        CancellationToken ct;
        lock (_runnerLock)
        {
            var cts = new CancellationTokenSource();
            _autoRestartCts[id] = cts;
            _pendingRestarts[id] = attempt;
            ct = cts.Token;
        }

        _ = RestartAfterDelayAsync(oldRunner, id, plan, settings, killSwitch, attempt, delay, ct);
    }

    /// <summary>
    /// A scheduled restart fired but the session could not start — the
    /// connection being down at restart time is the common case: the runner
    /// already exited, so the hub's disconnect bookkeeping no longer cancels
    /// pending restarts and the timer fires into the outage. Consuming the
    /// attempt silently there was the restart ladder's own
    /// never-retried-after-a-clean-tree gap: no runner, no retry, nothing
    /// announced — the engine sat dead until a human noticed. The attempt
    /// instead runs through the same bounded ladder as a failed runner:
    /// within the budget it is re-announced and retried after the next
    /// exponential delay; past the budget the hub gives up exactly as
    /// <see cref="OnRunnerExited"/> does.
    /// </summary>
    private void HandleDeferredRestart(GrowthRunner oldRunner, Guid id,
        GrowthPlan plan, Func<AppSettings> settings, Func<bool> killSwitch, int attempt)
    {
        int restarts;
        bool governorTripped;
        lock (_runnerLock)
        {
            restarts = _autoRestartCounts.TryGetValue(id, out var count) ? count : 0;
            governorTripped = _governorTripped;
        }

        if (governorTripped || restarts >= plan.MaxAutoRestarts)
        {
            GiveUpRestart(oldRunner, id, plan, restarts, governorTripped);
            return;
        }

        restarts++;
        lock (_runnerLock)
        {
            _autoRestartCounts[id] = restarts;
        }

        ScheduleNextRestart(oldRunner, id, plan, settings, killSwitch, restarts,
            journalReason: "deferred — the connection was down at restart time",
            activityReason: "after a deferred restart (the connection was down)",
            webhookReason: "after a deferred restart (connection was down)");
    }

    /// <summary>
    /// Waits out one restart delay and starts the session again — unless the
    /// restart was superseded by a manual start/stop or a disconnect, which
    /// cancel the token. The wait runs on the hub's injected
    /// <see cref="TimeProvider"/> (virtual in tests, wall clock in the app),
    /// so the exponential restart ladder is testable without real sleeps.
    /// When the delayed start cannot run (the connection being down at
    /// restart time is the common case), the attempt is deferred through the
    /// same bounded ladder instead of being consumed silently — see
    /// <see cref="HandleDeferredRestart"/>. Fire-and-forget: failures are
    /// journaled, never thrown.
    /// </summary>
    private async Task RestartAfterDelayAsync(GrowthRunner oldRunner, Guid id,
        GrowthPlan plan, Func<AppSettings> settings, Func<bool> killSwitch,
        int attempt, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timer = _timeProvider.CreateTimer(
                state => ((TaskCompletionSource)state!).TrySetResult(), resolved,
                delay, Timeout.InfiniteTimeSpan);
            await resolved.Task.WaitAsync(ct).ConfigureAwait(false);

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
                // The start can silently no-op — the connection was down at
                // restart time, the governor latched mid-wait, or the account
                // can no longer run. Consuming the attempt then (no runner, no
                // retry, nothing announced) was the ladder's own
                // never-retried-after-a-clean-tree gap; defer it through the
                // same bounded ladder instead.
                if (StartGrowthCore(oldRunner.Connection, plan, settings, killSwitch) is null)
                {
                    HandleDeferredRestart(oldRunner, id, plan, settings, killSwitch, attempt);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a manual start/stop or a disconnect — expected, but
            // it still belongs in the audit trail: a restart that quietly
            // vanishes is indistinguishable from one that was never scheduled.
            _journal.LogGrowthState(id, "autorestart", oldRunner.Engine?.Bankroll ?? 0,
                oldRunner.Engine?.LossStreak ?? 0,
                $"restart {attempt}/{plan.MaxAutoRestarts} superseded (cancelled while waiting)");
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
