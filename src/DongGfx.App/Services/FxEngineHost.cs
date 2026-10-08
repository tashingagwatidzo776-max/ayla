using DongGfx.App.Infrastructure;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>
/// App-side host for the DON G FX forex brain: owns the DispatcherTimer,
/// fetches candles/quotes from the MT5 bridge, runs pure engine cycles, and
/// executes ORDER decisions through the SAME rail sequence as the manual
/// ticket (kill switch → Mt5MaxLots → real-money gate → bridge order).
/// Every state lands in the journal (FX_REGIME / FX_SIGNAL / FX_DECISION /
/// FX_ORDER / FX_MODE). Paper by default; demo-live is an explicit flip.
/// </summary>
public sealed class FxEngineHost : IDisposable
{
    private readonly Mt5BridgeClient _mt5;
    private readonly TradeJournal _journal;
    private readonly Func<bool> _killSwitchEngaged;
    private readonly Func<decimal> _lotsCap;
    private readonly Func<decimal> _equityFloorFloor;
    private readonly Func<bool> _realMoneyUnlocked;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    /// <summary>Between-cycle breach detection: a locked profit floor must
    /// not wait up to 60s for the next cycle (observed 2026-10-08 —
    /// +0.71R sampled, the next sample -0.46R straight THROUGH a 0.3R
    /// floor). The watch advances every locked-floor guard on a fresh tick
    /// every FloorWatchIntervalSeconds; see RunFloorWatchTickAsync.</summary>
    internal const int FloorWatchIntervalSeconds = 5;
    private readonly System.Windows.Threading.DispatcherTimer _floorWatchTimer;
    private string? _floorWatchLastError;
    private readonly TimeSpan _cycleOffset;
    private bool _firstCycle = true;
    private bool _cycleRunning;
    private readonly FxEngine _engine;
    private bool _busy;

    /// <summary>Cross-venue risk layer: kill switch, governor, loss stops.
    /// Evaluated BEFORE every cycle and BEFORE every order.</summary>
    public FxSupervisor Supervisor { get; }

    public string Symbol { get; }
    public string Timeframe { get; } = "M1";
    public bool IsRunning => _timer.IsEnabled;
    public bool IsLiveEngine => _engine.IsLive;
    public event Action<string>? StatusChanged;

    /// <summary>Paper soak requirement: the host will not GoLive until at
    /// least this many paper signals have been journaled (plan guardrail).</summary>
    public int PaperSoakSignalsRequired { get; set; } = 10;

    /// <summary>Paper signals this host RESTORED from the ledger when it was
    /// built (0 on a cold start). The account bar's soak pill names it, so a
    /// bar carried over from an earlier session is never mistaken for
    /// signals earned since this brain started.</summary>
    public int PaperSoakRestored { get; private set; }

    /// <summary>The build stamp whose counters were DROPPED for this host
    /// (null when nothing was): a build change restarts the bar on purpose,
    /// and the pill reports that instead of appearing to lose progress.</summary>
    public string? PaperSoakInvalidatedBuild { get; private set; }

    /// <summary>The MT5 login whose counters were DROPPED for this host
    /// (null when nothing was): an account switch restarts the bar on
    /// purpose — the soak is the evidence base for the go-live gate on the
    /// account about to trade, so it does not carry across a switch.</summary>
    public string? PaperSoakInvalidatedAccount { get; private set; }

    /// <summary>Portfolio veto (multi-symbol): returns a refusal reason when
    /// the requested lots would exceed the shared exposure cap.</summary>
    private readonly Func<double, Task<string?>>? _preOrderVeto;

    /// <summary>News veto: high-impact calendar window refusal.</summary>
    private readonly Func<(bool Blackout, string Reason)>? _newsVeto;

    /// <summary>Small-account mode: clamps the order size DOWN to the
    /// venue's minimum lot (one trade at a time at smallest size). Null on
    /// large accounts — sizing passes through untouched.</summary>
    private readonly Func<string, double, double>? _smallAccountClamp;

    /// <summary>Bridge equity refreshed every cycle - the engine's sizing
    /// budget reads it and fails closed at 0 while it is unknown.</summary>
    private double _lastEquity;

    /// <summary>HWARANG profit-floor memory per ticket: the floor is a
    /// one-way ratchet (never moves down, spec §16/§32), so each evaluation
    /// seeds from the last established value. Pruned on close.</summary>
    private readonly Dictionary<long, double> _profitFloors = new();

