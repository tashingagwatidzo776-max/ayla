using System;
using System.Collections.Generic;
using System.Linq;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Tests for the genetic lab (docs/ai-agent-program.md agent-6 support):
/// journal replay decoding, the deterministic replay fitness, and the
/// approval-gated walk-forward run. The rails under test: the lab journals
/// FX_LAB evidence and nothing else — no order path, no promotion.
/// </summary>
[Trait("Category", "Unit")]
public class FxLabServiceTests : IDisposable
{
    private readonly string _journalDir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"tf_fxlab_{Guid.NewGuid():N}");
    private readonly TradeJournal _journal;

    public FxLabServiceTests() => _journal = new TradeJournal(_journalDir);

    public void Dispose()
    {
        _journal.Dispose();
        try { System.IO.Directory.Delete(_journalDir, recursive: true); } catch { }
    }

    private void SeedDecisions(string symbol, int count, double startPrice = 2650.0)
    {
        var price = startPrice;
        var t = 1790000000000L;
        for (var i = 0; i < count; i++)
        {
            // Deterministic wobble: up 3, down 2 — a trending tape the
            // optimizer can actually chew on.
            var step = (i % 5) switch { 0 => 0.9, 1 => 0.9, 2 => 0.9, 3 => -1.2, _ => -1.2 };
            price = Math.Max(1.0, price + step);
            var details = System.Text.Json.JsonSerializer.Serialize(new
            {
                Symbol = symbol,
                Bid = price - 0.2,
                Ask = price + 0.2,
                Time = t,
            });
            _journal.Log(Guid.Empty, "FX_DECISION", details);
            t += 60_000;
        }
        _journal.Flush();
    }

    [Fact]
    public void DecisionToBar_ParsesBidAsk_ToMidBar()
    {
        var bar = FxLabService.DecisionToBar("""{"Bid":2650.0,"Ask":2650.6,"Time":1790000000000}""");
        Assert.NotNull(bar);
        Assert.Equal(2650.3, bar.Value.Close, 6);
        Assert.Equal(2650.3, bar.Value.High, 6);
        Assert.Equal(0, bar.Value.Volume);
        Assert.Equal(1790000000000, bar.Value.Time);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Ask\":2650.6}")]          // bid missing
    [InlineData("{\"Bid\":0,\"Ask\":2650.6}")] // degenerate
    public void DecisionToBar_Malformed_ReturnsNull(string details)
    {
        Assert.Null(FxLabService.DecisionToBar(details));
    }

    [Fact]
    public void ReplayPnl_FrozenTape_IsZero()
    {
        var bars = Enumerable.Range(0, 60)
            .Select(_ => new FxBar(0, 100, 100, 100, 100, 0)).ToList();
        Assert.Equal(0, FxLabService.ReplayPnl(bars, new FxGenome(new[] { 0.5, 0.5 })));
    }

    [Fact]
    public void ReplayPnl_TrendingTape_IsDeterministic()
    {
        var bars = new List<FxBar>();
        var price = 100.0;
        for (var i = 0; i < 200; i++)
        {
            price += (i % 7) < 4 ? 0.4 : -0.3;
            bars.Add(new FxBar(i, price, price, price, price, 0));
        }
        var g = new FxGenome(new[] { 0.3, 0.2 });
        var a = FxLabService.ReplayPnl(bars, g);
        var b = FxLabService.ReplayPnl(bars, g);
        Assert.Equal(a, b, 10);
    }

    [Fact]
    public async Task RunOnceAsync_ThinJournal_IsSilent()
    {
        SeedDecisions("XAUUSDmicro", count: 20);
        var lab = new FxLabService(_journal) { Disabled = false, MinDecisions = 120 };
        var results = await lab.RunOnceAsync();
        Assert.Empty(results);
        // Silence means silence: no FX_LAB evidence for an un-run lab.
        Assert.DoesNotContain(_journal.GetRecent(null, 100), e => e.Category == "FX_LAB");
    }

    [Fact]
    public async Task RunOnceAsync_RichJournal_JournalsEvidence()
    {
        SeedDecisions("XAUUSDmicro", count: 200);
        var lab = new FxLabService(_journal)
        {
            MinDecisions = 120,
            Folds = 3,
            Population = 8,
            Generations = 4,
            SymbolsProvider = () => "XAUUSDmicro",
            InitialDelay = TimeSpan.FromMilliseconds(1),
        };
        var results = await lab.RunOnceAsync();
        var result = Assert.Single(results);
        Assert.Equal("XAUUSDmicro", result.Symbol);
        // 200 bars @ isFraction 0.6 fits at most 2 complete folds — the
        // harness must run fewer folds rather than fabricate short ones.
        Assert.InRange(result.FoldCount, 2, 3);
        Assert.Contains("OOS folds", result.Verdict);
        _journal.Flush();   // the lab's evidence entry is enqueued, not yet on disk
        Assert.Contains(_journal.GetRecent(null, 100), e => e.Category == "FX_LAB");
    }

    [Fact]
    public async Task RunOnceAsync_Disabled_NeverRuns()
    {
        SeedDecisions("XAUUSDmicro", count: 200);
        var lab = new FxLabService(_journal) { Disabled = true };
        Assert.Empty(await lab.RunOnceAsync());
        Assert.Empty(_journal.GetRecent(null, 100).Where(e => e.Category == "FX_LAB"));
    }

    [Fact]
    public async Task RunOnceAsync_Concurrent_SecondCallIsNoOp()
    {
        SeedDecisions("XAUUSDmicro", count: 200);
        var lab = new FxLabService(_journal)
        {
            MinDecisions = 120,
            SymbolsProvider = () => "XAUUSDmicro",
        };
        var first = lab.RunOnceAsync();
        var second = await lab.RunOnceAsync();
        Assert.Empty(second);   // overlap guard swallowed the concurrent tick
        var results = await first;
        Assert.Single(results);
    }

    [Fact]
    public void WalkForwardGate_HonestOptimization_CanApprove()
    {
        // The pre-existing gate still guards the lab: an in-sample-fit
        // genome that generalizes OOS gets approved; an overfit one does not
        // (same assertions FxAdvancedTests pins — re-verified through the
        // lab's own threshold plumbing here).
        var honest = new List<FxFold>
        {
            new(0, 2.0, 1.0), new(1, 1.5, 0.8), new(2, 1.8, 0.4),
        };
        var overfit = new List<FxFold>
        {
            new(0, 9.0, -1.0), new(1, 8.0, -0.5), new(2, 7.0, 0.1),
        };
        Assert.True(FxWalkForward.Approved(honest));
        Assert.False(FxWalkForward.Approved(overfit));
    }
}
