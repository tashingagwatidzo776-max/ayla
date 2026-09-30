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
        public bool TicksDown;
        public object[] Positions = Array.Empty<object>();
        public int CloseCalls;
        public int ModifyCalls;
        public string? LastClosePath;

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
                r = Json(new { candles = Enumerable.Range(0, 120).Select(i => new
                {
                    time = 1_700_000_000L + i * 60,
                    open = 1.10 + i * 0.0004,
                    high = 1.10 + i * 0.0004 + 0.0004,
                    low = 1.10 + i * 0.0004 - 0.0004,
                    close = 1.10 + i * 0.0004 + 0.0002,
                }).ToArray() });
            }
            else if (path.Contains("/ticks/"))
            {
                r = TicksDown
                    ? Json(new { error = "no tick" }, HttpStatusCode.NotFound)
                    : Json(new { bid = 1.15000, ask = 1.15003, time = 1_700_000_000L + 120 * 60 });
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

    private static FxEngineHost NewHost(BridgeScript script, TradeJournal journal)
    {
        var client = new Mt5BridgeClient(script, new Uri("http://127.0.0.1:1/"));
        return new FxEngineHost(
            client, journal, "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            clock: NySession);
    }

    /// <summary>A fixed mid-week, mid-session instant: Wednesday 18:00 UTC.
    /// The regime detector vetoes the thin "late" UTC session (21:00–24:00),
    /// so a wall-clock-driven cycle would fail every evening — the clock is
    /// pinned where the rules say liquidity is deep.</summary>
    private static DateTimeOffset NySession() => new(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);

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
            clock: NySession);

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
            equityFloor: () => 0m);   // floor override silent

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
            realMoneyUnlocked: () => false);

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
            shadowLedgerPath: ledgerPath);

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
                webhook: webhook);

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
    public async Task Tp1_Partial_Arms_At_First_Sighting_And_Banks_On_The_Cross()
    {
        var journal = NewJournal();
        // Entry 1.1480, venue SL 1.1450 → risk ≈ 0.003. Cycle 1: price
        // 1.1550 (~2.3R) — MFE gate passes, the rung ARMS at the first
        // ladder target ahead of the price (the tape's swing high ≈ 1.1504,
        // which is BELOW the current price? No — ahead-only keeps targets
        // above 1.1550, so the rung sits above the current price).
        // Cycle 2: price walks to 1.1700 — past every rung the ladder can
        // name → the armed rung is crossed and the 25% plan banks once.
        // Volume 1.0 so the 25% rung clears the venue's 0.1 lot step.
        object[] At(double price, double profit) => new object[]
        {
            new { ticket = 444L, symbol = "XAUUSDmicro", side = "buy", volume = 1.0,
                  price_open = 1.1480, price_current = price, profit = profit,
                  sl = 1.1450, tp = 0.0, comment = "donggfx-brain" },
        };
        var script = new BridgeScript { Positions = At(1.1550, 70.0) };
        var host = NewHost(script, journal);
        try
        {
            // Default OFF: the plan is advisory only — no arming, no close.
            FxEngineHost.ExecuteTp1Partials = false;
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            Assert.DoesNotContain(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));

            // Armed (cycle 1): the rung is recorded, nothing executes yet.
            FxEngineHost.ExecuteTp1Partials = true;
            await host.RunCycleAsync();
            Assert.Equal(0, script.CloseCalls);
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-ARM"));

            // Crossed (cycle 2): the rung banks once — AND the hard floor
            // (armed by this trade's own FX_PROFIT row: peak ≈ +22R → a
            // high never-down floor) fires its full close the SAME cycle.
            // Priority in action: the prototype banks its rung; the hard
            // risk constraint still takes the whole position. Two closes,
            // one per layer, exactly once each.
            script.Positions = At(1.1700, 220.0);
            await host.RunCycleAsync();
            Assert.Equal(2, script.CloseCalls);
            journal.Flush();
            // At +22R the plan is the strong-continuation variant (15/20/25/40)
            // and 0.15 lots snaps DOWN to the venue's 0.1 step.
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("TP1-EXEC: banked 0.1 lots (15% plan)"));
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_PROFIT" && e.Details.Contains("\"PlanPct\":15"));
            // The hard floor's close is on the trail too.
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("HARD PROFIT FLOOR BREACH"));

            // Once per ticket per layer: the next cycle must not re-bank
            // the rung nor re-fire the floor (the position is gone; the
            // guard reconciles to EXIT_CONFIRMED on the agreeing reads).
            script.Positions = Array.Empty<object>();
            await host.RunCycleAsync();
            Assert.Equal(2, script.CloseCalls);
            journal.Flush();
            Assert.Contains(journal.GetRecent(null, 200), e =>
                e.Category == "FX_FLOOR" && e.Details.Contains("EXIT CONFIRMED"));
        }
        finally
        {
            FxEngineHost.ExecuteTp1Partials = false;   // never leak into other tests
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

}
