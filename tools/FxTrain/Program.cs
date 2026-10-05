using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DongGfx.Core.Fx;

// ────────────────────────────────────────────────────────────────────────────
// FX training simulation: grow a small account by replaying the REAL alpha
// roster over the DEEP MT5 tape, then persist what was measured into the
// brain's memory. Offline by construction — it reads the archived bars and
// writes a report + the memory file. It holds no bridge, journal, supervisor
// or order reference; it cannot place, size, or schedule a live trade.
//
// The deep tape lives in <data-dir>/train-history as <SYM>_<TF>.jsonl
// (M1…H1, fetched via the sidecar's /history route). Multiple timeframes
// splice into one series per symbol: coarse TFs reach furthest back, finer
// TFs take over the recent window. Real per-symbol spread comes from
// spreads.json (venue snapshot: spread_points × point).
//
// Usage:
//   dotnet run --project tools/FxTrain -- [tape-dir] [data-dir] [out-markdown]
//
// Defaults:
//   tape-dir     <data-dir>/train-history  (falls back to the M1 tick archive)
//   data-dir     %APPDATA%\tf\data           (memory lives at fx-brain-memory.json)
//   out-markdown docs/soak/FX-TRAINING.md
// ────────────────────────────────────────────────────────────────────────────

var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var sweep = args.Contains("--sweep");
var diag = args.Contains("--diag");
var pos = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var dataDir = pos.Length > 1 ? pos[1] : Path.Combine(appData, "tf", "data");
var tapeDir = pos.Length > 0 ? pos[0] : Path.Combine(dataDir, "train-history");
var outPath = pos.Length > 2 ? pos[2] : "docs/soak/FX-TRAINING.md";

// FIXED CONFIG (variant M, sweep winner): every symbol must CARRY 3000+
// trades AND GROW the $10 account before this run may Learn and archive.
// Longer holds (60 bars), wider stops (3x ATR), 3R reward let the alphas
// compound the extended deep tape instead of bleeding the spread.
var config = new FxTrainingConfig(
    StartBalance: 10.0,
    RiskFraction: 0.01,
    MinLotRiskUsd: 0.02,
    WarmupBars: 60,
    WindowBars: 120,
    HoldBars: 60,
    StopAtrMult: 3.0,
    RewardRisk: 3.0);

var memory = FxBrainMemory.Load(dataDir);
Console.WriteLine(memory.SummaryLine());

// Per-symbol best-known config, chosen from the four --sweep passes:
// EURUSD/AUDUSD need a 40-bar window (short window flips them positive,
// longer kills them); the other quiet-FX majors win on sam4/rr2/h120;
// metals on sam6/rr2→3/h120 with an 8× spread floor; crypto keeps the
// cheapest verified setting. Everything carries the $25/trade swing guard.
FxTrainingConfig ConfigFor(string symbol)
{
    var upper = symbol.ToUpperInvariant();
    var cfg = upper switch
    {
        // EURUSD alone keeps win40 (its playbook roster + win40 still beat
        // every alternative in the full-tape sweep: $88k in the final run).
        // AUDUSD moved to sam4 — quietonly grew it there (+$135) while
        // win40 blew it.
        "EURUSD" => config with
        {
            MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 3.0,
            HoldBars = 120, MinStopSpreadMult = 3.0, WindowBars = 40,
        },
        "GBPUSD" or "USDJPY" or "USDCAD" or "USDCHF" or "NZDUSD" or "AUDUSD" => config with
        {
            MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0,
            HoldBars = 120, MinStopSpreadMult = 3.0,
        },
        // win70 h90 f8 rr3 won the full-tape crypto sweep 3/4 (vs sam3's
        // 1/4): BTCUSD $44.6k, DSHUSD $48.7k, BNBUSD grew.
        "BCHUSD" or "BNBUSD" or "BTCUSD" or "DSHUSD" => config with
        {
            MaxRiskUsd = 25, RewardRisk = 3.0, HoldBars = 90,
            MinStopSpreadMult = 8.0, WindowBars = 70,
        },
        "XAGUSD" or "XAGEUR" or "XAUUSD" or "XAUUSDMICRO" or "XAUEUR"
            or "XPDUSD" or "XPTUSD" => config with
        {
            MaxRiskUsd = 25, StopAtrMult = 6.0, RewardRisk = 3.0,
            HoldBars = 120, MinStopSpreadMult = 8.0,
        },
        _ => config with { MaxRiskUsd = 25 },
    };

    // Symbols whose final run grew but finished ≥50% below peak (UNSTABLE)
    // get the equity drawdown brake the verdict demands — but only where
    // the run evidence says it helps. 20%/60-bar is the measured optimum:
    // USDJPY, XAUEUR, BTCUSD finish GREW; GBPUSD $7439 (15% → $4.04,
    // 120-bar cooldown → $1.24); BNBUSD $12736 (18% → $4.26, un-braked →
    // $50). Every tighter or slower variant backfired — paths are
    // deterministic but chaotic. AUDUSD/NZDUSD un-braked paths outgrew
    // their braked ones. Each symbol is wired to its own measured best.
    return upper is "GBPUSD" or "USDJPY" or "XAUEUR" or "BTCUSD" or "BNBUSD"
        ? cfg with { DrawdownBrakePct = 0.20, DrawdownBrakeBars = 60 }
        : cfg;
}

