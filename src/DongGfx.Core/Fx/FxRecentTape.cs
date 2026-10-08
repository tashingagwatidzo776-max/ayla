namespace DongGfx.Core.Fx;

/// <summary>
/// Recent-tape expectancy cells — ROSTER-POLICY items 2-4 implemented
/// (docs/soak/ROSTER-POLICY.md): a rolling window of the last
/// <see cref="WindowN"/> settled trades per (symbol, family), consulted at
/// entry so a family whose CURRENT tape is clearly losing gets cut from the
/// symbol's roster while lifetime cells may only tilt weights.
///
/// Laws (policy constants, not settings):
/// - <b>Default-keep</b>: n &lt; <see cref="WindowN"/> is never excluded,
///   no matter how negative the mean — thin data may tilt, never cut.
/// - <b>Asymmetric bar</b>: a failing evaluation is n ≥ <see cref="WindowN"/>
///   AND mean ≤ <see cref="ExcludeMeanAtOrBelowR"/> ("clearly negative").
///   A merely mediocre family stays (with its existing weight tilt).
/// - <b>Hysteresis</b> (§4): <see cref="FailStreakToExclude"/> consecutive
///   failing evaluations to exclude; ONE passing evaluation re-enters —
///   cuts flip-flop at the window's edges otherwise.
///
/// Pure + thread-safe (a lock guards the cell map): snapshots in, exclusion
/// state out; no I/O, no clock. The journal reseed and the live hosts both
/// Record into one instance.
/// </summary>
public sealed class FxRecentTape
{
    /// <summary>Rolling window per (symbol, family) — ROSTER-POLICY's
    /// "n ≥ ~30 before any exclusion".</summary>
    public const int WindowN = 30;

    /// <summary>The asymmetric bar: a window mean at or below this is a
    /// FAILING evaluation. Mediocre (above) keeps the family.</summary>
    public const double ExcludeMeanAtOrBelowR = -0.10;

    /// <summary>Consecutive failing evaluations required before exclusion
    /// (ROSTER-POLICY §4 hysteresis).</summary>
    public const int FailStreakToExclude = 2;

    private sealed class Cell
    {
        public readonly Queue<double> Window = new(WindowN);
        public int FailStreak;
        public bool Excluded;
    }

    private readonly Dictionary<(string Symbol, string Family), Cell> _cells = new();
    private readonly object _lock = new();

    /// <summary>Feed one settled trade. Re-evaluates the cell: failing
    /// windows raise the fail streak (2 → excluded), any passing window
    /// clears the streak AND re-enters an excluded family.</summary>
    public void Record(string symbol, string family, double r)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(family)
            || double.IsNaN(r) || double.IsInfinity(r))
        {
            return;
        }

        lock (_lock)
        {
            var key = (symbol, family);
            if (!_cells.TryGetValue(key, out var cell))
            {
                cell = new Cell();
                _cells[key] = cell;
            }

            cell.Window.Enqueue(r);
            while (cell.Window.Count > WindowN)
            {
                cell.Window.Dequeue();
            }

            var mean = Mean(cell.Window);
            var failing = cell.Window.Count >= WindowN && mean <= ExcludeMeanAtOrBelowR;
            if (failing)
            {
                cell.FailStreak++;
                if (cell.FailStreak >= FailStreakToExclude)
                {
                    cell.Excluded = true;
                }
            }
            else
            {
                // Default-keep and re-entry share one law: any evaluation
                // that isn't a qualifying failure restores the family.
                cell.FailStreak = 0;
                cell.Excluded = false;
            }
        }
    }

    /// <summary>True only after two consecutive qualifying failures.
    /// Unknown cells are kept (default-keep).</summary>
    public bool IsExcluded(string symbol, string family)
    {
        lock (_lock)
        {
            return _cells.TryGetValue((symbol, family), out var cell) && cell.Excluded;
        }
    }

    /// <summary>Trades currently in the window (0 for unknown cells).</summary>
    public int WindowCount(string symbol, string family)
    {
        lock (_lock)
        {
            return _cells.TryGetValue((symbol, family), out var cell) ? cell.Window.Count : 0;
        }
    }

    /// <summary>Mean R of the window, or null for an empty cell —
    /// inspection/report surface (fx_win_rate's live twin).</summary>
    public double? WindowMeanR(string symbol, string family)
    {
        lock (_lock)
        {
            return _cells.TryGetValue((symbol, family), out var cell) && cell.Window.Count > 0
                ? Mean(cell.Window)
                : null;
        }
    }

    /// <summary>Consecutive failing evaluations at the window's tail
    /// (0 when passing; ≥ <see cref="FailStreakToExclude"/> means excluded).</summary>
    public int FailStreak(string symbol, string family)
    {
        lock (_lock)
        {
            return _cells.TryGetValue((symbol, family), out var cell) ? cell.FailStreak : 0;
        }
    }

    /// <summary>Snapshot of every cell — (symbol, family, n, mean, excluded),
    /// newest-first, for refusal-row payloads and reports.</summary>
    public IReadOnlyList<(string Symbol, string Family, int N, double? MeanR, bool Excluded)> Cells()
    {
        lock (_lock)
        {
            return _cells
                .Select(kv => (kv.Key.Symbol, kv.Key.Family, kv.Value.Window.Count,
                    kv.Value.Window.Count > 0 ? Mean(kv.Value.Window) : (double?)null,
                    kv.Value.Excluded))
                .OrderByDescending(c => c.Item5)
                .ThenBy(c => c.Symbol)
                .ThenBy(c => c.Family)
                .ToList();
        }
    }

    private static double Mean(Queue<double> window)
    {
        var sum = 0.0;
        foreach (var r in window)
        {
            sum += r;
        }
        return sum / window.Count;
    }
}
