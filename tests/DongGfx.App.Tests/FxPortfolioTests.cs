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
        public bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (Down)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"));
            }

            var path = req.RequestUri!.AbsolutePath;
            var json = path.Contains("/positions") ? PositionsJson
                : path.Contains("/account")
                    ? "{\"ok\":true,\"login\":201587365,\"server\":\"Deriv-Demo\",\"currency\":\"USD\"," +
                       "\"balance\":2632.19,\"equity\":2632.19,\"margin_free\":2632.19,\"leverage\":1000,\"trade_mode\":0}"
                    : "{\"ok\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static Mt5BridgeClient NewClient(PositionsHandler handler) =>
        new(handler, new Uri("http://127.0.0.1:1/"));

    private static string Pos(string symbol, double volume) =>
        $"{{\"ticket\":{symbol.Length * 100},\"symbol\":\"{symbol}\",\"side\":\"buy\",\"volume\":{volume.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"price_open\":1,\"price_current\":1,\"profit\":0}}";

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
}
