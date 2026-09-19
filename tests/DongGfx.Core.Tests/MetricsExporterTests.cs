using DongGfx.Core.Analytics;
using DongGfx.Core.Models;

namespace DongGfx.Core.Tests;

[Trait("Category", "Unit")]
public class MetricsExporterTests
{
    private static Trade Trade(decimal profit, string account, string source = "Growth",
        decimal stake = 1.00m, DateTimeOffset? settledAt = null) => new(
        Guid.NewGuid(), "frxEURUSD",
        profit >= 0 ? Direction.Rise : Direction.Fall,
        stake, "USD", 1.17, 1700000300, $"C-{Guid.NewGuid():N}",
        profit >= 0 ? ContractStatus.Won : ContractStatus.Lost,
        profit, 1.165, 1700000600,
        settledAt ?? new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero),
        Guid.NewGuid(), account, source);

    private static readonly List<Trade> Trades =
    [
        Trade(+0.90m, "Alpha"),
        Trade(-1.00m, "Alpha"),
        Trade(+0.50m, "Beta", stake: 2.50m),
    ];

    // ─── CSV ───────────────────────────────────────────────

    [Fact]
    public void ToCsv_EmitsHeaderAndOneRowPerAccount()
    {
        var csv = MetricsExporter.ToCsv(Trades);
        var lines = csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("account,trades,wins,losses,win_rate,total_pnl,mean_pnl,max_stake", lines[0]);
        Assert.Equal(3, lines.Length); // header + Alpha + Beta

        Assert.Contains("Alpha,2,1,1,0.5,-0.1,-0.05,1", lines[1]);
        Assert.Contains("Beta,1,1,0,1,0.5,0.5,2.5", lines[2]);
    }

    [Fact]
    public void ToCsv_EmptyTrades_HeaderOnly()
    {
        var csv = MetricsExporter.ToCsv([]);

        Assert.Equal("account,trades,wins,losses,win_rate,total_pnl,mean_pnl,max_stake\n", csv);
    }

    [Fact]
    public void ToCsv_QuotesAccountNamesWithCommas()
    {
        var csv = MetricsExporter.ToCsv([Trade(0.10m, "Alpha, Prime")]);

        Assert.Contains("\"Alpha, Prime\"", csv);
    }

    // ─── JSON ──────────────────────────────────────────────

    [Fact]
    public void ToJson_IncludesPerAccountStats()
    {
        var json = MetricsExporter.ToJson(Trades);

        Assert.Contains("\"total_trades\": 3", json);
        Assert.Contains("\"account\": \"Alpha\"", json);
        Assert.Contains("\"win_rate\": 0.5", json);
        Assert.Contains("\"total_pnl\": -0.10", json);
        Assert.Contains("\"max_stake\": 2.50", json);
    }

    [Fact]
    public void ToJson_WithoutLatencyOrErrors_OmitsSections()
    {
        var json = MetricsExporter.ToJson(Trades);

        Assert.DoesNotContain("latency_ms", json);
        Assert.DoesNotContain("\"errors\": {", json); // the section, not a substring coincidence
        Assert.DoesNotContain("\"count\":", json);
        Assert.DoesNotContain("p95_ms", json);
    }

    [Fact]
    public void ToJson_WithLatency_IncludesPercentiles()
    {
        var latency = new List<MetricSample>
        {
            new("cycle", 100, DateTimeOffset.UtcNow),
            new("cycle", 200, DateTimeOffset.UtcNow),
            new("cycle", 300, DateTimeOffset.UtcNow),
            new("cycle", 1000, DateTimeOffset.UtcNow),
        };

        var json = MetricsExporter.ToJson(Trades, latency: latency);

        Assert.Contains("\"latency_ms\"", json);
        Assert.Contains("\"mean_ms\": 400", json);
        Assert.Contains("\"max_ms\": 1000", json);
        Assert.Contains("\"p95_ms\": 1000", json); // ceil(0.95*4)-1 = index 3
    }

    [Fact]
    public void ToJson_WithErrors_GroupsByAccount()
    {
        var errors = new List<MetricSample>
        {
            new("cycle_error", 1, DateTimeOffset.UtcNow, "Alpha"),
            new("cycle_error", 2, DateTimeOffset.UtcNow, "Alpha"),
            new("cycle_error", 1, DateTimeOffset.UtcNow, "Beta"),
        };

        var json = MetricsExporter.ToJson(Trades, errors: errors);

        Assert.Contains("\"errors\"", json);
        Assert.Contains("\"count\": 3", json);
        Assert.Contains("\"total\": 4", json);
        Assert.Contains("\"account\": \"Alpha\"", json);
    }

    [Fact]
    public void ToJson_EmptyTrades_StillValidJson()
    {
        var json = MetricsExporter.ToJson([]);

        Assert.Contains("\"total_trades\": 0", json);
        Assert.Contains("\"accounts\": []", json);
    }
}