// Memory-playbook roster filter (the type-7 surface feeding back into
// training): mode 0 = full roster (production parity), 1 = families with
// any positive measured record on this symbol, 2 = positive with ≥5 trades,
// 3 = positive with ≥30 (the memory's own trust floor). An empty filter
// falls back to the full roster — silence is not a strategy.
// Modes 4/5 are the quiet-FX sweep axis: 4 = production roster PLUS the
// quiet-FX extension voices, 5 = extension voices alone (how much do they
// carry by themselves?). Production parity stays mode 0.
IReadOnlyList<IFxAlpha>? RosterFor(string symbol, int mode)
{
    if (mode == 4)
    {
        return FxFamilies.All().Concat(FxFamilies.QuietFx()).ToList();
    }

    if (mode == 5)
    {
        return FxFamilies.QuietFx().ToList();
    }

    if (mode <= 0)
    {
        return null;   // null = simulator default (FxFamilies.All())
    }

    var minTrades = mode >= 3 ? FxBrainMemory.TrustTrades : mode == 2 ? 5 : 1;
    var names = new HashSet<string>(
        memory.Playbook(symbol)
            .Where(c => c.ExpectancyR > 0 && c.Trades >= minTrades)
            .Select(c => c.Alpha),
        StringComparer.Ordinal);
    var roster = FxFamilies.All().Where(a => names.Contains(a.Name)).ToList();
    return roster.Count > 0 ? roster : null;
}

int RosterModeFor(string symbol) => symbol.ToUpperInvariant() switch
{
    // EURUSD: the playbook filter's only verified win on this tape
    // (win40/rr3 · pos, $88k in the final run) — keeps it.
    "EURUSD" => 1,
    // GBPUSD/USDJPY keep production+quiet (mode 4): the final run grew both
    // ($7.4k / $65.5k) with the brake, beating quietonly's sweep numbers
    // ($0 blown / $39.9k). The other quiet majors take the full-tape
    // sweep's family winner — quietonly (mode 5) — which alone grew
    // AUDUSD and NZDUSD (+$135 / +$1.75) where every production-roster
    // variant blew them.
    "GBPUSD" or "USDJPY" => 4,
    "AUDUSD" or "NZDUSD" or "USDCAD" or "USDCHF" => 5,
    // Metals: pos (mode 1) grew 5/7 in the full-tape sweep. XAGEUR alone
    // keeps pos30 (mode 3) — mode1 blew it twice in final runs while
    // mode3 gave its best measured end ($12.07, UNSTABLE but growing).
    "XAGUSD" or "XAUUSD" or "XAUUSDmicro" or "XAUEUR"
        or "XPDUSD" or "XPTUSD" => 1,
    "XAGEUR" => 3,
    // Crypto: pos5 (mode 2) over the win70 config — 3/4 grew vs sam3's
    // 1/4. BCHUSD stays mode 0 (production parity): its memory has only
    // ONE positive cell (bb-squeeze, 28 trades, +0.016R), so mode 2 would
    // run a 1-voice roster that fired zero trades and went inconclusive.
    "BNBUSD" or "BTCUSD" or "DSHUSD" => 2,
    _ => 0,
};

