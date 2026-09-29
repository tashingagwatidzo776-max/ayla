using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.App.Services;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Stage 3: the multi-symbol portfolio (shared supervisor, aggregate soak),
/// the portfolio-wide exposure cap (live positions + requested ≤ cap, bridge
/// down fails closed, cap 0 disables), the news veto's file loading, and
/// symbol CSV parsing with its fallbacks.
/// </summary>
[Trait("Category", "Unit")]
public class FxPortfolioTests
{
    // ── symbol parsing ──────────────────────────────────────────────────

    [Fact]
    public void ParseSymbols_Splits_Trim_Dedupes()
    {
        var s = FxScorecardService.ParseSymbols("XAUUSDmicro, eurusd , GBPUSD,EURUSD", "FALLBACK");
        Assert.Equal(3, s.Count);
        Assert.Contains("eurusd", s, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSymbols_Empty_Falls_Back()
    {
        var s = FxScorecardService.ParseSymbols("  ,, ", "XAUUSDmicro");
        Assert.Single(s);
        Assert.Equal("XAUUSDmicro", s[0]);
    }

    // ── exposure guard ──────────────────────────────────────────────────

    private sealed class PositionsHandler : HttpMessageHandler
    {
        public string PositionsJson = "{\"positions\":[]}";
        public string SymbolsJson = "{\"symbols\":[]}";
        public double Equity = 2632.19;
        public bool Down;
        // Scripted /positions behavior: each entry is one read — a JSON
        // body (served verbatim) or "!fail" (transport error, which the
        // client degrades to an empty list — exactly the production hazard).
        public Queue<string>? PositionsReads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (Down)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"));
            }

            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/positions") && PositionsReads is { } plan && plan.Count > 0)
            {
                var scripted = plan.Dequeue();
                if (scripted == "!fail")
                {
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("flaky read"));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(scripted, Encoding.UTF8, "application/json")
                });
            }

            var eq = Equity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = path.Contains("/positions") ? PositionsJson
                : path.Contains("/symbols") ? SymbolsJson
                : path.Contains("/account")
                    ? "{\"ok\":true,\"login\":201587365,\"server\":\"Deriv-Demo\",\"currency\":\"USD\"," +
                       "\"balance\":" + eq + ",\"equity\":" + eq +
                       ",\"margin_free\":" + eq + ",\"leverage\":1000,\"trade_mode\":0}"
                    : "{\"ok\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static Mt5BridgeClient NewClient(PositionsHandler handler) =>
        new(handler, new Uri("http://127.0.0.1:1/"));

    private static string Pos(string symbol, double volume, string comment = "") =>
        $"{{\"ticket\":{symbol.Length * 100},\"symbol\":\"{symbol}\",\"side\":\"buy\",\"volume\":{volume.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"price_open\":1,\"price_current\":1,\"profit\":0,\"comment\":\"{comment}\"}}";

    /// <summary>One brain symbol with real Deriv-like geometry: 0.01 lot
    /// minimum on a 100k contract — the case where a minimum trade risks
    /// a noticeable slice of a small account.</summary>
    private const string SmallAccountSymbolsJson =
        "{\"symbols\":[{\"symbol\":\"EURUSD\",\"description\":\"Euro\",\"digits\":5," +
        "\"volume_min\":0.01,\"volume_step\":0.01,\"volume_max\":100.0,\"contract_size\":100000.0}]}";

    [Fact]
    public async Task Exposure_Within_Cap_Allows()
    {
        var h = new PositionsHandler { PositionsJson = $"{{\"positions\":[{Pos("XAUUSDmicro", 0.05)}]}}" };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro", "EURUSD" });
        Assert.Null(await guard.VetoAsync(0.05));
    }

    [Fact]
    public async Task Exposure_Over_Cap_Refuses_With_Reason()
    {
        var h = new PositionsHandler { PositionsJson = $"{{\"positions\":[{Pos("XAUUSDmicro", 0.08)}]}}" };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro", "EURUSD" });
        var veto = await guard.VetoAsync(0.05);
        Assert.NotNull(veto);
        Assert.Contains("portfolio exposure", veto);
        Assert.Contains("cap", veto);
    }

