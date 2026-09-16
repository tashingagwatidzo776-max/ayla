using System.Globalization;
using System.Text;
using Tf.Core.Models;

namespace Tf.Core.Analytics;

/// <summary>One operational metric sample: a timestamped counter/gauge the
/// exporter turns into rows. Kept deliberately primitive — the value is a
/// double and the name is free text.</summary>
public sealed record MetricSample(string Name, double Value, DateTimeOffset At, string? Account = null);

/// <summary>
/// Derives operational metrics from the trade store: per-account trade
/// counts, win rates, P&amp;L, per-day aggregates, and — when the caller
/// supplies them — cycle latency and error observations. Exports CSV (for
/// spreadsheets / the trend pipeline) and JSON (for external monitoring).
/// Pure and headless: reads trades, writes strings, never touches the clock
/// except for the export stamp.
/// </summary>
public static class MetricsExporter
{
    /// <summary>CSV rows: one per account, plus a TOTAL row. Columns cover
    /// trade count, wins/losses, win rate, total/mean P&amp;L, mean and max
    /// stake — the numbers the performance dashboard shows, in exportable
    /// form.</summary>
    public static string ToCsv(IReadOnlyList<Trade> trades)
    {
        var sb = new StringBuilder("account,trades,wins,losses,win_rate,total_pnl,mean_pnl,max_stake\n");
        foreach (var group in ByAccount(trades))
        {
            var (name, list) = group;
            sb.Append(Csv(name)).Append(',')
                .Append(list.Count).Append(',')
                .Append(list.Count(t => t.IsWin)).Append(',')
                .Append(list.Count(t => !t.IsWin)).Append(',')
                .Append(WinRate(list).ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(TotalPnl(list).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(MeanPnl(list).ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(list.Max(t => t.Stake).ToString("0.##", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>JSON summary: per-account stats plus optional latency and
    /// error sections, suitable for a monitoring pipeline to ingest.</summary>
    public static string ToJson(
        IReadOnlyList<Trade> trades,
        IReadOnlyList<MetricSample>? latency = null,
        IReadOnlyList<MetricSample>? errors = null)
    {
        var accounts = ByAccount(trades)
            .Select(g => new
            {
                account = g.Item1,
                trades = g.Item2.Count,
                wins = g.Item2.Count(t => t.IsWin),
                losses = g.Item2.Count(t => !t.IsWin),
                win_rate = Math.Round(WinRate(g.Item2), 4),
                total_pnl = TotalPnl(g.Item2),
                mean_pnl = Math.Round(MeanPnl(g.Item2), 4),
                max_stake = g.Item2.Max(t => t.Stake),
                last_settled_utc = g.Item2.Max(t => t.SettledAt).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            })
            .ToArray();

        var latencySummary = latency is { Count: > 0 }
            ? new
            {
                count = latency.Count,
                mean_ms = Math.Round(latency.Average(m => m.Value), 1),
                p95_ms = Math.Round(Percentile(latency.Select(m => m.Value).OrderBy(v => v), 0.95), 1),
                max_ms = Math.Round(latency.Max(m => m.Value), 1)
            }
            : null;

        var errorSummary = errors is { Count: > 0 }
            ? new
            {
                count = errors.Count,
                total = errors.Sum(m => m.Value),
                by_account = errors.Where(m => m.Account is not null)
                    .GroupBy(m => m.Account!)
                    .Select(g => new { account = g.Key, errors = g.Sum(m => m.Value) })
                    .ToArray()
            }
            : null;

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            exported_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            total_trades = trades.Count,
            accounts,
            latency_ms = latencySummary,
            errors = errorSummary
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    private static IEnumerable<(string, List<Trade>)> ByAccount(IReadOnlyList<Trade> trades) =>
        trades
            .Where(t => !string.IsNullOrEmpty(t.AccountName))
            .GroupBy(t => t.AccountName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToList()));

    private static double WinRate(List<Trade> list) =>
        list.Count == 0 ? 0 : (double)list.Count(t => t.IsWin) / list.Count;

    private static decimal TotalPnl(List<Trade> list) => list.Sum(t => t.Profit);

    private static decimal MeanPnl(List<Trade> list) =>
        list.Count == 0 ? 0 : list.Average(t => t.Profit);

    private static double Percentile(IEnumerable<double> sorted, double p)
    {
        var list = sorted.ToArray();
        if (list.Length == 0)
        {
            return 0;
        }

        var idx = (int)Math.Ceiling(p * list.Length) - 1;
        return list[Math.Clamp(idx, 0, list.Length - 1)];
    }

    private static string Csv(string value) => value.Contains(',') ? $"\"{value}\"" : value;
}
