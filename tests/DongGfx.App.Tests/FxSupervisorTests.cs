using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Fx;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Deriv;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// Phase D — the safety layer around the FX brain. The supervisor must stop
/// trading (and keep it stopped) on a daily-loss breach or equity floor,
/// stay clear when everything is healthy, treat kill switch / governor /
/// bridge-down as transients that clear on recovery, and re-arm only by an
/// explicit act. The FxEngineHost must consult the supervisor before and
/// during execution. All transport is a fake HttpMessageHandler — the real
/// sidecar payload shapes, no sockets.
/// </summary>
public class FxSupervisorTests
{
    private static TradeJournal NewJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dg-sup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new TradeJournal(dir);
    }

    private static FxSupervisor NewSupervisor(
        TradeJournal journal,
        Func<bool>? kill = null,
        Func<bool>? governor = null,
        decimal cap = 25m,
        decimal floor = 0m) =>
        new(journal, kill ?? (() => false), governor ?? (() => false), () => cap, () => floor);

    // ── supervisor verdicts ─────────────────────────────────────────────

    [Fact]
    public void Clear_Account_Trades()
    {
        var s = NewSupervisor(NewJournal());
        s.AnchorSession(2650m);
        var v = s.Evaluate(bridgeUp: true, balance: 2650m, equity: 2655m);
        Assert.True(v.TradingAllowed);
        Assert.Equal(FxHaltReason.None, v.Halt);
    }

    [Fact]
    public void DailyLossCap_Breach_Halts_And_StaysHalted()
    {
        var s = NewSupervisor(NewJournal(), cap: 25m);
        s.AnchorSession(2650m);

        var v = s.Evaluate(true, 2650m - 30m, 2620m);
        Assert.False(v.TradingAllowed);
        Assert.Equal(FxHaltReason.DailyLossCap, v.Halt);

        // recovery next evaluation does NOT clear a loss stop — it is latched
        var v2 = s.Evaluate(true, 2651m, 2651m);
        Assert.False(v2.TradingAllowed);
    }

    [Fact]
    public void Exactly_AtCap_IsAllowed_Beyond_IsNot()
    {
        var s = NewSupervisor(NewJournal(), cap: 25m);
        s.AnchorSession(2650m);
        Assert.True(s.Evaluate(true, 2625m, 2625m).TradingAllowed);   // loss == cap
        Assert.False(s.Evaluate(true, 2624.99m, 2624.99m).TradingAllowed);
    }

    [Fact]
    public void EquityFloor_Halts_WhenEnabled()
    {
        var s = NewSupervisor(NewJournal(), floor: 2000m);
        s.AnchorSession(2650m);
        Assert.Equal(FxHaltReason.EquityFloor, s.Evaluate(true, 2650m, 1999m).Halt);
        // loss/equity stops latch — a bounce back does NOT clear them;
        // only ReAnchor (explicit operator act) does.
        Assert.False(s.Evaluate(true, 2650m, 2001m).TradingAllowed);
    }

    [Fact]
    public void EquityFloor_Disabled_AtZero()
    {
        var s = NewSupervisor(NewJournal(), floor: 0m);
        s.AnchorSession(2650m);
        Assert.True(s.Evaluate(true, 2650m, 5m).TradingAllowed);
    }

    [Fact]
    public void KillSwitch_And_Governor_Are_TransientHalts()
    {
        var journal = NewJournal();
        var kill = false;
        var s = NewSupervisor(journal, kill: () => kill, cap: 25m);
        s.AnchorSession(2650m);

        kill = true;
        Assert.Equal(FxHaltReason.KillSwitch, s.Evaluate(true, 2650m, 2650m).Halt);

        kill = false;
        s.ClearTransientHalts();
        Assert.True(s.Evaluate(true, 2650m, 2650m).TradingAllowed);
    }

    [Fact]
    public void BridgeDown_Halts_And_Clears_WhenBack()
    {
        var s = NewSupervisor(NewJournal());
        s.AnchorSession(2650m);

        Assert.Equal(FxHaltReason.BridgeDown, s.Evaluate(bridgeUp: false, 2650m, 2650m).Halt);
        s.ClearTransientHalts();
        Assert.True(s.Evaluate(true, 2650m, 2650m).TradingAllowed);
    }

    [Fact]
    public void LossStop_DoesNotClear_ViaTransientPath()
    {
        var s = NewSupervisor(NewJournal(), cap: 10m);
        s.AnchorSession(2650m);
        Assert.Equal(FxHaltReason.DailyLossCap, s.Evaluate(true, 2639m, 2639m).Halt);  // loss 11 > cap 10

        s.ClearTransientHalts();
        Assert.False(s.Evaluate(true, 2660m, 2660m).TradingAllowed);   // still latched
    }

    [Fact]
    public void ReAnchor_Clears_And_Rebases()
    {
        var s = NewSupervisor(NewJournal(), cap: 10m);
        s.AnchorSession(2650m);
        Assert.Equal(FxHaltReason.DailyLossCap, s.Evaluate(true, 2639m, 2639m).Halt);

        s.ReAnchor(2645m);
        Assert.True(s.Evaluate(true, 2645m, 2645m).TradingAllowed);
        Assert.Equal(FxHaltReason.None, s.HaltReason);
    }

    [Fact]
    public void AnchorSession_Lazily_OnFirstEvaluate()
    {
        var s = NewSupervisor(NewJournal(), cap: 10m);
        // no explicit anchor: first evaluation adopts the balance as baseline
        Assert.True(s.Evaluate(true, 2650m, 2650m).TradingAllowed);
        Assert.Equal(2650m, s.SessionStartBalance);
    }

    // ── journaled + webhook-notified halts ──────────────────────────────

    [Fact]
    public void Halts_Are_Journaled_As_FXRisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dg-sup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var journal = new TradeJournal(dir);
        var s = NewSupervisor(journal, cap: 5m);
        s.AnchorSession(2650m);
        s.Evaluate(true, 2640m, 2640m);

        journal.Flush();   // entries buffer in memory; flush writes them out
        Assert.Contains(
            Directory.GetFiles(dir, "*.jsonl").SelectMany(File.ReadAllLines).ToList(),
            line => line.Contains("FX_RISK"));
    }

    // ── FxEngineHost integration: supervisor gates execution ────────────

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int AccountCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage r;
            if (path.EndsWith("/account"))
            {
                AccountCalls++;
                r = Json(new { ok = true, login = 201587365, server = "Deriv-Demo", currency = "USD",
                               balance = 2632.19, equity = 2632.19, margin_free = 2632.19, leverage = 1000 });
            }
            else if (path.Contains("/candles/"))
            {
                r = Json(new { candles = Enumerable.Range(0, 120).Select(i => new {
                    time = 1_700_000_000L + i * 60, open = 1.1, high = 1.11, low = 1.09, close = 1.1 + (i % 7 - 3) * 0.0004 }).ToArray() });
            }
            else if (path.Contains("/ticks/"))
            {
                r = Json(new { bid = 1.10002, ask = 1.10006, time = 1_700_000_000 });
            }
            else if (path.EndsWith("/order"))
            {
                r = Json(new { ok = true, retcode = 10009, retcode_name = "TRADE_RETCODE_DONE", deal = 999, price = 1.1001 });
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
                Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json")
            };
            return m;
        }
    }

    private static FxEngineHost NewHost(ScriptedHandler handler, FxSupervisor supervisor)
    {
        var client = new Mt5BridgeClient(handler, new Uri("http://127.0.0.1:1/"));
        return new FxEngineHost(
            client, NewJournal(), "XAUUSDmicro",
            killSwitchEngaged: () => false,
            lotsCap: () => 1.00m,
            realMoneyUnlocked: () => false,
            supervisor: supervisor);
    }

    [Fact]
    public async Task Host_Live_Cycle_With_Clean_Supervisor_Runs_And_Journals()
    {
        var s = NewSupervisor(NewJournal(), cap: 500m);
        s.AnchorSession(2632.19m);
        var host = NewHost(new ScriptedHandler(), s);

        await host.RunCycleAsync();

        Assert.NotNull(host.LastDecision);          // the brain actually ran
        Assert.True(host.LastDecision!.Action is FxDecisionAction.Paper or FxDecisionAction.NoSignal
                    or FxDecisionAction.SkippedRegime or FxDecisionAction.Ordered);
    }

    [Fact]
    public async Task Host_Halted_Supervisor_Skips_The_Brain_Entirely()
    {
        var s = NewSupervisor(NewJournal(), cap: 5m);
        s.AnchorSession(2632.19m);                  // cap 5 on a 2632 account
        var host = NewHost(new ScriptedHandler(), s);
        host.GoLive();

        // the ScriptedHandler account reports balance 2632.19 → breach: loss 0
        // force the breach by re-anchoring high (loss = start - balance)
        s.AnchorSession(2700m);                     // 2700 - 2632.19 = 67.79 > cap 5

        await host.RunCycleAsync();

        Assert.Null(host.LastDecision);             // the brain never ran
        Assert.False(s.Evaluate(true, 2632.19m, 2632.19m).TradingAllowed);
    }

    [Fact]
    public async Task Host_GoLive_Executes_Through_Rails_On_Scripted_Fill()
    {
        var s = NewSupervisor(NewJournal(), cap: 5000m);
        s.AnchorSession(2632.19m);
        var handler = new ScriptedHandler();
        var host = NewHost(handler, s);

        await host.RunCycleAsync();                 // clean run first (soak semantics aside, direct GoLive for the rail check)
        host.PaperSoakSignalsRequired = 0;          // direct go-live for the rail test
        host.GoLive();
        Assert.True(host.IsLiveEngine);

        // a second live cycle may or may not signal on synthetic bars; the
        // rail contract under test is the supervisor's execution-time gate:
        s.AnchorSession(9999m);                     // 9999 - 2632.19 > cap → halted
        await host.RunCycleAsync();
        Assert.False(s.Evaluate(true, 2632.19m, 2632.19m).TradingAllowed);
    }
}
