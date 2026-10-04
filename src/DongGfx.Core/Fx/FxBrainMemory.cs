using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DongGfx.Core.Fx;

/// <summary>One (symbol, family, regime) cell of the brain's memory: what
/// the family has actually done there across every training run so far.</summary>
public sealed record FxFamilyMemory(
    string Symbol,
    string Alpha,
    string Regime,
    int Trades,
    int Wins,
    double TotalR,
    double TotalPnlUsd,
    string LastTrainedAt)
{
    public double WinRate => Trades > 0 ? (double)Wins / Trades : 0;

    /// <summary>Mean R per trade — the honest per-trade edge, so a family
    /// that only "won" by trading constantly does not top the playbook.</summary>
    public double ExpectancyR => Trades > 0 ? TotalR / Trades : 0;
}

/// <summary>
/// Persistent brain memory — the type-7 "self-learning" surface, kept inside
/// the strict guard rules of docs/ai-agent-program.md: it remembers MEASURED
/// outcomes (the training simulator's per-family record), it never invents
/// them, and it can only ever NUDGE a live signal's confidence — never place,
/// size, or schedule an order. The deterministic rails, the paper soak and
/// the real-money gate all still own every trade path.
///
/// State lives at %APPDATA%\tf\data\fx-brain-memory.json (beside the other FX
/// state, never next to the trading journal). Read-modify-write is atomic
/// (temp + move) and best-effort: a lost write costs one training run's
/// learning, never a cycle, and a corrupt file resets to an empty memory
/// rather than breaking startup.
///
/// Scope note: memory is keyed on symbol + family + regime, NOT on the build
/// stamp. Unlike the paper-soak bar (which is evidence for a specific engine
/// and so drops on a build change), memory is a research record that
/// deliberately accumulates across builds — that is what "remembers
/// everything" means. The training report is what is stamped and auditable.
/// </summary>
public sealed class FxBrainMemory
{
    /// <summary>Memory file name inside the data dir.</summary>
    public const string FileName = "fx-brain-memory.json";

    /// <summary>Confidence nudges are clamped to this band, so a long memory
    /// can tilt the vote but can never silence the roster or fake conviction.</summary>
    public const double MinWeight = 0.5;
    public const double MaxWeight = 1.5;

    /// <summary>How many trades a cell needs before it is trusted enough to
    /// move the live vote at all — below this the family is still "learning".</summary>
    public const int TrustTrades = 30;

    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Cell> _cells = new(StringComparer.Ordinal);
    private int _runs;

    /// <summary>When the memory was first and last written (ISO-8601).</summary>
    public string FirstTrainedAt { get; private set; } = "";
    public string LastTrainedAt { get; private set; } = "";

    /// <summary>How many training runs have fed this memory.</summary>
    public int Runs { get { lock (_gate) { return _runs; } } }

    private sealed class Cell
    {
        public string Symbol = "";
        public string Alpha = "";
        public string Regime = "";
        public int Trades;
        public int Wins;
        public double TotalR;
        public double TotalPnlUsd;
    }

    private FxBrainMemory(string path) => _path = path;

    /// <summary>Load the memory from disk (empty on a missing/corrupt file —
    /// never an exception).</summary>
    public static FxBrainMemory Load(string dataDir) => LoadFile(PathFor(dataDir));

    public static FxBrainMemory LoadFile(string path)
    {
        var memory = new FxBrainMemory(path);
        try
        {
            if (!File.Exists(path))
            {
                return memory;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return memory;
            }

            if (root.TryGetProperty("runs", out var runsEl)
                && runsEl.ValueKind == JsonValueKind.Number
                && runsEl.TryGetInt32(out var runs))
            {
                memory._runs = runs;
            }

            memory.FirstTrainedAt = Str(root, "first_trained_at");
            memory.LastTrainedAt = Str(root, "last_trained_at");

            if (root.TryGetProperty("cells", out var cellsEl)
                && cellsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cellsEl.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var symbol = Str(c, "symbol");
                    var alpha = Str(c, "alpha");
                    var regime = Str(c, "regime");
                    if (symbol.Length == 0 || alpha.Length == 0)
                    {
                        continue;
                    }

                    memory._cells[Key(symbol, alpha, regime)] = new Cell
                    {
                        Symbol = symbol,
                        Alpha = alpha,
                        Regime = regime,
                        Trades = Int(c, "trades"),
                        Wins = Int(c, "wins"),
                        TotalR = Dbl(c, "total_r"),
                        TotalPnlUsd = Dbl(c, "total_pnl_usd"),
                    };
                }
            }
        }
        catch
        {
            // Missing / unreadable / malformed: start empty, never throw.
            memory._cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
            memory._runs = 0;
        }