    [Fact]
    public async Task Exposure_Ignores_Symbols_Outside_The_Portfolio()
    {
        var h = new PositionsHandler
        {
            PositionsJson = $"{{\"positions\":[{Pos("XAUUSDmicro", 0.05)},{Pos("USDZAR", 9.9)}]}}"
        };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro" });
        Assert.Null(await guard.VetoAsync(0.05));   // the 9.9-lot USDZAR position is not ours
    }

    [Fact]
    public async Task Exposure_CapZero_Disables_The_Veto()
    {
        var h = new PositionsHandler { PositionsJson = $"{{\"positions\":[{Pos("XAUUSDmicro", 50)}]}}" };
        var guard = new FxExposureGuard(NewClient(h), () => 0m, new[] { "XAUUSDmicro" });
        Assert.Null(await guard.VetoAsync(10));
    }

    [Fact]
    public async Task Exposure_BridgeDown_FailsClosed()
    {
        var h = new PositionsHandler { Down = true };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro" });
        var veto = await guard.VetoAsync(0.01);
        Assert.NotNull(veto);
        Assert.Contains("unreachable", veto);
    }

    [Fact]
    public async Task Exposure_FlakyPositionRead_FailsClosed_NotOpen()
    {
        // The 2026-09-29 cap-failure mechanism: /account answers while a
        // /positions read times out; the degraded-empty read made a loaded
        // book look flat and the cap failed open. Now the second read must
        // disagree with the first (0.3 vs 0) and refuse.
        var loaded = $"{{\"positions\":[{Pos("XAUUSDmicro", 0.3)}]}}";
        var h = new PositionsHandler
        {
            PositionsReads = new Queue<string>(new[] { loaded, "!fail" }),
        };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro" });
        var veto = await guard.VetoAsync(0.01);
        Assert.NotNull(veto);
        Assert.Contains("disagree", veto);
    }

    [Fact]
    public async Task Exposure_ChangingBook_Between_Reads_Refuses()
    {
        // Two healthy-looking reads that disagree (a fill landed in
        // between): the exposure number is not trustworthy — refuse.
        var h = new PositionsHandler
        {
            PositionsReads = new Queue<string>(new[]
            {
                $"{{\"positions\":[{Pos("XAUUSDmicro", 0.05)}]}}",
                $"{{\"positions\":[{Pos("XAUUSDmicro", 0.09)}]}}",
            }),
        };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro" });
        var veto = await guard.VetoAsync(0.01);
        Assert.NotNull(veto);
        Assert.Contains("disagree", veto);
    }

    [Fact]
    public async Task SmallAccount_FlakyPositionRead_FailsClosed()
    {
        // Same mechanism on the one-slot rail: a degraded read must not
        // present a used slot as free.
        var brainPos = $"{{\"positions\":[{Pos("EURUSD", 0.01, "donggfx-brain")}]}}";
        var h = new PositionsHandler
        {
            SymbolsJson = SmallAccountSymbolsJson,
            Equity = 20,
            PositionsReads = new Queue<string>(new[] { brainPos, "!fail" }),
        };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD" });
        var veto = await guard.VetoAsync("EURUSD", 0.01);
        Assert.NotNull(veto);
        Assert.Contains("small-account mode", veto);
    }

    [Fact]
    public async Task Exposure_Chained_After_Dormant_Small_Guard_Still_Vetoes()
    {
        // The 2026-09-29 10:33 fill: `smallTask ?? exposureTask` coalesced
        // on the TASK REFERENCE (always non-null), so the exposure guard
        // never ran at all once the small guard was wired in. The chain
        // must coalesce on the RESULT.
        var h = new PositionsHandler
        {
            PositionsJson = $"{{\"positions\":[{Pos("XAUUSDmicro", 0.3)}]}}",
        };
        var small = new FxSmallAccountGuard(NewClient(h), new[] { "XAUUSDmicro" });   // dormant: large account
        var exposure = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro" });

        var veto = await small.VetoAsync("XAUUSDmicro", 0.1)
                  ?? await exposure.VetoAsync(0.1);
        Assert.NotNull(veto);
        Assert.Contains("portfolio exposure", veto);
    }

    [Fact]
    public async Task Exposure_DegradedAgreeingEmptyReads_Are_Floored_By_The_LocalBook()
    {
        // The 07:19 mechanism exactly: both reads return a healthy-looking
        // EMPTY book in agreement while the account really holds 0.3 lots
        // (degraded reads agree on the lie). The brain's own book floors
        // the exposure — the cap must not fail open because the venue
        // forgot to answer twice.
        var h = new PositionsHandler { PositionsJson = "{\"positions\":[]}" };
        var guard = new FxExposureGuard(NewClient(h), () => 0.10m, new[] { "XAUUSDmicro" },
            localBookLots: () => 0.3);
        var veto = await guard.VetoAsync(0.01);
        Assert.NotNull(veto);
        Assert.Contains("portfolio exposure 0.3", veto);
    }

