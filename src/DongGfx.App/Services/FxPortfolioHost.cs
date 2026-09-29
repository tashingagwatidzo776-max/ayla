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

    public FxExposureGuard(Mt5BridgeClient mt5, Func<decimal> maxTotalLots, IEnumerable<string> symbols)
    {
        _mt5 = mt5;
        _maxTotalLots = maxTotalLots;
        _symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
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
            // down — indistinguishable from a flat account. Probe /account
            // first: null there means unreachable, so we fail closed instead
            // of happily allowing orders against unknown exposure.
            var account = await _mt5.GetAccountAsync().ConfigureAwait(false);
            if (account is null)
            {
                return "bridge unreachable (exposure unknown)";
            }

            var positions = await _mt5.GetPositionsAsync().ConfigureAwait(false);
            open = positions.Where(p => _symbols.Contains(p.Symbol)).Sum(p => p.Volume);
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

    public FxSmallAccountGuard(Mt5BridgeClient mt5, IEnumerable<string> symbols,
        Func<DateTimeOffset>? clock = null)
    {
        _mt5 = mt5;
        _symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        _clock = clock;
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

            var positions = await _mt5.GetPositionsAsync().ConfigureAwait(false);
            var ours = positions.FirstOrDefault(p =>
                Core.Fx.FxExitBrain.Owns(p.Comment));
            if (ours is not null)
            {
                return $"small-account mode — one brain trade at a time " +
                       $"(slot used by #{ours.Ticket} {ours.Symbol})";
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
        string? shadowLedgerDir = null)
    {
        Symbols = symbols;
        Supervisor = new FxSupervisor(journal, killSwitchEngaged, governorTripped,
            dailyLossCap, equityFloor, webhook);

        var exposure = new FxExposureGuard(mt5, portfolioMaxLots, symbols);
        var news = new FxNewsVeto(newsCalendarPath, newsWindow);
        var small = new FxSmallAccountGuard(mt5, symbols);

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
                preOrderVeto: lots => small.VetoAsync(symbol, lots) ?? exposure.VetoAsync(lots),
                newsVeto: () => news.Evaluate(DateTimeOffset.UtcNow),
                smallAccountClamp: (sym, lots) => small.ClampedLots(sym, lots),
                cycleOffset: TimeSpan.FromSeconds(15 * staggerIndex),
                shadowLedgerPath: shadowLedgerDir is null
                    ? null
                    : Path.Combine(shadowLedgerDir, $"fx-shadow-{symbol}.jsonl"));
            staggerIndex++;
            host.StatusChanged += s => StatusChanged?.Invoke($"[{symbol}] {s}");
            _hosts.Add(host);
        }
    }

    public bool IsRunning => _hosts.Any(h => h.IsRunning);

    public bool IsLiveEngine => _hosts.Any(h => h.IsLiveEngine);

    public int PaperSignalsSeen => _hosts.Sum(h => h.PaperSignalsSeen);

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
        foreach (var h in _hosts)
        {
            h.Dispose();
        }
    }
}
