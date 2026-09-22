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
        double riskFraction = 0.02)
    {
        Symbols = symbols;
        Supervisor = new FxSupervisor(journal, killSwitchEngaged, governorTripped,
            dailyLossCap, equityFloor, webhook);

        var exposure = new FxExposureGuard(mt5, portfolioMaxLots, symbols);
        var news = new FxNewsVeto(newsCalendarPath, newsWindow);

        foreach (var symbol in symbols)
        {
            var host = new FxEngineHost(
                mt5, journal, symbol, killSwitchEngaged, lotsCap, realMoneyUnlocked,
                riskFraction, Supervisor, webhook: webhook,
                preOrderVeto: lots => exposure.VetoAsync(lots),
                newsVeto: () => news.Evaluate(DateTimeOffset.UtcNow));
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
            StatusChanged?.Invoke(
                $"go-live refused — portfolio paper soak {PaperSignalsSeen}/{PaperSoakSignalsRequired} signals");
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