    [Fact]
    public async Task SmallAccount_Book_Holds_Slot_Even_When_Venue_Reads_Empty()
    {
        // One-slot rail, same floor: venue says flat (degraded), the brain
        // knows it holds a position — the slot is not free.
        var h = new PositionsHandler
        {
            SymbolsJson = SmallAccountSymbolsJson,
            Equity = 20,
            PositionsJson = "{\"positions\":[]}",
        };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD" },
            localBookLots: () => 0.01);
        var veto = await guard.VetoAsync("EURUSD", 0.01);
        Assert.NotNull(veto);
        Assert.Contains("slot held by the brain's own book", veto);
    }

    // ── journal-derived book (FxJournalBook) ─────────────────────────

    private static string FillLine(string ts, string side, string lots, string sym, long ticket) =>
        $"{{\"Timestamp\":\"{ts}\",\"Category\":\"FX_ORDER\",\"Details\":\"" +
        $"paper-exec fill (demo): {side} {lots} lots {sym} @ 1.1 — ticket {ticket}: {{}}\"}}";

    private static string CloseLine(string ts, long ticket) =>
        $"{{\"Timestamp\":\"{ts}\",\"Category\":\"FX_EXIT\",\"Details\":\"" +
        $"closed #{ticket} — deal : {{}}\"}}";

    [Fact]
    public void JournalBook_Fills_Open_And_Closes_Retire_Tickets()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dg-jbook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "journal_20260929.jsonl"), new[]
            {
                FillLine("2026-09-29T08:00:00Z", "buy", "0.1", "XAUUSDmicro", 111),
                FillLine("2026-09-29T08:05:00Z", "sell", "0.07", "USDJPY", 222),
            });
            var book = new FxJournalBook(dir);
            Assert.Equal(0.17, book.OpenLots(), 8);

            File.AppendAllLines(Path.Combine(dir, "journal_20260929.jsonl"),
                new[] { CloseLine("2026-09-29T08:10:00Z", 111) });
            var book2 = new FxJournalBook(dir, clock: () => DateTimeOffset.UtcNow.AddSeconds(60));
            Assert.Equal(0.07, book2.OpenLots(), 8);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void JournalBook_Matched_Close_Retires_The_Fill_Fully()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dg-jbook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "journal_20260929.jsonl"), new[]
            {
                FillLine("2026-09-29T08:00:00Z", "buy", "0.1", "XAUUSDmicro", 333),
                CloseLine("2026-09-29T08:10:00Z", 333),
            });
            // The close's retirement uses the lots the fill recorded in the
            // same journal: ticket 333 opened 0.1 and its close retires
            // exactly 0.1 — the book reads flat.
            var book = new FxJournalBook(dir, clock: () => DateTimeOffset.UtcNow.AddSeconds(30));
            Assert.Equal(0.0, book.OpenLots(), 8);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void JournalBook_Close_For_An_Unrecorded_Ticket_Retires_The_Minimum()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dg-jbook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A close for a ticket whose fill is unparsable (older format,
            // truncated line): only the conservative minimum retires — the
            // floor errs high, never negative.
            File.WriteAllLines(Path.Combine(dir, "journal_20260929.jsonl"), new[]
            {
                FillLine("2026-09-29T08:00:00Z", "buy", "0.1", "XAUUSDmicro", 444),
                CloseLine("2026-09-29T08:10:00Z", 999),
            });
            var book = new FxJournalBook(dir, clock: () => DateTimeOffset.UtcNow.AddSeconds(30));
            Assert.Equal(0.09, book.OpenLots(), 8);   // 0.1 fill − 0.01 stray close
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void JournalBook_MissingDir_Floors_At_Zero_Without_Throwing()
    {
        var book = new FxJournalBook(Path.Combine(Path.GetTempPath(), $"dg-jbook-none-{Guid.NewGuid():N}"));
        Assert.Equal(0, book.OpenLots(), 8);
    }

    // ── small-account guard ───────────────────────────────────────────

    [Fact]
    public async Task SmallAccount_LargeEquity_Stays_Dormant()
    {
        // 0.01 lot EURUSD at a 15-pip stop risks ~$1.50 (1,000 units ×
        // 0.0015) — well under 5% of the fixture equity. Normal behavior.
        // (On gold-like geometry the same check trips far sooner: contract
        // size is what makes an account "small" for a symbol.)
        var h = new PositionsHandler { SymbolsJson = SmallAccountSymbolsJson };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD" });
        Assert.False(guard.SmallAccountDetected());
        Assert.Null(await guard.VetoAsync("EURUSD", 0.1));
        Assert.Equal(0.1, guard.ClampedLots("EURUSD", 0.1));   // pass-through
    }

    [Fact]
    public async Task SmallAccount_MinLotRisk_Over_FivePercent_Trips_The_Mode()
    {
        // At $20 equity, 5% is $1.00 — the same 0.01-lot trade's ~$1.50
        // risk trips it. No fixed dollar threshold anywhere: the venue's
        // own geometry vs live equity decides.
        var h = new PositionsHandler { SymbolsJson = SmallAccountSymbolsJson, Equity = 20 };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD" });
        Assert.True(guard.SmallAccountDetected());
    }

    [Fact]
    public async Task SmallAccount_OneBrainSlot_BrainOwnedOnly()
    {
        var h = new PositionsHandler
        {
            SymbolsJson = SmallAccountSymbolsJson,
            Equity = 20,
            PositionsJson = $"{{\"positions\":[{Pos("GBPUSD", 0.1, "donggfx")}]}}",
        };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD", "GBPUSD" });
        // A legacy donggfx (non-brain) trade does NOT consume the slot.
        Assert.Null(await guard.VetoAsync("EURUSD", 0.01));
        // The clamp still applies: small mode = venue minimum lot.
        Assert.Equal(0.01, guard.ClampedLots("EURUSD", 0.1));
    }

    [Fact]
    public async Task SmallAccount_Second_Brain_Trade_Refused()
    {
        var h = new PositionsHandler
        {
            SymbolsJson = SmallAccountSymbolsJson,
            Equity = 20,
            PositionsJson = $"{{\"positions\":[{Pos("EURUSD", 0.01, "donggfx-brain")}]}}",
        };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD", "GBPUSD" });
        var veto = await guard.VetoAsync("GBPUSD", 0.01);
        Assert.NotNull(veto);
        Assert.Contains("one brain trade at a time", veto);
    }

    [Fact]
    public void SmallAccount_NoSpecs_Never_Trips_From_Ignorance()
    {
        // No /symbols data at all: "small" cannot be declared from
        // ignorance — normal (unclamped) behavior until geometry says so.
        var h = new PositionsHandler { Equity = 100 };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD" });
        Assert.False(guard.SmallAccountDetected());
        Assert.Equal(0.1, guard.ClampedLots("EURUSD", 0.1));
    }

    [Fact]
    public void SmallAccount_Symbol_Missing_From_Specs_Clamps_To_Engine_Floor()
    {
        // Small mode tripped by EURUSD's geometry; a clamp for a symbol
        // the snapshot doesn't describe falls to the engine-wide 0.01
        // floor — conservative, and never up.
        var h = new PositionsHandler { SymbolsJson = SmallAccountSymbolsJson, Equity = 20 };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD", "USDJPY" });
        Assert.True(guard.SmallAccountDetected());
        Assert.Equal(0.01, guard.ClampedLots("USDJPY", 0.1));
    }

    [Fact]
    public async Task SmallAccount_BridgeDown_FailsClosed_On_The_Veto()
    {
        var h = new PositionsHandler { Down = true };
        var guard = new FxSmallAccountGuard(NewClient(h), new[] { "EURUSD" });
        var veto = await guard.VetoAsync("EURUSD", 0.01);
        Assert.NotNull(veto);
        Assert.Contains("small-account mode", veto);
    }

    // ── news veto ───────────────────────────────────────────────────────

    [Fact]
    public void NewsVeto_Reads_The_Calendar_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dg-nv-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                "{\"events\":[{\"time\":\"2026-09-24T12:30:00Z\",\"impact\":\"high\",\"title\":\"FOMC\"}]}");
            var veto = new FxNewsVeto(() => path, () => TimeSpan.FromMinutes(15));

            var (blackout, reason) = veto.Evaluate(new DateTimeOffset(2026, 9, 24, 12, 30, 0, TimeSpan.Zero));
            Assert.True(blackout);
            Assert.Contains("FOMC", reason);

            var (clear, _) = veto.Evaluate(new DateTimeOffset(2026, 9, 24, 15, 0, 0, TimeSpan.Zero));
            Assert.False(clear);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NewsVeto_ZeroWindow_Never_Blackouts()
    {
        var veto = new FxNewsVeto(() => "/nonexistent/nowhere.json", () => TimeSpan.Zero);
        Assert.False(veto.Evaluate(DateTimeOffset.UtcNow).Blackout);
    }

    [Fact]
    public void NewsVeto_Missing_File_Is_Silent()
    {
        var veto = new FxNewsVeto(
            () => Path.Combine(Path.GetTempPath(), $"dg-gone-{Guid.NewGuid():N}.json"),
            () => TimeSpan.FromMinutes(15));
        Assert.False(veto.Evaluate(DateTimeOffset.UtcNow).Blackout);
    }

    // ── portfolio ───────────────────────────────────────────────────────

    private static TradeJournal NewJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dg-pf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new TradeJournal(dir);
    }

    private static FxPortfolioHost NewPortfolio(PositionsHandler handler, params string[] symbols) =>
        new(
            NewClient(handler), NewJournal(), symbols,
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            governorTripped: () => false,
            dailyLossCap: () => 5000m,
            equityFloor: () => 0m,
            portfolioMaxLots: () => 0.10m,
            webhook: null,
            newsCalendarPath: () => Path.Combine(Path.GetTempPath(), $"dg-none-{Guid.NewGuid():N}.json"),
            newsWindow: () => TimeSpan.FromMinutes(15));

    [Fact]
    public void Portfolio_Builds_One_Host_Per_Symbol_Sharing_One_Supervisor()
    {
        var p = NewPortfolio(new PositionsHandler(), "XAUUSDmicro", "EURUSD", "GBPUSD");
        Assert.Equal(3, p.Hosts.Count);
        Assert.All(p.Hosts, h => Assert.Same(p.Supervisor, h.Supervisor));
        Assert.Equal(3, p.Symbols.Count);
        p.Dispose();
    }

    [Fact]
    public void Portfolio_GoLive_Refuses_Until_Every_Symbol_Soaked()
    {
        var p = NewPortfolio(new PositionsHandler(), "XAUUSDmicro", "EURUSD");
        var messages = new List<string>();
        p.StatusChanged += messages.Add;

        Assert.False(p.PaperSoakComplete);
        p.GoLive();
        Assert.Contains(messages, m => m.Contains("go-live refused"));
        Assert.All(p.Hosts, h => Assert.False(h.IsLiveEngine));
        p.Dispose();
    }

    [Fact]
    public void Portfolio_Aggregate_Soak_Counts_All_Symbols()
    {
        var p = NewPortfolio(new PositionsHandler(), "XAUUSDmicro", "EURUSD");
        Assert.Equal(20, p.PaperSoakSignalsRequired);   // 10 per symbol × 2
        Assert.Equal(0, p.PaperSignalsSeen);
        p.Dispose();
    }

    [Fact]
    public void Soak_Counter_Rule_Signal_In_Paper_Counts_Live_Does_Not()
    {
        // Pins the soak rule: an alpha signal in PAPER advances the counter
        // (that is what GO LIVE demands evidence of); in LIVE the order path
        // is the proof, so the counter must not move.
        FxSignal signal = new("Momentum", FxDirection.Buy, 0.9, 0.0010, "test", 0);
        Assert.True(FxEngineHost.CountsTowardSoak(
            new FxDecision(0, null!, signal, FxDecisionAction.Paper, 0.01, "paper mode"),
            engineIsLive: false));
        Assert.True(FxEngineHost.CountsTowardSoak(
            new FxDecision(0, null!, signal, FxDecisionAction.Ordered, 0.01, "live"),
            engineIsLive: false));
        Assert.False(FxEngineHost.CountsTowardSoak(
            new FxDecision(0, null!, signal, FxDecisionAction.Ordered, 0.01, "live"),
            engineIsLive: true));
        Assert.False(FxEngineHost.CountsTowardSoak(
            new FxDecision(0, null!, null, FxDecisionAction.NoSignal, 0, "nothing"),
            engineIsLive: false));
    }
}
