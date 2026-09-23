using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DongGfx.App.Services;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The nightly scorecard service: short bridge data is skipped with a
/// journal row, sufficient data runs every family through the IS/OOS split
/// and lands a summary, errors are surfaced not thrown, and symbol parsing
/// covers the CSV/fallback/dedupe rules.
/// </summary>
[Trait("Category", "Unit")]
public class FxScorecardServiceTests : IDisposable
{
    private readonly FakeBridge _fake = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "donggfx-tests", Guid.NewGuid().ToString("N"));

    private sealed class FakeBridge : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json)> Routes { get; } = new();

        public void Route(string path, string json) => Routes[path] = (HttpStatusCode.OK, json);
        public void RouteStatus(string path, HttpStatusCode code) => Routes[path] = (code, "{}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (Routes.TryGetValue(path, out var r))
            {
                return Task.FromResult(new HttpResponseMessage(r.Status)
                {
                    Content = new StringContent(r.Json, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private (FxScorecardService Svc, TradeJournal Journal) NewSvc(AppSettings settings)
    {
        var client = new Mt5BridgeClient(_fake, new Uri("http://127.0.0.1:53190/"));
        var journal = new TradeJournal(Path.Combine(_root, "journal"));
        var svc = new FxScorecardService(client, journal, () => settings);
        return (svc, journal);
    }

    private static AppSettings Settings(string symbols = "EURUSD") => new()
    {
        FxSymbols = symbols,
        FxSymbol = "EURUSD",
    };

    private static string Candles(int count) =>
        "{\"candles\": [" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $"{{\"time\":{1790000000L + i * 60},\"open\":1.1,\"high\":1.12,\"low\":1.09,\"close\":{1.1 + (i % 7 - 3) * 0.0007:0.####}}}")) + "]}";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ShortBridgeData_Is_Skipped_With_A_Journal_Row()
    {
        _fake.Route("candles/EURUSD", Candles(50));   // < 150 bars
        var (svc, journal) = NewSvc(Settings());

        var result = await svc.RunAsync();

        Assert.Equal("scorecard: no data", result);
        journal.Flush();
        Assert.Contains(journal.GetRecent(count: 20), e =>
            e.Category == "FX_SCORECARD" && e.Details.Contains("skipped"));
    }

    [Fact]
    public async Task SufficientData_Runs_Families_And_Lands_A_Summary()
    {
        _fake.Route("candles/EURUSD", Candles(700));
        var (svc, journal) = NewSvc(Settings());

        var result = await svc.RunAsync();

        Assert.NotNull(result);
        Assert.StartsWith("scorecard ", result);
        Assert.Contains("families approved", result);
        Assert.Equal(result, svc.LastSummary);
        journal.Flush();
        Assert.Contains(journal.GetRecent(count: 40), e =>
            e.Category == "FX_SCORECARD" && e.Details.Contains("EURUSD:"));
    }

    [Fact]
    public async Task Bridge_Refusal_Yields_Empty_Candles_And_No_Data()
    {
        // The client maps GET refusals to an empty candle list; the service
        // must surface that as a clean "no data", never an exception.
        _fake.RouteStatus("candles/EURUSD", HttpStatusCode.InternalServerError);
        var (svc, journal) = NewSvc(Settings());

        var result = await svc.RunAsync();

        Assert.Equal("scorecard: no data", result);
        journal.Flush();
        Assert.Contains(journal.GetRecent(count: 20), e =>
            e.Category == "FX_SCORECARD" && e.Details.Contains("skipped"));
    }

    [Fact]
    public async Task Busy_Guard_Rejects_A_Second_Run()
    {
        var (svc, _) = NewSvc(Settings());

        // Reflect the internal flag as if a run is in flight.
        typeof(FxScorecardService)
            .GetField("_running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(svc, true);

        var result = await svc.RunAsync();

        Assert.Equal("scorecard already running", result);
    }

    [Theory]
    [InlineData("EURUSD,GBPUSD , EURUSD", "EURUSD|GBPUSD")]   // trim + dedupe
    [InlineData("", "EURUSD")]                                // empty → fallback
    [InlineData("  ", "XAUUSD")]                              // blank → fallback
    public void ParseSymbols_Covers_Csv_Fallback_And_Dedupe(string csv, string expected)
    {
        var fallback = csv.Trim() == "" && csv.Length > 0 ? "XAUUSD" : "EURUSD";
        var got = FxScorecardService.ParseSymbols(csv, fallback);
        Assert.Equal(expected.Split('|'), got);
    }
}
