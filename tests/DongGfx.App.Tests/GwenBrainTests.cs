using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>GWEN — the local Qwen confluence analyst — and the venue-side
/// 1:2. These tests pin the three load-bearing rules:
///
///  1. A concurring verdict dispatches; an opposing one vetoes BEFORE the
///     dispatch cooldown ever stamps (a refused signal is observed, not
///     throttled), and the veto journals as FX_GWEN + FX_ORDER.
///  2. An unavailable model is fail-NEUTRAL: the desk trades exactly as
///     before it existed, announced once in the journal.
///  3. Brain-placed orders carry the armed venue take-profit at 2R — the
///     "actually close to 1:2" guarantee — and drop it when disarmed.
///
/// The client rules ride along in the same suite: loopback-only endpoints,
/// a repeat assessment costs zero inference, and a malformed reply yields
/// a null verdict (never a throw).</summary>
[Trait("Category", "Unit")]
[Collection("Shared-DataDir-Directory")]
public class GwenBrainTests : IDisposable
{
    // The knobs are static (they must reach a RUNNING brain from the
    // Settings toggle); the collection serializes us with the other order
    // placing suites, and Dispose restores whatever they were.
    private readonly double _savedTpR = FxEngineHost.VenueRewardTargetR;
    private readonly bool _savedGwen = FxEngineHost.GwenConfluenceEnabled;

    public void Dispose()
    {
        FxEngineHost.VenueRewardTargetR = _savedTpR;
        FxEngineHost.GwenConfluenceEnabled = _savedGwen;
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    private static TradeJournal NewJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-gwen-tests", Guid.NewGuid().ToString("N"));
        return new TradeJournal(dir);
    }

    /// <summary>A pinned mid-week, mid-session instant (Wednesday 18:00 UTC)
    /// — same reasoning as DemoPaperExecutionTests.NySession: the regime
    /// detector vetoes the thin late session, so wall clock would make every
    /// evening run fail.</summary>
    private static DateTimeOffset NySession() => new(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);

    /// <summary>The model side: a canned Ollama /api/chat responder that
    /// counts posts and records the prompt it was asked.</summary>
    private sealed class GwenScript : HttpMessageHandler
    {
        public int PostCount;
        public string? LastBody;
        public bool Down;