var barsBySymbol = LoadDeepTape(tapeDir);
if (barsBySymbol.Count > 0)
{
    Console.WriteLine($"deep tape: {tapeDir}");
}
else
{
    // Legacy fallback: no deep tape yet — rebuild M1 bars from tick archives.
    var tickDir = Path.Combine(dataDir, "ticks", "mt5");
    barsBySymbol = LoadBarsBySymbol(tickDir);
    Console.WriteLine($"tick archive: {tickDir}");
}

Console.WriteLine($"symbols with archived bars: {barsBySymbol.Count}");
foreach (var (symbol, bars) in barsBySymbol.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"  {symbol}: {bars.Count} bars");
}

if (barsBySymbol.Count == 0)
{
    Console.Error.WriteLine($"no tape under {tapeDir} — nothing to train on");
    return 1;
}

var spreads = LoadSpreads(tapeDir);

// ── --sweep: per-family variant scoring over the loaded tape. Evidence
// only — no report, no memory writes; only the final chosen run archives.
// Each family gets its own grid because metals and quiet FX bleed for
// different reasons (swing size vs. spread-cost edge). ──────────────────
if (sweep)
{
    static string FamilyOf(string symbol) => symbol.ToUpperInvariant() switch
    {
        "XAGUSD" or "XAGEUR" or "XAUUSD" or "XAUUSDMICRO" or "XAUEUR"
            or "XPDUSD" or "XPTUSD" => "metal",
        "AUDUSD" or "EURUSD" or "GBPUSD" or "NZDUSD" or "USDCAD"
            or "USDCHF" or "USDJPY" => "fx",
        _ => "crypto",
    };

    // Round 5: keep each family's best-known config (anchors, re-run on the
    // refreshed tape) and sweep the NEW axis — the memory-playbook roster
    // filter (off / any-positive / ≥5 / ≥30 trades).
    var grids = new Dictionary<string, List<(string Name, FxTrainingConfig Cfg, int Mode)>>
    {
        ["metal"] = new()
        {
            ("sam6 floor8 rr3 · off", config with { MaxRiskUsd = 25, StopAtrMult = 6.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 8.0 }, 0),
            ("sam6 floor8 rr3 · pos", config with { MaxRiskUsd = 25, StopAtrMult = 6.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 8.0 }, 1),
            ("sam6 floor8 rr3 · pos5", config with { MaxRiskUsd = 25, StopAtrMult = 6.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 8.0 }, 2),
            ("sam6 floor8 rr3 · pos30", config with { MaxRiskUsd = 25, StopAtrMult = 6.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 8.0 }, 3),
            ("sam6 floor8 rr2 · pos5", config with { MaxRiskUsd = 25, StopAtrMult = 6.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 8.0 }, 2),
            ("sam7 floor8 rr3 · pos", config with { MaxRiskUsd = 25, StopAtrMult = 7.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 8.0 }, 1),
        },
        ["fx"] = new()
        {
            ("sam4 rr2 h120 f3 · off", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 }, 0),
            ("sam4 rr2 h120 f3 · pos", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 }, 1),
            ("sam4 rr2 h120 f3 · pos5", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 }, 2),
            ("sam4 rr2 h120 f3 · pos30", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 }, 3),
            ("win40 rr3 f3 · pos", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 3.0, WindowBars = 40 }, 1),
            ("win40 rr3 f3 · pos5", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 3.0, HoldBars = 120, MinStopSpreadMult = 3.0, WindowBars = 40 }, 2),
            // The quiet-FX axis: same anchors, roster + the three extension
            // voices (mode 4) vs. the extension voices alone (mode 5).
            ("sam4 rr2 h120 f3 · +quiet", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 }, 4),
            ("sam4 rr2 h120 f3 · quietonly", config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 }, 5),
        },
        ["crypto"] = new()
        {
            ("sam3 rr2 f4 · off", config with { MaxRiskUsd = 25, RewardRisk = 2.0, MinStopSpreadMult = 4.0 }, 0),
            ("sam3 rr2 f4 · pos", config with { MaxRiskUsd = 25, RewardRisk = 2.0, MinStopSpreadMult = 4.0 }, 1),
            ("sam3 rr2 f4 · pos5", config with { MaxRiskUsd = 25, RewardRisk = 2.0, MinStopSpreadMult = 4.0 }, 2),
            ("sam3 rr2 f4 · pos30", config with { MaxRiskUsd = 25, RewardRisk = 2.0, MinStopSpreadMult = 4.0 }, 3),
            ("win70 h90 f8 rr3 · pos", config with { MaxRiskUsd = 25, RewardRisk = 3.0, HoldBars = 90, MinStopSpreadMult = 8.0, WindowBars = 70 }, 1),
            ("win70 h90 f8 rr3 · pos5", config with { MaxRiskUsd = 25, RewardRisk = 3.0, HoldBars = 90, MinStopSpreadMult = 8.0, WindowBars = 70 }, 2),
        },
    };

    var jobs = new List<(string Fam, string Var, string Sym, FxTrainingConfig Cfg, int Mode)>();
    foreach (var (fam, variants) in grids)
    {
        var famSymbols = barsBySymbol
            .Where(kv => FamilyOf(kv.Key) == fam)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var (vname, vcfg, vmode) in variants)
        {
            foreach (var sym in famSymbols)
            {
                jobs.Add((fam, vname, sym, vcfg, vmode));
            }
        }
    }

    var jobReports = new FxTrainingReport[jobs.Count];
    var jobSecs = new double[jobs.Count];
    var sweepWatch = Stopwatch.StartNew();
    Console.WriteLine($"\nsweep: {jobs.Count} job(s) — " +
        $"{grids.Count} families, variants per family, × their symbols");

    Parallel.For(0, jobs.Count, i =>
    {
        var (_, _, sym, vcfg, vmode) = jobs[i];
        // Real venue spread: points feed the regime detector, points × point
        // becomes the per-trade cost in price units.
        var spreadPoints = 10.0;
        var spreadPrice = 0.0;
        if (spreads.TryGetValue(sym, out var sp) && sp.SpreadPoints > 0 && sp.Point > 0)
        {
            spreadPoints = sp.SpreadPoints;
            spreadPrice = sp.SpreadPoints * sp.Point;
        }

        var symCfg = spreadPrice > 0 ? vcfg with { SpreadPrice = spreadPrice } : vcfg;
        var spreadMax = Math.Max(60.0, spreadPoints * 1.5);
        var sw = Stopwatch.StartNew();
        jobReports[i] = FxTrainingSimulator.Run(sym, barsBySymbol[sym],
            symCfg, RosterFor(sym, vmode), spreadPoints, spreadMax);
        jobSecs[i] = sw.Elapsed.TotalSeconds;
    });

    foreach (var (fam, variants) in grids)
    {
        Console.WriteLine($"\n### family {fam}");
        Console.WriteLine("| variant | both | grew | ≥3k | Σnet | worst end | time |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        var famScores = new List<(string Name, int Both, int Grow, int Rich,
                                  double Net, double WorstEnd, double Secs)>();
        foreach (var (vname, _, _) in variants)
        {
            var idxs = jobs.Select((j, i) => (j, i))
                .Where(x => x.j.Fam == fam && x.j.Var == vname)
                .Select(x => x.i).ToList();
            var rs = idxs.Select(i => jobReports[i]).ToList();
            if (rs.Count == 0)
            {
                Console.WriteLine($"| {vname} | n/a — no symbols in this family |");
                continue;
            }

            var both = rs.Count(r => r.EndBalance > config.StartBalance && r.TradeCount >= 3000);
            var grew = rs.Count(r => r.EndBalance > config.StartBalance);
            var rich = rs.Count(r => r.TradeCount >= 3000);
            var net = rs.Sum(r => r.NetUsd);
            var worstEnd = rs.Min(r => r.EndBalance);
            var secs = idxs.Sum(i => jobSecs[i]);
            famScores.Add((vname, both, grew, rich, net, worstEnd, secs));
            Console.WriteLine($"| {vname} | {both}/{rs.Count} | {grew} | {rich} | " +
                              $"${net:0.00} | ${worstEnd:0.00} | {secs:0.0}s |");
        }

        if (famScores.Count == 0)
        {
            continue;
        }

        var best = famScores.OrderByDescending(s => s.Both).ThenByDescending(s => s.Net).First();
        Console.WriteLine($"\nbest[{fam}]: {best.Name}");
        Console.WriteLine("| symbol | trades | win | $10 → | net | PF |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var x in jobs.Select((j, i) => (j, i))
                     .Where(x => x.j.Fam == fam && x.j.Var == best.Name))
        {
            var r = jobReports[x.i];
            Console.WriteLine($"| {r.Symbol} | {r.TradeCount} | {r.WinRate:P0} | ${r.EndBalance:0.00} | " +
                              $"{r.NetUsd:+$0.00;-$0.00;$0.00} | {r.ProfitFactor:0.00} |");
        }
    }

    Console.WriteLine($"\nsweep wall time: {sweepWatch.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

// ── --diag: one best-known config per family, full per-symbol breakdown —
// where does the R actually leak (exit mix, spread cost, which families)?
if (diag)
{
    static string FamOf(string symbol) => symbol.ToUpperInvariant() switch
    {
        "XAGUSD" or "XAGEUR" or "XAUUSD" or "XAUUSDMICRO" or "XAUEUR"
            or "XPDUSD" or "XPTUSD" => "metal",
        "AUDUSD" or "EURUSD" or "GBPUSD" or "NZDUSD" or "USDCAD"
            or "USDCHF" or "USDJPY" => "fx",
        _ => "crypto",
    };

    var best = new Dictionary<string, FxTrainingConfig>(StringComparer.Ordinal)
    {
        ["metal"] = config with { MaxRiskUsd = 25, StopAtrMult = 5.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 },
        ["fx"] = config with { MaxRiskUsd = 25, StopAtrMult = 4.0, RewardRisk = 2.0, HoldBars = 120, MinStopSpreadMult = 3.0 },
        ["crypto"] = config with { MaxRiskUsd = 25, RewardRisk = 2.0, MinStopSpreadMult = 4.0 },
    };

    var djobs = barsBySymbol.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kv => kv.Key).ToList();
    var dreports = new FxTrainingReport[djobs.Count];
    var dsecs = new double[djobs.Count];
    var dsw = Stopwatch.StartNew();
    Console.WriteLine("\n### diag: exit mix, implied spread cost, family split");
    Console.WriteLine("| symbol | fam | trades | PF | E[r] | stop% | tgt% | time% | tgtAvgR | c_est | E[r]+c | top family (trades, E[r]) |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

    Parallel.For(0, djobs.Count, i =>
    {
        var sym = djobs[i];
        var dcfg = best[FamOf(sym)];
        var spreadPoints = 10.0;
        var spreadPrice = 0.0;
        if (spreads.TryGetValue(sym, out var sp) && sp.SpreadPoints > 0 && sp.Point > 0)
        {
            spreadPoints = sp.SpreadPoints;
            spreadPrice = sp.SpreadPoints * sp.Point;
        }

        var symCfg = spreadPrice > 0 ? dcfg with { SpreadPrice = spreadPrice } : dcfg;
        var spreadMax = Math.Max(60.0, spreadPoints * 1.5);
        var sw = Stopwatch.StartNew();
        dreports[i] = FxTrainingSimulator.Run(sym, barsBySymbol[sym], symCfg, null, spreadPoints, spreadMax);
        dsecs[i] = sw.Elapsed.TotalSeconds;
    });

    for (var i = 0; i < djobs.Count; i++)
    {
        var r = dreports[i];
        var n = r.Trades.Count;
        if (n == 0)
        {
            Console.WriteLine($"| {r.Symbol} | {FamOf(r.Symbol)} | 0 | — | — | — | — | — | — | — | — | — |");
            continue;
        }

        static double Avg(IEnumerable<FxTrainingTrade> xs, Func<FxTrainingTrade, double> f) =>
            xs.Any() ? xs.Average(f) : double.NaN;

        var stops = r.Trades.Where(t => t.ExitReason == "stop").ToList();
        var targets = r.Trades.Where(t => t.ExitReason == "target").ToList();
        var times = r.Trades.Where(t => t.ExitReason == "time").ToList();
        var eR = r.Trades.Average(t => t.RMultiple);
        var tgtAvg = Avg(targets, t => t.RMultiple);
        const double rr = 2.0;   // every diag variant uses rr2
        var cEst = double.IsNaN(tgtAvg) ? double.NaN : rr - tgtAvg;
        var erC = double.IsNaN(cEst) ? double.NaN : eR + cEst;
        var top = r.FamilyStats.OrderByDescending(s => s.Trades).FirstOrDefault();
        var topTxt = top is null ? "—" :
            $"{top.Alpha} ({top.Trades}, {(top.Trades > 0 ? top.TotalR / top.Trades : 0):+0.00;-0.00;0.00})";
        var cTxt = double.IsNaN(cEst) ? "n/a" : cEst.ToString("+0.00;-0.00;0.00");
        var ercTxt = double.IsNaN(erC) ? "n/a" : erC.ToString("+0.00;-0.00;0.00");
        Console.WriteLine($"| {r.Symbol} | {FamOf(r.Symbol)} | {n} | {r.ProfitFactor:0.00} | {eR:+0.00;-0.00;0.00} | " +
            $"{100.0 * stops.Count / n:0}% | {100.0 * targets.Count / n:0}% | {100.0 * times.Count / n:0}% | " +
            $"{tgtAvg:+0.00;-0.00;0.00} | {cTxt} | {ercTxt} | {topTxt} |");
    }

    Console.WriteLine($"\ndiag wall time: {dsw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

var reports = new List<FxTrainingReport>();
foreach (var (symbol, bars) in barsBySymbol.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
{
    // Real venue spread: points feed the regime detector, points × point
    // becomes the per-trade cost in price units.
    var spreadPoints = 10.0;
    var spreadPrice = 0.0;
    if (spreads.TryGetValue(symbol, out var sp) && sp.SpreadPoints > 0 && sp.Point > 0)
    {
        spreadPoints = sp.SpreadPoints;
        spreadPrice = sp.SpreadPoints * sp.Point;
    }

    var symBase = ConfigFor(symbol);
    var symCfg = spreadPrice > 0 ? symBase with { SpreadPrice = spreadPrice } : symBase;
    var spreadMax = Math.Max(60.0, spreadPoints * 1.5);
    var roster = RosterFor(symbol, RosterModeFor(symbol));
    reports.Add(FxTrainingSimulator.Run(
        symbol, bars, symCfg, roster, spreadPoints, spreadMax));
}

foreach (var report in reports)
{
    memory.Learn(report);
}

// ── report ──────────────────────────────────────────────────────────────────
var sb = new StringBuilder();
sb.AppendLine("# FX training simulation — small-account growth");
sb.AppendLine();
sb.AppendLine($"- run at: {DateTimeOffset.UtcNow:u}");
sb.AppendLine($"- config: start ${config.StartBalance:0.##}, risk {config.RiskFraction:P0} per trade, " +
              $"min-lot risk ${config.MinLotRiskUsd:0.##}, $25/trade swing cap; " +
              "per-symbol best-known routing from 7 sweep passes over the completed " +
              "H4/D1/H1 tape (metal sam6/floor8/rr3 · pos roster, quiet majors " +
              "quietonly or +quiet by symbol, EURUSD win40/rr3 + playbook-filtered " +
              "roster, crypto win70/h90/f8/rr3 · pos5)");
sb.AppendLine($"- tape: {tapeDir}");
sb.AppendLine("- spread: venue snapshot (spreads.json) — points into the regime veto, " +
              "points × point as per-trade cost");
sb.AppendLine($"- {memory.SummaryLine()}");
sb.AppendLine();
sb.AppendLine("> Evidence only. The simulator replays the production alpha roster and the");
sb.AppendLine("> production regime detector; it never trades. A human ports anything worth");
sb.AppendLine("> keeping. The paper soak and real-money gate still own every live path.");
sb.AppendLine();

sb.AppendLine("## Per-symbol result");
sb.AppendLine();
sb.AppendLine("| symbol | bars | trades | win | $10 → | net | worst DD | PF | verdict |");
sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
foreach (var r in reports.OrderByDescending(r => r.NetUsd))
{
    sb.AppendLine($"| {r.Symbol} | {r.Bars} | {r.TradeCount} | {r.WinRate:P0} | " +
                  $"${r.EndBalance:0.00} | {r.NetUsd:+$0.00;-$0.00;$0.00} | " +
                  $"${Math.Abs(r.MaxDrawdownUsd):0.00} | {r.ProfitFactor:0.00} | {r.Verdict} |");
}

sb.AppendLine();
sb.AppendLine("## Brain memory (what the brain now remembers)");
sb.AppendLine();
var playbook = memory.All();
if (playbook.Count == 0)
{
    sb.AppendLine("_no family cells yet — run again once more tape has accrued_");
}
else
{
    sb.AppendLine("| symbol | family | trades | win | total R | expectancy R | $ P/L |");
    sb.AppendLine("|---|---|---|---|---|---|---|");
    foreach (var m in playbook.Take(40))
    {
        sb.AppendLine($"| {m.Symbol} | {m.Alpha} | {m.Trades} | {m.WinRate:P0} | " +
                      $"{m.TotalR:+0.0;-0.0;0.0} | {m.ExpectancyR:+0.000;-0.000;0.000} | " +
                      $"${m.TotalPnlUsd:+0.00;-0.00;0.00} |");
    }
}

sb.AppendLine();
sb.AppendLine("## Best per-family record per symbol");
sb.AppendLine();
foreach (var r in reports)
{
    var best = memory.Playbook(r.Symbol).FirstOrDefault();
    sb.AppendLine(best is null
        ? $"- {r.Symbol}: no family spoke on this tape"
        : $"- {r.Symbol}: **{best.Alpha}** — {best.Trades} trades, {best.WinRate:P0} win, " +
          $"expectancy {best.ExpectancyR:+0.000;-0.000;0.000}R, ${best.TotalPnlUsd:+0.00;-0.00;0.00}");
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await File.WriteAllTextAsync(outPath, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"report written: {outPath}");
Console.WriteLine(memory.SummaryLine());
return 0;

// ── helpers ─────────────────────────────────────────────────────────────────

/// <summary>Read the deep MT5 tape: <SYM>_<TF>.jsonl files with one bar per
/// line ({"t":unix-seconds,"o","h","l","c","v"}). Coarse timeframes reach
/// furthest back; each finer TF drops the overlapping coarse tail and takes
/// over the recent window, so one symbol becomes one continuous series with
/// the finest resolution where it matters most (the present).</summary>
static Dictionary<string, IReadOnlyList<FxBar>> LoadDeepTape(string tapeDir)
{
    var result = new Dictionary<string, IReadOnlyList<FxBar>>(StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(tapeDir))
    {
        return result;
    }

    var bySymbol = new Dictionary<string, List<(string Tf, string Path)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in Directory.GetFiles(tapeDir, "*.jsonl"))
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var underscore = name.LastIndexOf('_');
        if (underscore <= 0)
        {
            continue;
        }

        var tf = name[(underscore + 1)..];
        if (TfSeconds(tf) <= 0)
        {
            continue;   // unknown timeframe suffix — not part of the tape
        }

        var symbol = name[..underscore];
        if (!bySymbol.TryGetValue(symbol, out var list))
        {
            list = new List<(string, string)>();
            bySymbol[symbol] = list;
        }

        list.Add((tf, file));
    }

    foreach (var (symbol, files) in bySymbol)
    {
        var tape = new List<FxBar>();
        foreach (var (tf, path) in files.OrderByDescending(f => TfSeconds(f.Tf)))
        {
            var series = ReadTape(path);
            if (series.Count == 0)
            {
                continue;
            }

            // Drop anything already covered by this (finer) series — overlap
            // lives at the tail of the coarser tape, since the finer window
            // is strictly more recent.
            if (tape.Count > 0 && tape[^1].Time >= series[0].Time)
            {
                var keep = tape.Count;
                while (keep > 0 && tape[keep - 1].Time >= series[0].Time)
                {
                    keep--;
                }

                tape.RemoveRange(keep, tape.Count - keep);
            }

            tape.AddRange(series);
        }

        if (tape.Count > 0)
        {
            result[symbol] = tape;
        }
    }

    return result;
}

static List<FxBar> ReadTape(string path)
{
    var bars = new List<FxBar>();
    foreach (var line in ReadLinesShared(path))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            bars.Add(new FxBar(
                root.GetProperty("t").GetInt64(),
                root.GetProperty("o").GetDouble(),
                root.GetProperty("h").GetDouble(),
                root.GetProperty("l").GetDouble(),
                root.GetProperty("c").GetDouble(),
                root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetDouble() : 0));
        }
        catch (JsonException)
        {
            // skip malformed lines — same tolerance as the tick reader
        }
    }

    return bars;
}

static long TfSeconds(string tf) => tf.ToUpperInvariant() switch
{
    "M1" => 60,
    "M5" => 300,
    "M15" => 900,
    "M30" => 1800,
    "H1" => 3600,
    "H4" => 14400,
    "D1" => 86400,
    _ => 0,
};

/// <summary>Venue spread snapshot written by scripts/fetch_mt5_history.py:
/// spread_points (in the symbol's own point units) and point (price per
/// point) — their product is the spread in price units.</summary>
static Dictionary<string, (double SpreadPoints, double Point)> LoadSpreads(string tapeDir)
{
    var map = new Dictionary<string, (double SpreadPoints, double Point)>(StringComparer.OrdinalIgnoreCase);
    var path = Path.Combine(tapeDir, "spreads.json");
    if (!File.Exists(path))
    {
        return map;
    }

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Value.TryGetProperty("spread_points", out var sp)
                || !prop.Value.TryGetProperty("point", out var pt)
                || sp.ValueKind != JsonValueKind.Number
                || pt.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            map[prop.Name] = (sp.GetDouble(), pt.GetDouble());
        }
    }
    catch (JsonException)
    {
        // unreadable snapshot → default spread is used per symbol
    }

    return map;
}

