using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;

namespace DongGfx.App.Infrastructure;

/// <summary>One nightly lab run for one symbol: the walk-forward verdict
/// over the journal's bar replay, plus the fold-by-fold evidence.</summary>
public sealed record FxLabResult(
    string Symbol,
    DateTimeOffset RanAt,
    bool Approved,
    int FoldCount,
    double PositiveFraction,
    double WorstFoldPnl,
    string Verdict,
    string FoldSummary);

/// <summary>
/// AI program agent 6 support: the genetic lab. Nightly, journal-only walk-
/// forward over the SAME M1 bars the brain saw (replayed from FX_DECISION
/// entries — no market-data feed, no bridge calls), optimizing the existing
/// alpha families' parameters with <see cref="DongGfx.Core.Fx.FxGenetic"/>
/// and gating the result through <see cref="DongGfx.Core.Fx.FxWalkForward"/>.
///
/// Rails (non-negotiable, mirror scripts/ai_alpha):
///   - it NEVER places, sizes, or schedules a trade — it holds no bridge,
///     supervisor, or order-path reference at all;
///   - it NEVER promotes anything: approval is journaled as FX_LAB evidence
///     for a human to read, and a human port is still a reviewed PR;
///   - a throwing lab tick never breaks the timer (MetricsDigestService
///     guardrail), and the lab is silent when the journal has too few
///     FX_DECISION entries to walk forward on.
/// </summary>
public sealed class FxLabService : IDisposable
{
    /// <summary>Time between lab runs. Default: nightly (24h).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Delay before the first run (lets the session warm up).</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>True disables the service entirely (no timer, no runs).</summary>
    public bool Disabled { get; set; }

    /// <summary>Minimum FX_DECISION entries before the lab attempts a
    /// walk-forward (below this the folds would be degenerate).</summary>
    public int MinDecisions { get; set; } = 120;

    /// <summary>Walk-forward folds per run (small: journals hold days, not
    /// years, of M1 decisions).</summary>
    public int Folds { get; set; } = 3;

    /// <summary>Genetic budget per fold. Small by design: this runs on the
    /// trading machine's spare cycles, not a research cluster.</summary>
    public int Population { get; set; } = 16;
    public int Generations { get; set; } = 10;

    /// <summary>Walk-forward approval thresholds (FxWalkForward defaults:
    /// ≥60% OOS folds positive, no fold below the loss cap).</summary>
    public double MinPositiveFraction { get; set; } = 0.6;

    /// <summary>Symbols to lab (default: the brain's symbol list).</summary>
    public Func<string>? SymbolsProvider { get; set; }

    private readonly TradeJournal _journal;
    private readonly Action<string>? _log;
    private readonly Func<int>? _seedProvider;
    private System.Threading.Timer? _timer;
    private int _busy;

    public FxLabService(TradeJournal journal, Action<string>? log = null,
        Func<int>? seedProvider = null)
    {
        _journal = journal;
        _log = log;
        _seedProvider = seedProvider;
    }

    /// <summary>Optional live gate: when set, it is re-read on every timer
    /// tick and run, so the settings checkbox takes effect without an app
    /// restart (the narrator's live-toggle pattern).</summary>
    public Func<bool>? EnabledToggle { get; set; }

