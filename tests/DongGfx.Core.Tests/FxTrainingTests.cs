using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The offline training simulation: the deterministic $10-account replayer
/// over the REAL alpha roster and regime detector, and the persistent brain
/// memory that remembers what was measured across runs (the guarded type-7
/// surface — a bounded confidence tilt only, never a decision or an order).
/// </summary>
[Trait("Category", "Unit")]
public class FxTrainingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tf-training-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── bar fixtures ───────────────────────────────────────────────────────

    /// <summary>A directional trending tape: up then down, enough bars to
    /// clear the warm-up, with real range so ATR/ADX are meaningful.</summary>
    private static List<FxBar> TrendBars(int count = 400, double seed = 100.0)
    {
        var bars = new List<FxBar>(count);
        var price = seed;
        var rng = new Random(42);
        for (var i = 0; i < count; i++)
        {
            // Slow drift plus noise, alternating so there are trends and
            // ranges for the regime detector to call.
            var drift = (i % 60) < 30 ? 0.35 : -0.35;
            var open = price;
            var close = price + drift + rng.NextDouble() * 0.6 - 0.3;
            var high = Math.Max(open, close) + rng.NextDouble() * 0.4;
            var low = Math.Min(open, close) - rng.NextDouble() * 0.4;
            bars.Add(new FxBar(1_700_000_000 + i * 60, open, high, low, close, 1));
            price = close;
        }

        return bars;
    }

    // ── simulator ──────────────────────────────────────────────────────────

    [Fact]
    public void Simulator_Starts_At_Configured_Balance()
    {
        var report = FxTrainingSimulator.Run("TEST", TrendBars());
        Assert.Equal(10.0, report.StartBalance);
        Assert.True(report.EndBalance > 0);
        Assert.True(report.Bars > 0);
    }

    [Fact]
    public void Simulator_Deterministic_On_Same_Tape()
    {
        var cfg = new FxTrainingConfig();
        var a = FxTrainingSimulator.Run("TEST", TrendBars(), cfg);
        var b = FxTrainingSimulator.Run("TEST", TrendBars(), cfg);
        Assert.Equal(a.EndBalance, b.EndBalance);
        Assert.Equal(a.TradeCount, b.TradeCount);
    }

    [Fact]
    public void Simulator_Thin_Tape_Is_Silent_Not_An_Error()
    {
        var report = FxTrainingSimulator.Run("TEST", TrendBars(20));
        Assert.Equal(0, report.TradeCount);
        Assert.Empty(report.FamilyStats);
        Assert.Contains("not enough bars", report.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simulator_Account_Is_Never_Negative()
    {
        var cfg = new FxTrainingConfig(StartBalance: 10.0, RiskFraction: 0.5,
            MinLotRiskUsd: 5.0, RewardRisk: 1.0);
        var report = FxTrainingSimulator.Run("TEST", TrendBars(), cfg);
        Assert.True(report.EndBalance >= 0);
        Assert.True(report.MaxDrawdownUsd <= 10.01);   // cannot draw past zero
    }

    [Fact]
    public void Simulator_Rollup_Covers_The_Families_That_Spoke()
    {
        var report = FxTrainingSimulator.Run("TEST", TrendBars());
        if (report.TradeCount == 0)
        {
            return;   // no signals on this tape — the rollup is vacuously correct
        }

        var fromTrades = report.Trades.GroupBy(t => t.Alpha).ToDictionary(g => g.Key, g => g.Count());
        foreach (var stat in report.FamilyStats)
        {
            Assert.Equal(fromTrades[stat.Alpha], stat.Trades);
        }

        Assert.Equal(report.TradeCount, report.FamilyStats.Sum(s => s.Trades));
        Assert.Equal(report.Trades.Count(t => t.PnlUsd > 0), report.Wins);
    }

    [Fact]
    public void Simulator_Spans_Multiple_Symbols_Without_Leaking()
    {
        var bars = new Dictionary<string, IReadOnlyList<FxBar>>
        {
            ["AAA"] = TrendBars(),
            ["BBB"] = TrendBars(count: 400, seed: 200),
        };
        var reports = FxTrainingSimulator.RunAll(bars);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.True(r.Bars > 0));
        Assert.Contains(reports, r => r.Symbol == "AAA");
        Assert.Contains(reports, r => r.Symbol == "BBB");
    }

    [Fact]
    public void Simulator_Risk_Cap_Bounds_Every_Trades_Dollar_Swing()
    {
        // The cap binds every trade (the min-lot floor alone would exceed it),
        // so no single round-trip may move the book by more than the cap —
        // the guard against the runaway compounding that once drove the
        // reported drawdown past the ending balance by orders of magnitude.
        var cfg = new FxTrainingConfig(StartBalance: 10.0, RiskFraction: 0.5,
            MinLotRiskUsd: 5.0, RewardRisk: 3.0, MaxRiskUsd: 0.25);
        var report = FxTrainingSimulator.Run("TEST", TrendBars(), cfg);
        if (report.TradeCount == 0)
        {
            return;   // no signals on this tape — nothing to bound
        }

        Assert.All(report.Trades, t =>
        {
            Assert.True(t.PnlUsd >= -0.25 - 1e-9, $"loss {-t.PnlUsd:0.###} exceeded the cap");
            Assert.True(t.PnlUsd <= 0.25 * (cfg.RewardRisk + 1) + 1e-9,
                $"win {t.PnlUsd:0.###} exceeded cap × (RR+1)");
        });
    }

    [Fact]
    public void Simulator_Confidence_Floor_Refuses_Speakers_Below_It()
    {
        // No roster speaker ever reports above 0.9, so a 0.95 floor must
        // trade nothing while the unfiltered run takes whatever it can.
        var bars = TrendBars();
        var open = FxTrainingSimulator.Run("TEST", bars);
        var picky = FxTrainingSimulator.Run("TEST", bars,
            new FxTrainingConfig(MinConfidence: 0.95));

        Assert.Equal(0, picky.TradeCount);
        Assert.True(picky.TradeCount <= open.TradeCount);
    }

    [Fact]
    public void Simulator_Stop_Floor_Keeps_Stops_Out_Of_The_Noise()
    {
        // Same tape, same huge spread: without the floor the ATR stop sits
        // inside the noise and trades get stopped out; with spread × 4 the
        // stop is unreachable at this drift, so nothing exits on "stop".
        var bars = TrendBars();
        var trap = new FxTrainingConfig(SpreadPrice: 10.0, StopAtrMult: 1.0, RewardRisk: 2.0);
        var floored = trap with { MinStopSpreadMult = 4.0 };   // stop ≥ $40 vs ±0.35/bar drift

        var rTrap = FxTrainingSimulator.Run("TEST", bars, trap);
        var rFloored = FxTrainingSimulator.Run("TEST", bars, floored);

        if (rFloored.TradeCount == 0)
        {
            return;   // no signals on this tape
        }

        Assert.DoesNotContain(rFloored.Trades, t => t.ExitReason == "stop");
        Assert.Contains(rTrap.Trades, t => t.ExitReason == "stop");
    }

    // ── equity drawdown brake ───────────────────────────────────────────

    /// <summary>TrendBars stamps bars 60s apart from a fixed epoch, so a
    /// trade's entry time maps back to its bar index exactly.</summary>
    private static int BarIndex(FxTrainingTrade t) =>
        (int)((t.EntryTimeUtc - 1_700_000_000) / 60);

    [Fact]
    public void Simulator_Drawdown_Brake_Is_Off_Unless_Armed()
    {
        // Default-off (and off at 100% depth, which no live book can sit at
        // without already being zero): an unarmed brake replays exactly the
        // tape it always has.
        var bars = TrendBars();
        var plain = FxTrainingSimulator.Run("TEST", bars, new FxTrainingConfig());
        var overproof = FxTrainingSimulator.Run("TEST", bars,
            new FxTrainingConfig(DrawdownBrakePct: 1.0));

        Assert.Equal(0.0, new FxTrainingConfig().DrawdownBrakePct);
        Assert.Equal(plain.TradeCount, overproof.TradeCount);
        Assert.Equal(plain.EndBalance, overproof.EndBalance);
    }

    [Fact]
    public void Simulator_Drawdown_Brake_Skips_Entries_While_Deep()
    {
        // Big risk per trade on a $10 book: one losing round-trip drops the
        // equity meaningfully below its high-water mark, arming the brake.
        var cfg = new FxTrainingConfig(StartBalance: 10.0, RiskFraction: 0.5,
            MinLotRiskUsd: 5.0, RewardRisk: 1.0);
        var armed = cfg with { DrawdownBrakePct = 0.05, DrawdownBrakeBars = 120 };
        var bars = TrendBars(count: 800);

        var open = FxTrainingSimulator.Run("TEST", bars, cfg);
        var braked = FxTrainingSimulator.Run("TEST", bars, armed);
        if (open.TradeCount == 0)
        {
            return;   // no signals on this tape — nothing to brake
        }

        // The brake only ever skips: entry decisions are equity-independent,
        // so every braked entry sits on a bar the open run also took.
        Assert.True(braked.TradeCount <= open.TradeCount);
        var openIdx = open.Trades.Select(BarIndex).ToList();
        var p = 0;
        foreach (var g in braked.Trades.Select(BarIndex))
        {
            while (p < openIdx.Count && openIdx[p] != g)
            {
                p++;
            }

            Assert.True(p < openIdx.Count,
                $"braked entry at bar {g} was never taken by the open run");
            p++;
        }

        // Walk the braked equity curve the same way the simulator does and
        // find the first settle that leaves the book ≥5% below its peak.
        var equity = braked.StartBalance;
        var peak = equity;
        var trip = -1;
        foreach (var t in braked.Trades)
        {
            equity = Math.Max(0.0, equity + t.PnlUsd);
            peak = Math.Max(peak, equity);
            if (peak > 0 && (peak - equity) / peak >= armed.DrawdownBrakePct)
            {
                trip = BarIndex(t);
                break;
            }
        }

        Assert.True(trip >= 0, "armed brake never tripped on this tape");

        // While engaged no entry opens before trip bar + the cooldown …
        var after = braked.Trades.Where(t => BarIndex(t) > trip).ToList();
        if (after.Count > 0)
        {
            Assert.True(BarIndex(after[0]) >= trip + armed.DrawdownBrakeBars,
                $"re-entered {BarIndex(after[0]) - trip} bars after the trip " +
                $"(cooldown {armed.DrawdownBrakeBars})");
        }

        // … but the brake is a stand-down, not a halt: on a deep tape it
        // must let go and trade again.
        Assert.True(braked.TradeCount > 0);
    }

    // ── memory: learn / accumulate ─────────────────────────────────────────

    private static FxTrainingReport Report(string symbol, params FxFamilyTrainingStat[] stats) =>
        new(symbol, DateTimeOffset.UtcNow, 100, 10, 10, 0, 0, 0, 0, 0, false,
            Array.Empty<FxTrainingTrade>(), stats, "test");

    private static FxFamilyTrainingStat Stat(string alpha, int trades, int wins, double totalR) =>
        new(alpha, trades, wins, trades > 0 ? (double)wins / trades : 0,
            totalR, totalR);

    [Fact]
    public void Memory_Empty_Reports_Honestly()
    {
        var memory = FxBrainMemory.Load(_dir);
        Assert.Equal(0, memory.Runs);
        Assert.Empty(memory.Playbook("XAUUSD"));
        Assert.Contains("empty", memory.SummaryLine(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.0, memory.ConfidenceWeight("XAUUSD", "hurst-trend"));
    }

    [Fact]
    public void Memory_Accumulates_Across_Runs_Instead_Of_Replacing()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 10, 6, 3.0)));
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 5, 3, 1.5)));

        var cell = Assert.Single(memory.Playbook("XAUUSD"));
        Assert.Equal(15, cell.Trades);
        Assert.Equal(9, cell.Wins);
        Assert.Equal(4.5, cell.TotalR, 3);
        Assert.Equal(2, memory.Runs);
    }

    [Fact]
    public void Memory_Ranks_Best_Expectancy_First()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD",
            Stat("loser", 10, 2, -4.0),
            Stat("winner", 10, 8, 6.0)));

        var playbook = memory.Playbook("XAUUSD");
        Assert.Equal(2, playbook.Count);
        Assert.Equal("winner", playbook[0].Alpha);
        Assert.Equal("loser", playbook[1].Alpha);
        Assert.True(playbook[0].ExpectancyR > playbook[1].ExpectancyR);
    }

    [Fact]
    public void Memory_Per_Symbol_Playbook_Does_Not_Blend_Symbols()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 10, 6, 3.0)));
        memory.Learn(Report("EURUSD", Stat("z-rev(20)", 4, 2, 1.0)));

        Assert.Single(memory.Playbook("XAUUSD"));
        Assert.Single(memory.Playbook("EURUSD"));
        Assert.Equal(2, memory.All().Count);
    }

    // ── memory: persistence ────────────────────────────────────────────────

    [Fact]
    public void Memory_Survives_A_Reload_Roundtrip()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 12, 7, 3.5)));
        Assert.True(File.Exists(FxBrainMemory.PathFor(_dir)));

        var reloaded = FxBrainMemory.Load(_dir);
        Assert.Equal(1, reloaded.Runs);
        var cell = Assert.Single(reloaded.Playbook("XAUUSD"));
        Assert.Equal(12, cell.Trades);
        Assert.Equal(7, cell.Wins);
        Assert.Equal(3.5, cell.TotalR, 3);
        Assert.NotEmpty(reloaded.LastTrainedAt);
        Assert.NotEmpty(reloaded.FirstTrainedAt);
    }

    [Fact]
    public void Memory_Corrupt_File_Resets_To_Empty_Never_Throws()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FxBrainMemory.PathFor(_dir), "{ not valid json !!!");
        var memory = FxBrainMemory.Load(_dir);
        Assert.Equal(0, memory.Runs);
        Assert.Empty(memory.All());
    }

    [Fact]
    public void Memory_Reset_Clears_Everything()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 10, 6, 3.0)));
        memory.Reset();
        Assert.Equal(0, memory.Runs);
        Assert.Empty(memory.All());
    }

    // ── memory: the bounded confidence tilt ────────────────────────────────

    [Fact]
    public void ConfidenceWeight_Untrusted_Sample_Leans_Neutral()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 5, 0, -3.0)));
        // Below the trust floor the weight stays 1.0 — a thin record must
        // not move the live vote at all.
        Assert.Equal(1.0, memory.ConfidenceWeight("XAUUSD", "hurst-trend"));
    }

    [Fact]
    public void ConfidenceWeight_Is_Bounded_Even_For_A_Steamroller()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 500, 450, 900.0)));
        var weight = memory.ConfidenceWeight("XAUUSD", "hurst-trend");
        Assert.True(weight >= FxBrainMemory.MinWeight);
        Assert.True(weight <= FxBrainMemory.MaxWeight);
        Assert.True(weight > 1.0);   // a real edge tilts up, still clamped
    }

    [Fact]
    public void ConfidenceWeight_Understanding_Family_Tilts_Down()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 500, 50, -900.0)));
        var weight = memory.ConfidenceWeight("XAUUSD", "hurst-trend");
        Assert.True(weight >= FxBrainMemory.MinWeight);
        Assert.True(weight < 1.0);
    }

    [Fact]
    public void ConfidenceWeight_Unknown_Cell_Is_Neutral()
    {
        var memory = FxBrainMemory.Load(_dir);
        memory.Learn(Report("XAUUSD", Stat("hurst-trend", 500, 450, 900.0)));
        // A different symbol and an unknown family both stay neutral.
        Assert.Equal(1.0, memory.ConfidenceWeight("EURUSD", "hurst-trend"));
        Assert.Equal(1.0, memory.ConfidenceWeight("XAUUSD", "never-heard-of-it"));
    }

    // ── the engine applies the tilt without changing its rails ─────────────

    private sealed class StubAlpha(string name, FxRegime[] regimes, double confidence) : IFxAlpha
    {
        public string Name => name;
        public FxRegime[] Regimes => regimes;
        public FxSignal? Evaluate(IReadOnlyList<FxBar> bars, FxRegimeVerdict regime) =>
            new(name, FxDirection.Buy, confidence, FxFeatures.Atr(bars, 14),
                "stub", regime.TimeUtc);
    }

    [Fact]
    public void Engine_Picks_Highest_Confidence_By_Default()
    {
        var engine = new FxEngine("TEST", "M1", (_, _, _) => { },
            lotsCap: 1, equityProvider: () => 100_000);
        engine.AddAlpha(new StubAlpha("weak", new[] { FxRegime.Trend }, 0.6));
        engine.AddAlpha(new StubAlpha("strong", new[] { FxRegime.Trend }, 0.9));

        var decision = engine.RunOnce(DateTimeOffset.UtcNow, TrendBars(), 100, 100);
        Assert.NotNull(decision.Signal);
        Assert.Equal("strong", decision.Signal!.Alpha);
    }

    [Fact]
    public void Engine_Memory_Weight_Can_Flip_The_Winner()
    {
        // The memory says the weaker-confidence family has a proven record;
        // the bounded tilt re-ranks the vote. Nothing else about the cycle
        // changes — no sizing, no order path.
        var engine = new FxEngine("TEST", "M1", (_, _, _) => { },
            lotsCap: 1, equityProvider: () => 100_000,
            confidenceWeight: alpha => alpha == "weak" ? 1.5 : 1.0);
        engine.AddAlpha(new StubAlpha("weak", new[] { FxRegime.Trend }, 0.7));
        engine.AddAlpha(new StubAlpha("strong", new[] { FxRegime.Trend }, 0.9));

        var decision = engine.RunOnce(DateTimeOffset.UtcNow, TrendBars(), 100, 100);
        Assert.NotNull(decision.Signal);
        // 0.7 × 1.5 = 1.05 beats 0.9 × 1.0 — the memory tilt re-ranks the
        // vote while the raw confidences stay what they were.
        Assert.Equal("weak", decision.Signal!.Alpha);
    }

    [Fact]
    public void Engine_Without_Memory_Weights_Is_The_Plain_Confidence_Vote()
    {
        var plain = new FxEngine("TEST", "M1", (_, _, _) => { },
            lotsCap: 1, equityProvider: () => 100_000);
        plain.AddAlpha(new StubAlpha("weak", new[] { FxRegime.Trend }, 0.6));
        plain.AddAlpha(new StubAlpha("strong", new[] { FxRegime.Trend }, 0.9));

        var neutral = new FxEngine("TEST", "M1", (_, _, _) => { },
            lotsCap: 1, equityProvider: () => 100_000,
            confidenceWeight: _ => 1.0);
        neutral.AddAlpha(new StubAlpha("weak", new[] { FxRegime.Trend }, 0.6));
        neutral.AddAlpha(new StubAlpha("strong", new[] { FxRegime.Trend }, 0.9));

        var bars = TrendBars();
        var a = plain.RunOnce(DateTimeOffset.UtcNow, bars, 100, 100);
        var b = neutral.RunOnce(DateTimeOffset.UtcNow, bars, 100, 100);
        Assert.Equal(a.Signal?.Alpha, b.Signal?.Alpha);
        Assert.Equal("strong", a.Signal?.Alpha);
    }

    [Fact]
    public void Engine_A_Garbage_Weight_Fails_Safe_To_Neutral()
    {
        var engine = new FxEngine("TEST", "M1", (_, _, _) => { },
            lotsCap: 1, equityProvider: () => 100_000,
            confidenceWeight: _ => double.NaN);
        engine.AddAlpha(new StubAlpha("weak", new[] { FxRegime.Trend }, 0.6));
        engine.AddAlpha(new StubAlpha("strong", new[] { FxRegime.Trend }, 0.9));

        var decision = engine.RunOnce(DateTimeOffset.UtcNow, TrendBars(), 100, 100);
        Assert.NotNull(decision.Signal);
        Assert.Equal("strong", decision.Signal!.Alpha);   // NaN must not win
    }
}
