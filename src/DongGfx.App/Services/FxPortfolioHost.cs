using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>
/// Shared exposure guard for the multi-symbol FX portfolio: sums open MT5
/// position volume for the brain's symbols and refuses any order that would
/// push the total past the portfolio cap (FxPortfolioMaxLots). One guard
/// instance is shared by every per-symbol engine, so four engines cannot
/// each independently decide 0.10 lots is fine — the cap is portfolio-wide
/// and checked against live positions at order time.
/// </summary>
public sealed class FxExposureGuard
{
    private readonly Mt5BridgeClient _mt5;
    private readonly Func<decimal> _maxTotalLots;
    private readonly HashSet<string> _symbols;
    private readonly Func<double>? _localBookLots;

    public FxExposureGuard(Mt5BridgeClient mt5, Func<decimal> maxTotalLots,
        IEnumerable<string> symbols, Func<double>? localBookLots = null)
    {
        _mt5 = mt5;
        _maxTotalLots = maxTotalLots;
        _symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        _localBookLots = localBookLots;
    }

    /// <summary>Returns a refusal reason when adding <paramref name="requestedLots"/>
    /// would exceed the portfolio cap; null when the order fits. A cap of 0
    /// disables the veto (per-order Mt5MaxLots still applies).</summary>
    public async Task<string?> VetoAsync(double requestedLots)
    {
        var cap = _maxTotalLots();
        if (cap <= 0)
        {
            return null;
        }

        double open;
        try
        {
            // GetPositionsAsync degrades to an EMPTY list when the bridge is
            // down — indistinguishable from a flat account, and the 2026-09-29
            // congestion incident rode exactly that hole: /account probed fine
            // while /positions timed out, the guard read a flat book, and the
            // cap failed OPEN (7 fills past a 0.10 cap). Rule: a position read
            // only counts when TWO reads AGREE; any disagreement or a null
            // account is unknown exposure → refuse.
            var account = await _mt5.GetAccountAsync().ConfigureAwait(false);
            if (account is null)
            {
                return "bridge unreachable (exposure unknown)";
            }

            var first = await _mt5.GetPositionsAsync().ConfigureAwait(false);
            var second = await _mt5.GetPositionsAsync().ConfigureAwait(false);
            open = first.Where(p => _symbols.Contains(p.Symbol)).Sum(p => p.Volume);
            var open2 = second.Where(p => _symbols.Contains(p.Symbol)).Sum(p => p.Volume);
            if (Math.Abs(open - open2) > 1e-9)
            {
                return $"exposure reads disagree ({open:0.##} vs {open2:0.##}) — refusing while unknown";
            }

            // The agreeing-reads rule still has a hole: both reads can
            // degrade to EMPTY in agreement (the 2026-09-29 07:19 fills),
            // reading a loaded book as flat. Floor the exposure at the
            // brain's own book — the engines' tracking of what THEY hold,
            // which no bridge flake can erase.
            open = Math.Max(open, _localBookLots?.Invoke() ?? 0);
        }
        catch
        {
            // Defensive: the client already degrades cleanly, but any surprise
            // exception here must still fail closed.
            return "bridge unreachable (exposure unknown)";
        }

        if (open + requestedLots > (double)cap + 1e-9)
        {
            return $"portfolio exposure {open:0.##} + {requestedLots:0.##} lots > cap {cap:0.##} lots";
        }

        return null;
    }
}

/// <summary>
/// Small-account guard: on a balance where ONE minimum-lot trade already
/// risks more than <see cref="MaxRiskFraction"/> of equity, the portfolio
/// drops to one brain trade at a time at each symbol's venue minimum lot —
/// a small account cannot diversify its way out of a bad night, so the
/// point is to slow the bleed, not to size up. The "small" threshold is
/// deliberately auto-detected (no fixed dollar number): it compares the
/// venue's own minimum-lot notional against live equity, so the mode
/// activates on whatever account is actually connected. Brain-stamped
/// positions only consume the slot — manual/legacy trades don't block the
/// brain, but the portfolio exposure cap still bounds them. Bridge-down
/// fails closed exactly like <see cref="FxExposureGuard"/>.
/// </summary>
public sealed class FxSmallAccountGuard
{
    /// <summary>Representative stop distance when the venue's real stop is
    /// unknown: 15 pips — a conventional swing-size stop on FX majors.
    /// Only used to DECIDE the mode (auto small-account detection), never
    /// to place an order.</summary>
    public const int RepresentativeStopPips = 15;

