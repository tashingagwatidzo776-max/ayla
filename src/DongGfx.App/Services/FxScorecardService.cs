using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>
/// The nightly alpha scorecard: pulls a few hundred M1 bars per configured
/// symbol from the MT5 bridge, runs every production family through the
/// chronological IS/OOS split, journals an FX_SCORECARD entry per family,
/// and exposes a one-line summary for the metrics digest. Runs once per
/// local day (03:00) automatically, and on demand from the Terminal —
/// this is the walk-forward guardrail made visible: a family that stops
/// being OOS-profitable shows up in monitoring before it ever receives a
/// real order.
/// </summary>
public sealed class FxScorecardService : IDisposable
{
    private readonly Mt5BridgeClient _mt5;
    private readonly TradeJournal _journal;
    private readonly Func<AppSettings> _settings;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private bool _running;
    private DateTime _lastRunDate = DateTime.MinValue;

    public FxScorecardService(Mt5BridgeClient mt5, TradeJournal journal, Func<AppSettings> settings)
    {
        _mt5 = mt5;
        _journal = journal;
        _settings = settings;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null,
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
    }

    /// <summary>Latest run's summary line for the digest (null before the
    /// first successful run this process).</summary>
    public string? LastSummary { get; private set; }

    /// <summary>True while a run is in flight (manual triggers skip).</summary>
    public bool IsRunning
    {
        get { lock (_gate) { return _running; } }
    }

    private async Task TickAsync()
    {
        // 03:00–04:00 local, once per day
        var now = DateTime.Now;
        if (now.Hour != 3 || now.Date == _lastRunDate)
        {
            return;
        }

        await RunAsync().ConfigureAwait(false);
    }

    /// <summary>Run the scorecard now (manual trigger or the nightly tick).
    /// Returns null on success with a summary, or an error description.</summary>
    public async Task<string?> RunAsync()
    {
        lock (_gate)
        {
            if (_running)
            {
                return "scorecard already running";
            }

            _running = true;
        }

        try
        {
            var symbols = ParseSymbols(_settings().FxSymbols, _settings().FxSymbol);
            var approved = 0;
            var total = 0;

            foreach (var symbol in symbols)
            {
                var candles = await _mt5.GetCandlesAsync(symbol, "M1", 700).ConfigureAwait(false);
                if (candles.Count < 150)
                {
                    _journal.Log(Guid.Empty, "FX_SCORECARD",
                        $"{symbol}: skipped — only {candles.Count} bars from the bridge", "{}");
                    continue;
                }

                var bars = candles
                    .Select(c => new FxBar(c.Time, c.Open, c.High, c.Low, c.Close, 0))
                    .ToList();
                var entries = FxScorecard.Run(bars);

                foreach (var e in entries)
                {
                    total++;
                    if (e.Approved)
                    {
                        approved++;
                    }

                    _journal.Log(Guid.Empty, "FX_SCORECARD",
                        $"{symbol}: {e.Family} — IS {e.IsTrades} trades {e.IsPnl:+0.0000;-0.0000} | " +
                        $"OOS {e.OosTrades} trades {e.OosPnl:+0.0000;-0.0000} — " +
                        $"{(e.Approved ? "APPROVED" : "NOT APPROVED")} ({e.Note})",
                        System.Text.Json.JsonSerializer.Serialize(e));
                }
            }

            LastSummary = total == 0
                ? "scorecard: no data"
                : $"scorecard {approved}/{total} families approved ({DateTime.Now:MM-dd HH:mm})";
            _lastRunDate = DateTime.Now.Date;
            _journal.Log(Guid.Empty, "FX_SCORECARD", LastSummary, "{}");
            return LastSummary;
        }
        catch (Exception ex)
        {
            _journal.Log(Guid.Empty, "FX_SCORECARD", $"scorecard failed: {ex.Message}", "{}");
            return $"scorecard failed: {ex.Message}";
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    internal static IReadOnlyList<string> ParseSymbols(string csv, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(csv) ? fallback : csv;
        var symbols = source
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return symbols.Length > 0 ? symbols : new[] { fallback };
    }

    public void Dispose() => _timer.Dispose();
}
