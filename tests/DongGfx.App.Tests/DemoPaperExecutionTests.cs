using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The demo account IS the paper account: paper signals execute as real
/// MT5 orders on the connected demo venue. These tests pin the three
/// load-bearing rules — verified-demo-only execution, the full order path
/// still applies, and soak counting unchanged.
/// </summary>
[Trait("Category", "Unit")]
[Collection("Shared-DataDir-Directory")]
public class DemoPaperExecutionTests
{
    private static TradeJournal NewJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-paper-exec-tests", Guid.NewGuid().ToString("N"));
        return new TradeJournal(dir);
    }

    /// <summary>Bridge script: candles 120 bars (donchian-respecting tape),
    /// demo (or overridden) account, and an order recorder.</summary>
    private sealed class BridgeScript : HttpMessageHandler
    {
        public int OrderCalls;
        public string? LastOrderBody;
        public bool AccountUnverified;
        public bool AccountReal;
        public bool AccountMissing;

        /// <summary>The /account used-margin value the flat-proof gate reads
        /// (book_recovery.ps1: margin == 0 is part of "flat"). null (the
        /// default) serializes as JSON null → the client reads an absent
        /// margin → every proven-flat prune fails CLOSED, exactly like an
        /// older sidecar. Tests that want the prune to prove set it to 0.</summary>
        public double? AccountMargin;
        public bool TicksDown;

        /// <summary>The served tick — mutable so a test can move price
        /// BETWEEN a cycle and a floor-watch tick (the between-cycle
        /// shoot-through the watch exists for).</summary>
        public double TickBid = 1.15000;
        public double TickAsk = 1.15003;
        public object[] Positions = Array.Empty<object>();
        public int CloseCalls;
        public int ModifyCalls;
        public string? LastClosePath;

        /// <summary>The candle tape the regime detector reads. Default: a
        /// rising walk (closes 1.10 → 1.15) that classifies Trend — the
        /// STRUCTURE_TRAIL path. Tests needing the HYBRID path set a flat
        /// tape via FlatTape().</summary>
        public object[] Bars = Enumerable.Range(0, 120).Select(i => (object)new
        {
            time = 1_700_000_000L + i * 60,
            open = 1.10 + i * 0.0004,
            high = 1.10 + i * 0.0004 + 0.0004,
            low = 1.10 + i * 0.0004 - 0.0004,
            close = 1.10 + i * 0.0004 + 0.0002,
        }).ToArray();

        /// <summary>Replace the default rising (Trend) tape with a flat,
        /// low-volatility tape: no donchian breakout, sub-threshold ADX —
        /// the regime falls through to hybrid trailing.</summary>
        public void FlatTape(double mid = 1.15, double amp = 0.0004)
        {
            Bars = Enumerable.Range(0, 120).Select(i => (object)new
            {
                time = 1_700_000_000L + i * 60,
                open = mid + Math.Sin(i * 0.5) * amp,
                high = mid + Math.Sin(i * 0.5) * amp + amp / 2,
                low = mid + Math.Sin(i * 0.5) * amp - amp / 2,
                close = mid + Math.Sin(i * 0.5) * amp + amp / 4,
            }).ToArray();
        }

        /// <summary>When false, /close refuses (the retry-ladder test).</summary>
        public bool CloseOk = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage r;
            if (path.EndsWith("/account"))
            {
                r = AccountMissing
                    ? Json(new { error = "no account" }, HttpStatusCode.NotFound)
                    : Json(new
                    {
                        ok = true,
                        login = 201587365,
                        server = "Deriv-Demo",
                        currency = "USD",
                        balance = 2632.19,
                        equity = 2632.19,
                        margin_free = 2632.19,
                        margin = AccountMargin,
                        leverage = 1000,
                        trade_mode = AccountReal ? 2 : AccountUnverified ? 1 : 0,
                    });
            }
            else if (path.EndsWith("/symbols"))
            {
                r = Json(new { symbols = new[]
                {
                    new { symbol = "XAUUSDmicro", description = "Gold micro", bid = 4344.0, ask = 4344.3,
                          spread_points = 27, digits = 2, trade_mode = 4,
                          volume_min = 0.1, volume_step = 0.1, volume_max = 100.0, contract_size = 1.0 },
                } });
            }
            else if (path.Contains("/candles/"))
            {
                // Rising tape: 120 bars, closes 1.10 → 1.15 — a clean
                // donchian-breakout walk so the brain has a signal to speak.
                r = Json(new { candles = Bars });
            }
            else if (path.Contains("/ticks/"))
            {
                r = TicksDown
                    ? Json(new { error = "no tick" }, HttpStatusCode.NotFound)
                    : Json(new { bid = TickBid, ask = TickAsk, time = 1_700_000_000L + 120 * 60 });
            }
            else if (path.EndsWith("/order"))
            {
                OrderCalls++;
                LastOrderBody = req.Content is null ? null : req.Content.ReadAsStringAsync(ct).Result;
                r = Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE", deal = 999, price = 1.15002 });
            }
            else if (path.EndsWith("/positions"))
            {
                r = Json(new { positions = Positions });
            }
            else if (path.StartsWith("/close/"))
            {
                CloseCalls++;
                LastClosePath = path;
                r = CloseOk
                    ? Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE", deal = 777 })
                    : Json(new { ok = false, retcode = 10019, retcode_name = "TRADE_RETCODE_NO_MONEY", deal = (long?)null });
            }
            else if (path.EndsWith("/modify"))
            {
                ModifyCalls++;
                r = Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE" });
            }
            else
            {
                r = Json(new { error = $"no route {path}" }, HttpStatusCode.NotFound);
            }

            return Task.FromResult(r);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK)
        {
            var m = new HttpResponseMessage(code)
            {
                Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json"),
            };
            return m;
        }
    }

    private static FxEngineHost NewHost(BridgeScript script, TradeJournal journal,
                                        PaperSoakLedger? soakLedger = null,
                                        string? shadowLedgerPath = null)
    {
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        return new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            shadowLedgerPath: shadowLedgerPath,
            soakLedger: soakLedger,
            clock: NySession)
        {
            // Synthetic tape: these fixtures stamp bars in 2023 against a
            // pinned 2026 clock, which the feed-freshness guard would hold
            // as stale. Freshness has its own dedicated tests.
            StaleBarSeconds = 0,
        };
    }

    /// <summary>A fixed mid-week, mid-session instant: Wednesday 18:00 UTC.
    /// The regime detector vetoes the thin "late" UTC session (21:00–24:00),
    /// so a wall-clock-driven cycle would fail every evening — the clock is
    /// pinned where the rules say liquidity is deep.</summary>
    private static DateTimeOffset NySession() => new(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);

    // ── feed freshness (the weekend / closed-market guard) ────────────────
    //
    // The dead-tape ATR floor reads the FROZEN history of a stopped book,
    // so a quiet-but-not-dead Friday close keeps clearing it forever. Bar
    // AGE against wall clock is the only honest "is this tape live" test:
    // observed 2026-10-03 (Saturday) when gold cleared the floor at ATR%
    // 0.0248 and re-fired the same signal 55 times on a closed market.

    /// <summary>Same tape read as fresh: wall clock sits 30s past the last
    /// bar, exactly as a live forming M1 bar looks.</summary>
    private static DateTimeOffset FreshClock()
    {
        var lastBarSeconds = 1_700_000_000L + 119 * 60;   // BridgeScript.Bars
        return DateTimeOffset.FromUnixTimeSeconds(lastBarSeconds + 30);
    }

    /// <summary>A host with the feed-freshness guard ACTIVE, on the pinned
    /// 2026 clock — its bars are stamped 2023, so the tape reads stale.
    /// NewHost deliberately disables the guard for the synthetic fixtures
    /// around it; this is the one that exercises it.</summary>
    private static FxEngineHost NewGuardedHost(BridgeScript script, TradeJournal journal,
                                                PaperSoakLedger? ledger = null)
    {
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        return new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            soakLedger: ledger,
            clock: NySession);
    }

    [Fact]
    public async Task Stale_Feed_Holds_Entries_And_Credits_No_Soak()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var ledger = new PaperSoakLedger(
            Path.Combine(Path.GetTempPath(), "dg-fresh-" + Guid.NewGuid().ToString("N"), "soak.json"),
            "test-scope");
        var host = NewGuardedHost(script, journal, ledger);   // guard ON (300s)

        await host.RunCycleAsync();
        await host.RunCycleAsync();

        // The brain never ran: no decision, no signal, no order.
        Assert.Null(host.LastDecision);
        Assert.Equal(0, script.OrderCalls);

        // And the GO LIVE bar is untouched — a closed market is not
        // evidence that anything behaved in paper.
        Assert.Equal(0, host.PaperSignalsSeen);
        Assert.Equal(0, ledger.Seen("XAUUSDmicro"));

        journal.Flush();
        var entries = journal.GetRecent(null, 200);
        Assert.DoesNotContain(entries, e => e.Category == "FX_SIGNAL");
        Assert.DoesNotContain(entries, e => e.Category == "FX_DECISION");

        // Held once, not once per cycle — a frozen book must not spam.
        Assert.Single(entries, e => e.Details.Contains("holding entries — feed stale"));
    }

    [Fact]
    public async Task Fresh_Feed_Runs_And_Credits_Soak_As_Before()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var ledger = new PaperSoakLedger(
            Path.Combine(Path.GetTempPath(), "dg-fresh-" + Guid.NewGuid().ToString("N"), "soak.json"),
            "test-scope");
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        var host = new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            soakLedger: ledger,
            clock: FreshClock);   // bars 30s old: a live tape, guard ON

        await host.RunCycleAsync();

        // Fresh tape behaves exactly as it did before the guard existed.
        Assert.NotNull(host.LastDecision);
        Assert.Equal(1, script.OrderCalls);
        Assert.True(host.PaperSignalsSeen >= 1);

        journal.Flush();
        var entries = journal.GetRecent(null, 200);
        Assert.DoesNotContain(entries, e => e.Details.Contains("holding entries — feed stale"));
    }

    [Fact]
    public async Task Feed_That_Freshens_Resume_Once()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewGuardedHost(script, journal);   // guard ON, tape is 2023

        await host.RunCycleAsync();

        // The venue starts publishing again (here: the operator disables the
        // threshold to model bars catching up). One recovery line, then work.
        host.StaleBarSeconds = 0;
        await host.RunCycleAsync();

        journal.Flush();
        var entries = journal.GetRecent(null, 200);
        Assert.Single(entries, e => e.Details.Contains("holding entries — feed stale"));
        Assert.Single(entries, e => e.Details.Contains("feed fresh — newest"));
        Assert.NotNull(host.LastDecision);
    }

    [Fact]
    public void PaperExecutionAllowed_Requires_VerifiedDemo()
    {
        // The rule, pinned without plumbing: true passes; false (verified
        // real) and null (unverified) both fail closed.
        Assert.True(FxEngineHost.PaperExecutionAllowed(true));
        Assert.False(FxEngineHost.PaperExecutionAllowed(false));
        Assert.False(FxEngineHost.PaperExecutionAllowed(null));
    }

    [Fact]
    public async Task PaperSignal_Executes_On_VerifiedDemo()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        Assert.NotNull(host.LastDecision);
        Assert.Equal(FxDecisionAction.PaperExecuted, host.LastDecision!.Action);
        Assert.Equal(1, script.OrderCalls);
        Assert.Contains("\"lots\"", script.LastOrderBody);
        Assert.Contains("\"action\":\"buy\"", script.LastOrderBody);

        // The fill is auditable: PAPER-EXEC decision line, the order line
        // with its ticket, and the PaperExec tag in the order JSON.
        journal.Flush();
        var entries = journal.GetRecent(null, 200);
        Assert.Contains(entries, e =>
            e.Category == "FX_DECISION" && e.Details.Contains("PAPER-EXEC"));
        Assert.Contains(entries, e =>
            e.Category == "FX_ORDER" && e.Details.Contains("ticket 999"));
        Assert.Contains(entries, e =>
            e.Category == "FX_ORDER" && e.Details.Contains("\"PaperExec\":true"));
    }

    [Fact]
    public async Task PaperSignal_Refused_On_UnverifiedAccount()
    {
        var journal = NewJournal();
        var script = new BridgeScript { AccountUnverified = true };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        // Contest/unverified venue: the paper signal still fires (paper
        // mode is still on) but the fill is refused fail-closed.
        Assert.NotNull(host.LastDecision);
        Assert.Equal(FxDecisionAction.PaperExecuted, host.LastDecision!.Action);
        Assert.Equal(0, script.OrderCalls);
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200), e =>
            e.Category == "FX_ORDER" && e.Details.Contains("requires a VERIFIED demo"));
    }

    [Fact]
    public async Task PaperSignal_Refused_On_VerifiedRealAccount()
    {
        var journal = NewJournal();
        var script = new BridgeScript { AccountReal = true };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        Assert.Equal(0, script.OrderCalls);
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200), e =>
            e.Category == "FX_ORDER" && e.Details.Contains("connected account is REAL"));
    }

    [Fact]
    public async Task PaperSignal_Refused_When_BridgeDown()
    {
        var journal = NewJournal();
        var script = new BridgeScript { AccountMissing = true };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        // No verified account → no paper fill, ever.
        Assert.Equal(0, script.OrderCalls);
    }

    [Fact]
    public void SoakCounting_Unchanged_By_DemoExecution()
    {
        FxSignal signal = new("Momentum", FxDirection.Buy, 0.9, 0.0010, "test", 0);
        // Paper-exec still counts: the signal spoke while in paper.
        Assert.True(FxEngineHost.CountsTowardSoak(
            new FxDecision(0, null!, signal, FxDecisionAction.PaperExecuted, 0.01, "demo exec"),
            engineIsLive: false));
        // Live proof stays execution-counted, not soak-counted.
        Assert.False(FxEngineHost.CountsTowardSoak(
            new FxDecision(0, null!, signal, FxDecisionAction.Ordered, 0.01, "live"),
            engineIsLive: true));
    }

    [Fact]
    public async Task PaperSoak_Survives_A_Restart_And_Resets_On_A_New_Build()
    {
        // The GO LIVE bar counts per symbol and is all-or-nothing across the
        // portfolio, so a session that closed at 9/10 on one symbol used to
        // throw the whole book's evidence away. The counters now live in a
        // ledger scoped to the build.
        var dir = Path.Combine(Path.GetTempPath(), "dg-paper-soak",
            Guid.NewGuid().ToString("N"));
        var path = PaperSoakLedger.PathFor(dir);
        var journal = NewJournal();
        try
        {
            // Session one: a paper signal observed and persisted.
            var first = NewHost(new BridgeScript(), journal,
                new PaperSoakLedger(path, "build-a"));
            await first.RunCycleAsync();
            Assert.Equal(1, first.PaperSignalsSeen);
            Assert.Equal(1, new PaperSoakLedger(path, "build-a").Seen("XAUUSDmicro"));

            // Session two, as after an app restart: restored, then extended.
            var second = NewHost(new BridgeScript(), journal,
                new PaperSoakLedger(path, "build-a"));
            Assert.Equal(1, second.PaperSignalsSeen);
            await second.RunCycleAsync();
            Assert.Equal(2, second.PaperSignalsSeen);

            // The restore is visible in the journal, not just in the badge.
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 300), e =>
                e.Category == "FX_MODE" && e.Details.Contains("paper soak restored 1/10"));

            // A different build inherits nothing — the bar is evidence about
            // THIS engine — and the drop is journaled rather than silent.
            var rebuilt = NewHost(new BridgeScript(), journal,
                new PaperSoakLedger(path, "build-b"));
            Assert.Equal(0, rebuilt.PaperSignalsSeen);
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 300), e =>
                e.Category == "FX_MODE" && e.Details.Contains("build changed since build-a"));

            // No ledger at all (bare/headless hosts): counting is unchanged.
            var bare = NewHost(new BridgeScript(), journal);
            Assert.Equal(0, bare.PaperSignalsSeen);
            await bare.RunCycleAsync();
            Assert.Equal(1, bare.PaperSignalsSeen);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Order_Carries_The_Sized_Stop_As_Sl()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        Assert.Equal(1, script.OrderCalls);
        Assert.NotNull(script.LastOrderBody);
        // Buy at mid ≈ 1.150015; the sized stop is the engine's structural
        // floor — 1.5×ATR(14) of the rising tape ≈ 0.0012 (far above the
        // hint) → SL ≈ 1.1488, comfortably below entry and clear of the
        // venue's forbidden band.
        Assert.Contains("\"sl\":1.148", script.LastOrderBody);
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200),
            e => e.Category == "FX_ORDER" && e.Details.Contains("\"Sl\":1.148"));
    }

    [Fact]
    public async Task Order_Journals_The_Structural_R_Unit_And_Seeds_The_Exit_Ruler()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        // The FX_ORDER row carries the R unit sizing actually used, so a
        // restart can re-seed the exit brain's ruler from the journal.
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200), e =>
            e.Category == "FX_ORDER" && e.Details.Contains("\"SizedStopDistance\":0.001"));

        // The static reader reproduces the unit from the journal alone —
        // the restart-reseed path for a ticket the process has forgotten.
        var dir = journal.JournalDir;
        Assert.Equal(0.0012, FxEngineHost.SizedStopFromJournal(dir, 999), 9);
        Assert.Equal(0, FxEngineHost.SizedStopFromJournal(dir, 42));
    }

    [Fact]
    public async Task Order_Refused_FailClosed_When_No_Tick_For_The_Stop()
    {
        var journal = NewJournal();
        var script = new BridgeScript { TicksDown = true };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        // Without a tick there is no honest stop: an unprotected order
        // never leaves the app.
        Assert.Equal(0, script.OrderCalls);
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200), e =>
            e.Category == "FX_ORDER" && e.Details.Contains("no usable tick/stop distance"));
    }

    [Fact]
    public void OrderCooldown_Rule_Pins()
    {
        var now = DateTimeOffset.UtcNow;
        // Never dispatched → allowed.
        Assert.False(FxEngineHost.OrderCooldownActive(null, now, TimeSpan.FromMinutes(5)));
        // Recent attempt → throttled.
        Assert.True(FxEngineHost.OrderCooldownActive(now - TimeSpan.FromSeconds(30), now, TimeSpan.FromMinutes(5)));
        // Old attempt → allowed again.
        Assert.False(FxEngineHost.OrderCooldownActive(now - TimeSpan.FromMinutes(6), now, TimeSpan.FromMinutes(5)));
        // Exactly at the boundary → allowed (strictly-less-than comparison).
        Assert.False(FxEngineHost.OrderCooldownActive(now - TimeSpan.FromMinutes(5), now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Cooldown_Stops_Hammering_But_Keeps_Signals()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewHost(script, journal);

        await host.RunCycleAsync();
        Assert.Equal(1, script.OrderCalls);

        // Immediate second cycle: the signal may fire again (soak counts
        // it — nothing below suppresses the brain), but the dispatch is
        // throttled inside the 5-minute window.
        await host.RunCycleAsync();
        Assert.Equal(1, script.OrderCalls);
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200), e =>
            e.Category == "FX_ORDER" && e.Details.Contains("dispatch throttled"));

        // Cooldown elapsed (tests set it to zero): dispatch flows again.
        host.OrderCooldown = TimeSpan.Zero;
        await host.RunCycleAsync();
        Assert.Equal(2, script.OrderCalls);
    }

    [Fact]
    public async Task Confidence_Gate_Refuses_Dispatch_But_Keeps_Signal_And_Soak()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewHost(script, journal);
        try
        {
            // Floor above the fixture signal's conviction → the signal is
            // observed (journaled + soaked) but never dispatched, and the
            // refusal must NOT consume the per-symbol dispatch cooldown.
            FxEngineHost.MinEntryConfidence = 0.99;
            await host.RunCycleAsync();

            Assert.Equal(0, script.OrderCalls);
            Assert.Equal(1, host.PaperSignalsSeen);   // soak still counts

            journal.Flush();
            var entries = journal.GetRecent(null, 200);
            Assert.Contains(entries, e => e.Category == "FX_SIGNAL");
            Assert.Contains(entries, e =>
                e.Category == "FX_ORDER" && e.Details.Contains("confidence gate"));
            Assert.DoesNotContain(entries, e =>
                e.Category == "FX_ORDER" && e.Details.Contains("dispatch throttled"));

            // Gate off (the default) → the same fixture dispatches on the
            // very next cycle: a gated refusal never armed the cooldown.
            FxEngineHost.MinEntryConfidence = 0;
            await host.RunCycleAsync();
            Assert.Equal(1, script.OrderCalls);
        }
        finally
        {
            FxEngineHost.MinEntryConfidence = 0;   // never leak into other tests
        }
    }

    [Fact]
    public async Task Cooldown_Counts_Refused_Attempts_Too()
    {
        // A refused attempt (AutoTrading off → client-disabled) must also
        // arm the cooldown: re-sending into a refusing venue every cycle
        // is exactly the journal-spam this feature exists to stop.
        var journal = NewJournal();
        var script = new BridgeScript { AccountUnverified = true };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();
        Assert.Equal(0, script.OrderCalls);

        await host.RunCycleAsync();
        journal.Flush();
        var orderEntries = journal.GetRecent(null, 200)
            .Where(e => e.Category == "FX_ORDER").ToList();
        // First cycle: the demo-verification refusal. Second cycle: the
        // cooldown, NOT a second verification refusal.
        Assert.Equal(1, orderEntries.Count(e => e.Details.Contains("requires a VERIFIED demo")));
        Assert.Contains(orderEntries, e => e.Details.Contains("dispatch throttled"));
    }

    [Fact]
    public async Task KillSwitch_Also_Blocks_PaperExec()
    {
        var journal = NewJournal();
        var script = new BridgeScript();
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        var host = new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => true,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            clock: NySession)
        {
            StaleBarSeconds = 0,
        };   // synthetic tape — see NewHost

        await host.RunCycleAsync();

        // The demo-execution path runs the same rails as live: a kill
        // switch blocks the fill even on a verified demo.
        Assert.Equal(0, script.OrderCalls);
    }

    [Fact]
    public async Task LocalBook_Survives_DegradedEmpty_PositionReads()
    {
        // The 08:21-09:06 cap-failure mechanism, pinned: cycle 1 seeds the
        // book from a real positions read; cycle 2's reads degrade to EMPTY
        // and agree on the lie — the book must NOT wipe, because the guards
        // floor exposure at it.
        var journal = NewJournal();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                // Near entry (mae ~0.1R): the brain must HOLD it, so the
                // book survives cycle 1 and can be tested for wiping.
                new { ticket = 111L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1475, profit = -5.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var host = NewHost(script, journal);
        await host.RunCycleAsync();
        var seeded = host.LocalBookLots;
        Assert.True(seeded > 0, "the book must seed from a real read");

        script.Positions = Array.Empty<object>();   // degraded-empty both reads
        await host.RunCycleAsync();
        Assert.Equal(seeded, host.LocalBookLots);   // the lie must not wipe it

        // A healthy NON-empty read that agrees the ticket is gone prunes:
        // a different brain ticket seeds, the vanished one is dropped.
        script.Positions = new object[]
        {
            new { ticket = 555L, symbol = "XAUUSDmicro", side = "sell", volume = 0.1,
                  price_open = 1.1480, price_current = 1.1475, profit = 5.0,
                  sl = 1.1500, tp = 0.0, comment = "donggfx-brain" },
        };
        await host.RunCycleAsync();
        Assert.Equal(0.1, host.LocalBookLots);
    }

    [Fact]
    public async Task ProvenFlat_Prune_Retires_Stale_Book_And_Journals_The_Closed_Line()
    {
        // The host-book limitation (2026-10-06): the prune below needed
        // owned.Count > 0, so after the venue dropped a ticket the tracking
        // state stranded FOREVER while flat — LocalBookLots, and with it the
        // exposure floor, pinned at a position nobody holds until the
        // ops-layer reconcile + restart. On a PROVEN-flat venue (here: a
        // healthy non-empty read lacking our ticket) the stale entry must
        // retire AND the journal must get the "closed #" line FxJournalBook
        // parses — no ops script in the loop.
        var journal = NewJournal();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                // Near entry (mae ~0.1R): the brain HOLDs it, so cycle 1
                // seeds the book without closing anything.
                new { ticket = 111L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1475, profit = -5.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var host = NewHost(script, journal);
        await host.RunCycleAsync();
        Assert.True(host.LocalBookLots > 0, "the book must seed from a real read");

        // Cycle 2: the ticket vanished (SL hit — no app-driven close, so no
        // close line exists yet) while a SIBLING row keeps the read
        // non-empty: absence from a healthy non-empty read IS proof (the
        // same rule the confirmation loop uses), so the never-submitted
        // guard and the tracking state retire TOGETHER and the journal
        // gets its close line — in one cycle. (Before the 2026-10-07 fix
        // the position's own guard blocked this very prune: tracking
        // survived until a later flat cycle — and on a fully flat venue,
        // forever: #8792100270, 35 minutes stranded until a restart.)
        script.Positions = new object[]
        {
            new { ticket = 901L, symbol = "EURUSD", side = "buy", volume = 0.1,
                  price_open = 1.1500, price_current = 1.1505, profit = 5.0,
                  sl = 0.0, tp = 0.0, comment = "" },
        };
        await host.RunCycleAsync();
        Assert.Equal(0.0, host.LocalBookLots);
        journal.Flush();
        var closed = journal.GetRecent(null, 400).Where(e =>
            e.Category == "FX_EXIT" && e.Details.Contains("closed #111")).ToList();
        Assert.Single(closed);
        // Settled-R recording (spec §1): the retire carries entry vs this
        // cycle's mid over the sized stop — never invented, never absent.
        Assert.Contains("RealizedR", closed[0].Details);
        Assert.Contains("profit-snapshot", closed[0].Details);

        // Idempotent: further flat cycles must not duplicate the close row.
        await host.RunCycleAsync();
        journal.Flush();
        Assert.Single(journal.GetRecent(null, 400).Where(e =>
            e.Category == "FX_EXIT" && e.Details.Contains("closed #111")));
    }

    [Fact]
    public async Task Flat_Prune_Fails_Closed_Without_A_Settled_Account()
    {
        // The empty-pair lie: two agreeing EMPTY position reads can still
        // be the congestion degrade that the local book exists to survive
        // (2026-09-29: /account probed fine while /positions degraded). So
        // case (b) also demands the account independently say flat — equity
        // ≈ balance AND margin == 0, the ops venue_is_flat gate. An absent
        // or non-zero margin must NEVER prune; margin 0 (with equity ==
        // balance) proves it.
        var journal = NewJournal();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                new { ticket = 111L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1475, profit = -5.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var host = NewHost(script, journal);
        await host.RunCycleAsync();
        Assert.True(host.LocalBookLots > 0, "the book must seed");

        // Two agreeing EMPTY reads — but the account reports a LIVE margin:
        // the venue is not flat by the ops gate. The book must NOT wipe,
        // and the position's own never-submitted guard must survive too
        // (unproven = touch nothing — no removals, no close line).
        script.Positions = Array.Empty<object>();
        script.AccountMargin = 5.0;
        await host.RunCycleAsync();
        Assert.Equal(0.1, host.LocalBookLots);

        // Margin 0 + equity == balance (the stub's account): proven flat.
        // This is the #8792100270 deadlock shape (2026-10-07: a never-
        // submitted guard blocked its own ticket's prune on a flat venue
        // for 35 minutes — the guard blocked the flat branch while the
        // confirmation loop only ever fired for submitted guards). Now
        // the proven sweep clears the guard AND retires the stale entry,
        // and the journal gets its close row.
        script.AccountMargin = 0.0;
        await host.RunCycleAsync();
        Assert.Equal(0.0, host.LocalBookLots);
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_EXIT" && e.Details.Contains("closed #111"));
    }

    [Fact]
    public void Reentrant_Cycle_Skip_Is_Journaled_Not_Silent()
    {
        // 2026-09-29: a wedged cycle swallowed every timer tick for an
        // hour with zero journal trace. The guard must now SHOUT when it
        // refuses a cycle.
        var journal = NewJournal();
        var host = NewHost(new BridgeScript(), journal);

        Assert.True(host.TryBeginCycle("timer"));   // free → runs

        typeof(FxEngineHost).GetField("_cycleRunning",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, true);                       // a cycle in flight
        Assert.False(host.TryBeginCycle("timer"));   // must refuse…
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 200),
            e => e.Category == "FX_CYCLE" && e.Details.Contains("cycle skipped (timer)"));
    }

    [Fact]
    public async Task ExitBrain_Manages_Owned_Positions_And_Leaves_Others_Alone()
    {
        var journal = NewJournal();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                // Ours: brain-stamped long_crushed far past the emergency
                // bar -> the drawdown override must close it in full.
                new { ticket = 111L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1400, profit = -80.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
                // Not ours: a manual position in even worse shape — the
                // brain must never touch what it did not open.
                new { ticket = 222L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1380, profit = -100.0,
                      sl = 0.0, tp = 0.0, comment = "manual" },
                // Stamped but on ANOTHER symbol: only this host's own
                // symbol's positions may be evaluated — a foreign-symbol
                // ATR as risk yardstick produced nonsense MAE (207R) and a
                // bogus emergency close (2026-09-28 live incident).
                new { ticket = 333L, symbol = "EURUSD", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1400, profit = -80.0,
                      sl = 0.0, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        var host = new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            equityFloor: () => 0m)
        {
            StaleBarSeconds = 0,
        };   // synthetic tape — see NewHost

        await host.RunCycleAsync();

        // Exactly one close — ours, in full — and no stop modify.
        Assert.Equal(1, script.CloseCalls);
        Assert.Equal("/close/111", script.LastClosePath);
        Assert.Equal(0, script.ModifyCalls);

        // The stamp rides on entries, so the brain can find its children
        // on the next cycle.
        if (script.OrderCalls > 0)
        {
            Assert.Contains("donggfx-brain", script.LastOrderBody);
        }

        // The settlement trail: FX_EXIT only for the owned, own-symbol ticket.
        journal.Flush();
        var entries = journal.GetRecent(null, 200);
        var exitLines = entries.Where(e => e.Category == "FX_EXIT").ToList();
        Assert.Contains(exitLines, e => e.Details.Contains("#111"));
        Assert.DoesNotContain(exitLines, e => e.Details.Contains("#222"));
        Assert.DoesNotContain(exitLines, e => e.Details.Contains("#333"));
    }

    [Fact]
    public async Task ProfitBrain_Journals_Advisory_Report_And_Never_Trades()
    {
        // The HWARANG law: the Profit Brain computes and reports, the Exit
        // Brain executes. A near-entry position gets a full advisory report
        // (state, floor, targets) and ZERO orders/modifies from it.
        var journal = NewJournal();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1482, profit = 2.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        var host = new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false)
        {
            StaleBarSeconds = 0,
        };   // synthetic tape — see NewHost

        await host.RunCycleAsync();

        journal.Flush();
        var profitLines = journal.GetRecent(null, 200)
            .Where(e => e.Category == "FX_PROFIT").ToList();
        Assert.Single(profitLines);
        var details = profitLines[0].Details;
        Assert.Contains("PROFIT_", details);            // state machine spoke
        Assert.Contains("\"FloorBreached\":false", details);
        Assert.Contains("\"TrailingMode\":", details);

        // Advisory only: no closes, no stop modifications — the exit
        // brain held (near entry), and the profit brain had no vote.
        Assert.Equal(0, script.CloseCalls);
        Assert.Equal(0, script.ModifyCalls);
    }

    [Fact]
    public async Task Target_Tp_Shadow_Vote_Grades_Into_The_Ledger_On_Close()
    {
        // On a full close, the target-tp engine's final vote lands in the
        // same promotion ledger as the exit brain's own shadow engines —
        // the take-profit hypothesis races under the identical evidence bar.
        var journal = NewJournal();
        var ledgerPath = Path.Combine(
            Path.GetTempPath(), $"dg-tpl-{Guid.NewGuid():N}", "fx-shadow-XAUUSDmicro.jsonl");
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                // Crushed past the 1.6R emergency bar → drawdown override
                // closes it in full in cycle 1 (same shape as the manage test).
                new { ticket = 555L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1400, profit = -80.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        var host = new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            equityFloor: () => 0m,
            shadowLedgerPath: ledgerPath)
        {
            StaleBarSeconds = 0,
        };   // synthetic tape — see NewHost

        await host.RunCycleAsync();
        Assert.Equal(1, script.CloseCalls);   // the override closed it

        var rows = File.ReadAllLines(ledgerPath).Select(l =>
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(l)).ToList();
        var targetRows = rows.Where(r => r.GetProperty("Engine").GetString() == "target-tp").ToList();
        Assert.Single(targetRows);
        var row = targetRows[0];
        Assert.Equal(555, row.GetProperty("Ticket").GetInt64());
        Assert.False(row.GetProperty("Won").GetBoolean());   // the crushed trade lost
        Assert.False(row.GetProperty("Helped").GetBoolean()); // losers rescue nobody
        Assert.Equal("full", row.GetProperty("ResolvedAction").GetString());

        // FIX 3: drawdown rides at weight 2.0, so the old weight-0 filter
        // dropped its settled verdict from EVERY full close — 0 "full"
        // drawdown rows across 11 live closes. It is graded now: one row,
        // same ticket, and a loser still credits nobody.
        var drawdownRows = rows.Where(r => r.GetProperty("Engine").GetString() == "drawdown").ToList();
        var dd = Assert.Single(drawdownRows);
        Assert.Equal(555, dd.GetProperty("Ticket").GetInt64());
        Assert.Equal("full", dd.GetProperty("ResolvedAction").GetString());
        Assert.False(dd.GetProperty("Won").GetBoolean());
        Assert.False(dd.GetProperty("Helped").GetBoolean());

        // ONE row per engine per settled close: the in-evaluation giveback
        // vote and its settled mirror used to BOTH land (live: 14 giveback
        // rows for 11 tickets) — the same trade counted twice toward the
        // promotion denominator.
        Assert.Single(rows.Where(r => r.GetProperty("Engine").GetString() == "giveback"));
        Assert.Single(rows.Where(r => r.GetProperty("Engine").GetString() == "counterfactual"));
    }

    [Fact]
    public async Task Floor_Breach_Alerts_The_Webhook_In_Process()
    {
        var (listener, port) = TestHttpListenerFactory.CreateOnFreeLoopbackPort();
        var bodies = new List<string>();
        using var cts = new CancellationTokenSource();
        var capture = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(cts.Token); }
                catch { return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(cts.Token);
                lock (bodies) { bodies.Add(body); }
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
            }
        });
        try
        {
            var journal = NewJournal();
            // A prior FX_PROFIT row (the restart-reseed substrate) arms the
            // position's never-down floor at 4.0R; the venue position sits
            // near entry, so the FIRST cycle reads current ~0.1R < floor —
            // a genuine breach without waiting for a peak to build.
            var prior = System.Text.Json.JsonSerializer.Serialize(new
            {
                Ticket = 444L,
                State = "PROFIT_PROTECTED",
                PeakR = 6.5,
                MaeR = 0.4,
                FloorR = 4.0,
                GivebackPct = 38.0,
            });
            journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #444: prior — {prior}");
            journal.Flush();
            var script = new BridgeScript
            {
                Positions = new object[]
                {
                    new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                          price_open = 1.1480, price_current = 1.1482, profit = 2.0,
                          sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
                },
            };
            using var webhook = new WebhookService(TimeSpan.FromSeconds(30))
            {
                WebhookUrl = $"http://127.0.0.1:{port}/hook",
                MinInterval = TimeSpan.Zero,
            };
            var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
            var host = new FxEngineHost(
                client, journal, "XAUUSDmicro",
                killSwitchEngaged: () => false,
                lotsCap: () => 1.00m,
                realMoneyUnlocked: () => false,
                webhook: webhook)
            {
                StaleBarSeconds = 0,
            };   // synthetic tape — see NewHost

            await host.RunCycleAsync();

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && bodies.Count == 0)
            {
                await Task.Delay(50);
            }
            Assert.NotEmpty(bodies);
            // Since PR hard-floor: a breach no longer merely alerts — the
            // 🚨 alert fires WITH the submitted hard exit (and this fake
            // position also survives the ensemble, so the guard drove it).
            Assert.Contains("HARD PROFIT FLOOR BREACH", bodies[0]);
            Assert.Contains("444", bodies[0]);
            Assert.Contains("embeds", bodies[0]);   // Discord shape

            // The FX_FLOOR command trail exists; the advisory FX_RISK row
            // is belt-and-braces only.
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("EXIT SUBMITTED"));
        }
        finally
        {
            cts.Cancel();
            try { listener.Close(); } catch { /* best effort */ }
            GC.KeepAlive(capture);
        }
    }

    [Fact]
    public async Task Floor_Watch_Tick_Detects_A_Breach_Between_Cycles()
    {
        // The 60s cycle is the source of truth, but a locked floor can be
        // shot through inside one window (observed 2026-10-08: +0.71R
        // sampled, next sample -0.46R straight THROUGH a 0.3R floor). The
        // floor watch must catch the breach on a fresh tick WITHOUT a
        // second cycle — and must not re-submit while one is in flight.
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 445L,
            State = "PROFIT_PROTECTED",
            PeakR = 1.0,
            MaeR = 0.4,
            FloorR = 0.5,          // locked by the restart reseed
            GivebackPct = 10.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #445: prior — {prior}");
        journal.Flush();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                new { ticket = 445L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1500, profit = 2.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
            // +0.667R with risk 0.003 — ABOVE the 0.5R floor: cycle 1 must
            // NOT breach.
            TickBid = 1.15000,
            TickAsk = 1.15003,
        };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        journal.Flush();
        Assert.Equal(0, script.CloseCalls);
        Assert.DoesNotContain(journal.GetRecent(null, 200),
            e => e.Category == "FX_FLOOR" && e.Details.Contains("EXIT SUBMITTED"));

        // Price collapses BETWEEN cycles: -0.333R — straight through the
        // 0.5R floor. The watch tick must catch it with no cycle involved.
        script.TickBid = 1.14700;
        script.TickAsk = 1.14703;
        await host.RunFloorWatchTickAsync();

        journal.Flush();
        Assert.Equal(1, script.CloseCalls);
        Assert.Equal("/close/445", script.LastClosePath);
        Assert.Contains(journal.GetRecent(null, 200),
            e => e.Category == "FX_FLOOR" && e.Details.Contains("EXIT SUBMITTED"));

        // Idempotent: another tick while the exit is in flight does NOT
        // re-submit (once-per-breach event + in-flight skip).
        await host.RunFloorWatchTickAsync();
        journal.Flush();
        Assert.Equal(1, script.CloseCalls);
    }

    [Fact]
    public async Task Ownership_Survives_The_Partial_Close_Comment_Flip()
    {
        // MT5 overwrites the POSITION comment with the partial-close
        // deal's comment: the TP1 rung re-stamped the live #8792846716
        // to "donggfx-close" on 2026-10-08 and Owns() then returned
        // false — the brain dropped a still-open, floor-armed position
        // (no FX_EXIT/FX_PROFIT rows, guard never re-created after the
        // restart). The close stamp is written only by this system, so
        // such a position must stay managed.
        var journal = NewJournal();
        var script = new BridgeScript
        {
            Positions = new object[]
            {
                new { ticket = 446L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1500, profit = 2.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-close" },
            },
        };
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        journal.Flush();
        var recent = journal.GetRecent(null, 200);
        Assert.Contains(recent,
            e => e.Category == "FX_EXIT" && e.Details.Contains("#446"));
        Assert.Contains(recent,
            e => e.Category == "FX_PROFIT" && e.Details.Contains("#446"));
    }

    [Fact]
    public void Shadow_Ledger_Credits_The_Giveback_Evidence_On_A_Verified_Save()
    {
        var path = Path.Combine(Path.GetTempPath(), "dg-shadow", Guid.NewGuid().ToString("N") + ".jsonl");
        var ledger = new FxShadowLedger(path);

        var save = new FxExitDecision(9, "full", 100,
            new[] { new FxExitVote("drawdown", 0.95, 2.0,
                "round-trip: peaked 8.0R, now +0.10R — the move was given back") },
            0, 0, 8.0, 0, 0.1, "profit-floor", "gave back a peak");
        var shadows = new List<FxExitVote>
        {
            FxEngineHost.GivebackShadowVote(save),
        };
        ledger.Append(9, "XAUUSDmicro", shadows, "full", won: true, DateTimeOffset.UtcNow, save);

        var rows = ledger.Summarize().ToDictionary(r => r.Engine);
        Assert.Equal(1, rows["giveback"].Helped);
        Assert.Equal(1.0, rows["giveback"].HitRate, 6);
    }

    [Fact]
    public void Giveback_Shadow_Vote_Mirrors_The_Drawdown_Evidence()
    {
        var decided = new FxExitDecision(1, "full", 100,
            new[] { new FxExitVote("drawdown", 0.85, 2.0,
                "give-back 81% of a 8.0R peak — protect what remains") },
            0, 0, 8.0, 0, 1.5, null, "ensemble full");
        var vote = FxEngineHost.GivebackShadowVote(decided);
        Assert.Equal("giveback", vote.Engine);
        Assert.Equal(0, vote.Weight, 6);
        Assert.Equal(0.85, vote.Exit, 6);
        Assert.Contains("give-back 81%", vote.Reason);

        var noEvidence = FxEngineHost.GivebackShadowVote(new FxExitDecision(
            1, "hold", 0, Array.Empty<FxExitVote>(), 0, 0, 0, 0, 0, null, ""));
        Assert.Equal(0, noEvidence.Exit, 6);
    }

    [Fact]
    public async Task Tp1_Partial_Never_Arms_On_Structure_Trail_The_Floor_Owns_The_Exit()
    {
        // TRAILING-MODE GATE (docs/soak/TP1-FLOOR-INTERACTION.md). The
        // default rising tape classifies Trend → STRUCTURE_TRAIL trailing:
        // the never-down floor ratchets with the peak and out-executes any
        // static rung (the five-save backtest measured −1.90R/−1.65R for
        // arming on such tickets). The rung must never arm; the hard
        // floor's full close is untouched.
        var journal = NewJournal();
        object[] At(double price, double profit) => new object[]
        {
            new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = price, profit = profit,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        var script = new BridgeScript { Positions = At(1.1550, 70.0) };
        var host = NewHost(script, journal);
        // The engine re-speaks on the same pinned bars every cycle, so an
        // unchanged 5-minute cooldown throttles the dispatch AND (pre-fix)
        // returned before position management — a swallowed cycle. Zero it:
        // these tests pin the TP1/floor layers, not the dispatch throttle.
        host.OrderCooldown = TimeSpan.Zero;
        try
        {
            FxEngineHost.ExecuteTp1Partials = true;
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            var recent = journal.GetRecent(null, 200).ToList();
            // The gate answered, audibly, exactly once.
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-SKIP"));
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("STRUCTURE_TRAIL"));
            Assert.Equal(1, recent.Count(e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-SKIP")));
            Assert.DoesNotContain(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));

            // Crossed price, deep peak: the never-down floor ratchets to a
            // high floor — the guard transitions Armed→Protected this cycle
            // (no breach check on the transition), still no close, and
            // still no rung.
            script.Positions = At(1.1700, 220.0);
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("PROFIT FLOOR ARMED"));
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));

            // Next cycle: the guard BREACHES (the executable bid is far
            // through the ratcheted floor) and its full close is the ONLY
            // close. No rung ever armed, so there is no partial to bank:
            // the floor owns the exit entire.
            await host.RunCycleAsync();
            journal.Flush();
            Assert.True(script.CloseCalls == 1, $"closes={script.CloseCalls}\n" +
                string.Join("\n", journal.GetRecent(null, 60).Select(e => $"{e.Timestamp:HH:mm:ss} {e.Category}: {e.Details}")));
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("HARD PROFIT FLOOR BREACH"));
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-EXEC"));
            // The skip row never repeats.
            Assert.Equal(1, journal.GetRecent(null, 200).Count(e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-SKIP")));

            // Position gone: the guard reconciles to EXIT_CONFIRMED exactly
            // once, and nothing re-arms or re-banks.
            script.Positions = Array.Empty<object>();
            await host.RunCycleAsync();
            Assert.Equal(1, script.CloseCalls);
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));
        }
        finally
        {
            FxEngineHost.ExecuteTp1Partials = false;   // never leak into other tests
        }
    }

    [Fact]
    public async Task Tp1_Partial_Arms_And_Banks_On_A_Hybrid_Floor_Ticket()
    {
        var journal = NewJournal();
        // Same walk as the STRUCTURE_TRAIL test but on a FLAT tape: the
        // regime falls through to HYBRID_STRUCTURE_ATR, the gate does not
        // fire, and the rung arms at first sighting and banks on the cross
        // exactly as before the gate existed.
        object[] At(double price, double profit) => new object[]
        {
            new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = price, profit = profit,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        var script = new BridgeScript { Positions = At(1.1550, 70.0) };
        script.FlatTape();
        var host = NewHost(script, journal);
        host.OrderCooldown = TimeSpan.Zero;   // see the gate test — determinism
        try
        {
            FxEngineHost.ExecuteTp1Partials = true;
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            var recent = journal.GetRecent(null, 200).ToList();
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));
            Assert.DoesNotContain(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-SKIP"));

            // Crossed (cycle 2): the rung banks once. On a hybrid ticket
            // the floor does NOT ratchet with the peak, so there is no
            // guard breach here — the rung is the only partial.
            script.Positions = At(1.1700, 220.0);
            await host.RunCycleAsync();
            Assert.Equal(1, script.CloseCalls);
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-EXEC: banked"));
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("\"PlanPct\":15"));
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("HARD PROFIT FLOOR BREACH"));
        }
        finally
        {
            FxEngineHost.ExecuteTp1Partials = false;   // never leak into other tests
        }
    }

    [Fact]
    public async Task Tp1_Plan_Pct_Override_Replaces_The_Allocation_Leg()
    {
        // The dashboard's one-click arm persists a plan-% override the engine
        // must actually HONOR: same hybrid walk as the bank test, but the
        // armed 30% (in place of the allocation plan's 15% leg) drives the
        // rung's eligibility, lot sizing and journaling.
        var journal = NewJournal();
        object[] At(double price, double profit) => new object[]
        {
            new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = price, profit = profit,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        var script = new BridgeScript { Positions = At(1.1550, 70.0) };
        script.FlatTape();
        var host = NewHost(script, journal);
        host.OrderCooldown = TimeSpan.Zero;
        try
        {
            FxEngineHost.ExecuteTp1Partials = true;
            FxEngineHost.Tp1PlanPctOverride = 30;
            await host.RunCycleAsync();
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("\"PlanPct\":30"));

            // Crossed: the rung banks at the armed 30%, sized off that figure.
            script.Positions = At(1.1700, 220.0);
            await host.RunCycleAsync();
            Assert.Equal(1, script.CloseCalls);
            journal.Flush();
            var recent = journal.GetRecent(null, 200).ToList();
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-EXEC: banked"));
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("(30% plan)"));
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("\"Lots\":0.3"));
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            FxEngineHost.ExecuteTp1Partials = false;
        }
    }

    [Fact]
    public async Task Tp1_Zero_Plan_Pct_Override_Makes_The_Rung_Ineligible()
    {
        // A 0% call is the fail-safe reading of "do not take a partial": the
        // rung is below the 10% eligibility floor, so it never arms and the
        // crossing banks nothing.
        var journal = NewJournal();
        object[] At(double price, double profit) => new object[]
        {
            new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = price, profit = profit,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        var script = new BridgeScript { Positions = At(1.1550, 70.0) };
        script.FlatTape();
        var host = NewHost(script, journal);
        host.OrderCooldown = TimeSpan.Zero;
        try
        {
            FxEngineHost.ExecuteTp1Partials = true;
            FxEngineHost.Tp1PlanPctOverride = 0;
            await host.RunCycleAsync();
            script.Positions = At(1.1700, 220.0);
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-EXEC"));
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            FxEngineHost.ExecuteTp1Partials = false;
        }
    }

    [Fact]
    public async Task Tp1_Rung_Is_Held_While_An_Overdue_Plan_Review_Is_Open()
    {
        // CIRCUIT BREAKER: an overdue plan-% review holds the rung. Same
        // hybrid walk as the bank test — without the breaker the rung arms
        // and banks; with it the arm is held (one TP1-HOLD row) and even the
        // cross banks nothing, because the rung never armed.
        var journal = NewJournal();
        object[] At(double price, double profit) => new object[]
        {
            new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = price, profit = profit,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        var script = new BridgeScript { Positions = At(1.1550, 70.0) };
        script.FlatTape();   // hybrid regime: the trailing-mode gate never fires
        var host = NewHost(script, journal);
        host.OrderCooldown = TimeSpan.Zero;
        try
        {
            FxEngineHost.ExecuteTp1Partials = true;
            FxEngineHost.Tp1PlanReviewHold = () => true;   // overdue review open

            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            var recent = journal.GetRecent(null, 200).ToList();
            Assert.Contains(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-HOLD"));
            Assert.Equal(1, recent.Count(e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-HOLD")));
            Assert.DoesNotContain(recent, e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));

            // Crossed price: nothing banks, because the rung never armed.
            script.Positions = At(1.1700, 220.0);
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-EXEC"));
        }
        finally
        {
            FxEngineHost.Tp1PlanReviewHold = null;   // never leak into other tests
            FxEngineHost.ExecuteTp1Partials = false;
        }
    }

    // ── the HARD PROFIT FLOOR: the guard commands, the ensemble cannot veto ──

    /// <summary>A position deep past its restored floor (the screenshot
    /// scenario: peak 12.4R, floor 8.2R restored from a prior FX_PROFIT
    /// row, price now +5.4R).</summary>
    private BridgeScript FloorBreachScript() => new()
    {
        Positions = new object[]
        {
            new { ticket = 555L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = 1.1582, profit = 102.0,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        },
    };

    [Fact]
    public async Task Hard_Floor_Breach_Closes_The_Position_That_The_Ensemble_Would_Hold()
    {
        var journal = NewJournal();
        // The prior FX_PROFIT row arms the guard's floor at 8.2R (peak
        // 12.4R); the venue position sits at ~+3.4R (executable 1.1582 vs
        // entry 1.1480 over the sized stop ~0.003) — through the floor.
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L,
            State = "PROFIT_PROTECTED",
            PeakR = 12.4,
            MaeR = 0.4,
            FloorR = 8.2,
            GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        // THE FIX: the guard's close (full position) went out — the exact
        // close the ensemble's HOLD vote refused to make for two days.
        Assert.Equal(1, script.CloseCalls);

        journal.Flush();
        var entries = journal.GetRecent(null, 400);
        // The command trail, verbatim: BREACH → SUBMITTED, FX_FLOOR rows.
        Assert.Contains(entries, e => e.Category == "FX_FLOOR"
            && e.Details.Contains("HARD PROFIT FLOOR BREACH"));
        Assert.Contains(entries, e => e.Category == "FX_FLOOR"
            && e.Details.Contains("PROFIT FLOOR EXIT SUBMITTED"));
        // The ensemble's HOLD is recorded as evidence, not obeyed.
        Assert.Contains(entries, e => e.Category == "FX_EXIT"
            && e.Details.Contains("hold") && e.Details.Contains("#555"));
    }

    [Fact]
    public async Task Hard_Floor_Breach_Retries_After_Rejection_And_Stays_Protected()
    {
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();
        script.CloseOk = false;   // the broker refuses every attempt
        var host = NewHost(script, journal);

        await host.RunCycleAsync();

        // The retry ladder fired MaxSubmitAttempts times in-cycle.
        Assert.Equal(Core.Fx.FxProfitFloorGuard.MaxSubmitAttempts, script.CloseCalls);

        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("PROFIT FLOOR EXIT FAILED"));
        // Protection survives: the guard keeps the ticket in its command
        // pipeline (the next cycle re-queues the exit).
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("protection remains active"));
    }

    [Fact]
    public async Task Hard_Floor_Confirms_Only_On_Broker_Reconciliation()
    {
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();
        var host = NewHost(script, journal);

        // Cycle 1: breach → submit. The venue still lists the position in
        // the SAME cycle's reads, so nothing may claim confirmation yet.
        await host.RunCycleAsync();
        Assert.Equal(1, script.CloseCalls);
        journal.Flush();
        Assert.DoesNotContain(journal.GetRecent(null, 400), e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));

        // Cycle 2: the position vanished from the venue — only NOW is the
        // exit confirmed (broker reconciliation, not assumed execution).
        script.Positions = Array.Empty<object>();
        await host.RunCycleAsync();
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));
        // The floor's close now writes the "closed #" row FxJournalBook
        // parses, and the host book retires WITH it — the exposure floor
        // frees itself here, no ops-layer reconcile + restart needed.
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_EXIT" && e.Details.Contains("closed #555"));
        // The floor's close carries its settled R too (spec §1): entry vs
        // this cycle's mid over the sized stop — tier 1 (close-price), the
        // same computation the ensemble's full close grades.
        var floorClose = journal.GetRecent(null, 400).Single(e =>
            e.Category == "FX_EXIT" && e.Details.Contains("closed #555"));
        Assert.Contains("RealizedR", floorClose.Details);
        Assert.Contains("close-price", floorClose.Details);
        Assert.Equal(0.0, host.LocalBookLots);
    }

    [Fact]
    public async Task Hard_Floor_Confirms_Despite_Sibling_Positions_On_Other_Symbols()
    {
        // The 2026-09-30 live starvation: two deals accepted at 11:27 but
        // confirmation never landed, because the reconcile gate demanded
        // the WHOLE account read to equal this symbol's managed book —
        // sibling positions on other symbols kept the sets apart forever.
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();
        var host = NewHost(script, journal);

        // Cycle 1: breach → submit; the venue still lists the position.
        await host.RunCycleAsync();
        Assert.Equal(1, script.CloseCalls);

        // Cycle 2: the floor-exited ticket vanished, but a SIBLING position
        // on another symbol (not this brain's book) remains — exactly the
        // shape that starved the live confirmation. A non-empty read that
        // lacks the ticket is definitionally real; absence is the evidence.
        script.Positions = new object[]
        {
            new { ticket = 901L, symbol = "EURUSD", side = "buy", volume = 0.1,
                  price_open = 1.1500, price_current = 1.1505, profit = 5.0,
                  sl = 0.0, tp = 0.0, comment = "" },
        };
        await host.RunCycleAsync();
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));
    }

    [Fact]
    public async Task Floor_Confirmation_Settles_R_After_The_Prune_Deferred_The_Close_Line()
    {
        // The 2026-10-08 live defect: every floor confirm landed
        // OutcomeSource=unknown (4 of 4 post-payload). The prune that
        // retires the vanished ticket DEFERS its "closed #" line to the
        // confirmation loop (exactly one row per ticket) but destroyed
        // _exitStates on the way — so floorR re-read a book that had
        // already forgotten the ticket. With a sibling position the
        // proven-flat prune runs in the SAME cycle as the confirmation:
        // the snapshot must ride over on the guard, never burn to
        // unknown (and never starve RecordSettled of the floor save).
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();
        var host = NewHost(script, journal);

        // Cycle 1: breach → submit; the venue still lists the ticket.
        await host.RunCycleAsync();
        Assert.Equal(1, script.CloseCalls);

        // Cycle 2: 555 is gone, a sibling on ANOTHER symbol keeps the
        // book non-empty — the prune retires 555 from _exitStates
        // (deferring its close line) moments before the confirmation
        // loop settles it.
        script.Positions = new object[]
        {
            new { ticket = 901L, symbol = "EURUSD", side = "buy", volume = 0.1,
                  price_open = 1.1500, price_current = 1.1505, profit = 5.0,
                  sl = 0.0, tp = 0.0, comment = "" },
        };
        await host.RunCycleAsync();
        journal.Flush();

        var entries = journal.GetRecent(null, 400);
        Assert.Contains(entries, e => e.Category == "FX_FLOOR"
            && e.Details.Contains("EXIT CONFIRMED"));
        var close = entries.Single(e => e.Category == "FX_EXIT"
            && e.Details.Contains("closed #555"));
        // Exactly one close row, carrying the settled R as tier 1
        // (close-price) — never "unknown": the book forgot the ticket, the
        // guard did not.
        Assert.Contains("\"OutcomeSource\":\"close-price\"", close.Details);
        Assert.DoesNotContain("\"RealizedR\":null", close.Details);
    }

    [Fact]
    public async Task Hard_Floor_Downgrades_When_The_Broker_Still_Holds_The_Ticket()
    {
        // §13.4: a non-empty read that STILL lists a submitted guard's
        // ticket means the close did not flatten the position — downgrade
        // to failed, protection continues, next cycle re-queues the exit.
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();
        var host = NewHost(script, journal);

        try
        {
            FxEngineHost.FloorSubmitGrace = TimeSpan.Zero;  // no lag tolerance in-test
            // Cycle 1: breach → submit; venue still lists the position.
            await host.RunCycleAsync();
            Assert.Equal(1, script.CloseCalls);
            journal.Flush();
            Assert.DoesNotContain(journal.GetRecent(null, 400), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));

            // Cycle 2: the position is STILL listed (non-empty read!) —
            // the §13.4 downgrade fires instead of a false confirmation.
            await host.RunCycleAsync();
            journal.Flush();
            var entries = journal.GetRecent(null, 400);
            Assert.Contains(entries, e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("downgrade to EXIT_FAILED"));
            Assert.DoesNotContain(entries, e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));

            // Cycle 3: protection continues — the guard re-issues the exit.
            var callsAfterDowngrade = script.CloseCalls;
            await host.RunCycleAsync();
            Assert.True(script.CloseCalls > callsAfterDowngrade, "the exit must be re-queued");
        }
        finally
        {
            FxEngineHost.FloorSubmitGrace = TimeSpan.FromMinutes(2);
        }
    }

    [Fact]
    public async Task Partial_Close_Never_Disables_The_Armed_Floor()
    {
        // Spec §11/G: after a TP1-style partial (volume 1.0 → 0.75), the
        // remaining position stays fully protected — floors are R-based
        // (per-price-distance), never volume-proportional, so banking a
        // rung cannot disarm or shrink the floor. Cycle 1 arms the guard
        // above the floor; a partial close happens between cycles; cycle 2
        // breaches — the guard must command the FULL remaining close.
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var script = FloorBreachScript();   // +3.4R executable — THROUGH the floor
        var host = NewHost(script, journal);

        // Simulate the already-partial book: a TP1 rung was banked earlier
        // (volume 0.75 of the original 1.0). The guard must still arm.
        script.Positions = new object[]
        {
            new { ticket = 555L, symbol = "XAUUSDmicro", side = "buy", volume = 0.75,
                  price_open = 1.1480, price_current = 1.1582, profit = 76.5,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        await host.RunCycleAsync();

        // The breach on the REMAINING 0.75 lots commands the full close —
        // one close, whole remaining position (null lots), no demotion.
        Assert.Equal(1, script.CloseCalls);
        journal.Flush();
        var entries = journal.GetRecent(null, 400);
        Assert.Contains(entries, e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("HARD PROFIT FLOOR BREACH"));
        Assert.Contains(entries, e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("PROFIT FLOOR EXIT SUBMITTED"));
    }

    [Fact]
    public async Task Guard_Save_Ledger_Appends_Drawdown_And_Its_Giveback_Mirror()
    {
        // FIX 2 (2026-10-07): the guard-save append carried ONLY drawdown's
        // vote, and Helped()'s floor-exit rule named drawdown alone — so
        // giveback was starved on every one of the 12 live guard saves
        // even though the peak→breach evidence IS the giveback story.
        // Both rows must land on confirmation, both credited.
        var journal = NewJournal();
        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 555L, State = "PROFIT_PROTECTED", PeakR = 12.4,
            MaeR = 0.4, FloorR = 8.2, GivebackPct = 33.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #555: prior — {prior}");
        journal.Flush();
        var ledgerPath = Path.Combine(
            Path.GetTempPath(), $"dg-guard-save-{Guid.NewGuid():N}", "fx-shadow-XAUUSDmicro.jsonl");
        var script = FloorBreachScript();
        var host = NewHost(script, journal, shadowLedgerPath: ledgerPath);

        // Cycle 1: breach → submit. The venue still holds the ticket, so
        // nothing is settled and nothing may be graded yet.
        await host.RunCycleAsync();
        Assert.Equal(1, script.CloseCalls);
        Assert.False(File.Exists(ledgerPath));

        // Cycle 2: the broker no longer holds it → EXIT CONFIRMED is when
        // the save settles and the ledger rows are written.
        script.Positions = Array.Empty<object>();
        await host.RunCycleAsync();
        journal.Flush();
        Assert.Contains(journal.GetRecent(null, 400), e =>
            e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));

        var rows = File.ReadAllLines(ledgerPath)
            .Select(l => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(l))
            .Where(r => r.GetProperty("ResolvedAction").GetString() == "floor-exit")
            .ToList();
        Assert.Equal(2, rows.Count);

        var byEngine = rows.ToDictionary(r => r.GetProperty("Engine").GetString()!);
        Assert.Equal(2, byEngine.Count);
        Assert.Contains("drawdown", byEngine.Keys);
        Assert.Contains("giveback", byEngine.Keys);
        foreach (var r in rows)
        {
            Assert.Equal(555, r.GetProperty("Ticket").GetInt64());
            Assert.Equal("floor-exit", r.GetProperty("ResolvedAction").GetString());
            Assert.True(r.GetProperty("Won").GetBoolean());      // a save IS the win
            Assert.True(r.GetProperty("Helped").GetBoolean());   // both engines credited
            Assert.Equal(0.95, r.GetProperty("ExitAtClose").GetDouble(), 6);
        }
    }

    [Fact]
    public void Profit_State_Reseeds_From_The_Journal_After_A_Restart()
    {
        // The restart defect (2026-09-29): peaks (16.9R) and established
        // floors wiped on relaunch — violating the never-down law and
        // re-arming the giveback override on a peak it could no longer
        // see. First sighting must merge the journal's last FX_PROFIT row.
        var journal = NewJournal();
        var host = NewHost(new BridgeScript(), journal);

        var prior = System.Text.Json.JsonSerializer.Serialize(new
        {
            Ticket = 777L,
            State = "PROFIT_EXTENDED",
            PeakR = 6.5,
            MaeR = 0.4,
            FloorR = 4.0,
        });
        journal.Log(Guid.Empty, "FX_PROFIT", $"XAUUSDmicro #777: reseed — {prior}");
        journal.Flush();

        var state = host.LastProfitStateFromJournal(777L);
        Assert.NotNull(state);
        Assert.Equal(6.5, state.Value.MfeR, 6);
        Assert.Equal(0.4, state.Value.MaeR, 6);
        Assert.Equal(4.0, state.Value.FloorR, 6);

        // An unknown ticket is a cold start, never a crash.
        Assert.Null(host.LastProfitStateFromJournal(999L));
    }

    // ── recent-tape roster gate + settled-R recording ─────────────────
    // docs/superpowers/specs/2026-10-08-fx-win-rate-design.md — the
    // measurement substrate (RealizedR on closes) and the ROSTER-POLICY
    // items 2-4 exclusion bar at the entry path.

    [Fact]
    public async Task Recent_Tape_Gate_Refuses_An_Excluded_Family_Before_Dispatch()
    {
        // The bar, exactly: every roster family on this symbol has failed
        // two consecutive evaluations over a full window (n = WindowN + 1,
        // mean −1.0R). The signal must still SPEAK (observed, counted
        // toward the soak) but never dispatch — a journaled refusal instead.
        var journal = NewJournal();
        var script = new BridgeScript();
        var host = NewHost(script, journal);
        foreach (var name in FxFamilies.All().Select(a => a.Name))
        {
            for (var i = 0; i < FxRecentTape.WindowN + 1; i++)
            {
                host.RecentTape.Record("XAUUSDmicro", name, -1.0);
            }
        }

        await host.RunCycleAsync();

        Assert.NotNull(host.LastDecision);
        Assert.Equal(0, script.OrderCalls);
        journal.Flush();
        var entries = journal.GetRecent(null, 400);
        Assert.Contains(entries, e => e.Category == "FX_ORDER"
            && e.Details.Contains("roster recent-tape excluded"));
        // The window rides along in the payload for the audit trail.
        Assert.Contains(entries, e => e.Category == "FX_ORDER"
            && e.Details.Contains("WindowN"));
    }

    [Fact]
    public async Task Ensemble_Full_Close_Journals_Settled_R_And_Feeds_The_Tape()
    {
        // Close-writer site 1: the ensemble's full close carries
        // RealizedR = the same ProfitR the shadow ledger's `won` reads,
        // and the settled R feeds the recent-tape cell — family from the
        // fill row's Signal (no in-memory dispatch happened here: the
        // restart path every real restart takes).
        var journal = NewJournal();
        var fill = System.Text.Json.JsonSerializer.Serialize(new
        {
            Side = "buy", Lots = 0.1, Sl = 1.1450, SizedStopDistance = 0.003,
            PaperExec = false, Retcode = 10009, Order = 31337L, Deal = 31336L,
            Price = 1.1480, Server = "Deriv-Demo", Signal = "vol-breakout",
        });
        journal.Log(Guid.Empty, "FX_ORDER",
            $"buy 0.1 lots XAUUSDmicro @ 1.148 — ticket 31337: {fill}");
        journal.Flush();

        var script = new BridgeScript
        {
            Positions = new object[]
            {
                // Deep underwater: 1.1400 vs entry 1.1480 over the 0.003
                // stop = −2.67R → the MAE emergency forces the full exit.
                new { ticket = 31337L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1400, profit = -50.0,
                      sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
            },
        };
        var host = NewHost(script, journal);
        await host.RunCycleAsync();
        // The venue flattens after the fill — a position still listed next
        // cycle would re-seed tracking and close a SECOND time (double-
        // counting the tape cell; the one-close-per-ticket law keeps this
        // impossible live).
        script.Positions = Array.Empty<object>();
        await host.RunCycleAsync();

        Assert.True(script.CloseCalls >= 1,
            "the MAE emergency must dispatch the full close");
        journal.Flush();
        var close = journal.GetRecent(null, 400).FirstOrDefault(e =>
            e.Category == "FX_EXIT" && e.Details.Contains("closed #31337"));
        Assert.NotNull(close);
        Assert.Contains("RealizedR", close!.Details);
        Assert.Contains("close-price", close.Details);
        // Family attribution fell back to the fill row: exactly one
        // settled sample landed in the (XAUUSDmicro, vol-breakout) cell.
        Assert.Equal(1, host.RecentTape.WindowCount("XAUUSDmicro", "vol-breakout"));
    }

    [Fact]
    public void FamilyFromJournal_Reads_The_Fill_Signal()
    {
        var journal = NewJournal();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            Side = "buy", Lots = 0.1, Sl = 4102.27, SizedStopDistance = 1.87,
            PaperExec = false, Retcode = 10009, Order = 4242L, Deal = 4241L,
            Price = 4104.51, Server = "Deriv-Demo", Signal = "vol-breakout",
        });
        journal.Log(Guid.Empty, "FX_ORDER",
            $"buy 0.1 lots XAUUSDmicro @ 4104.51 — ticket 4242: {payload}");
        journal.Flush();

        Assert.Equal("vol-breakout", FxEngineHost.FamilyFromJournal(journal.JournalDir, 4242L));
        // Unknown ticket: null — never guess a family.
        Assert.Null(FxEngineHost.FamilyFromJournal(journal.JournalDir, 999999L));
    }

}