        /// <summary>Stateful on purpose: tests flip the verdict between
        /// cycles (the cache keys on market state, so a flip only lands when
        /// the tape itself changed).</summary>
        public Func<int, string> Reply = _ =>
            "{\"bias\":\"long\",\"confidence\":80,\"rr_ok\":true,\"reasons\":[\"trend aligned\"]}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            PostCount++;
            LastBody = req.Content is null ? null : req.Content.ReadAsStringAsync(ct).Result;
            if (Down)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            var payload = JsonSerializer.Serialize(new { message = new { content = Reply(PostCount) } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Minimal loopback bridge: the routes one engine cycle touches
    /// (account, symbols, candles, ticks, order, positions) with a rising
    /// tape that classifies Trend and speaks a donchian buy.</summary>
    private sealed class BridgeScript : HttpMessageHandler
    {
        public int OrderCalls;
        public string? LastOrderBody;

        /// <summary>Offset applied to every bar stamp — shifting it between
        /// cycles moves the assessment cache key without changing the tape's
        /// structure (same signal, new market state).</summary>
        public long BarTimeShift;

        private static object Bar(int i, long shift) => new
        {
            time = 1_700_000_000L + i * 60 + shift,
            open = 1.10 + i * 0.0004,
            high = 1.10 + i * 0.0004 + 0.0004,
            low = 1.10 + i * 0.0004 - 0.0004,
            close = 1.10 + i * 0.0004 + 0.0002,
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage r;
            if (path.EndsWith("/account"))
            {
                r = Json(new
                {
                    ok = true,
                    login = 201587365,
                    server = "Deriv-Demo",
                    currency = "USD",
                    balance = 2632.19,
                    equity = 2632.19,
                    margin_free = 2632.19,
                    margin = (double?)0,
                    leverage = 1000,
                    trade_mode = 0,        // verified demo
                });
            }
            else if (path.EndsWith("/symbols"))
            {
                r = Json(new
                {
                    symbols = new[]
                    {
                        new
                        {
                            symbol = "XAUUSDmicro", description = "Gold micro",
                            bid = 4344.0, ask = 4344.3, spread_points = 27, digits = 2,
                            trade_mode = 4, volume_min = 0.1, volume_step = 0.1,
                            volume_max = 100.0, contract_size = 1.0,
                        },
                    },
                });
            }
            else if (path.Contains("/candles/"))
            {
                var shift = BarTimeShift;
                r = Json(new { candles = Enumerable.Range(0, 120).Select(i => Bar(i, shift)).ToArray() });
            }
            else if (path.Contains("/ticks/"))
            {
                r = Json(new { bid = 1.15000, ask = 1.15003, time = 1_700_000_000L + 120 * 60 + BarTimeShift });
            }
            else if (path.EndsWith("/order"))
            {
                OrderCalls++;
                LastOrderBody = req.Content is null ? null : req.Content.ReadAsStringAsync(ct).Result;
                r = Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE", deal = 999, price = 1.15002 });
            }
            else if (path.EndsWith("/positions"))
            {
                r = Json(new { positions = Array.Empty<object>() });
            }
            else if (path.EndsWith("/modify"))
            {
                r = Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE" });
            }
            else
            {
                r = Json(new { error = $"no route {path}" }, HttpStatusCode.NotFound);
            }
            return Task.FromResult(r);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    private static FxEngineHost NewHost(BridgeScript script, TradeJournal journal, GwenScript? gwen)
    {
        var mt5 = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        var client = gwen is null
            ? null
            : new GwenBrainClient(gwen, new Uri("http://127.0.0.1:2/"));
        return new FxEngineHost(
            mt5, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            clock: NySession,
            gwen: client)
        {
            // Synthetic tape: fixtures stamp bars in 2023 against a pinned
            // 2026 clock — the feed-freshness guard would hold them stale.
            StaleBarSeconds = 0,
        };
    }

    private static IReadOnlyList<JournalEntry> Entries(TradeJournal journal)
    {
        journal.Flush();
        return journal.GetRecent(null, 300);
    }

    // ── the confluence gate ──────────────────────────────────────────────

    [Fact]
    public async Task Concurring_Verdict_Dispatches_And_Journals_FxGwen()
    {
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var gwen = new GwenScript();
        var host = NewHost(bridge, journal, gwen);

        await host.RunCycleAsync();

        Assert.Equal(1, bridge.OrderCalls);
        Assert.NotNull(host.LastGwenVerdict);
        Assert.Equal(true, host.LastGwenPass);
        Assert.Contains("long", host.LastGwenVerdict!.Bias);
        Assert.Contains("GWEN confluence check", gwen.LastBody, StringComparison.Ordinal);

        var rows = Entries(journal);
        var gwenRow = Assert.Single(rows, e => e.Category == "FX_GWEN");
        Assert.Contains("\"Pass\":true", gwenRow.Details);
        Assert.DoesNotContain(rows, e => e.Details.Contains("GWEN confluence veto"));
    }

    [Fact]
    public async Task Opposing_Verdict_Vetoes_The_Dispatch_But_Still_Counts_The_Soak()
    {
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var gwen = new GwenScript
        {
            Reply = _ => "{\"bias\":\"short\",\"confidence\":91,\"rr_ok\":true,\"reasons\":[\"counter-trend\"]}",
        };
        var host = NewHost(bridge, journal, gwen);

        await host.RunCycleAsync();

        // Observed, journaled, not traded: the signal still reached the
        // engine (soak credit is the engine's, before any host gate), but
        // no order left the desk.
        Assert.Equal(0, bridge.OrderCalls);
        Assert.True(host.PaperSignalsSeen >= 1);
        Assert.Equal(false, host.LastGwenPass);

        var rows = Entries(journal);
        Assert.Contains(rows, e => e.Category == "FX_SIGNAL");            // the signal spoke
        var veto = Assert.Single(rows, e => e.Details.Contains("GWEN confluence veto"));
        Assert.Contains("FX_ORDER", veto.Category);
        var gwenRow = Assert.Single(rows, e => e.Category == "FX_GWEN");
        Assert.Contains("\"Pass\":false", gwenRow.Details);
        Assert.Contains("\"Bias\":\"short\"", gwenRow.Details);
    }

    [Fact]
    public async Task A_Vetoed_Dispatch_Never_Consumes_The_Cooldown_Window()
    {
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var gwen = new GwenScript
        {
            Reply = n => n == 1
                ? "{\"bias\":\"short\",\"confidence\":91,\"rr_ok\":true,\"reasons\":[\"counter-trend\"]}"
                : "{\"bias\":\"long\",\"confidence\":84,\"rr_ok\":true,\"reasons\":[\"aligned now\"]}",
        };
        var host = NewHost(bridge, journal, gwen);

        await host.RunCycleAsync();                       // cycle 1: veto
        Assert.Equal(0, bridge.OrderCalls);

        // Move the tape's stamps: same structure (the alpha still speaks),
        // new market state → the client's cache cannot answer from the
        // veto it stored, so GWEN is asked again and concurs.
        bridge.BarTimeShift = 60;
        await host.RunCycleAsync();                       // cycle 2: concur

        Assert.Equal(1, bridge.OrderCalls);
        var rows = Entries(journal);
        // Had the veto stamped the cooldown, cycle 2 would have been
        // throttled before reaching the venue — the throttle line is the
        // proof it wasn't.
        Assert.DoesNotContain(rows, e => e.Details.Contains("dispatch throttled"));
    }

    [Fact]
    public async Task Unavailable_Model_Fails_Neutral_And_Is_Announced_Once()
    {
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var gwen = new GwenScript { Down = true };
        var host = NewHost(bridge, journal, gwen);

        await host.RunCycleAsync();
        await host.RunCycleAsync();

        // The desk trades exactly as it did before the brain existed.
        Assert.Equal(1, bridge.OrderCalls);   // cycle 2 is the normal cooldown throttle
        Assert.Null(host.LastGwenVerdict);

        var rows = Entries(journal);
        Assert.Single(rows, e => e.Details.Contains("GWEN unavailable"));
        Assert.Contains(rows, e => e.Details.Contains("dispatch throttled"));
    }

    [Fact]
    public async Task Disarming_The_Gate_Skips_The_Model_Entirely()
    {
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var gwen = new GwenScript();
        FxEngineHost.GwenConfluenceEnabled = false;
        var host = NewHost(bridge, journal, gwen);

        await host.RunCycleAsync();

        Assert.Equal(1, bridge.OrderCalls);
        Assert.Equal(0, gwen.PostCount);          // never even asked
        Assert.False(host.GwenArmed);
        Assert.Null(host.LastGwenVerdict);
        Assert.DoesNotContain(Entries(journal), e => e.Category == "FX_GWEN");
    }

    // ── the venue-side 1:2 ───────────────────────────────────────────────

    [Fact]
    public async Task Brain_Orders_Carry_The_Venue_TakeProfit_At_2R()
    {
        FxEngineHost.VenueRewardTargetR = 2.0;
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var host = NewHost(bridge, journal, gwen: null);   // no gate: isolate the TP

        await host.RunCycleAsync();
        Assert.Equal(1, bridge.OrderCalls);

        using var doc = JsonDocument.Parse(bridge.LastOrderBody!);
        var body = doc.RootElement;
        Assert.True(body.TryGetProperty("tp", out var tpEl), $"order body lacks tp: {bridge.LastOrderBody}");
        Assert.True(body.TryGetProperty("sl", out var slEl), $"order body lacks sl: {bridge.LastOrderBody}");

        var tp = tpEl.GetDouble();
        var sl = slEl.GetDouble();
        const double bid = 1.15000;                 // the buy's anchor
        Assert.True(tp > bid, "buy TP must sit above the anchor");
        Assert.True(sl < bid, "buy SL must sit below the anchor");
        // The reward leg is exactly 2x the risk leg — the 1:2 rides ON the
        // order, filled by the venue even with the app closed.
        Assert.Equal(2.0 * (bid - sl), tp - bid, precision: 5);

        var row = Assert.Single(Entries(journal), e => e.Category == "FX_ORDER");
        Assert.Contains("\"TpR\":2", row.Details);
        Assert.Contains("1:2", row.Details);
    }

    [Fact]
    public async Task Disarming_The_Venue_Target_Drops_The_Tp_From_The_Order()
    {
        FxEngineHost.VenueRewardTargetR = 0;
        var journal = NewJournal();
        var bridge = new BridgeScript();
        var host = NewHost(bridge, journal, gwen: null);

        await host.RunCycleAsync();
        Assert.Equal(1, bridge.OrderCalls);

        using var doc = JsonDocument.Parse(bridge.LastOrderBody!);
        Assert.False(doc.RootElement.TryGetProperty("tp", out _),
            $"disarmed order must carry no tp: {bridge.LastOrderBody}");

        var row = Assert.Single(Entries(journal), e => e.Category == "FX_ORDER");
        Assert.Contains("\"TpR\":null", row.Details);
    }

    // ── client rules ─────────────────────────────────────────────────────

    private static FxGwenRequest Req(long barTime = 1_700_000_000L) => new(
        Symbol: "EURUSD", Timeframe: "M1", Session: "london", Regime: "Trend",
        Adx: 27, AtrPct: 0.05, SpreadPoints: 6, Side: "buy", Alpha: "breakout",
        AlphaConfidence: 0.7, StopPips: 9, TargetR: 2,
        Bars: new List<FxBar>
        {
            new(barTime, 1.0831, 1.0834, 1.0829, 1.0832, 10),
        });

    [Fact]
    public async Task Repeat_Assessments_Cost_Zero_Inference()
    {
        var gwen = new GwenScript();
        using var client = new GwenBrainClient(gwen, new Uri("http://127.0.0.1:2/"));

        var first = await client.AssessAsync(Req());
        var second = await client.AssessAsync(Req());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
        Assert.Equal(1, gwen.PostCount);           // cached

        // A different bar time = a different market state = a fresh ask.
        _ = await client.AssessAsync(Req(barTime: 1_700_000_060L));
        Assert.Equal(2, gwen.PostCount);
    }

    [Fact]
    public async Task Malformed_Reply_Yields_Null_And_Is_Never_Cached()
    {
        var gwen = new GwenScript { Reply = _ => "sorry, I cannot answer as JSON" };
        using var client = new GwenBrainClient(gwen, new Uri("http://127.0.0.1:2/"));

        Assert.Null(await client.AssessAsync(Req()));
        Assert.Null(await client.AssessAsync(Req()));
        Assert.Equal(2, gwen.PostCount);           // a null verdict is not an answer
    }

    [Fact]
    public async Task Non_Loopback_Endpoints_Are_Refused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new GwenBrainClient(new GwenScript(), new Uri("https://api.openai.com/")));
        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Transport_Errors_Are_Null_Verdicts_Not_Throws()
    {
        var gwen = new GwenScript { Down = true };
        using var client = new GwenBrainClient(gwen, new Uri("http://127.0.0.1:2/"));
        Assert.Null(await client.AssessAsync(Req()));

        // Connection-refused (the ollama server not running): the injected
        // handler IS the transport, so the refusal is simulated by one that
        // throws exactly what SocketsHttpHandler throws on a refused dial.
        using var dead = new GwenBrainClient(new RefusedScript(), new Uri("http://127.0.0.1:9/"));
        Assert.Null(await dead.AssessAsync(Req()));
    }

    /// <summary>A transport that fails the way a loopback connection to a
    /// stopped Ollama fails.</summary>
    private sealed class RefusedScript : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