        return memory;
    }

    /// <summary>Merge one training report's per-family record into memory and
    /// persist. Accumulating (not replacing) is the point: the brain remembers
    /// every run, so a family's record sharpens as tape accrues.</summary>
    public void Learn(FxTrainingReport report, DateTimeOffset? at = null)
    {
        var stamp = (at ?? DateTimeOffset.UtcNow).ToString("o");
        lock (_gate)
        {
            foreach (var stat in report.FamilyStats)
            {
                // Regime is not carried on the rollup, so attribute each
                // family's record to the symbol+family cell (regime "*"):
                // regime-level memory can be split out later without a
                // schema change, and the playbook reads the family cell.
                var key = Key(report.Symbol, stat.Alpha, "*");
                if (!_cells.TryGetValue(key, out var cell))
                {
                    cell = new Cell { Symbol = report.Symbol, Alpha = stat.Alpha, Regime = "*" };
                    _cells[key] = cell;
                }

                cell.Trades += stat.Trades;
                cell.Wins += stat.Wins;
                cell.TotalR += stat.TotalR;
                cell.TotalPnlUsd += stat.TotalPnlUsd;
            }

            _runs++;
            if (FirstTrainedAt.Length == 0)
            {
                FirstTrainedAt = stamp;
            }

            LastTrainedAt = stamp;
            SaveLocked();
        }
    }

    /// <summary>Every remembered cell for a symbol, best expectancy first —
    /// the "playbook" a human reads to see which families actually work where.</summary>
    public IReadOnlyList<FxFamilyMemory> Playbook(string symbol)
    {
        lock (_gate)
        {
            return _cells.Values
                .Where(c => string.Equals(c.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .Select(ToRecord)
                .OrderByDescending(m => m.ExpectancyR)
                .ThenByDescending(m => m.Trades)
                .ToList();
        }
    }

    /// <summary>All remembered cells (every symbol), best expectancy first.</summary>
    public IReadOnlyList<FxFamilyMemory> All()
    {
        lock (_gate)
        {
            return _cells.Values
                .Select(ToRecord)
                .OrderByDescending(m => m.ExpectancyR)
                .ThenByDescending(m => m.Trades)
                .ToList();
        }
    }

    /// <summary>
    /// The live confidence weight for one family on one symbol (1.0 when the
    /// cell is unknown or untrusted). Positive measured expectancy nudges the
    /// weight up, negative down, scaled by sample size and clamped to the
    /// guard band. This is the ONLY way memory can influence the brain: a
    /// multiplier on an already-computed confidence, applied at signal
    /// selection — never a decision, size, or order.
    /// </summary>
    public double ConfidenceWeight(string symbol, string alpha)
    {
        lock (_gate)
        {
            if (!_cells.TryGetValue(Key(symbol, alpha, "*"), out var cell) || cell.Trades < TrustTrades)
            {
                return 1.0;
            }

            // Squash mean R into a bounded nudge; tanh keeps a hot streak
            // from pinning the weight, and the clamp is the hard guard.
            var expectancy = cell.TotalR / cell.Trades;
            var weight = 1.0 + Math.Tanh(expectancy) * 0.5;
            return Math.Clamp(weight, MinWeight, MaxWeight);
        }
    }

    /// <summary>One line describing the memory for a report header.</summary>
    public string SummaryLine()
    {
        lock (_gate)
        {
            var cells = _cells.Count;
            var trades = _cells.Values.Sum(c => c.Trades);
            return cells == 0
                ? "brain memory: empty (no training runs yet)"
                : $"brain memory: {cells} family cell(s) across {_runs} run(s), " +
                  $"{trades} measured trades, last trained {LastTrainedAt}";
        }
    }

    /// <summary>Clear every cell (a deliberate restart of the learning).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
            _runs = 0;
            FirstTrainedAt = "";
            LastTrainedAt = "";
            SaveLocked();
        }
    }

    private static FxFamilyMemory ToRecord(Cell c) =>
        new(c.Symbol, c.Alpha, c.Regime, c.Trades, c.Wins,
            Math.Round(c.TotalR, 4), Math.Round(c.TotalPnlUsd, 4), "");

    private static string Key(string symbol, string alpha, string regime) =>
        $"{symbol}\u0001{alpha}\u0001{regime}";

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var n) ? n : 0;

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = JsonSerializer.Serialize(new
            {
                version = 1,
                runs = _runs,
                first_trained_at = FirstTrainedAt,
                last_trained_at = LastTrainedAt,
                cells = _cells.Values
                    .OrderBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.Alpha, StringComparer.Ordinal)
                    .Select(c => new
                    {
                        symbol = c.Symbol,
                        alpha = c.Alpha,
                        regime = c.Regime,
                        trades = c.Trades,
                        wins = c.Wins,
                        total_r = Math.Round(c.TotalR, 4),
                        total_pnl_usd = Math.Round(c.TotalPnlUsd, 4),
                    })
                    .ToList(),
            }, new JsonSerializerOptions { WriteIndented = true });

            // Write-then-move: the app may read this while another host writes.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, payload);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort: a lost write costs one run's learning, never a cycle.
        }
    }
}
