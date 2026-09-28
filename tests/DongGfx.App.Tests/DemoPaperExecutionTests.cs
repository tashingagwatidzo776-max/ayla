using System.Net;
using System.Net.Http;
using System.IO;
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
        public object[] Positions = Array.Empty<object>();
        public int CloseCalls;
        public int ModifyCalls;
        public string? LastClosePath;

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
                r = Json(new { bid = 1.15000, ask = 1.15003, time = 1_700_000_000L + 120 * 60 });
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
                r = Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE", deal = 777 });
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
            realMoneyUnlocked: () => false);
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
            realMoneyUnlocked: () => false);

        await host.RunCycleAsync();

        // The demo-execution path runs the same rails as live: a kill
        // switch blocks the fill even on a verified demo.
        Assert.Equal(0, script.OrderCalls);
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
                      sl = 0.0, tp = 0.0, comment = "donggfx-brain" },
                // Not ours: a manual position in even worse shape — the
                // brain must never touch what it did not open.
                new { ticket = 222L, symbol = "XAUUSDmicro", side = "buy", volume = 0.1,
                      price_open = 1.1480, price_current = 1.1380, profit = -100.0,
                      sl = 0.0, tp = 0.0, comment = "manual" },
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

        // The settlement trail: FX_EXIT only for the owned ticket.
        journal.Flush();
        var entries = journal.GetRecent(null, 200);
        var exitLines = entries.Where(e => e.Category == "FX_EXIT").ToList();
        Assert.Contains(exitLines, e => e.Details.Contains("#111"));
        Assert.DoesNotContain(exitLines, e => e.Details.Contains("#222"));
    }

}