static Dictionary<string, IReadOnlyList<FxBar>> LoadBarsBySymbol(string tickDir)
{
    var result = new Dictionary<string, IReadOnlyList<FxBar>>(StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(tickDir))
    {
        return result;
    }

    // Group every <symbol>_<date>.jsonl into one continuous per-symbol series.
    foreach (var group in Directory.GetFiles(tickDir, "*.jsonl")
                 .GroupBy(f => SymbolOf(f), StringComparer.OrdinalIgnoreCase))
    {
        var buckets = new Dictionary<long, List<double>>();
        foreach (var file in group.OrderBy(f => f))
        {
            // The live app holds today's archive open for append; open with
            // shared read so training can run beside a live session.
            foreach (var line in ReadLinesShared(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("b", out var b) || !root.TryGetProperty("a", out var a)
                        || !root.TryGetProperty("t", out var t)
                        || b.ValueKind != JsonValueKind.Number || a.ValueKind != JsonValueKind.Number
                        || t.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var mid = (b.GetDouble() + a.GetDouble()) / 2.0;
                    if (mid <= 0)
                    {
                        continue;
                    }

                    var ms = t.GetInt64();
                    var minute = ms / 60000;
                    if (!buckets.TryGetValue(minute, out var list))
                    {
                        list = new List<double>();
                        buckets[minute] = list;
                    }

                    list.Add(mid);
                }
                catch (JsonException)
                {
                    // skip malformed lines — same tolerance as the archive readers
                }
            }
        }

        var bars = buckets.OrderBy(kv => kv.Key).Select(kv =>
        {
            var mids = kv.Value;
            return new FxBar(kv.Key * 60, mids[0], mids.Max(), mids.Min(), mids[^1], mids.Count);
        }).ToList();

        if (bars.Count > 0)
        {
            result[group.Key] = bars;
        }
    }

    return result;
}

static string SymbolOf(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    var underscore = name.LastIndexOf('_');
    return underscore > 0 ? name[..underscore] : name;
}

static IEnumerable<string> ReadLinesShared(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
    {
        yield return line;
    }
}