    /// <summary>Journal-backed MFE/MAE/floor reseed: the newest FX_PROFIT
    /// row for a ticket, parsed for its PeakR/MaeR/FloorR. Restarts must
    /// not reset the high-water mark — a wiped peak re-arms the giveback
    /// override on a peak the brain can no longer see, and silently drops
    /// an established floor (the never-down law). Best-effort: an
    /// unreadable journal just means a cold start, never a crash.</summary>
    internal (double MfeR, double MaeR, double FloorR)? LastProfitStateFromJournal(long ticket)
    {
        try
        {
            var dir = _journal.JournalDir;   // journal_*.jsonl live here directly
            if (!System.IO.Directory.Exists(dir)) return null;
            (double, double, double)? found = null;
            foreach (var file in System.IO.Directory.GetFiles(dir, "journal_*.jsonl").OrderBy(f => f))
            {                    foreach (var line in Core.Logging.TradeJournal.ReadLinesShared(file))
                {
                    if (!line.Contains("FX_PROFIT")) continue;
                    try
                    {
                        // The line is a JSON envelope; Details carries the
                        // payload. Parse both — never string-surgery on the
                        // escaped raw text.
                        using var env = System.Text.Json.JsonDocument.Parse(line);
                        if (!env.RootElement.TryGetProperty("Details", out var det)) continue;
                        var text = det.GetString();
                        var brace = text?.IndexOf('{') ?? -1;
                        if (brace < 0) continue;
                        using var doc = System.Text.Json.JsonDocument.Parse(text![brace..]);
                        var r = doc.RootElement;
                        if (!r.TryGetProperty("Ticket", out var t) || t.GetInt64() != ticket) continue;
                        double Num(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : 0;
                        var mfe = Num("PeakR");
                        var mae = Num("MaeR");
                        var fl = Num("FloorR");
                        if (mfe > 0 || mae > 0)
                        {
                            found = (mfe, mae, fl);
                        }
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            return found;
        }
        catch
        {
            return null;   // best-effort substrate
        }
    }

    /// <summary>Rebuild the recent-tape windows from the journal once per
    /// process — n≥WindowN takes days of tape to fill, so a deploy must not
    /// reset it. Set only AFTER the replay lands, so a close recording
    /// concurrently waits on the lock instead of racing the historical
    /// rows. Never throws: FxRecentTapeReseed treats an unreadable journal
    /// as an empty one, which default-keeps every family.</summary>
    private void EnsureRecentTapeSeeded()
    {
        if (_recentTapeSeeded)
        {
            return;
        }
        lock (_tapeSeedLock)
        {
            if (_recentTapeSeeded)
            {
                return;
            }
            Core.Fx.FxRecentTapeReseed.Run(_journal.JournalDir, _recentTape);
            _recentTapeSeeded = true;
        }
    }

    /// <summary>The family a ticket bins under: in-memory first (set at
    /// dispatch), else the fill row's Signal (restart reseed). Null when
    /// neither exists — never mis-bin a trade.</summary>
    internal string? FamilyFor(long ticket)
    {
        if (_familiesByTicket.TryGetValue(ticket, out var family))
        {
            return family;
        }
        family = FamilyFromJournal(_journal.JournalDir, ticket);
        if (!string.IsNullOrEmpty(family))
        {
            _familiesByTicket[ticket] = family;
            return family;
        }
        return null;
    }

    /// <summary>Feed one settled outcome into the recent-tape cells —
    /// called by the close writers that know their R. Non-finite R or
    /// unknown family: no Record (never fabricate, never mis-bin).</summary>
    private void RecordSettled(long ticket, string symbol, double realizedR)
    {
        if (double.IsNaN(realizedR) || double.IsInfinity(realizedR))
        {
            return;
        }
        var family = FamilyFor(ticket);
        if (!string.IsNullOrEmpty(family))
        {
            EnsureRecentTapeSeeded();
            _recentTape.Record(symbol, family, realizedR);
        }
    }

    /// <summary>The alpha family (fill row's Signal) a ticket was entered
    /// with — the restart source for <see cref="_familiesByTicket"/>: the
    /// fill payload carries Signal and the journal outlives the process.
    /// Newest match wins (partial-fill re-entries). Null when no family is
    /// journaled. Static so tests can point it at a scratch journal dir.</summary>
    internal static string? FamilyFromJournal(string journalDir, long ticket)
    {
        try
        {
            if (!System.IO.Directory.Exists(journalDir)) return null;
            string? found = null;
            foreach (var file in System.IO.Directory.GetFiles(journalDir, "journal_*.jsonl").OrderBy(f => f))
            {                    foreach (var line in Core.Logging.TradeJournal.ReadLinesShared(file))
                {
                    if (!line.Contains("FX_ORDER")) continue;
                    try
                    {
                        // JSON envelope first, Details payload second — the
                        // escaped-quote raw text is never string-surgered.
                        using var env = System.Text.Json.JsonDocument.Parse(line);
                        if (!env.RootElement.TryGetProperty("Details", out var det)) continue;
                        var text = det.GetString();
                        var brace = text?.IndexOf('{') ?? -1;
                        if (brace < 0) continue;
                        using var doc = System.Text.Json.JsonDocument.Parse(text![brace..]);
                        var r = doc.RootElement;
                        var order = r.TryGetProperty("Order", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetInt64() : 0;
                        var deal = r.TryGetProperty("Deal", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number ? d.GetInt64() : 0;
                        if (order != ticket && deal != ticket) continue;
                        if (r.TryGetProperty("Signal", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            found = s.GetString();
                        }
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            return found;
        }
        catch
        {
            return null;   // best-effort substrate
        }
    }

    /// <summary>The structural R unit an engine-owned ticket was sized
    /// with, read back from the FX_ORDER journal (the restart reseed — the
    /// in-memory <see cref="_sizedStops"/> map dies with the process, the
    /// journal does not). Returns the newest positive
    /// <c>SizedStopDistance</c> for the ticket, or 0 when none exists.
    /// Static so tests can point it at a scratch journal dir.</summary>
    internal static double SizedStopFromJournal(string journalDir, long ticket)
    {
        try
        {
            if (!System.IO.Directory.Exists(journalDir)) return 0;
            var found = 0.0;
            foreach (var file in System.IO.Directory.GetFiles(journalDir, "journal_*.jsonl").OrderBy(f => f))
            {                    foreach (var line in Core.Logging.TradeJournal.ReadLinesShared(file))
                {
                    if (!line.Contains("FX_ORDER")) continue;
                    try
                    {
                        // JSON envelope first, Details payload second — the
                        // escaped-quote raw text is never string-surgered.
                        using var env = System.Text.Json.JsonDocument.Parse(line);
                        if (!env.RootElement.TryGetProperty("Details", out var det)) continue;
                        var text = det.GetString();
                        var brace = text?.IndexOf('{') ?? -1;
                        if (brace < 0) continue;
                        using var doc = System.Text.Json.JsonDocument.Parse(text![brace..]);
                        var r = doc.RootElement;
                        var order = r.TryGetProperty("Order", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetInt64() : 0;
                        var deal = r.TryGetProperty("Deal", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number ? d.GetInt64() : 0;
                        if (order != ticket && deal != ticket) continue;
                        if (r.TryGetProperty("SizedStopDistance", out var v)
                            && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            var sd = v.GetDouble();
                            if (sd > 0) found = sd;
                        }
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            return found;
        }
        catch
        {
            return 0;   // best-effort substrate
        }
    }

    /// <summary>The venue's lot geometry for <see cref="Symbol"/>, fetched
    /// once from the bridge /symbols snapshot — sizing ground truth
    /// (contract size and volume grid) instead of a price heuristic.</summary>
    private FxVenueSymbolSpec? _venueSpec;

    public int PaperSignalsSeen { get; private set; }
    public bool PaperSoakComplete => PaperSignalsSeen >= PaperSoakSignalsRequired;

    /// <summary>Whether one engine decision advances the paper soak: a
    /// signal must have spoken (any action — ordered, paper, or a sizing
    /// skip still proves the alpha fired) while the engine is in PAPER.
    /// Internal static so tests can pin the rule without fake-market
    /// plumbing.</summary>
    internal static bool CountsTowardSoak(FxDecision decision, bool engineIsLive)
        => !engineIsLive && decision.Signal is not null;

    /// <summary>The demo account IS the paper account: a paper-exec fill
    /// is allowed only on a venue the bridge VERIFIED as demo (trade_mode,
    /// else the demo-server heuristic). Real or unverified refuses — paper
    /// practice can never leak into real money. Internal static so tests
    /// pin the rule without fake-market plumbing.</summary>
    internal static bool PaperExecutionAllowed(bool? verifiedVirtual)
        => verifiedVirtual is true;

    /// <summary>Per-symbol order cooldown. Each symbol runs its own host,
    /// so this spacing is per-symbol by construction: after a dispatch
    /// ATTEMPT (fill or refusal — the point is to stop hammering a venue
    /// that just refused us), no further order goes out for this symbol
    /// until the cooldown elapses. Signals still journal and still count
    /// toward the soak; only the dispatch is throttled.</summary>
    internal TimeSpan OrderCooldown { get; set; } = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _lastOrderDispatchUtc;

    /// <summary>The pure cooldown rule: null (never dispatched) or an old
    /// enough last attempt allows a dispatch. Internal static so tests pin
    /// it without fake-market plumbing.</summary>
    internal static bool OrderCooldownActive(DateTimeOffset? lastDispatchUtc, DateTimeOffset now, TimeSpan cooldown)
        => lastDispatchUtc is { } last && now - last < cooldown;

    /// <summary>The engine's clock. Production: the real UTC clock. Tests
    /// pin it — the regime detector vetoes the thin "late" UTC session
    /// (21:00–24:00) as LowLiquidity, so a wall-clock-driven test suite
    /// would fail every evening. Time-dependent rules stay testable behind
    /// this seam.</summary>
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Durable soak counters (null in bare tests): the GO LIVE bar
    /// accrues across restarts instead of dying with the process.</summary>
    private readonly PaperSoakLedger? _soakLedger;

    /// <summary>Feed-freshness threshold: hold new ENTRIES once the venue's
    /// newest bar is older than this many seconds. Default 300 (five M1
    /// bars) — a live tape never lets a forming bar age past ~60s, while a
    /// closed market leaves the book frozen for hours.
    ///
    /// This is the guard the dead-tape ATR floor CANNOT provide: the floor
    /// measures the volatility of frozen history, so a quiet-but-not-dead
    /// Friday close (gold read ATR% 0.0248 vs the 0.02 floor) passes it
    /// forever and re-fires the same signal every cycle on a closed market —
    /// observed 2026-10-03, when XAUUSDmicro logged 55 identical signals and
    /// pushed its paper soak to 51/10 while the venue was shut. Bar AGE is
    /// the only honest test of "is this tape live".
    ///
    /// 0 disables the guard (test fixtures carry synthetic timestamps).
    /// Risk management is NOT held: open positions are still managed.
    /// Fail-closed — it can only suppress a decision, never produce one.</summary>
    public int StaleBarSeconds { get; set; } = 300;

    /// <summary>True while entries are held for a stale feed, so the hold is
    /// journaled ONCE instead of once per cycle.</summary>
    private bool _feedStaleLatched;

    /// <summary>The portfolio's shared webhook (null in bare tests): breach
    /// alerts are in-process and instant, minutes before the scheduled
    /// watcher can confirm the close.</summary>
    private readonly WebhookService? _webhook;

    /// <summary>Optional persistent brain memory (null in bare tests):
    /// supplies a bounded per-alpha confidence weight so measured training
    /// evidence can tilt which alpha wins the vote. It can never place, size
    /// or schedule — only re-rank an already-computed confidence.</summary>
    private readonly FxBrainMemory? _memory;

    public FxEngineHost(
        Mt5BridgeClient mt5,
        TradeJournal journal,
        string symbol,
        Func<bool> killSwitchEngaged,
        Func<decimal> lotsCap,
        Func<bool> realMoneyUnlocked,
        double riskFraction = 0.02,
        FxSupervisor? supervisor = null,
        Func<bool>? governorTripped = null,
        Func<decimal>? dailyLossCap = null,
        Func<decimal>? equityFloor = null,
        WebhookService? webhook = null,
        Func<double, Task<string?>>? preOrderVeto = null,
        Func<(bool Blackout, string Reason)>? newsVeto = null,
        Func<string, double, double>? smallAccountClamp = null,
        TimeSpan cycleOffset = default,
        string? shadowLedgerPath = null,
        PaperSoakLedger? soakLedger = null,
        Func<DateTimeOffset>? clock = null,
        FxBrainMemory? memory = null)
    {
        _cycleOffset = cycleOffset;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _preOrderVeto = preOrderVeto;
        _smallAccountClamp = smallAccountClamp;
        _newsVeto = newsVeto;
        _mt5 = mt5;
        _journal = journal;
        _webhook = webhook;
        _memory = memory;
        Symbol = symbol;
        _killSwitchEngaged = killSwitchEngaged;
        _lotsCap = lotsCap;
        _equityFloorFloor = equityFloor ?? (() => 0m);
        _realMoneyUnlocked = realMoneyUnlocked;
        Supervisor = supervisor ?? new FxSupervisor(
            journal,
            killSwitchEngaged,
            governorTripped ?? (() => false),
            dailyLossCap ?? (() => 0m),
            equityFloor ?? (() => 0m),
            webhook);
        _shadowLedger = shadowLedgerPath is null
            ? null
            : new Core.Fx.FxShadowLedger(shadowLedgerPath);

        // Cross-session soak: restore this symbol's accrued paper signals
        // BEFORE the first cycle, so the GO LIVE bar reads the evidence the
        // journal already holds instead of a fresh zero. Two outcomes are
        // journaled — a restore (progress kept) and a drop (the ledger was
        // stamped by another build, which is evidence for that engine, not
        // this one). Silence would leave the operator guessing why the bar
        // moved between launches.
        _soakLedger = soakLedger;
        if (soakLedger is { } soak)
        {
            PaperSignalsSeen = soak.Seen(symbol);
            PaperSoakRestored = PaperSignalsSeen;
            PaperSoakInvalidatedBuild = soak.InvalidatedScope;
            PaperSoakInvalidatedAccount = soak.InvalidatedAccount;
            if (soak.InvalidatedScope is { } oldScope)
            {
                Journal("FX_MODE",
                    $"paper soak counters reset — build changed since {oldScope} (now {VersionInfo.Stamp})",
                    "{}");
            }
            else if (soak.InvalidatedAccount is { } oldAccount)
            {
                // Account switch: the soak restarts with the account, so a
                // bar that fell back to zero is explained instead of reading
                // as lost progress.
                Journal("FX_MODE",
                    $"paper soak counters reset — account changed since {oldAccount}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        DroppedAccount = oldAccount,
                        Build = VersionInfo.Stamp,
                    }));
            }
            else if (PaperSignalsSeen > 0)
            {
                Journal("FX_MODE",
                    $"paper soak restored {PaperSignalsSeen}/{PaperSoakSignalsRequired} on {symbol} (build {VersionInfo.Stamp})",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Symbol = symbol,
                        Restored = PaperSignalsSeen,
                        Required = PaperSoakSignalsRequired,
                        Build = VersionInfo.Stamp,
                    }));
            }
        }
        _engine = new FxEngine(symbol, Timeframe, Journal, lotsCap: (double)_lotsCap(), riskFraction: riskFraction,
            equityProvider: () => _lastEquity,
            // Memory tilt: the alpha's own confidence stays the journaled
            // value; this weight only re-ranks the winner. Null memory =
            // 1.0 for everyone, i.e. the raw-confidence comparison.
            confidenceWeight: _memory is null ? null : alpha => _memory.ConfidenceWeight(symbol, alpha));
        // The canonical 20-family roster (FxFamilies.All): the engine picks
        // the highest-confidence speaker per regime, so twenty voices widen
        // the vote without touching the risk model.
        foreach (var alpha in FxFamilies.All())
        {
            _engine.AddAlpha(alpha);
        }

        // Fire-and-forget: the venue's lot geometry (contract size, volume
        // grid) arrives once from the bridge; sizing uses the heuristic
        // fallback until then and switches when the spec lands.
        _ = LoadVenueSpecAsync();

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) =>
        {
            if (!TryBeginCycle("timer"))
            {
                return;
            }
            if (_firstCycle)
            {
                _firstCycle = false;
                // Stagger symbols so their requests never hit the bridge in
                // one burst: the sidecar serializes MT5 access, so four
                // simultaneous cycles queue behind each other and read as
                // timeouts (2026-09-28 bridge congestion).
                await Task.Delay(_cycleOffset).ConfigureAwait(true);
            }
            _cycleRunning = true;
            try
            {
                await RunCycleAsync().ConfigureAwait(true);
            }
            finally
            {
                _cycleRunning = false;
            }
        };

        // ── THE FLOOR WATCH (between cycles) ───────────────────────────
        // The 60s cycle above is the source of truth, but a locked floor
        // can be shot through inside one window. This secondary timer
        // never runs a cycle and never journals the profit snapshot — it
        // only feeds a FRESH tick to guards that hold a locked floor, and
        // defers entirely while a cycle is running.
        _floorWatchTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(FloorWatchIntervalSeconds),
        };
        _floorWatchTimer.Tick += async (_, _) =>
        {
            try
            {
                await RunFloorWatchTickAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Opportunistic accelerator: the 60s cycle still owns the
                // position. Journal only when the failure CHANGES so a
                // persistent fault cannot flood the log every 5 seconds.
                var msg = ex.GetBaseException().Message;
                if (msg != _floorWatchLastError)
                {
                    _floorWatchLastError = msg;
                    Journal("FX_FLOOR",
                        $"floor watch tick failed (cycle still owns protection): {msg}",
                        System.Text.Json.JsonSerializer.Serialize(new { Error = msg }));
                }
            }
        };
    }

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _timer.Start();
        _floorWatchTimer.Start();
        if (Supervisor.SessionStartBalance is null)
        {
            _ = AnchorSupervisorAsync();   // first host to start anchors; siblings share
        }

        _ = FirstCycleAsync();
    }

    /// <summary>The first cycle after Start: delayed by the symbol's
    /// stagger offset (portfolio hosts must not burst the bridge) and
    /// guarded against stacking with the timer tick.</summary>
    private async Task FirstCycleAsync()
    {
        if (!TryBeginCycle("first-cycle"))
        {
            return;
        }
        if (_firstCycle)
        {
            _firstCycle = false;
            await Task.Delay(_cycleOffset).ConfigureAwait(true);
        }
        _cycleRunning = true;
        try
        {
            await RunCycleAsync().ConfigureAwait(true);
        }
        finally
        {
            _cycleRunning = false;
        }
    }

    /// <summary>Shared re-entrancy guard for the timer tick and the first
    /// cycle: refuses while a cycle is in flight and JOURNALS the skip —
    /// 2026-09-29 saw a wedged cycle silently swallow every tick for an
    /// hour, with journal silence as the only symptom. Returns false when
    /// the cycle is skipped.</summary>
    internal bool TryBeginCycle(string source)
    {
        if (_cycleRunning)
        {
            Journal("FX_CYCLE", $"cycle skipped ({source}) — previous cycle still running", "{}");
            return false;
        }

        return true;
    }

    /// <summary>Anchor the supervisor's loss baseline to the live balance
    /// (network call → fire-and-forget off Start).</summary>
    private async Task AnchorSupervisorAsync()
    {
        var acct = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (acct is not null)
        {
            Supervisor.AnchorSession((decimal)acct.Balance);
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _floorWatchTimer.Stop();
    }

    public void GoLive()
    {
        if (!PaperSoakComplete)
        {
            Journal("FX_MODE", $"go-live refused — paper soak incomplete ({PaperSignalsSeen}/{PaperSoakSignalsRequired} signals)",
                "{}");
            StatusChanged?.Invoke($"go-live refused — paper soak {PaperSignalsSeen}/{PaperSoakSignalsRequired}");
            return;
        }

        _engine.GoLive();
        StatusChanged?.Invoke("engine LIVE — demo orders enabled");
    }

    public void GoPaper()
    {
        _engine.GoLive();
        _engine.GoPaper();
        StatusChanged?.Invoke("paper: fills route to the connected demo account");
    }

    /// <summary>Operator re-arm after a loss stop: forget the latch and
    /// re-anchor the supervisor to the live balance.</summary>
    public void ReArmLossStop()
    {
        _ = ReArmLossStopAsync();
    }

    private async Task ReArmLossStopAsync()
    {
        var acct = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (acct is not null)
        {
            Supervisor.ReAnchor((decimal)acct.Balance);
            StatusChanged?.Invoke("loss stop re-armed");
        }
    }

    /// <summary>One brain cycle: bridge data in → decision → maybe order.
    /// The order path re-checks the rails (they can change mid-session).</summary>
    public async Task RunCycleAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            // Seed the recent-tape windows BEFORE anything can journal a
            // close this cycle: the seeder replays history, and a close
            // journaled first would be counted twice (once by the replay,
            // once by RecordSettled).
            EnsureRecentTapeSeeded();
            var candles = await _mt5.GetCandlesAsync(Symbol, Timeframe, 120).ConfigureAwait(true);
            if (candles.Count < 35)
            {
                return;
            }

            var bars = candles.Select(c => new FxBar(c.Time, c.Open, c.High, c.Low, c.Close, 0)).ToList();
            if (_venueSpec is null) await LoadVenueSpecAsync().ConfigureAwait(true);   // first snapshot may have failed
            var tick = await _mt5.GetTickAsync(Symbol).ConfigureAwait(true);
            double bid = 0, ask = 0;
            if (tick is { } t)
            {
                bid = t.Bid;
                ask = t.Ask;
            }
            else
            {
                var last = bars[^1];
                bid = ask = last.Close;
            }

            // Supervisor gate — the brain does not even think while halted.
            var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
            _lastEquity = account?.Equity ?? 0;
            var verdict = Supervisor.Evaluate(account is not null, (decimal)(account?.Balance ?? 0), (decimal)(account?.Equity ?? 0));
            if (!verdict.TradingAllowed)
            {
                if (_engine.IsLive)
                {
                    _engine.GoPaper();
                    Journal("FX_RISK", $"live engine returned to PAPER — {verdict.Reason}", "{}");
                }

                LastDecision = null;
                StatusChanged?.Invoke($"halted: {verdict.Reason}");
                return;
            }

            // A transient halt (bridge/kill/governor) that has recovered.
            Supervisor.ClearTransientHalts();

            // Feed-freshness gate: hold NEW entries when the newest bar has
            // aged past the threshold (weekend, holiday, stalled feed). The
            // regime layer cannot catch this — it only reads the frozen
            // bars themselves, which can still look like a healthy trend.
            var cycleEpoch = _clock().ToUnixTimeSeconds();
            var barAgeSeconds = cycleEpoch - bars[^1].Time;
            if (StaleBarSeconds > 0 && barAgeSeconds > StaleBarSeconds)
            {
                if (!_feedStaleLatched)
                {
                    _feedStaleLatched = true;
                    Journal("FX_MODE",
                        $"holding entries — feed stale: newest {Symbol} bar is " +
                        $"{TimeSpan.FromSeconds(Math.Max(0, barAgeSeconds)).TotalMinutes:0} min old " +
                        $"(market closed?); entries resume when the tape moves",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Symbol,
                            BarAgeSeconds = barAgeSeconds,
                            ThresholdSeconds = StaleBarSeconds,
                            NewestBarUtc = DateTimeOffset.FromUnixTimeSeconds(bars[^1].Time).ToString("o"),
                        }));
                    StatusChanged?.Invoke($"holding — newest bar " +
                        $"{TimeSpan.FromSeconds(Math.Max(0, barAgeSeconds)).TotalMinutes:0} min old (market closed?)");
                }

                // Risk management still runs: an open position is managed
                // even while entries are held (and the venue refuses any
                // close it cannot fill). Soak is untouched — no decision,
                // no signal, no credit.
                await ManageOwnedPositionsAsync(bars, FxRegime.LowLiquidity).ConfigureAwait(true);
                return;
            }

            if (_feedStaleLatched)
            {
                _feedStaleLatched = false;
                Journal("FX_MODE",
                    $"feed fresh — newest {Symbol} bar is {Math.Max(0, barAgeSeconds)}s old; entries resumed",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Symbol,
                        BarAgeSeconds = barAgeSeconds,
                        ThresholdSeconds = StaleBarSeconds,
                    }));
                StatusChanged?.Invoke("feed fresh — entries resumed");
            }

            var decision = _engine.RunOnce(_clock(), bars, bid, ask);
            LastDecision = decision;

            // Paper soak: an alpha SPOKE while the engine is in paper — that
            // is one observed signal toward this symbol's soak bar. Live
            // signals are proven by execution, not counted here. Without
            // this the counter never advanced and GO LIVE could never fire.
            if (CountsTowardSoak(decision, _engine.IsLive))
            {
                // Persist as we count: the ledger takes the higher of the
                // in-memory count and the stored one, so a lost write can
                // only understate the soak — never manufacture progress.
                PaperSignalsSeen = _soakLedger is { } soakLedger
                    ? soakLedger.Record(Symbol, PaperSignalsSeen)
                    : PaperSignalsSeen + 1;
                Journal("FX_MODE",
                    $"paper soak {PaperSignalsSeen}/{PaperSoakSignalsRequired} on {Symbol}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Symbol,
                        Seen = PaperSignalsSeen,
                        Required = PaperSoakSignalsRequired,
                        Alpha = decision.Signal!.Alpha,
                    }));
            }

            // Exit brain: manage the positions this brain owns (comment-
            // stamped) BEFORE considering a new entry — risk management
            // outranks new exposure.
            await ManageOwnedPositionsAsync(bars, decision.Regime.Regime).ConfigureAwait(true);

            if (decision.Action is FxDecisionAction.Ordered or FxDecisionAction.PaperExecuted
                && decision.Signal is not null)
            {
                // Entry-quality gate: below the operator's floor the signal
                // is observed (soaked, journaled) but not traded. Placed
                // BEFORE the cooldown so a refused entry never consumes the
                // dispatch window a qualifying one would.
                // Recent-tape roster gate (ROSTER-POLICY §2-4): a family
                // whose LAST WindowN settled trades on THIS symbol are clearly
                // negative — two consecutive failing evaluations — is cut from
                // the symbol's roster. Lifetime memory may only tilt weights,
                // never cut (rule 1); n < WindowN always keeps (default-keep).
                // Same refusal shape and placement as the confidence gate
                // below: observed, journaled, not traded — and a refusal never
                // consumes the dispatch cooldown.
                EnsureRecentTapeSeeded();
                if (_recentTape.IsExcluded(Symbol, decision.Signal.Alpha))
                {
                    var tapeMean = _recentTape.WindowMeanR(Symbol, decision.Signal.Alpha);
                    Journal("FX_ORDER",
                        $"roster recent-tape excluded — {decision.Signal.Alpha} mean {(tapeMean is { } m ? m.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture) : "?")}R over last {_recentTape.WindowCount(Symbol, decision.Signal.Alpha)} settled, not dispatched",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Symbol,
                            Alpha = decision.Signal.Alpha,
                            WindowN = Core.Fx.FxRecentTape.WindowN,
                            WindowMeanR = tapeMean is { } mm ? Core.Fx.FxJson.Sanitize(mm) : (double?)null,
                            FailStreak = _recentTape.FailStreak(Symbol, decision.Signal.Alpha),
                        }));
                    return;
                }

                if (decision.Signal.Confidence < MinEntryConfidence)
                {
                    Journal("FX_ORDER",
                        $"confidence gate — {decision.Signal.Alpha} conf {decision.Signal.Confidence:0.00} < min {MinEntryConfidence:0.00}, not dispatched",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Symbol,
                            Alpha = decision.Signal.Alpha,
                            Confidence = FxJson.Sanitize(decision.Signal.Confidence),
                            MinEntryConfidence,
                        }));
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (OrderCooldownActive(_lastOrderDispatchUtc, now, OrderCooldown))
                {
                    // Throttle, not censor: the signal already journaled and
                    // counted toward the soak. Only the dispatch waits —
                    // re-sending every cycle into a refusing venue is noise,
                    // not edge (and spams the journal while AutoTrading is
                    // off, which is exactly what this stops).
                    Journal("FX_ORDER",
                        $"dispatch throttled — per-symbol cooldown active ({OrderCooldown.TotalMinutes:0} min), signal kept: {decision.Signal.Alpha}",
                        "{}");
                    return;
                }

                _lastOrderDispatchUtc = now;
                // Paper-exec: the demo account IS the paper account — the
                // signal fills as a real (demo) MT5 order.
                // ExecuteOrderAsync re-verifies the venue is demo first.
                await ExecuteOrderAsync(decision,
                    paperExec: decision.Action == FxDecisionAction.PaperExecuted).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // A failed cycle is a skipped cycle — the next timer tick
            // retries. But it must leave a TRACE: an invisible skipped
            // cycle is indistinguishable from a dead bridge, and nothing
            // else in the pass says "the cycle itself blew up".
            try
            {
                Journal("FX_MODE",
                    $"cycle skipped — {ex.GetType().Name}: {ex.Message}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Symbol,
                        Error = ex.GetType().Name,
                        Message = ex.Message,
                        Stack = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim(),
                    }));
            }
            catch
            {
                // journaling must never be the thing that crashes the cycle
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ExecuteOrderAsync(FxDecision decision, bool paperExec = false)
    {
        // Paper-exec hard guard FIRST: the demo account is the paper
        // account, so a paper fill is only ever allowed on a venue the
        // bridge has VERIFIED as demo (trade_mode, else the demo-server
        // heuristic). A real account — even with the session unlock armed —
        // or an unverified one refuses here: paper practice can never leak
        // into real money.
        if (paperExec)
        {
            var account0 = await _mt5.GetAccountAsync().ConfigureAwait(true);
            if (!PaperExecutionAllowed(account0?.GateVerifiedVirtual))
            {
                Journal("FX_ORDER",
                    "refused: paper execution requires a VERIFIED demo account — " +
                    (account0 is null
                        ? "bridge unavailable (account unknown)"
                        : account0.GateVerifiedVirtual is false
                            ? "connected account is REAL — paper never routes to real money"
                            : "account demo/real state unverified — connect a demo account"),
                    "{}");
                StatusChanged?.Invoke("paper execution refused: connected account is not a verified demo");
                return;
            }
        }

        // Rails, in order, at execution time.
        if (_killSwitchEngaged())
        {
            Journal("FX_ORDER", "refused: kill switch engaged", "{}");
            StatusChanged?.Invoke("order refused: kill switch engaged");
            return;
        }

        var cap = _lotsCap();
        if (cap <= 0)
        {
            Journal("FX_ORDER", "refused: Mt5MaxLots = 0 (MT5 orders disabled)", "{}");
            StatusChanged?.Invoke("order refused: MT5 orders disabled (cap 0)");
            return;
        }

        var lots = Math.Min((double)cap, decision.SuggestedLots);

        // Small-account mode: one trade at a time at the venue's minimum —
        // shrink the size before anything else consumes it, so the trade's
        // real risk follows the clamp (the stop distance stays the sized
        // hint; only the lot count shrinks).
        lots = _smallAccountClamp?.Invoke(Symbol, lots) ?? lots;

        if (lots < 0.01)
        {
            Journal("FX_ORDER", $"refused: sized {decision.SuggestedLots:0.##} lots below minimum", "{}");
            return;
        }

        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (account is null)
        {
            Journal("FX_ORDER", "refused: bridge unavailable", "{}");
            return;
        }

        // Rails, order 2: the supervisor re-checked at execution time.
        var execVerdict = Supervisor.Evaluate(true, (decimal)account.Balance, (decimal)account.Equity);
        if (!execVerdict.TradingAllowed)
        {
            Journal("FX_ORDER", $"refused: supervisor — {execVerdict.Reason}", "{}");
            StatusChanged?.Invoke($"order refused: {execVerdict.Reason}");
            return;
        }

        // Rails, order 3: news blackout (high-impact calendar window).
        if (_newsVeto is not null)
        {
            var (blackout, reason) = _newsVeto();
            if (blackout)
            {
                Journal("FX_RISK", $"order refused — {reason}", "{}");
                StatusChanged?.Invoke($"order refused: {reason}");
                return;
            }
        }

        // Rails, order 4: portfolio exposure cap (shared across symbols).
        if (_preOrderVeto is not null)
        {
            var veto = await _preOrderVeto(decision.SuggestedLots).ConfigureAwait(true);
            if (veto is not null)
            {
                Journal("FX_ORDER", $"refused: portfolio — {veto}", "{}");
                StatusChanged?.Invoke($"order refused: {veto}");
                return;
            }
        }

        // Same venue verdict as the manual order card: trade_mode from the
        // bridge, demo-only heuristic fallback (see Mt5Account).
        var verifiedVirtual = account.GateVerifiedVirtual;
        var unlocked = _realMoneyUnlocked();
        var gate = RealMoneyGate.Evaluate(configIsDemo: verifiedVirtual is true,
                                          apiVerifiedVirtual: verifiedVirtual,
                                          unlockArmed: unlocked);
        if (gate is not (RealMoneyDecision.DemoPassthrough or RealMoneyDecision.Allowed))
        {
            Journal("FX_ORDER", $"refused: real-money gate — {RealMoneyGate.Explain(gate)}", "{}");
            StatusChanged?.Invoke($"order refused: {RealMoneyGate.Explain(gate)}");
            return;
        }

        // Rails, order 5 (pre-flight): no live order on a guessed size.
        if (LiveOrderBlockedByMissingSpec)
        {
            Journal("FX_ORDER",
                "refused: live order without venue lot geometry (contract " +
                "size/volume grid unknown) — fix the bridge /symbols snapshot first", "{}");
            StatusChanged?.Invoke("order refused: venue spec unavailable");
            return;
        }

        var side = decision.Signal!.Direction == FxDirection.Buy ? "buy" : "sell";

        // The order carries the stop the trade was sized with: the alpha's
        // stop-distance hint IS the R unit, so it goes to the venue as an
        // SL (floored at the venue's stops_level — a stop inside that band
        // is rejected outright). Sizing, the exit brain's MAE ruler and the
        // broker's own risk accounting then all measure the same distance;
        // with no SL the exit brain fell back to a sub-pip ATR ruler and
        // the 1.6R emergency bar fired within a pip of entry.
        var tick = await _mt5.GetTickAsync(Symbol).ConfigureAwait(true);
        var mid = tick is { } t && t.Ask > 0 && t.Bid > 0 ? (t.Ask + t.Bid) / 2 : 0;
        var vspec = _venueSpec ?? Core.Fx.FxVenueSymbolSpec.Heuristic(mid);
        // The R unit is the stop the trade was SIZED with — the engine's
        // structural floor (ATR-scaled) already includes the venue band, so
        // the hint is only re-normalized when sizing ran without the bar
        // series (EffectiveStopDistance == 0). Sizing, the placed SL and
        // the exit brain's MAE ruler then all measure one distance.
        var stopDistance = decision.EffectiveStopDistance > 0
            ? decision.EffectiveStopDistance
            : Core.Fx.FxExitBrain.NormalizedStopDistance(
                decision.Signal.StopDistanceHint, vspec.StopsLevel, vspec.Point);
        // Anchor the stop on the side the venue will measure it from: a
        // buy's SL is checked against BID (which is below mid by half the
        // spread), a sell's against ASK — anchoring on mid once landed
        // stops inside the venue's forbidden band and MT5 refused the
        // order outright (invalid-stops).
        var anchor = tick is { } tk
            ? (side == "buy" ? tk.Bid : tk.Ask)
            : 0;
        var sl = anchor > 0 && stopDistance is { } dist
            ? Math.Round(side == "buy" ? anchor - dist : anchor + dist, 6)
            : (double?)null;
        if (sl is null)
        {
            // Fail closed: without a stop the position's risk is unsized —
            // the exit brain would fall back to a guess and the venue would
            // hold an unprotected position. Never dispatch one.
            Journal("FX_ORDER",
                "refused: no usable tick/stop distance for the sized risk " +
                $"(hint {decision.Signal.StopDistanceHint:0.#####}, stopsLevel {vspec.StopsLevel:0.#})", "{}");
            StatusChanged?.Invoke("order refused: stop distance unusable");
            return;
        }

        var result = await _mt5.PlaceOrderAsync(
            Symbol, side, "market", lots, null, null, sl, null,
            comment: Core.Fx.FxExitBrain.OwnershipComment).ConfigureAwait(true);
        // The comment stamp is how the exit engine recognizes the positions
        // it owns — manual trades are never managed.

        Journal("FX_ORDER",
            result.Ok
                ? $"{(paperExec ? "paper-exec fill (demo): " : string.Empty)}{side} {lots:0.##} lots {Symbol} @ {result.Price:0.#####} — ticket {result.Order ?? result.Deal}"
                : $"{side} {lots:0.##} lots {Symbol} refused: {result.RetcodeName}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Side = side,
                Lots = lots,
                Sl = sl,
                // The structural R unit the size was computed with — the
                // restart reseed reads this back for the exit brain.
                SizedStopDistance = Core.Fx.FxJson.Sanitize(
                    stopDistance is { } sd ? sd : 0),
                PaperExec = paperExec,
                result.Retcode,
                result.Order,
                result.Deal,
                result.Price,
                Server = account.Server,
                Signal = decision.Signal.Alpha,
                // The lot geometry the size was computed against: an audit
                // entry can be re-derived against the venue's rules later
                // (was the 100x XAUUSDmicro bug the spec's fault or the math's?).
                VenueSpec = _venueSpec is { } spec
                    ? new { spec.ContractSize, spec.VolumeMin, spec.VolumeStep, spec.VolumeMax }
                    : null,
            }));

        if (result.Ok)
        {
            var ticket = result.Order ?? result.Deal ?? 0;
            if (ticket != 0)
            {
                // The thesis engine needs the regime the trade was born in.
                _entryRegimes[ticket] = decision.Regime.Regime;
                // The roster the recent-tape filter bins this trade under —
                // the fill row journals the same Signal for restarts.
                _familiesByTicket[ticket] = decision.Signal.Alpha;
                // Remember the structural R unit: a restart must re-seed the
                // exit brain's ruler from the journal, not from a normalized
                // venue SL.
                if (stopDistance is { } sdMem && sdMem > 0)
                {
                    _sizedStops[ticket] = sdMem;
                }
            }
        }

        StatusChanged?.Invoke(result.Ok
            ? $"{(paperExec ? "paper filled (demo): " : "filled: ")}{side} {lots:0.##} {Symbol} @ {result.Price:0.#####}"
            : $"refused: {result.RetcodeName}");
    }

    /// <summary>True once the venue's lot geometry for <see cref="Symbol"/>
    /// has landed from the bridge. Until then sizing uses the heuristic
    /// fallback — good enough for paper mode, never trusted for live orders.</summary>
    public bool VenueSpecLoaded { get; private set; }

    private bool _specWarned;

    // ---- Exit brain state ------------------------------------------------
    // Per-position tracking (MFE/MAE in R, bars held) keyed by ticket, plus
    // the entry regime each position was born in (thesis engine input).
    private readonly Core.Fx.FxShadowLedger? _shadowLedger;
    private readonly Dictionary<long, Core.Fx.FxPositionState> _exitStates = new();
    private readonly Dictionary<long, Core.Fx.FxRegime> _entryRegimes = new();

    /// <summary>The structural stop each engine-owned position was born
    /// with (the SizeWithStop effective distance, journaled at dispatch).
    /// The exit brain's R ruler must equal the sizing R unit: reading the
    /// venue's SL instead lets MT5's min-stop normalization silently
    /// shrink the unit and re-arm the MAE emergency on spread noise.</summary>
    private readonly Dictionary<long, double> _sizedStops = new();

    /// <summary>The alpha family each engine-owned ticket was entered with
    /// (Signal at dispatch; the fill row's Signal otherwise — the restart
    /// source, same lazy pattern as SizedStopFromJournal). Bins settled
    /// outcomes into the recent-tape cells.</summary>
    private readonly Dictionary<long, string> _familiesByTicket = new();

    /// <summary>Recent-tape expectancy cells (ROSTER-POLICY items 2-4):
    /// every close writer that knows its R Records here; the entry gate
    /// next to the confidence gate consults IsExcluded. Seeded from the
    /// journal once per process so deploys don't reset the windows.</summary>
    private readonly Core.Fx.FxRecentTape _recentTape = new();

    private readonly object _tapeSeedLock = new();
    private bool _recentTapeSeeded;

    /// <summary>Test seam: the recent-tape cells (pre-seed exclusions and
    /// inspect windows without touching the live journal).</summary>
    internal Core.Fx.FxRecentTape RecentTape => _recentTape;

    /// <summary>Tickets whose TP1 rung is ARMED (ticket → the rung price
    /// first sighted once the trade had banked ≥1R of peak). The ladder
    /// always projects ahead of price, so execution waits for the cross.</summary>
    private readonly Dictionary<long, double> _tp1ArmedTickets = new();

    /// <summary>The per-ticket HARD PROFIT FLOOR guards (the independent
    /// risk-control layer): a breached never-down floor COMMANDS a full
    /// exit no ensemble vote can veto.</summary>
    private readonly Dictionary<long, Core.Fx.FxProfitFloorGuard> _floorGuards = new();

    /// <summary>Grace window after a close submission during which a venue
    /// read still listing the position is treated as list lag, not as
    /// evidence the close failed. TEST-SAFETY CONTRACT: internal static
    /// purely as a test seam (production timing never changes); suites
    /// that set it belong in the serialized Shared-DataDir-Directory
    /// collection and must restore it in a finally block.</summary>
    internal static TimeSpan FloorSubmitGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Tickets whose 🛡️ ARMED notification already fired — the
    /// alert-once law (spec §10): alerts ride STATE CHANGES, not ticks.</summary>
    private readonly HashSet<long> _floorArmedAlerted = new();

    /// <summary>Tickets whose TP1 rung has already been banked (or
    /// attempted) — one execution per ticket, per process lifetime.</summary>
    private readonly HashSet<long> _tp1ExecutedTickets = new();

    /// <summary>Tickets whose TP1 arming was suppressed by the
    /// trailing-mode gate (docs/soak/TP1-FLOOR-INTERACTION.md): on
    /// STRUCTURE_TRAIL tickets the profit-floor schedule ratchets with the
    /// peak and dominates any static rung — the backtest on the five
    /// 2026-09-30 saves measured −1.90R/−1.65R for arming there versus
    /// +1.78R on a hybrid-floor ticket. One skip row per ticket, per
    /// process lifetime; a skipped ticket may still EXEC if the rung was
    /// armed before the gate fired (restart ordering).</summary>
    private readonly HashSet<long> _tp1GateSkippedTickets = new();

    /// <summary>Tickets whose TP1 arming was held by the overdue plan-%
    /// review circuit breaker (Tp1PlanBreaker): one TP1-HOLD row per ticket,
    /// per process lifetime. The set only gates the journal line — arming
    /// resumes by itself once the breaker clears, because eligibility is
    /// re-evaluated every cycle and a held ticket was never armed.</summary>
    private readonly HashSet<long> _tp1BreakerSkippedTickets = new();

    /// <summary>TP1 partial prototype master switch (AppSettings, default
    /// OFF). When armed, the Profit Brain's TP1 rung is EXECUTED once per
    /// ticket through the same close path the Exit Brain uses — the
    /// allocation plan is then graded live, not just journaled. Every
    /// execution is journaled as TP1-EXEC under FX_PROFIT.
    /// TEST-SAFETY CONTRACT: deliberately a process-wide static — the arm
    /// is a fleet-wide operator decision spanning every symbol host, and
    /// settings/UI/restart all funnel here. Because statics are shared,
    /// every suite that writes this (SettingsViewModel arm tests) or
    /// triggers a write (AppStartupWiringTests via ConfigureFromSettings)
    /// MUST live in the serialized Shared-DataDir-Directory collection —
    /// the 2026-09-30 flake. Restore in a finally block; never assert the
    /// default outside that collection.</summary>
    public static bool ExecuteTp1Partials { get; set; }

    /// <summary>Overdue plan-% review circuit breaker (Tp1PlanBreaker): when
    /// this returns true the TP1 rung is HELD — no arm, no bank — until the
    /// review is acted on or cleared. Injected by the app so the engine stays
    /// filesystem-free in tests; null = the breaker is off.
    /// TEST-SAFETY: process-wide static, restore in a finally block.</summary>
    public static Func<bool>? Tp1PlanReviewHold { get; set; }

    /// <summary>Operator-armed TP1 plan-% override (AppSettings, null = the
    /// brain's own allocation plan). When set, the engine replaces the plan's
    /// TP1 leg with this percentage for eligibility, lot sizing and the ARM/
    /// EXEC journal rows — the one-click arm's path from the gate's advice to
    /// an effective plan. The dashboard's Arm action writes it and Revert
    /// clears it; a value of 0 makes the rung ineligible ([0,100] clamped at
    /// load). TEST-SAFETY: process-wide static, restore in a finally block.</summary>
    public static double? Tp1PlanPctOverride { get; set; }

    /// <summary>Minimum entry confidence (AppSettings, default 0 = off).
    /// A winning signal whose RAW confidence falls below this is observed —
    /// journaled as FX_SIGNAL and counted toward the paper soak — but never
    /// dispatched: the host refuses it with an FX_ORDER "confidence gate"
    /// row instead of paying spread on a coin flip (the training simulator's
    /// MinConfidence, enforced live and measured on the tape before adoption).
    /// TEST-SAFETY: process-wide static, restore in a finally block.</summary>
    public static double MinEntryConfidence { get; set; }

    /// <summary>The brain's own book: lots it currently tracks as open.
    /// A FLOOR for the portfolio exposure guard — venue reads can degrade
    /// to an empty list under congestion (the 2026-09-29 cap failure), but
    /// the engines' own tracking cannot forget a position they opened.
    /// Deliberately conservative: entries never shrink it below the truth
    /// for long, and pruning only happens on trusted reads.</summary>
    public double LocalBookLots => _exitStates.Values.Sum(s => s.InitialLots);

    /// <summary>Tickets this host currently tracks — the portfolio-level
    /// journal-book reconcile must NOT proof-close a ticket any host still
    /// manages (the host's own prune writes its close row); a torn read
    /// during a concurrent cycle write degrades to "tracks nothing", which
    /// the two-pass reconcile absorbs before it ever writes a close.</summary>
    internal IReadOnlyCollection<long> TrackedTickets
    {
        get
        {
            try
            {
                return _exitStates.Keys.ToArray();
            }
            catch (InvalidOperationException)
            {
                // Dictionary mutated mid-enumeration by the host's cycle.
                return Array.Empty<long>();
            }
        }
    }

    /// <summary>One-shot fetch of this symbol's venue spec from the bridge
    /// /symbols snapshot. Fire-and-forget and retry-safe: failures leave the
    /// heuristic fallback in place and the next cycle retries. The first
    /// failure journals a loud warning so the gap is visible in monitoring
    /// instead of silently sizing on a guess.</summary>
    private async Task LoadVenueSpecAsync()
    {
        try
        {
            var symbols = await _mt5.GetSymbolsAsync().ConfigureAwait(false);
            var match = symbols.FirstOrDefault(s =>
                s.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase));
            if (match is null || match.ContractSize <= 0)
            {
                WarnSpecMissing(
                    match is null
                        ? "the bridge /symbols snapshot does not name this symbol"
                        : "the snapshot carries no contract size for this symbol (old sidecar?)");
                return;
            }

            _venueSpec = new FxVenueSymbolSpec(
                ContractSize: match.ContractSize,
                VolumeMin: match.VolumeMin > 0 ? match.VolumeMin : 0.01,
                VolumeStep: match.VolumeStep > 0 ? match.VolumeStep : 0.01,
                VolumeMax: match.VolumeMax > 0 ? match.VolumeMax : 100.0,
                // Stop geometry travels with the spec — dropping it here
                // (the 2026-09-29 invalid-stops incident) left the SL floor
                // on the default point size, 20x too small on JPY pairs.
                StopsLevel: match.StopsLevel,
                Point: match.Point > 0
                    ? match.Point
                    : Core.Fx.FxExitBrain.PipSizeOf(match.Bid ?? 0) / 10);
            _engine.SetVenueSpec(_venueSpec);
            VenueSpecLoaded = true;
        }
        catch
        {
            WarnSpecMissing("the /symbols probe failed (bridge down?)");
        }
    }

    /// <summary>Journal + surface the missing-venue-geometry condition once
    /// per host: sizing keeps its heuristic fallback, loudly.</summary>
    private void WarnSpecMissing(string why)
    {
        if (_specWarned)
        {
            return;
        }

        _specWarned = true;
        Journal("FX_ORDER",
            $"pre-flight warning: venue lot geometry unavailable — {why}. " +
            "Sizing is on the fallback heuristic; the venue's own volume grid " +
            "will reject off-grid sizes at its gate.", "{}");
        StatusChanged?.Invoke("venue spec missing — sizing on fallback heuristic");
    }

    /// <summary>Final pre-flight rail: a LIVE order may never be sized
    /// without the venue's own lot geometry (contract size + volume grid).
    /// Paper mode keeps the heuristic fallback — the soak is where the gap
    /// gets noticed, the money path refuses to gamble on a guess.</summary>
    internal bool LiveOrderBlockedByMissingSpec => IsLiveEngine && _venueSpec is null;

    /// <summary>Spread guard for the exit override, in pip points — far
    /// above the venue's normal quote (Deriv gold ~27 pts), so only a
    /// genuinely abnormal market trips it.</summary>
    internal const double MaxAbnormalSpreadPoints = 250;

    /// <summary>The Exit Brain's per-cycle pass: evaluate every position
    /// this brain owns (comment-stamped entries) and act on the resolver's
    /// decision — full/partial closes via /close, tighten via /modify SL.
    /// Everything lands in the journal as FX_EXIT (score, votes, MFE/MAE):
    /// the settlement trail fills, and the data-hungry engines of v2 get
    /// their substrate. Manual positions (no stamp) are never touched.</summary>
    private async Task ManageOwnedPositionsAsync(IReadOnlyList<FxBar> bars, FxRegime currentRegime)
    {
        var positions = await _mt5.GetPositionsAsync().ConfigureAwait(true);
        // Only positions this host's own symbol: a symbol-host's ATR is the
            // risk yardstick, and using e.g. EURUSD's tiny ATR on a USDJPY
            // position made every tick read as hundreds of R (2026-09-28
            // live incident: MAE 207R emergency close on a healthy trade).
            var owned = positions
                .Where(p => Core.Fx.FxExitBrain.Owns(p.Comment) && p.Symbol == Symbol)
                .ToList();
        if (owned.Count == 0)
        {
            // Nothing to manage. Both bookkeeping duties here used to be
            // gated behind `guards.Count == 0`, and that gate itself
            // stranded the book: EVERY managed position gets a guard
            // object on first sighting, so a venue-side vanish left the
            // position's OWN never-submitted guard behind — which skipped
            // this block, while the confirmation loop below only fires
            // for a non-empty read or an ExitSubmitted guard. On a fully
            // flat venue neither held: 2026-10-07 18:49-19:23 UTC,
            // #8792100270's guard blocked its own prune for 35 minutes —
            // _exitStates (and with it TrackedTickets, which the
            // portfolio reconcile trusts) pinned at a position nobody
            // held, until a restart wiped the guard.
            //
            // Now, when the venue PROVES flat (ProvenLiveTicketsAsync — a
            // bare empty read is the congestion lie the local book exists
            // to survive), clear the guards the venue disproves AND retire
            // the stale tracking entries — except a SUBMITTED guard's
            // ticket, whose close line belongs to the confirmation loop
            // (exactly one "closed #" row per ticket, same deferral the
            // healthy-read prune below uses). Fall through only while such
            // a guard remains.
            if (_exitStates.Count > 0 || _entryRegimes.Count > 0
                || _profitFloors.Count > 0 || _floorGuards.Count > 0)
            {
                var (live, proof) = await ProvenLiveTicketsAsync(positions).ConfigureAwait(true);
                if (live is not null)
                {
                    // Never-submitted (or failed) guards for tickets the
                    // venue no longer holds: pure memory — the close line
                    // for their ticket comes from the prune right below.
                    foreach (var stale in _floorGuards
                        .Where(kv => !live.Contains(kv.Key)
                                  && kv.Value.State != Core.Fx.FxFloorState.ExitSubmitted)
                        .Select(kv => kv.Key).ToList())
                    {
                        _floorGuards[stale].MarkReset();
                        _floorGuards.Remove(stale);
                        _floorArmedAlerted.Remove(stale);
                    }

                    foreach (var gone in _exitStates.Keys.Where(k => !live.Contains(k)).ToList())
                    {
                        // Snapshot the state BEFORE the removal below — the
                        // close row's settled R and the recent-tape feed need
                        // entry/side/stop after the book forgets the ticket.
                        var closedState = _exitStates.TryGetValue(gone, out var cs) ? cs : null;
                        // A submitted floor exit's close line belongs to the
                        // confirmation loop below — deferring here keeps
                        // exactly one "closed #" row per ticket.
                        var deferredToReconcile = _floorGuards.TryGetValue(gone, out var prunedGuard)
                            && prunedGuard.State == Core.Fx.FxFloorState.ExitSubmitted;
                        if (deferredToReconcile && prunedGuard is not null)
                        {
                            // The book forgets the ticket right here, but
                            // the DEFERRED close line below still needs
                            // entry/side/stop for its settled R — hand the
                            // snapshot to the guard the confirmation loop
                            // reads it back from (live 2026-10-08: every
                            // floor confirm landed unknown without this).
                            prunedGuard.DeferredCloseState = closedState;
                        }
                        _exitStates.Remove(gone);
                        _entryRegimes.Remove(gone);
                        _profitFloors.Remove(gone);
                        if (!deferredToReconcile)
                        {
                            // THE CLOSE LINE (the second half of the limitation):
                            // tickets the venue dropped without an app-driven
                            // close (SL hits, manual flattens) never got a
                            // "closed #" row, so FxJournalBook held the fill open
                            // until the ops script wrote one by hand. Same shape
                            // the ensemble's full close writes — every downstream
                            // parser (FxJournalBook, trade_lifecycle,
                            // watch_profit_floor, backfill_shadow_ledger) sees a
                            // normal close.
                            // No tick on this early path — bars[^1].Close is
                            // the codebase's own price fallback (the exit brain
                            // uses it identically), so SL-hit retires feed the
                            // recent tape now instead of waiting for the next
                            // restart's reseed replay.
                            var retireR3 = closedState is null
                                ? (double?)null
                                : Core.Fx.FxRealizedR.Compute(
                                    closedState.EntryPrice, bars[^1].Close,
                                    closedState.InitialStopDistance > 0
                                        ? closedState.InitialStopDistance
                                        : closedState.RiskPerLot,
                                    closedState.Side);
                            if (retireR3 is { } retireR3v)
                            {
                                RecordSettled(gone, Symbol, retireR3v);
                            }
                            _familiesByTicket.Remove(gone);
                            Journal("FX_EXIT",
                                $"#{gone}: closed #{gone} — broker no longer holds the ticket; " +
                                $"stale tracking retired ({proof})",
                                System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    Ticket = gone,
                                    Partial = false,
                                    Lots = (double?)null,
                                    Retcode = 10009,
                                    Reconciled = true,
                                    Proof = proof,
                                    RealizedR = retireR3 is { } r3 ? Core.Fx.FxJson.Sanitize(r3) : (double?)null,
                                    OutcomeSource = retireR3 is not null ? "profit-snapshot" : "unknown",
                                }));
                        }
                    }

                    // Orphaned regime/floor memory whose exit state was
                    // already dropped: same proven-flat sweep, no close line
                    // (the book is _exitStates).
                    foreach (var k in _entryRegimes.Keys.Where(k => !live.Contains(k)).ToList())
                    {
                        _entryRegimes.Remove(k);
                    }
                    foreach (var k in _profitFloors.Keys.Where(k => !live.Contains(k)).ToList())
                    {
                        _profitFloors.Remove(k);
                    }
                }
            }

            // Fall through ONLY for a submitted exit awaiting confirmation
            // (the loop below's gate — readsAgree + ExitSubmitted — is
            // satisfied on this flat book). Everything else stops here:
            // nothing to manage, book as current as the venue can prove.
            if (!_floorGuards.Values.Any(g => g.State == Core.Fx.FxFloorState.ExitSubmitted))
            {
                return;
            }
        }

        var atr = owned.Count > 0 && bars.Count >= 15
            ? Core.Fx.FxFeatures.Atr(bars, 14) : double.NaN;
        var atrMedian = Core.Fx.FxFeatures.AtrMedian(bars, 20);
        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        var tick = await _mt5.GetTickAsync(Symbol).ConfigureAwait(true);
        var mid = tick is { } t && t.Ask > t.Bid ? (t.Ask + t.Bid) / 2 : bars[^1].Close;
        var spreadPoints = tick is { } t2 && t2.Ask > t2.Bid
            ? (t2.Ask - t2.Bid) / Math.Max(PipSizeFor(mid), 1e-9)
            : 0;

        foreach (var p in owned)
        {
            if (!_exitStates.TryGetValue(p.Ticket, out var st))
            {
                // First sighting: seed the tracking state — and RESEED the
                // MFE/MAE/floor memory from the journal when we have prior
                // FX_PROFIT rows for this ticket. An app restart must not
                // reset the high-water mark: the 2026-09-29 restarts made
                // 16.9R peaks vanish, which both violates the never-down
                // floor law and would re-arm the giveback override on a
                // peak it can no longer see. The entry regime may still be
                // unknown — fall back to the current regime.
                _entryRegimes[p.Ticket] = currentRegime;
                // R unit, in priority order: (1) the structural stop the
                // order was SIZED with (memory or journal), (2) the venue
                // SL, (3) the ATR fallback. Sizing and the exit brain must
                // measure the same distance — MT5 can normalize the placed
                // SL outward, which would otherwise shrink the R unit.
                var sizedStop = _sizedStops.TryGetValue(p.Ticket, out var memStop) && memStop > 0
                    ? memStop
                    : SizedStopFromJournal(_journal.JournalDir, p.Ticket) is { } jStop && jStop > 0
                        ? jStop
                        : 0.0;
                var prior = LastProfitStateFromJournal(p.Ticket);
                st = new Core.Fx.FxPositionState(
                    p.Ticket, p.Symbol, p.Side, p.PriceOpen, p.Volume,
                    Core.Fx.FxExitBrain.RiskPerLot(p.PriceOpen, sizedStop > 0 ? p.PriceOpen - sizedStop * (p.Side == "sell" ? -1 : 1) : p.Sl, double.IsNaN(atr) ? 0 : atr),
                    MfeR: prior?.MfeR ?? 0, MaeR: prior?.MaeR ?? 0, BarsHeld: 0,
                    InitialStopDistance: sizedStop > 0 ? sizedStop : 0);
                if (prior is { } pr && pr.FloorR > 0)
                {
                    _profitFloors[p.Ticket] = pr.FloorR;
                }
            }

            var price = p.PriceCurrent > 0 ? p.PriceCurrent : bars[^1].Close;
            st = Core.Fx.FxExitBrain.UpdateState(st, price, st.BarsHeld + 1);
            _exitStates[p.Ticket] = st;
            var entryRegime = _entryRegimes[p.Ticket];

            // ── THE HARD PROFIT FLOOR (independent risk-control layer) ──
            // The guard runs BEFORE the ensemble: a breached never-down
            // floor COMMANDS a full exit that no HOLD vote, regime engine,
            // or model consensus can veto (the ensemble's decision for a
            // command-locked ticket is demoted to telemetry). The breach is
            // measured on the EXECUTABLE price (bid for longs, ask for
            // shorts) against the ORIGINAL risk baseline, fires exactly
            // once per breach event, retries on broker refusal, and is
            // confirmed only by broker reconciliation.
            if (!_floorGuards.TryGetValue(p.Ticket, out var guard))
            {
                guard = new Core.Fx.FxProfitFloorGuard(p.Ticket, p.Symbol, p.Side);
                if (_profitFloors.TryGetValue(p.Ticket, out var restoredFloor) && restoredFloor > 0)
                {
                    // A floor restored from the journal (restart reseed or
                    // earlier cycles) re-enters the machine already locked.
                    guard.Advance(double.MaxValue, restoredFloor, restoredFloor);
                }
                _floorGuards[p.Ticket] = guard;
            }

            // The executable price: a long exits at the BID, a short at the
            // ASK (the venue will fill the close there) — never the mid.
            var executablePrice = tick is { } tk && tk.Ask > 0 && tk.Bid > 0
                ? (st.Side == "buy" ? tk.Bid : tk.Ask)
                : price;
            var executableR = st.Side == "buy"
                ? (executablePrice - st.EntryPrice) / Math.Max(st.RiskPerLot, 1e-9)
                : (st.EntryPrice - executablePrice) / Math.Max(st.RiskPerLot, 1e-9);

            var guardVerdict = guard.Advance(executableR,
                _profitFloors.TryGetValue(p.Ticket, out var modelFloor) ? modelFloor : 0.0,
                Math.Max(st.MfeR, executableR));

            if (guardVerdict.Command == Core.Fx.FxFloorCommand.ExecuteExit)
            {
                await SubmitFloorExitAsync(p.Ticket, p.Symbol, p.Side, st.RiskPerLot,
                    guard, guardVerdict, executablePrice).ConfigureAwait(true);
            }


            var decision = Core.Fx.FxExitBrain.Evaluate(
                st, price, p.Volume,
                double.IsNaN(atr) ? 0 : atr, atrMedian,
                spreadPoints, MaxAbnormalSpreadPoints,
                account?.Equity ?? 0, (double)_equityFloorFloor(),
                bridgeUp: true,   // a dead bridge never reaches this method
                bars, currentRegime, entryRegime);

            Journal("FX_EXIT",
                $"{p.Symbol} #{p.Ticket}: {decision.Action} score {decision.Score:0} — {decision.Reason}",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Ticket = p.Ticket,
                    p.Symbol,
                    p.Side,
                    Action = decision.Action,
                    Score = Core.Fx.FxJson.Sanitize(decision.Score),
                    MfeR = Core.Fx.FxJson.Sanitize(decision.MfeR),
                    MaeR = Core.Fx.FxJson.Sanitize(decision.MaeR),
                    ProfitR = Core.Fx.FxJson.Sanitize(decision.ProfitR),
                    Override = decision.OverrideEngine,
                    Votes = decision.Votes.Select(v => new
                    {
                        v.Engine,
                        Exit = Core.Fx.FxJson.Sanitize(v.Exit),
                        Weight = Core.Fx.FxJson.Sanitize(v.Weight),
                        Reason = v.Reason,
                    }).ToList(),
                }));

            // COMMAND LOCK: while the floor's exit is in flight, the
            // ensemble may not act on this ticket — its decision is demoted
            // to telemetry (the FX_EXIT row above still records the true
            // opinion). A recommendation can be rejected; a hard risk
            // constraint cannot be.
            if (guardVerdict.State is Core.Fx.FxFloorState.ExitPending
                or Core.Fx.FxFloorState.ExitSubmitted
                or Core.Fx.FxFloorState.ExitFailed)
            {
                decision = decision with { Action = "hold", Reason = "telemetry only — hard profit floor owns this ticket" };
            }

            // HWARANG Profit Brain — ADVISORY ONLY. It computes the target
            // ladder, probabilities, giveback classification, and the
            // never-down profit floor, journals the report, and casts the
            // weight-0 target-tp shadow vote. It sends no orders and moves
            // no stops: the Exit Brain remains the sole executor until a
            // shadow engine earns weight through the promotion gates
            // (100 trades @ 60% + Monte-Carlo STABLE) — spec §26 priority.
            var profit = Core.Fx.FxProfitBrain.Evaluate(
                st, price, bars,
                double.IsNaN(atr) ? 0 : atr, atrMedian,
                currentRegime,
                prevFloorR: _profitFloors.TryGetValue(p.Ticket, out var prevFloor) ? prevFloor : 0.0);
            _profitFloors[p.Ticket] = profit.FloorR;
            Journal("FX_PROFIT",
                $"{p.Symbol} #{p.Ticket}: {profit.ProfitState} {profit.CurrentR:+0.0;-0.0}R " +
                $"(peak {profit.PeakR:0.0}R, giveback {profit.GivebackPct:0}% {profit.GivebackClass}, " +
                $"floor {profit.FloorR:0.0}R) → {profit.RecommendedAction}",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Ticket = p.Ticket,
                    State = profit.ProfitState,
                    CurrentR = Core.Fx.FxJson.Sanitize(profit.CurrentR),
                    PeakR = Core.Fx.FxJson.Sanitize(profit.PeakR),
                    MaeR = Core.Fx.FxJson.Sanitize(st.MaeR),
                    RiskPerLot = Core.Fx.FxJson.Sanitize(st.RiskPerLot),
                    GivebackPct = Core.Fx.FxJson.Sanitize(profit.GivebackPct),
                    GivebackClass = profit.GivebackClass,
                    FloorR = Core.Fx.FxJson.Sanitize(profit.FloorR),
                    FloorBreached = profit.FloorBreached,
                    RecommendedAction = profit.RecommendedAction,
                    Continuation = Core.Fx.FxJson.Sanitize(profit.ContinuationProbability),
                    Reversal = Core.Fx.FxJson.Sanitize(profit.ReversalProbability),
                    Momentum = profit.MomentumClass,
                    Regime = profit.RegimeClass,
                    Tp1 = profit.Tp1 is null ? null : new
                    {
                        profit.Tp1.Kind,
                        Price = Core.Fx.FxJson.Sanitize(profit.Tp1.Price),
                        R = Core.Fx.FxJson.Sanitize(profit.Tp1.R),
                        Score = Core.Fx.FxJson.Sanitize(profit.Tp1.Score),
                    },
                    Tp2 = profit.Tp2 is null ? null : new
                    {
                        profit.Tp2.Kind,
                        Price = Core.Fx.FxJson.Sanitize(profit.Tp2.Price),
                        R = Core.Fx.FxJson.Sanitize(profit.Tp2.R),
                        Score = Core.Fx.FxJson.Sanitize(profit.Tp2.Score),
                    },
                    Tp3 = profit.Tp3 is null ? null : new
                    {
                        profit.Tp3.Kind,
                        Price = Core.Fx.FxJson.Sanitize(profit.Tp3.Price),
                        R = Core.Fx.FxJson.Sanitize(profit.Tp3.R),
                        Score = Core.Fx.FxJson.Sanitize(profit.Tp3.Score),
                    },
                    Tp1Probability = Core.Fx.FxJson.Sanitize(profit.Tp1Probability),
                    Tp2Probability = Core.Fx.FxJson.Sanitize(profit.Tp2Probability),
                    Tp3Probability = Core.Fx.FxJson.Sanitize(profit.Tp3Probability),
                    Allocation = new
                    {
                        Tp1 = profit.Allocation.Tp1,
                        Tp2 = profit.Allocation.Tp2,
                        Tp3 = profit.Allocation.Tp3,
                        Runner = profit.Allocation.Runner,
                    },
                    TrailingMode = profit.TrailingMode,
                    ProfitScore = Core.Fx.FxJson.Sanitize(profit.ProfitScore),
                }));
            // Floor telemetry (the ALERT-ONLY era is over: the guard above
            // COMMANDS the exit; this row + a single ARMED notification are
            // evidence, not enforcement).
            if (profit.FloorR > 0 && guard.State == Core.Fx.FxFloorState.Protected)
            {
                var justArmed = _floorArmedAlerted.Add(p.Ticket);
                if (justArmed)
                {
                    Journal("FX_FLOOR",
                        $"{p.Symbol} #{p.Ticket}: PROFIT FLOOR ARMED — protected floor {profit.FloorR:0.0}R " +
                        $"(peak {profit.PeakR:0.0}R, current {profit.CurrentR:+0.0;-0.0}R)", "{}");
                    _webhook?.PostRiskRail(
                        $"🛡️ PROFIT FLOOR ARMED — #{p.Ticket}",
                        $"{p.Symbol} | Peak: {profit.PeakR:+0.0;-0.0}R | Protected: {profit.FloorR:0.0}R");
                }
            }
            if (profit.FloorBreached && guard.State == Core.Fx.FxFloorState.Protected)
            {
                // Belt-and-braces: the advisory layer still requests an
                // evaluation if it sees a breach the guard hasn't commanded
                // (e.g. the model floor moved within the same cycle).
                Journal("FX_RISK",
                    $"{p.Symbol} #{p.Ticket}: profit floor {profit.FloorR:0.0}R breached " +
                    $"(current {profit.CurrentR:0.0}R) — exit evaluation requested", "{}");
            }

            // ── TP1 partial prototype (gated, advisory-derived) ──────
            // The Profit Brain's allocation plan is graded LIVE at ONE rung.
            // The ladder always projects AHEAD of price, so "reached" is
            // arm-then-cross: the first cycle where the trade has banked ≥1R
            // of peak and the plan still front-loads exits ARMS the current
            // TP1 price; a later cycle where price has crossed it executes
            // the rung once, through the same close path the Exit Brain
            // uses. Every step is journaled (TP1-ARM / TP1-EXEC under
            // FX_PROFIT) so the graded-vs-advisory split stays auditable.
            // Defaults OFF — an explicit human act turns it on.
            //
            // TRAILING-MODE GATE (docs/soak/TP1-FLOOR-INTERACTION.md): on
            // STRUCTURE_TRAIL tickets the never-down floor ratchets with
            // the peak and out-executes any static rung — bank the rung
            // there and you sell the floor's better exit at 25% of the
            // book. Skip arming; let the floor own the partial.
            // The plan % the rung is armed/sized at: the operator's one-click
            // arm override when present, else the brain's own allocation leg.
            // A 0% override makes the rung ineligible below, which is exactly
            // "do not take a partial" — the fail-safe reading of a 0% call.
            var planPct = Tp1PlanPctOverride ?? profit.Allocation.Tp1;
            var tp1ArmEligible = ExecuteTp1Partials
                && _venueSpec is not null
                && decision.Action is not ("full" or "partial")
                && planPct >= 10
                && st.MfeR >= 1.0;
            var tp1GateBlocked = tp1ArmEligible
                && profit.TrailingMode == "STRUCTURE_TRAIL";
            if (tp1GateBlocked && _tp1GateSkippedTickets.Add(p.Ticket))
            {
                // Journaled once per ticket: the design doc's audit trail —
                // the rung question was reached and the gate answered it.
                Journal("FX_PROFIT",
                    $"TP1-SKIP: {profit.TrailingMode} floor owns the partial on #{p.Ticket} — rung not armed",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = p.Ticket,
                        TrailingMode = profit.TrailingMode,
                        PlanPct = planPct,
                    }));
            }
            // CIRCUIT BREAKER (Tp1PlanBreaker): an overdue plan-% review
            // means the allocation is under review — do not arm further
            // rungs until it is acted on or cleared. The row records the
            // held near-miss once per ticket.
            var tp1BreakerBlocked = tp1ArmEligible
                && Tp1PlanReviewHold?.Invoke() == true;
            if (tp1BreakerBlocked && _tp1BreakerSkippedTickets.Add(p.Ticket))
            {
                Journal("FX_PROFIT",
                    $"TP1-HOLD: overdue plan-% review open — rung not armed for #{p.Ticket}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = p.Ticket,
                        Reason = "plan-review-overdue",
                        PlanPct = planPct,
                    }));
            }
            if (tp1ArmEligible
                && !tp1GateBlocked
                && !tp1BreakerBlocked
                && _venueSpec is { } tp1Spec)
            {
                var tp1 = profit.Tp1;
                var armed = _tp1ArmedTickets.TryGetValue(p.Ticket, out var armedPrice)
                    ? armedPrice : 0.0;
                if (armed == 0.0 && tp1 is { } first)
                {
                    _tp1ArmedTickets[p.Ticket] = first.Price;
                    armed = first.Price;
                    Journal("FX_PROFIT",
                        $"TP1-ARM: rung {first.Kind} {first.Price:0.#####} ({first.R:+0.0;-0.0}R) " +
                        $"armed for #{p.Ticket} ({planPct:0}% plan) — executes when price crosses",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Ticket = p.Ticket,
                            Target = first.Kind,
                            TargetPrice = Core.Fx.FxJson.Sanitize(first.Price),
                            TargetR = Core.Fx.FxJson.Sanitize(first.R),
                            PlanPct = planPct,
                        }));
                }

                var crossed = st.Side == "buy" ? price >= armed : (armed > 0 && price <= armed);
                if (armed > 0 && crossed && _tp1ExecutedTickets.Add(p.Ticket))
                {
                    var lots = SnapLots(p.Volume * planPct / 100.0);
                    if (lots > 0 && lots < p.Volume)
                    {
                        var tp1Close = await _mt5.ClosePositionAsync(p.Ticket, lots).ConfigureAwait(true);
                        Journal("FX_PROFIT",
                            tp1Close.Ok
                                ? $"TP1-EXEC: banked {lots:0.##} lots ({planPct:0}% plan) of #{p.Ticket} at the armed rung " +
                                  $"{armed:0.#####} — allocation graded live"
                                : $"TP1-EXEC refused for #{p.Ticket}: {tp1Close.RetcodeName}",
                            System.Text.Json.JsonSerializer.Serialize(new
                            {
                                Ticket = p.Ticket,
                                Executed = tp1Close.Ok,
                                Lots = lots,
                                PlanPct = planPct,
                                ArmedPrice = Core.Fx.FxJson.Sanitize(armed),
                                tp1Close.Retcode,
                                VenueSpec = new { tp1Spec.ContractSize, tp1Spec.VolumeStep },
                            }));
                        if (tp1Close.Ok)
                        {
                            StatusChanged?.Invoke($"TP1 banked: {lots:0.##} lots of #{p.Ticket} @ {armed:0.#####}");
                        }
                    }
                }
            }

            if (decision.Action == "full"
                || (decision.Action == "partial" && decision.LotsToClose > 0))
            {
                var lots = decision.Action == "full" ? (double?)null : SnapLots(decision.LotsToClose);
                var close = await _mt5.ClosePositionAsync(p.Ticket, lots).ConfigureAwait(true);
                Journal("FX_EXIT",
                    close.Ok
                        ? $"closed {(lots.HasValue ? $"{lots:0.##} lots of " : string.Empty)}#{p.Ticket} — deal {close.Deal}"
                        : $"close refused for #{p.Ticket}: {close.RetcodeName}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = p.Ticket,
                        Partial = lots.HasValue,
                        // Settled R on a FULL close: the same ProfitR the
                        // shadow ledger's `won` reads (decision-time price ≈
                        // fill) — the measurement substrate for fx_win_rate
                        // and the recent-tape feed. Partial/refused closes
                        // have no settled outcome yet → never invented.
                        RealizedR = close.Ok && !lots.HasValue
                            ? Core.Fx.FxJson.Sanitize(decision.ProfitR)
                            : (double?)null,
                        OutcomeSource = close.Ok && !lots.HasValue ? "close-price" : "unknown",
                        Lots = lots,
                        close.Retcode,
                    }));
                if (close.Ok && decision.Action == "full")
                {
                    // The engines-earn-votes loop: grade every shadow
                    // engine's final vote into the promotion ledger (won or
                    // lost — the ledger, not this call, computes accuracy).
                    // HWARANG's target-TP engine rides the same race at
                    // weight 0 — "extract at the target" is a hypothesis
                    // the same evidence bar must confirm or bury.
                    // Drawdown rides at WEIGHT 2.0, so the weight-0 filter
                    // below would drop it — and with it every drawdown
                    // verdict on a full close (0 "full" rows across 11 full
                    // closes: the engine graded nothing at the bar it is
                    // measured by). Keep it in explicitly, the same shape
                    // as the giveback add underneath.
                    //
                    // The giveback vote that rides IN the evaluation is
                    // dropped here: the settled mirror below is the same
                    // trade's giveback row, so keeping both wrote TWO
                    // giveback rows per close (live: 14 rows for 11
                    // tickets) — one trade counted twice toward the
                    // promotion denominator. One row per engine per
                    // settled close.
                    var shadow = decision.Votes
                        .Where(v => v.Engine == "drawdown"
                                    || (v.Weight == 0 && v.Engine != "giveback"))
                        .ToList();
                    shadow.Add(profit.TargetTpVote);
                    // The settled giveback voice carries the drawdown
                    // engine's peak→breach evidence, so a profit-floor
                    // SAVE can credit it (Helped() rule 2).
                    shadow.Add(GivebackShadowVote(decision));
                    var won = decision.ProfitR > 0;
                    _shadowLedger?.Append(
                        p.Ticket, p.Symbol,
                        shadow,
                        decision.Action, won, DateTimeOffset.UtcNow,
                        decision);
                    _exitStates.Remove(p.Ticket);
                    RecordSettled(p.Ticket, Symbol, decision.ProfitR);
                    _familiesByTicket.Remove(p.Ticket);
                    _entryRegimes.Remove(p.Ticket);
                    _profitFloors.Remove(p.Ticket);
                }
            }
            else if (decision.Action == "tighten" && decision.NewSl > 0)
            {
                var mod = await _mt5.ModifyPositionAsync(p.Ticket, sl: decision.NewSl).ConfigureAwait(true);
                Journal("FX_EXIT",
                    mod.Ok
                        ? $"trailed #{p.Ticket} stop to {decision.NewSl:0.#####} (+0.2R)"
                        : $"stop trail refused for #{p.Ticket}: {mod.RetcodeName}",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = p.Ticket,
                        NewSl = Core.Fx.FxJson.Sanitize(decision.NewSl),
                        mod.Retcode,
                    }));
            }
        }

        // Bookkeeping: prune tracking state for tickets that vanished from
        // the venue — but ONLY on reads that are certainly real. A degraded
        // read degrades to EMPTY, and two degraded reads agree on the lie:
        // SetEquals(∅,∅) wiped the whole book during the 08:21-09:06
        // congestion, the local-book floor hit zero, and the cap failed
        // open AGAIN. So pruning demands both reads NON-empty and agreeing;
        // an empty read never shrinks the book. Cost: after a genuine
        // flatten the floor stays high until a non-empty read re-grounds
        // it — refusing trades is the safe direction.
        //
        // Reachability note (2026-10-08): `owned` derives from this cycle's
        // own positions read — the same read the proven-flat prune above
        // always runs first on a non-empty book — so this block's gone-set
        // is normally already retired there. It fires only when the fresh
        // confirm read below RACES the cycle read (position listed at cycle
        // start, gone by the confirm fetch). Kept as the belt-and-suspenders
        // twin with the same settled-R payload; its identical row shape was
        // exercised via the proven-flat path's test.
        var confirm = await _mt5.GetPositionsAsync().ConfigureAwait(true);
        if (owned.Count > 0 && confirm.Count > 0
            && owned.Select(p => p.Ticket).ToHashSet()
                .SetEquals(confirm.Select(p => p.Ticket)))
        {
            var live = owned.Select(p => p.Ticket).ToHashSet();
            foreach (var gone in _exitStates.Keys.Where(k => !live.Contains(k)).ToList())
            {
                // Snapshot the state BEFORE the removals below: the close
                // row's snapshot R and the recent-tape feed need
                // entry/side/stop after the book forgets the ticket.
                var closedState = _exitStates.TryGetValue(gone, out var cs) ? cs : null;
                // A submitted floor exit's close line belongs to the
                // reconciliation below (it fires THIS cycle — confirm.Count
                // > 0 gated this prune): deferring here keeps exactly one
                // "closed #" row per ticket. Every other vanish (SL hit,
                // refused-then-dropped close) has NO close line yet — write
                // it now so the journal book retires the fill without the
                // ops-layer reconcile.
                var deferredToReconcile = _floorGuards.TryGetValue(gone, out var prunedGuard)
                    && prunedGuard.State == Core.Fx.FxFloorState.ExitSubmitted;
                if (deferredToReconcile && prunedGuard is not null)
                {
                    // Same deferral as the proven-flat sweep above: the
                    // snapshot rides on the guard for the confirmation
                    // loop's settled R instead of dying with the book.
                    prunedGuard.DeferredCloseState = closedState;
                }
                _exitStates.Remove(gone);
                _entryRegimes.Remove(gone);
                _profitFloors.Remove(gone);
                if (!deferredToReconcile)
                {
                    // Snapshot R: entry vs this cycle's mid over the sized
                    // stop — SL hits and manual flattens retire here, so
                    // their outcome feeds the recent tape too.
                    var retireR = closedState is null
                        ? (double?)null
                        : Core.Fx.FxRealizedR.Compute(
                            closedState.EntryPrice, mid,
                            closedState.InitialStopDistance > 0
                                ? closedState.InitialStopDistance
                                : closedState.RiskPerLot,
                            closedState.Side);
                    if (retireR is { } retireRv)
                    {
                        RecordSettled(gone, Symbol, retireRv);
                    }
                    _familiesByTicket.Remove(gone);
                    Journal("FX_EXIT",
                        $"#{gone}: closed #{gone} — broker no longer holds the ticket; " +
                        "stale tracking retired (healthy positions read)",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Ticket = gone,
                            Partial = false,
                            Lots = (double?)null,
                            Retcode = 10009,
                            Reconciled = true,
                            Proof = "healthy positions read",
                            RealizedR = retireR is { } rr4 ? Core.Fx.FxJson.Sanitize(rr4) : (double?)null,
                            OutcomeSource = retireR is not null ? "profit-snapshot" : "unknown",
                        }));
                }
            }
        }

        // Reconciliation (§13): a floor exit is CONFIRMED only when the
        // broker verifiably no longer holds the ticket. Two safe shapes:
        // (a) a NON-EMPTY confirm read that lacks the ticket — a degraded
        // read degrades to EMPTY, so a non-empty read is definitionally
        // real and per-ticket absence is trustworthy evidence, even when
        // owned (this symbol's managed book) and confirm (the WHOLE
        // account) disagree because sibling positions are open — the old
        // whole-book SetEquals gate starved confirmation forever in
        // exactly that shape (2026-09-30 live: deals 11:27, guards still
        // unconfirmed at 11:32 while sibling tickets kept the sets apart);
        // (b) an EMPTY book right after this guard's own close submission
        // SUCCEEDED (the venue returned ok + deal — primary execution
        // evidence; the agreeing empty reads corroborate). A degraded
        // empty read alone can never invent a confirmation, because a
        // guard only reaches ExitSubmitted through an accepted close
        // request for that very ticket.
        var confirmLive = confirm.Select(p => p.Ticket).ToHashSet();
        var readsAgree = confirmLive.SetEquals(owned.Select(p => p.Ticket).ToHashSet());
        if (confirm.Count > 0
            || (readsAgree
                && _floorGuards.Values.Any(g => g.State == Core.Fx.FxFloorState.ExitSubmitted)))
        {
            foreach (var gone in _floorGuards.Keys.Where(k => !confirmLive.Contains(k)).ToList())
            {
                var g = _floorGuards[gone];
                var wasSubmitted = g.State == Core.Fx.FxFloorState.ExitSubmitted;
                g.MarkConfirmed();
                if (wasSubmitted)
                {
                    Journal("FX_FLOOR",
                        $"#{gone}: PROFIT FLOOR EXIT CONFIRMED — the broker no longer holds the ticket; giveback prevented",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Ticket = gone,
                            EventId = g.EventId,
                            ExitOrderId = g.ExitOrderId,
                            FloorState = g.State.ToString(),
                        }));
                    _webhook?.PostRiskRail(
                        $"✅ PROFIT FLOOR EXIT CONFIRMED — #{gone}",
                        $"{Symbol} | Exit confirmed by broker reconciliation (floor event {g.EventId}). Giveback prevented.");

                    // A guard save is promotion evidence too: the promotion
                    // ledger counts every engine's grade against its bar,
                    // and the drawdown voice's deep-giveback evidence drove
                    // this exit even though the guard (not the ensemble)
                    // executed it. Without this append, guard-commanded
                    // saves never reach the substrate (2026-09-30: four
                    // live saves, zero ledger rows). resolvedAction
                    // "floor-exit" tells FxExitShadow.Helped this was a
                    // save the giveback evidence itself drove home. Won:
                    // a save IS the win — the position closed with the
                    // giveback prevented. The giveback MIRROR rides along
                    // so the guard save credits giveback too — drawdown's
                    // peak→breach evidence IS the giveback story, and
                    // without the mirror every guard save starved the
                    // giveback row (2026-09-30: 6 saves, 0 giveback rows).
                    var guardEvidence =
                        "hard profit floor save (guard-executed); peak " +
                        g.PeakR.ToString("0.0") + "R → breach at " + g.BreachR.ToString("+0.0;-0.0") + "R";
                    _shadowLedger?.Append(
                        gone, Symbol,
                        new[]
                        {
                            new Core.Fx.FxExitVote("drawdown", 0.95, 0.0, guardEvidence),
                            new Core.Fx.FxExitVote("giveback", 0.95, 0.0,
                                "giveback (shadow, guard save): mirror of the drawdown engine's evidence — " +
                                guardEvidence),
                        },
                        resolvedAction: "floor-exit", won: true,
                        DateTimeOffset.UtcNow);

                    // THE CLOSE LINE: the floor's exit never wrote the
                    // "closed #" row FxJournalBook parses, so the journal
                    // book held the fill open — the exposure guard floored
                    // at a stale book until the ops-layer reconcile +
                    // restart (~2.5 min of refused trades after every floor
                    // save). Same shape the ensemble's full close writes:
                    // retire the ticket in the journal AND in the host's
                    // own book, right here, no ops script in the loop.
                    // Floor exits flatten AT the floor: entry vs this
                    // cycle's mid over the sized stop — the SAME
                    // computation the ensemble's full close grades, so a
                    // settled floor exit is tier 1 (close-price), not a
                    // snapshot: relabelled 2026-10-08 (spec §1 row 2).
                    // The prune that retired this ticket DEFERRED the close
                    // line to here and handed its snapshot over — the book
                    // no longer holds the state, so fall back to the guard's
                    // stash before giving up on a settled R (unknown).
                    var floorState = _exitStates.TryGetValue(gone, out var fstate)
                        ? fstate
                        : g.DeferredCloseState;
                    var floorR = floorState is { } fs
                        ? Core.Fx.FxRealizedR.Compute(
                            fs.EntryPrice, mid,
                            fs.InitialStopDistance > 0 ? fs.InitialStopDistance : fs.RiskPerLot,
                            fs.Side)
                        : (double?)null;
                    if (floorR is { } floorRv)
                    {
                        RecordSettled(gone, Symbol, floorRv);
                    }
                    _familiesByTicket.Remove(gone);
                    Journal("FX_EXIT",
                        $"#{gone}: closed #{gone} — deal {g.ExitOrderId ?? "?"} " +
                        "(profit floor exit confirmed by broker reconciliation)",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Ticket = gone,
                            Partial = false,
                            Lots = (double?)null,
                            Retcode = 10009,
                            Confirmed = true,
                            RealizedR = floorR is { } fr2 ? Core.Fx.FxJson.Sanitize(fr2) : (double?)null,
                            OutcomeSource = floorR is not null ? "close-price" : "unknown",
                            EventId = g.EventId,
                            ExitOrderId = g.ExitOrderId,
                        }));
                    _exitStates.Remove(gone);
                    _entryRegimes.Remove(gone);
                    _profitFloors.Remove(gone);
                }
                g.MarkReset();
                _floorGuards.Remove(gone);
                _floorArmedAlerted.Remove(gone);
            }

            // §13.4: a non-empty read that STILL holds a submitted guard's
            // ticket means the close did not flatten the position. Downgrade
            // to failed — protection continues, the next Advance re-queues
            // the exit. Skipped inside a submit grace window (2 min): MT5
            // position lists can lag the accepted deal for a breath. The
            // window is internal static so tests can shrink it — a real
            // broker never confirms inside 2 minutes, so production timing
            // is unaffected.
            foreach (var stuck in _floorGuards.Values
                .Where(g => g.State == Core.Fx.FxFloorState.ExitSubmitted
                            && confirmLive.Contains(g.Ticket)
                            && (g.SubmittedAtUtc is not { } sub
                                || DateTimeOffset.UtcNow - sub > FloorSubmitGrace))
                .ToList())
            {
                stuck.MarkFailed();
                Journal("FX_FLOOR",
                    $"#{stuck.Ticket}: close accepted but the broker still holds the position — downgrade to EXIT_FAILED; protection continues",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = stuck.Ticket,
                        EventId = stuck.EventId,
                        ExitOrderId = stuck.ExitOrderId,
                    }));
                _webhook?.PostRiskRail(
                    $"⚠️ PROFIT FLOOR EXIT UNVERIFIED — #{stuck.Ticket}",
                    $"{Symbol} | Close was accepted but the position persists; protection continues and the exit will be re-issued next cycle.");
            }
        }
    }

    /// <summary>Prove the venue is flat enough for this host to retire
    /// stale tracking state — the in-app twin of the ops-layer
    /// venue_is_flat gate (book_recovery.ps1: two empty /positions reads
    /// AND equity == balance AND margin == 0). Returns the ticket set a
    /// prune may trust as live, or null when flatness is NOT proven (never
    /// prune — the safe direction is refusing trades, not freeing the
    /// book). Two safe shapes:
    /// (a) a NON-empty positions read — a degraded read degrades to EMPTY
    ///     (never to a lie carrying rows), so absence from it is evidence;
    /// (b) two agreeing EMPTY reads PLUS a settled account: equity ≈ balance
    ///     AND margin == 0. The empty pair alone is the 2026-09-29
    ///     congestion lie (/account probed fine while /positions degraded),
    ///     so it only counts when the account independently says flat.
    /// Missing account data or an absent margin field fails CLOSED.</summary>
    private async Task<(HashSet<long>? Live, string Proof)> ProvenLiveTicketsAsync(
        IReadOnlyList<Mt5Position> positions)
    {
        if (positions.Count > 0)
        {
            return (positions.Select(p => p.Ticket).ToHashSet(), "healthy positions read");
        }

        var second = await _mt5.GetPositionsAsync().ConfigureAwait(true);
        if (second.Count > 0)
        {
            return (second.Select(p => p.Ticket).ToHashSet(), "healthy positions read");
        }

        var account = await _mt5.GetAccountAsync().ConfigureAwait(true);
        if (account is not null
            && account.Margin is { } margin && margin <= 1e-6
            && Math.Abs(account.Equity - account.Balance) <= 0.01)
        {
            return (new HashSet<long>(),
                "two agreeing empty reads + settled account (equity ≈ balance, margin 0)");
        }

        return (null, string.Empty);
    }

    /// <summary>Snap an exit's lots to the venue's volume step (never below
    /// one step; /close validates the upper bound).</summary>
    private double SnapLots(double lots)
    {
        var step = _venueSpec is { } spec && spec.VolumeStep > 0 ? spec.VolumeStep : 0.01;
        return Math.Round(Math.Max(step, Math.Round(lots / step) * step), 2);
    }

    /// <summary>The giveback engine's weight-0 shadow voice, reconstructed
    /// from the settled decision's final evidence so a profit-floor SAVE
    /// can credit the shadow ledger's giveback row (the ensemble's vote list
    /// carries weighted engines only at close time).</summary>
    internal static FxExitVote GivebackShadowVote(FxExitDecision decision)
    {
        var dd = decision.Votes.FirstOrDefault(v => v.Engine == "drawdown");
        return dd is not null
            ? new FxExitVote("giveback", dd.Exit, 0,
                $"giveback (shadow, settled): mirror of the drawdown engine's final evidence — {dd.Reason}")
            : new FxExitVote("giveback", 0, 0, "giveback (shadow, settled): no drawdown evidence in the final evaluation");
    }

    /// <summary>Pip-size heuristic for the spread override (gold-like 0.1,
    /// JPY-class 0.01, everything else 0.0001).</summary>
    private static double PipSizeFor(double price) =>
        price >= 400 ? 0.1 : price >= 20 ? 0.01 : 0.0001;

    /// <summary>The hard-floor breach handler: journal the command, then
    /// the retry ladder; reconciliation and further retries continue on the
    /// next cycle (or floor-watch tick). Shared by RunCycleAsync and
    /// RunFloorWatchTickAsync so there is exactly ONE submit path.</summary>
    private async Task SubmitFloorExitAsync(
        long ticket, string symbol, string side, double riskPerLot,
        Core.Fx.FxProfitFloorGuard guard, Core.Fx.FxFloorVerdict guardVerdict,
        double executablePrice)
    {
        Journal("FX_FLOOR",
            $"{symbol} #{ticket}: {guardVerdict.Reason}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Ticket = ticket,
                Symbol = symbol,
                Direction = side,
                OriginalRisk = Core.Fx.FxJson.Sanitize(riskPerLot),
                CurrentR = Core.Fx.FxJson.Sanitize(guardVerdict.ExecutableR),
                PeakR = Core.Fx.FxJson.Sanitize(guardVerdict.PeakR),
                ProtectedFloorR = Core.Fx.FxJson.Sanitize(guardVerdict.FloorR),
                ExecutablePrice = Core.Fx.FxJson.Sanitize(executablePrice),
                FloorState = guardVerdict.State.ToString(),
                EventId = guardVerdict.EventId,
                ExitReason = "hard profit floor breach",
            }));

        // Submit with a retry ladder; reconciliation and any further
        // retries continue on the next cycle/watch tick.
        for (var attempt = 1; attempt <= Core.Fx.FxProfitFloorGuard.MaxSubmitAttempts; attempt++)
        {
            var close = await _mt5.ClosePositionAsync(ticket, null).ConfigureAwait(true);
            if (close.Ok)
            {
                guard.MarkSubmitted(close.Order?.ToString() ?? close.Deal?.ToString() ?? "?");
                _webhook?.PostRiskRail(
                    $"🚨 HARD PROFIT FLOOR BREACH — #{ticket}",
                    $"{symbol} | Current: {guardVerdict.ExecutableR:+0.0;-0.0}R | Floor: {guardVerdict.FloorR:0.0}R " +
                    $"(peak {guardVerdict.PeakR:0.0}R). Hard exit submitted (deal {close.Deal ?? close.Order}).");
                Journal("FX_FLOOR",
                    $"{symbol} #{ticket}: PROFIT FLOOR EXIT SUBMITTED — deal {close.Deal ?? close.Order} " +
                    $"(event {guardVerdict.EventId}, attempt {attempt})",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = ticket,
                        EventId = guardVerdict.EventId,
                        ExitOrderId = close.Order,
                        Deal = close.Deal,
                        BrokerResponse = close.RetcodeName,
                        Attempt = attempt,
                        ExecutablePrice = Core.Fx.FxJson.Sanitize(executablePrice),
                    }));
                break;
            }

            Journal("FX_FLOOR",
                $"{symbol} #{ticket}: PROFIT FLOOR EXIT FAILED — {close.RetcodeName} " +
                $"(attempt {attempt}/{Core.Fx.FxProfitFloorGuard.MaxSubmitAttempts}); protection remains active, retrying",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Ticket = ticket,
                    EventId = guardVerdict.EventId,
                    BrokerResponse = close.RetcodeName,
                    Attempt = attempt,
                }));
        }

        if (guard.State != Core.Fx.FxFloorState.ExitSubmitted)
        {
            guard.MarkFailed();
            _webhook?.PostRiskRail(
                $"❌ PROFIT FLOOR EXIT FAILED — #{ticket}",
                $"{symbol} | All {Core.Fx.FxProfitFloorGuard.MaxSubmitAttempts} submit attempts refused. " +
                $"Protection remains active at {guardVerdict.FloorR:0.0}R — retrying next cycle. NEVER reverting to HOLD.");
        }
    }

    /// <summary>One floor-watch tick: advance every guard holding a locked
    /// floor (or awaiting a retry) on a FRESH executable tick, WITHOUT
    /// running a cycle. Defers while a cycle is running; skips guards with
    /// an exit in flight (the guard's once-per-breach event keeps submit
    /// idempotent). Returns when there is no locked floor to watch — the
    /// common case costs zero bridge calls. Test seam: called directly.</summary>
    internal async Task RunFloorWatchTickAsync()
    {
        if (_cycleRunning)
        {
            return;
        }

        List<long>? watchList = null;
        foreach (var kv in _floorGuards)
        {
            var g = kv.Value;
            var locked = g.FloorR > 0 || g.State == Core.Fx.FxFloorState.ExitFailed;
            var inFlight = g.State is Core.Fx.FxFloorState.ExitPending
                or Core.Fx.FxFloorState.ExitSubmitted;
            if (locked && !inFlight)
            {
                (watchList ??= new List<long>()).Add(kv.Key);
            }
        }
        if (watchList is null)
        {
            return;
        }

        foreach (var ticket in watchList)
        {
            if (!_exitStates.TryGetValue(ticket, out var st))
            {
                continue;   // first cycle hasn't seen it yet — the cycle owns it
            }
            var guard = _floorGuards[ticket];
            var tick = await _mt5.GetTickAsync(st.Symbol).ConfigureAwait(true);
            if (tick is not { } tk || tk.Bid <= 0 || tk.Ask <= 0)
            {
                continue;   // no fresh price, no opinion — next tick retries
            }
            var executablePrice = st.Side == "buy" ? tk.Bid : tk.Ask;
            var executableR = st.Side == "buy"
                ? (executablePrice - st.EntryPrice) / Math.Max(st.RiskPerLot, 1e-9)
                : (st.EntryPrice - executablePrice) / Math.Max(st.RiskPerLot, 1e-9);
            var guardVerdict = guard.Advance(executableR,
                _profitFloors.TryGetValue(ticket, out var modelFloor) ? modelFloor : 0.0,
                Math.Max(st.MfeR, executableR));
            if (guardVerdict.Command == Core.Fx.FxFloorCommand.ExecuteExit)
            {
                await SubmitFloorExitAsync(ticket, st.Symbol, st.Side, st.RiskPerLot,
                    guard, guardVerdict, executablePrice).ConfigureAwait(true);
            }
        }
    }

    private void Journal(string category, string detail, string json) =>
        _journal.Log(Guid.Empty, category, detail, json);

    public FxDecision? LastDecision { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _floorWatchTimer.Stop();
    }
}