    /// <summary>One minimum-lot trade costing more than this fraction of
    /// equity trips small-account mode.</summary>
    public const double MaxRiskFraction = 0.05;

    private readonly Mt5BridgeClient _mt5;
    private readonly HashSet<string> _symbols;
    private readonly Func<DateTimeOffset>? _clock;
    private readonly Func<double>? _localBookLots;

    public FxSmallAccountGuard(Mt5BridgeClient mt5, IEnumerable<string> symbols,
        Func<DateTimeOffset>? clock = null, Func<double>? localBookLots = null)
    {
        _mt5 = mt5;
        _symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        _clock = clock;
        _localBookLots = localBookLots;
    }

    /// <summary>True when the connected account is "small": at least one
    /// brain symbol's minimum-lot trade (contract size × volume min × a
    /// representative stop) would cost more than 5% of live equity. Bridge
    /// or parse failures THROW — callers decide the fail-closed policy
    /// (the veto rail must never fail open on a flaky bridge); missing
    /// per-symbol geometry merely can't vote.</summary>
    public bool SmallAccountDetected() =>
        SmallAccountDetected(
            _mt5.GetAccountAsync().ConfigureAwait(false).GetAwaiter().GetResult());

    /// <summary>Account-taking overload: lets the veto rail probe the
    /// bridge itself and fail closed on "unreachable" before deciding the
    /// mode (the client degrades transport errors to null, so "null" is
    /// the only honest signal of a dead bridge).</summary>
    public bool SmallAccountDetected(Mt5Account? account)
    {
        if (account is null || account.Equity <= 0)
        {
            return false;   // no account ≠ small account; the veto rail fails closed separately
        }

        var symbols = _mt5.GetSymbolsAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        var equity = account.Equity;
        foreach (var s in symbols.Where(s => _symbols.Contains(s.Symbol)))
        {
            if (s.ContractSize <= 0 || s.VolumeMin <= 0)
            {
                continue;   // unknown geometry — cannot declare "small" from it
            }

            var minNotional = s.ContractSize * s.VolumeMin;
            var minRisk = minNotional * RepresentativeStopPips * FxExitBrain.PipSizeOf(s.Bid ?? 0);
            if (minRisk > equity * MaxRiskFraction)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rail: in small-account mode at most ONE brain-owned
    /// position may exist portfolio-wide. Returns a refusal reason, or
    /// null when the order may proceed (also null when the mode is not
    /// active — large accounts are unlimited by this rail).</summary>
    public async Task<string?> VetoAsync(string symbol, double requestedLots)
    {
        try
        {
            // Probe the bridge FIRST: an unreachable bridge must refuse —
            // the veto must never fail open on a flaky connection.
            var account = await _mt5.GetAccountAsync().ConfigureAwait(false);
            if (account is null)
            {
                return "small-account mode — bridge unreachable (account unknown)";
            }

            if (!SmallAccountDetected(account))
            {
                return null;   // large account: this rail is dormant
            }

            // Same discipline as the exposure guard: one degraded read
            // reads as a free slot. Two agreeing reads, or refuse.
            var first = await _mt5.GetPositionsAsync().ConfigureAwait(false);
            var second = await _mt5.GetPositionsAsync().ConfigureAwait(false);
            var ours = first.FirstOrDefault(p => Core.Fx.FxExitBrain.Owns(p.Comment));
            var ours2 = second.FirstOrDefault(p => Core.Fx.FxExitBrain.Owns(p.Comment));
            if ((ours is null) != (ours2 is null) || ours?.Ticket != ours2?.Ticket)
            {
                return "small-account mode — position reads disagree (bridge flaked)";
            }

            if (ours is not null)
            {
                return $"small-account mode — one brain trade at a time " +
                       $"(slot used by #{ours.Ticket} {ours.Symbol})";
            }

            // Both reads can agree on EMPTY while degraded (the same
            // congestion mode that broke the cap): if the brain's own book
            // still tracks a position, the slot is NOT free.
            if (_localBookLots?.Invoke() > 0)
            {
                return "small-account mode — one brain trade at a time " +
                       "(slot held by the brain's own book; venue read could not confirm it)";
            }
        }
        catch
        {
            // Unknown state in an active mode: fail closed.
            return "small-account mode — account state unknown (bridge flaked)";
        }

        return null;
    }

    /// <summary>In small-account mode an order is clamped DOWN to the
    /// symbol's venue minimum ("the lowest minimum lot size"); normal
    /// accounts pass through untouched. Unknown geometry clamps to the
    /// engine-wide 0.01 floor — the smallest size a 0.01-step venue can
    /// accept — never up.</summary>
    public double ClampedLots(string symbol, double lots)
    {
        if (!SmallAccountDetected())
        {
            return lots;
        }

        try
        {
            var s = _mt5.GetSymbolsAsync().ConfigureAwait(false).GetAwaiter().GetResult()
                .FirstOrDefault(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (s is { } spec && spec.VolumeMin > 0)
            {
                return Math.Min(lots, spec.VolumeMin);
            }
        }
        catch
        {
            // fall through to the conservative floor
        }

        return Math.Min(lots, 0.01);
    }
}

/// <summary>
/// News veto with a file cache: re-reads the operator-maintained calendar
/// only when its mtime changes (cheap enough for an order-time check), and
/// never throws — a missing/rotated file just means no known events.
/// </summary>
public sealed class FxNewsVeto
{
    private readonly Func<string> _path;
    private readonly Func<TimeSpan> _window;
    private FxNewsCalendar _calendar = new();
    private string? _loadedPath;
    private DateTime _loadedMtime;
    private DateTime _lastRead;

    public FxNewsVeto(Func<string> path, Func<TimeSpan> window)
    {
        _path = path;
        _window = window;
    }

    public (bool Blackout, string Reason) Evaluate(DateTimeOffset now)
    {
        ReloadIfChanged();
        var half = _window();
        if (half <= TimeSpan.Zero)
        {
            return (false, "");
        }

        return _calendar.IsBlackout(now, half, half, out var reason)
            ? (true, reason)
            : (false, "");
    }

    private void ReloadIfChanged()
    {
        // at most one filesystem probe per 30 s
        if ((DateTime.UtcNow - _lastRead).TotalSeconds < 30)
        {
            return;
        }

        _lastRead = DateTime.UtcNow;
        try
        {
            var path = _path();
            var mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path != _loadedPath || mtime != _loadedMtime)
            {
                _calendar = FxNewsCalendar.Load(path);
                _loadedPath = path;
                _loadedMtime = mtime;
            }
        }
        catch (IOException)
        {
            // keep the previously loaded calendar
        }
    }
}

/// <summary>
/// The multi-symbol FX portfolio: one FxEngineHost per configured symbol,
/// all sharing ONE FxSupervisor (account-level risk), one exposure guard
/// (portfolio-level cap), and one news veto. Start/Stop/GoLive/GoPaper are
/// aggregate acts; per-symbol status is forwarded with a symbol prefix so
/// the Terminal's status line shows which engine spoke.
/// </summary>
public sealed class FxPortfolioHost : IDisposable
{
    private readonly List<FxEngineHost> _hosts = new();
    private readonly Mt5BridgeClient _mt5;
    private readonly FxJournalBook _journalBook;
    private readonly Func<double> _journalBookLots;
    private readonly TradeJournal _journal;
    private readonly Func<decimal> _portfolioMaxLots;
    private readonly System.Threading.Timer? _exposureAudit;

    /// <summary>Ticket → gone, remembered between reconcile passes: a
    /// journal-book ticket must be venue-provably gone in TWO passes
    /// (minutes apart) before its proof close is written. The window lets
    /// a host's own prune claim a vanish it already tracks — its close row
    /// lands first, the ticket leaves the open set, and the portfolio pass
    /// never writes a second (double-retiring) close for the same ticket.
    /// Restart-race strands have no host to claim them, so pass 2 writes.</summary>
    private readonly HashSet<long> _reconcilePending = new();
    private int _reconciling;

    public IReadOnlyList<FxEngineHost> Hosts => _hosts;
    public IReadOnlyList<string> Symbols { get; }
    public FxSupervisor Supervisor { get; }

    /// <summary>Per-symbol status lines, forwarded from every host.</summary>
    public event Action<string>? StatusChanged;

    public FxPortfolioHost(
        Mt5BridgeClient mt5,
        TradeJournal journal,
        IReadOnlyList<string> symbols,
        Func<bool> killSwitchEngaged,
        Func<decimal> lotsCap,
        Func<bool> realMoneyUnlocked,
        Func<bool> governorTripped,
        Func<decimal> dailyLossCap,
        Func<decimal> equityFloor,
        Func<decimal> portfolioMaxLots,
        WebhookService? webhook,
        Func<string> newsCalendarPath,
        Func<TimeSpan> newsWindow,
        double riskFraction = 0.02,
        string? shadowLedgerDir = null,
        PaperSoakLedger? soakLedger = null,
        FxBrainMemory? memory = null)
    {
        Symbols = symbols;
        Supervisor = new FxSupervisor(journal, killSwitchEngaged, governorTripped,
            dailyLossCap, equityFloor, webhook);

        // The local-book floor has two independent sources, and the guard
        // takes the MAX of both: (1) the per-symbol hosts' own tracking —
        // which seeds from venue reads and can be lied empty; (2) the
        // JOURNAL's book — fills minus close confirmations, written locally
        // before any venue round-trip can lie about it. The 09:45-09:51
        // relaunch leak rode (1): fresh hosts + degraded reads = zero floor.
        // The journal book cannot forget what the venue claims is gone.
        var journalBook = new FxJournalBook(journal.JournalDir);
        _mt5 = mt5;
        _journalBook = journalBook;
        Func<double> floor = () => Math.Max(
            _hosts.Sum(h => h.LocalBookLots), journalBook.OpenLots());

        // Self-audit: the 2026-09-29 cap leaks ran ~4 hours before a human
        // read the journal. A periodic WARN makes an over-cap book
        // self-reporting — it trades nothing, it only shouts.
        _journalBookLots = journalBook.OpenLots;
        _journal = journal;
        _portfolioMaxLots = portfolioMaxLots;
        // First tick at 30 s: the journal-book reconcile heals restart-race
        // strands (a venue-side exit that landed while the app was down
        // never gets a "closed #" row from any host — 9 of 10 real fills
        // stranded that way before this existed), and the self-audit starts
        // watching for over-cap books immediately instead of 5 minutes in.
        // Reconcile's second pass rides the 5-minute cadence that follows.
        _exposureAudit = new System.Threading.Timer(
            _ =>
            {
                AuditExposure();
                _ = ReconcileJournalBookAsync();
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        var exposure = new FxExposureGuard(mt5, portfolioMaxLots, symbols,
            localBookLots: floor);
        var news = new FxNewsVeto(newsCalendarPath, newsWindow);
        var small = new FxSmallAccountGuard(mt5, symbols,
            localBookLots: floor);

        // Per-engine sizing cap never exceeds the portfolio's total cap -
        // otherwise every live order would self-veto at the exposure guard.
        Func<decimal> engineCap = () =>
        {
            var total = portfolioMaxLots();
            var single = lotsCap();
            return total > 0 ? Math.Min(single, total) : single;
        };

        // Stagger the per-symbol engines by 15s: all four otherwise tick
        // together and their burst queues behind the sidecar's single MT5
        // lock, reading as bridge timeouts (2026-09-28 congestion incident).
        var staggerIndex = 0;
        foreach (var symbol in symbols)
        {
            var host = new FxEngineHost(
                mt5, journal, symbol, killSwitchEngaged, engineCap, realMoneyUnlocked,
                riskFraction, Supervisor, webhook: webhook,
                preOrderVeto: async lots => await small.VetoAsync(symbol, lots).ConfigureAwait(true)
                                         ?? await exposure.VetoAsync(lots).ConfigureAwait(true),
                newsVeto: () => news.Evaluate(DateTimeOffset.UtcNow),
                smallAccountClamp: (sym, lots) => small.ClampedLots(sym, lots),
                cycleOffset: TimeSpan.FromSeconds(15 * staggerIndex),
                shadowLedgerPath: shadowLedgerDir is null
                    ? null
                    : Path.Combine(shadowLedgerDir, $"fx-shadow-{symbol}.jsonl"),
                // One ledger, shared by every symbol: the GO LIVE bar counts
                // per symbol and is all-or-nothing across the portfolio, so
                // progress must survive restarts for the whole book at once.
                soakLedger: soakLedger,
                // One memory, shared by every symbol: measured training
                // evidence for a (symbol, alpha) cell tilts that alpha's
                // weight on that symbol's engine only. Research only.
                memory: memory);
            staggerIndex++;
            host.StatusChanged += s => StatusChanged?.Invoke($"[{symbol}] {s}");
            _hosts.Add(host);
        }
    }

    public bool IsRunning => _hosts.Any(h => h.IsRunning);

    public bool IsLiveEngine => _hosts.Any(h => h.IsLiveEngine);

    public int PaperSignalsSeen => _hosts.Sum(h => h.PaperSignalsSeen);

    /// <summary>Paper signals the book carried over from the ledger when the
    /// brain last started (0 on a cold start) — surfaced in the account bar's
    /// soak pill, so a resumed bar is visible rather than implied.</summary>
    public int PaperSoakRestored => _hosts.Sum(h => h.PaperSoakRestored);

    /// <summary>The build stamp whose counters were dropped for any symbol
    /// (null when none): a build change restarts the bar on purpose, and the
    /// pill says so instead of appearing to have lost progress.</summary>
    public string? PaperSoakInvalidatedBuild => _hosts
        .Select(h => h.PaperSoakInvalidatedBuild)
        .FirstOrDefault(b => b is not null);

    /// <summary>The MT5 login whose counters were dropped for any symbol
    /// (null when none): an account switch restarts the bar on purpose, and
    /// the pill says so instead of appearing to have lost progress.</summary>
    public string? PaperSoakInvalidatedAccount => _hosts
        .Select(h => h.PaperSoakInvalidatedAccount)
        .FirstOrDefault(a => a is not null);

    public int PaperSoakSignalsRequired => _hosts.Sum(h => h.PaperSoakSignalsRequired);

    /// <summary>The soak is complete only when EVERY symbol has soaked —
    /// go-live is all-or-nothing across the portfolio.</summary>
    public bool PaperSoakComplete => _hosts.All(h => h.PaperSoakComplete);

    /// <summary>The symbols still short of their soak bar, laggard first —
    /// the names a go-live refusal should show, because those are the ones
    /// holding the whole portfolio in paper.</summary>
    public IReadOnlyList<FxEngineHost> SoakLaggards => _hosts
        .Where(h => !h.PaperSoakComplete)
        .OrderBy(h => h.PaperSignalsSeen)
        .ThenBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public FxDecision? LastDecision => _hosts.LastOrDefault(h => h.LastDecision is not null)?.LastDecision;

    public void Start()
    {
        foreach (var h in _hosts)
        {
            h.Start();
        }
    }

    public void Stop()
    {
        foreach (var h in _hosts)
        {
            h.Stop();
        }
    }

    /// <summary>Prove which tickets the venue still holds, fail-closed —
    /// the portfolio-level twin of FxEngineHost.ProvenLiveTicketsAsync.
    /// Returns null when flatness is NOT proven (never let a reconcile
    /// close anything on unknown exposure). Two safe shapes:
    /// (a) a NON-empty positions read — a degraded read degrades to EMPTY,
    ///     so absence from it is evidence;
    /// (b) two agreeing EMPTY reads PLUS a settled account (margin 0,
    ///     equity ≈ balance) — the empty pair alone is the 2026-09-29
    ///     congestion lie. Missing account data or a null Margin fails
    ///     CLOSED. Proof string rides along for the journal row.</summary>
    internal static async Task<(HashSet<long>? Live, string Proof)> ProvenVenueTicketsAsync(
        Func<Task<IReadOnlyList<Mt5Position>>> getPositions,
        Func<Task<Mt5Account?>> getAccount)
    {
        var first = await getPositions().ConfigureAwait(false);
        if (first.Count > 0)
        {
            return (first.Select(p => p.Ticket).ToHashSet(), "healthy positions read");
        }

        var second = await getPositions().ConfigureAwait(false);
        if (second.Count > 0)
        {
            return (second.Select(p => p.Ticket).ToHashSet(), "healthy positions read");
        }

        var account = await getAccount().ConfigureAwait(false);
        if (account is not null
            && account.Margin is { } margin && margin <= 1e-6
            && Math.Abs(account.Equity - account.Balance) <= 0.01)
        {
            return (new HashSet<long>(),
                "two agreeing empty reads + settled account (equity ≈ balance, margin 0)");
        }

        return (null, string.Empty);
    }

    /// <summary>One reconcile pass: the journal-book tickets that are
    /// neither venue-held nor tracked by any host, remembered in
    /// <paramref name="pending"/>. Pass 1 only records; pass 2 (next tick,
    /// with the ticket STILL gone from journal, venue, and hosts) returns
    /// them for a proof close. Two passes are what keep this from ever
    /// writing a second close for a ticket a host is about to retire with
    /// its own row — a double close would retire 0.01 lots the fill never
    /// opened. Returns the tickets to proof-close, empty when flatness is
    /// not proven (pending is left untouched in that case).</summary>
    internal static async Task<List<long>> ReconcilePassAsync(
        IReadOnlyDictionary<long, double> journalOpen,
        IReadOnlyCollection<long> tracked,
        Func<Task<IReadOnlyList<Mt5Position>>> getPositions,
        Func<Task<Mt5Account?>> getAccount,
        ISet<long> pending)
    {
        var goneNow = journalOpen.Keys
            .Where(t => !tracked.Contains(t))
            .ToHashSet();

        // A ticket no longer journal-open (host close row landed, or ops
        // reconciled it) leaves the pending set — nothing to write.
        pending.IntersectWith(goneNow);

        var (live, _) = await ProvenVenueTicketsAsync(getPositions, getAccount)
            .ConfigureAwait(false);
        if (live is null)
        {
            // Unproven exposure: write nothing, and do NOT count this as
            // a pass — only proven observations age a ticket toward its
            // proof close.
            return new List<long>();
        }

        goneNow.ExceptWith(live);
        var result = goneNow.Where(pending.Contains).ToList();
        // Written tickets must NOT stay pending (they would be written a
        // second time next pass); everything else gone waits its turn.
        pending.Clear();
        pending.UnionWith(goneNow);
        pending.ExceptWith(result);
        return result;
    }

    /// <summary>The journal-book reconcile: retire journal-book fills the
    /// venue provably no longer holds and no host still tracks — with a
    /// normal "closed #N" row, so every downstream parser (FxJournalBook,
    /// trade_lifecycle, watch_profit_floor, reconcile_journal_book.py) sees
    /// an ordinary close. This is the in-app replacement for waiting on
    /// the ops-layer reconcile + restart: a venue-side exit that lands
    /// while the app is down (restart race) used to strand the fill
    /// forever — the exposure guard then floored at a phantom book and
    /// refused every trade until a human ran the script.</summary>
    internal async Task ReconcileJournalBookAsync()
    {
        if (Interlocked.Exchange(ref _reconciling, 1) == 1)
        {
            return;   // a slow pass defers to the next tick, never overlaps
        }
        try
        {
            var open = _journalBook.OpenTickets();
            if (open.Count == 0)
            {
                _reconcilePending.Clear();
                return;
            }

            var tracked = _hosts.SelectMany(h => h.TrackedTickets).ToHashSet();
            var gone = await ReconcilePassAsync(
                open, tracked,
                () => _mt5.GetPositionsAsync(),
                () => _mt5.GetAccountAsync(),
                _reconcilePending).ConfigureAwait(false);

            foreach (var ticket in gone)
            {
                _journal.Log(Guid.Empty, "FX_EXIT",
                    $"#{ticket}: closed #{ticket} — broker no longer holds the ticket; " +
                    "journal book reconcile (two-pass, portfolio)",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Ticket = ticket,
                        Partial = false,
                        Lots = (double?)null,
                        Retcode = 10009,
                        Reconciled = true,
                        Proof = "portfolio journal-book reconcile",
                        // No entry/side in scope at the portfolio layer —
                        // the outcome is NOT invented: fx_win_rate resolves
                        // it tier-2 from the last hold-row R for the ticket.
                        RealizedR = (double?)null,
                        OutcomeSource = "unknown",
                    }));
            }
        }
        catch
        {
            // The reconcile must never crash its timer — an unreadable
            // book or bridge hiccup just defers to the next pass.
        }
        finally
        {
            Interlocked.Exchange(ref _reconciling, 0);
        }
    }

    /// <summary>Self-audit: when the book (venue-derived hosts, or the
    /// journal's own — whichever reads higher) sits ABOVE the portfolio
    /// cap, that is the exact signature of the three 2026-09-29 leaks.
    /// WARN in the journal; never throws, never trades.</summary>
    internal void AuditExposure()
    {
        try
        {
            var cap = (double)_portfolioMaxLots();
            if (cap <= 0)
            {
                return;   // cap 0 disables trading entirely — no book check
            }

            var hostLots = _hosts.Sum(h => h.LocalBookLots);
            var journalLots = _journalBookLots();
            var worst = Math.Max(hostLots, journalLots);
            // 2026-10-06: epsilon like FxExposureGuard's. A full-cap book
            // carries representation noise — venue float32 serializes 0.1 as
            // 0.1000000015, and the journal book's fills-minus-closes sum
            // leaves ~1e-16 of residue (51 adds / 50 subtracts of 0.1) — and
            // an exact-cap book then warned on every audit tick. Real leaks
            // are >= one volume step (0.01 lots), so 1e-7 silences
            // representation noise without hiding a genuine breach.
            if (worst > cap + 1e-7)
            {
                _journal.Log(Guid.Empty, "FX_RISK",
                    $"exposure audit: book {worst:0.00} lots exceeds the {cap:0.00} cap " +
                    $"(host {hostLots:0.00} / journal {journalLots:0.00}) — new orders must refuse",
                    "{}");
            }
        }
        catch
        {
            // The audit must never crash its timer.
        }
    }

    public void GoLive()
    {
        if (!PaperSoakComplete)
        {
            var laggards = SoakLaggards;
            var names = string.Join(", ", laggards.Select(h => $"{h.Symbol} {h.PaperSignalsSeen}/{h.PaperSoakSignalsRequired}"));
            StatusChanged?.Invoke(
                $"go-live refused — portfolio paper soak {PaperSignalsSeen}/{PaperSoakSignalsRequired}; waiting on: {names}");
            return;
        }

        foreach (var h in _hosts)
        {
            h.GoLive();
        }
    }

    public void GoPaper()
    {
        foreach (var h in _hosts)
        {
            h.GoPaper();
        }
    }

    public void ReArmLossStop()
    {
        foreach (var h in _hosts)
        {
            h.ReArmLossStop();
        }
    }

    public void Dispose()
    {
        _exposureAudit?.Dispose();
        foreach (var h in _hosts)
        {
            h.Dispose();
        }
    }
}