    /// <summary>Starts the nightly lab loop (first run after InitialDelay).
    /// The loop itself stays live even when the toggle is off — the tick
    /// checks the gate and exits cheaply — so re-enabling never needs a
    /// restart.</summary>
    public void Start()
    {
        _timer = new System.Threading.Timer(
            _ =>
            {
                if (EnabledToggle?.Invoke() is false)
                {
                    return;
                }
                _ = RunOnceAsync();
            }, null, InitialDelay, Interval);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One lab pass over every configured symbol (nightly-tick
    /// entry). Returns the per-symbol results (empty when the journal is
    /// too thin — the lab is silent by design, never an error surface).
    /// Honors the FxLabEnabled toggle via the timer callback, not here.</summary>
    public Task<IReadOnlyList<FxLabResult>> RunOnceAsync() => RunPassAsync(ignoreToggle: false);

    /// <summary>Operator-invoked run (the LAB RUN button): the same pass,
    /// but the toggle and Disabled gate are bypassed — the click IS the
    /// intent. The busy lock still applies, so a nightly run and a manual
    /// run can never overlap.</summary>
    public Task<IReadOnlyList<FxLabResult>> RunNowAsync() => RunPassAsync(ignoreToggle: true);

    private async Task<IReadOnlyList<FxLabResult>> RunPassAsync(bool ignoreToggle)
    {
        if ((!ignoreToggle && Disabled) || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return Array.Empty<FxLabResult>();
        }

        try
        {
            var symbols = (SymbolsProvider?.Invoke() ?? "XAUUSDmicro")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();

            var results = new List<FxLabResult>();
            foreach (var symbol in symbols)
            {
                var result = await Task.Run(() => RunSymbol(symbol)).ConfigureAwait(false);
                if (result is not null)
                {
                    results.Add(result);
                }
            }

            return results;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>The replay unit: one journal FX_DECISION entry's embedded
    /// market snapshot, decoded to an FxBar. Returns null for entries whose
    /// details carry no parsable Quote/Bid/Ask (the schema has evolved;
    /// old entries are simply not lab-able).</summary>
    internal static DongGfx.Core.Fx.FxBar? DecisionToBar(string details)
    {
        try
        {
            using var doc = JsonDocument.Parse(details);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double? bid = null, ask = null;
            long? time = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("Bid") && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    bid = prop.Value.GetDouble();
                }
                else if (prop.NameEquals("Ask") && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    ask = prop.Value.GetDouble();
                }
                else if (prop.NameEquals("Time") && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    time = prop.Value.GetInt64();
                }
            }

            if (bid is not { } b || ask is not { } a || b <= 0 || a <= 0)
            {
                return null;
            }

            var mid = (b + a) / 2.0;
            // Journal snapshots are point-in-time: the "bar" is one tick
            // wide (high = low = mid). Volume is unknowable from the journal
            // and stays 0 — the lab's fitness functions must not lean on it.
            return new DongGfx.Core.Fx.FxBar(time ?? 0, mid, mid, mid, mid, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Collect the symbol's decision bars, oldest first.</summary>
    internal List<DongGfx.Core.Fx.FxBar> CollectBars(string symbol)
    {
        return _journal.GetRecent(null, 10000)
            .Where(e => e.Category == "FX_DECISION")
            .Where(e => e.Details.Contains($"\"{symbol}\"", StringComparison.OrdinalIgnoreCase))
            .Select(e => DecisionToBar(e.Details))
            .Where(b => b is not null)
            .Cast<DongGfx.Core.Fx.FxBar>()
            .OrderBy(b => b.Time)
            .ToList();
    }

    private FxLabResult? RunSymbol(string symbol)
    {
        var bars = CollectBars(symbol);
        if (bars.Count < MinDecisions)
        {
            return null;   // silent: not enough replay material yet
        }

        var seed = _seedProvider?.Invoke() ?? 20260927;
        var rng = new Random(seed);

        // Walk-forward: optimize a 2-gene momentum/mean-reversion blend
        // in-sample on the journal replay, evaluate out-of-sample once per
        // fold. FxWalkForward requires the fitness/evaluate contracts.
        var folds = FxWalkForward.Run(
            bars, Folds, isFraction: 0.6, rng,
            fitnessOf: (window, genome) => ReplayPnl(window, genome),
            evaluateOn: (window, genome) => ReplayPnl(window, genome),
            geneCount: 2, population: Population, generations: Generations);

        var approved = FxWalkForward.Approved(
            folds, MinPositiveFraction, lossCap: 0);

        var positive = folds.Count(f => f.OutOfSamplePnl > 0);
        var worst = folds.Count > 0 ? folds.Min(f => f.OutOfSamplePnl) : 0;
        var fraction = folds.Count > 0 ? (double)positive / folds.Count : 0;
        var verdict = folds.Count == 0
            ? "no folds (journal too short after warm-up)"
            : approved
                ? $"APPROVED-FOR-REVIEW — {positive}/{folds.Count} OOS folds positive, worst {worst:+0.0;-0.0}R (human port is a reviewed PR; nothing auto-promotes)"
                : $"not approved — {positive}/{folds.Count} OOS folds positive (need ≥ {MinPositiveFraction:P0}), worst {worst:+0.0;-0.0}R";

        var foldLines = folds.Select(f =>
            $"fold {f.Fold}: IS {f.InSamplePnl:+0.000;-0.000}R → OOS {f.OutOfSamplePnl:+0.000;-0.000}R");
        var result = new FxLabResult(
            symbol, DateTimeOffset.UtcNow, approved, folds.Count,
            fraction, worst, verdict, string.Join("; ", foldLines));

        _journal.Log(Guid.Empty, "FX_LAB", JsonSerializer.Serialize(new
        {
            Symbol = symbol,
            Bars = bars.Count,
            Folds = folds.Count,
            Positive = positive,
            Approved = approved,
            Verdict = verdict,
            FoldsDetail = foldLines.ToList(),
        }));

        _log?.Invoke($"[fx-lab] {symbol}: {verdict}");
        return result;
    }

    /// <summary>Deterministic replay PnL of the 2-gene blend over a bar
    /// window: gene 0 = EMA-cross lookback fraction (maps 0..1 → 5..60
    /// bars), gene 1 = confirmation threshold (0..1 → 0..0.5 × ATR proxy).
    /// The journal's bars are single-tick wide, so momentum is measured on
    /// mid-to-mid moves and every trade risks 1R (R = the window's median
    /// absolute mid move — the only honest risk unit a tick-wide series
    /// offers). No volume, no spread modeling: journal replays are honest
    /// about being coarse.</summary>
    internal static double ReplayPnl(IReadOnlyList<DongGfx.Core.Fx.FxBar> window, FxGenome genome)
    {
        if (window.Count < 40)
        {
            return 0;
        }

        var lookback = 5 + (int)Math.Round(genome.Genes[0] * 55);
        var threshold = genome.Genes[1] * 0.5;

        // Median absolute mid move = the window's 1R unit.
        var moves = new List<double>(window.Count - 1);
        for (var i = 1; i < window.Count; i++)
        {
            moves.Add(Math.Abs(window[i].Close - window[i - 1].Close));
        }
        moves.Sort();
        var unit = moves[moves.Count / 2];
        if (unit <= 0)
        {
            return 0;   // frozen tape: nothing to measure
        }

        // EMA over closes at the mapped lookback.
        var ema = window[0].Close;
        var k = 2.0 / (lookback + 1);
        var pnl = 0.0;
        var trades = 0;
        var position = 0;   // 0 flat, +1 long, -1 short
        var entry = 0.0;
        for (var i = 1; i < window.Count; i++)
        {
            var price = window[i].Close;
            var prevEma = ema;
            ema += k * (price - ema);
            var signal = price - ema;

            if (position == 0)
            {
                if (signal > threshold * unit)
                {
                    position = 1;
                    entry = price;
                }
                else if (signal < -threshold * unit)
                {
                    position = -1;
                    entry = price;
                }
            }
            else if (position == 1 && signal < 0)
            {
                pnl += price - entry;
                trades++;
                position = 0;
            }
            else if (position == -1 && signal > 0)
            {
                pnl += entry - price;
                trades++;
                position = 0;
            }
            _ = prevEma;
        }

        // Close any open position at the window's end.
        if (position == 1)
        {
            pnl += window[^1].Close - entry;
            trades++;
        }
        else if (position == -1)
        {
            pnl += entry - window[^1].Close;
            trades++;
        }

        // Normalize to R and penalize churn lightly (a parameter set that
        // only wins by trading every bar is noise, not edge).
        var r = trades > 0 ? pnl / (unit * Math.Sqrt(trades)) : 0;
        return r;
    }
}
