using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.Core;
using DongGfx.Core.Analytics;

namespace DongGfx.App.ViewModels;

/// <summary>
/// ViewModel for the performance dashboard. Shows P&L charts,
/// account comparison, and strategy analysis.
/// </summary>
public partial class PerformanceViewModel : ObservableObject
{
    private readonly PerformanceTracker _tracker;
    private readonly TradeStore? _tradeStore;
    private readonly string? _metricsExportDirectory;
    private readonly MetricsCollector? _metrics;
    private readonly Dispatcher _dispatcher;
    private System.Threading.Timer? _telemetryTimer;

    [ObservableProperty]
    private string summaryText = "No trades recorded yet";

    [ObservableProperty]
    private string selectedAccountId = "ALL";

    [ObservableProperty]
    private int selectedDays = 30;

    /// <summary>Points for the equity curve polyline ("x1,y1 x2,y2 ..." format).</summary>
    [ObservableProperty]
    private string equityCurvePoints = "";

    /// <summary>Live cycle-telemetry panel text (count/latency/errors),
    /// refreshed periodically between manual exports.</summary>
    [ObservableProperty]
    private string telemetrySummaryText = "no telemetry yet";

    [ObservableProperty]
    private int telemetryErrorCount;

    public ObservableCollection<AccountStatsViewModel> AccountStats { get; } = new();
    public ObservableCollection<StrategyStatsViewModel> StrategyStats { get; } = new();
    public ObservableCollection<DailyPerformanceViewModel> DailyHistory { get; } = new();
    public ObservableCollection<EquityPoint> EquityCurve { get; } = new();
    public ObservableCollection<StrategyComparisonItem> StrategyComparison { get; } = new();

    /// <param name="metricsExportDirectory">Overrides the metrics export
    /// target directory (defaults to Documents); injectable for tests.</param>
    /// <param name="metrics">Live cycle telemetry (latency/errors) collected
    /// by the growth runners; when present, the JSON export carries the
    /// latency and error sections with real runtime samples.</param>
    public PerformanceViewModel(PerformanceTracker tracker, TradeStore? tradeStore = null,
        string? metricsExportDirectory = null, MetricsCollector? metrics = null)
    {
        _tracker = tracker;
        _tradeStore = tradeStore;
        _metricsExportDirectory = metricsExportDirectory;
        _metrics = metrics;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Starts the periodic live-telemetry refresh (3s). Headless in
    /// tests — no dispatcher pump runs unless a WPF test context created one.</summary>
    public void StartTelemetryRefresh() =>
        _telemetryTimer = new System.Threading.Timer(
            _ => OnUiThread(RefreshTelemetry), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

    /// <summary>Recomputes the telemetry panel from the collector snapshot.
    /// Safe to call directly (tests); the timer path marshals to the UI thread.</summary>
    public void RefreshTelemetry()
    {
        if (_metrics is null)
        {
            return;
        }

        var summary = TelemetrySummaryBuilder.Build(_metrics.Latency, _metrics.Errors);
        TelemetrySummaryText = TelemetrySummaryBuilder.Format(summary);
        TelemetryErrorCount = summary?.ErrorCount ?? 0;
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    [RelayCommand]
    private void Refresh()
    {
        AccountStats.Clear();
        StrategyStats.Clear();
        DailyHistory.Clear();
        EquityCurve.Clear();

        // Summary
        var summary = _tracker.GetSummary();
        SummaryText = $"Accounts: {summary.TotalAccounts} | " +
                     $"Trades: {summary.TotalTrades} | " +
                     $"P&L: {(summary.TotalProfit >= 0 ? "+" : "")}{summary.TotalProfit:0.##} | " +
                     $"Win Rate: {summary.WinRate:P0}";

        // Account stats
        foreach (var stat in _tracker.GetAllAccountStats())
        {
            AccountStats.Add(new AccountStatsViewModel
            {
                AccountName = stat.AccountName,
                TotalTrades = stat.TotalTrades,
                Wins = stat.Wins,
                Losses = stat.Losses,
                WinRate = stat.WinRate,
                TotalProfit = stat.TotalProfit,
                ROI = stat.ROI,
                MaxDrawdown = stat.MaxDrawdown
            });
        }

        // Strategy stats + comparison
        StrategyComparison.Clear();
        var rankedStrategies = _tracker.GetAllStrategyStats()
            .OrderByDescending(s => s.TotalProfit)
            .ToList();

        for (var i = 0; i < rankedStrategies.Count; i++)
        {
            var stat = rankedStrategies[i];
            StrategyStats.Add(new StrategyStatsViewModel
            {
                Strategy = stat.Strategy,
                TotalTrades = stat.TotalTrades,
                Wins = stat.Wins,
                Losses = stat.Losses,
                WinRate = stat.WinRate,
                TotalProfit = stat.TotalProfit,
                ROI = stat.ROI,
                MaxDrawdown = stat.MaxDrawdown
            });

            var rank = i == 0 ? "🥇 Best" : i == 1 ? "🥈 2nd" : i == 2 ? "🥉 3rd" : $"#{i + 1}";
            StrategyComparison.Add(new StrategyComparisonItem
            {
                Strategy = stat.Strategy,
                TotalTrades = stat.TotalTrades,
                WinRate = stat.WinRate,
                TotalProfit = stat.TotalProfit,
                IsBest = i == 0,
                Rank = rank
            });
        }

        // Daily history
        var accountId = SelectedAccountId == "ALL" ? (Guid?)null :
            Guid.TryParse(SelectedAccountId, out var id) ? id : null;

        var history = _tracker.GetDailyHistory(accountId, SelectedDays);
        var runningPnl = 0m;

        foreach (var day in history)
        {
            runningPnl += day.Profit;
            DailyHistory.Add(new DailyPerformanceViewModel
            {
                Date = day.Date.ToString("MM/dd"),
                Trades = day.Trades,
                Profit = day.Profit,
                WinRate = day.WinRate,
                RunningPnl = runningPnl
            });

            EquityCurve.Add(new EquityPoint
            {
                Day = day.Date.ToString("MM/dd"),
                Value = runningPnl
            });
        }

        // Generate polyline points for the equity curve chart.
        if (EquityCurve.Count > 1)
        {
            var minVal = EquityCurve.Min(e => e.Value);
            var maxVal = EquityCurve.Max(e => e.Value);
            var range = maxVal - minVal;
            if (range == 0) range = 1; // avoid division by zero

            var points = new List<string>();
            for (var i = 0; i < EquityCurve.Count; i++)
            {
                var x = (double)i / (EquityCurve.Count - 1) * 400; // scale to 400px width
                var y = 140 - (double)(EquityCurve[i].Value - minVal) / (double)range * 130; // scale to 130px height, inverted
                points.Add($"{x:0},{y:0}");
            }
            EquityCurvePoints = string.Join(" ", points);
        }
        else
        {
            EquityCurvePoints = "";
        }

        RefreshTelemetry();

        if (_metrics is not null && _telemetryTimer is null)
        {
            StartTelemetryRefresh();
        }
    }

    [RelayCommand]
    private void ExportToCsv()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"tf_performance_{DateTime.UtcNow:yyyyMMdd}.csv");

        using var writer = new StreamWriter(path);
        writer.WriteLine("Date,Trades,Profit,WinRate,RunningPnl");

        foreach (var day in DailyHistory)
        {
            writer.WriteLine($"{day.Date},{day.Trades},{day.Profit:0.##},{day.WinRate:P0},{day.RunningPnl:0.##}");
        }

        statusMessage = $"Exported to {path}";
        OnPropertyChanged(nameof(StatusMessage));
    }

    /// <summary>Operational metrics export: per-account trades/wins/P&L to
    /// CSV and JSON straight from the trade store (independent of the
    /// tracker's daily aggregation), for spreadsheets and monitoring.</summary>
    [RelayCommand]
    private void ExportMetrics()
    {
        if (_tradeStore is null)
        {
            statusMessage = "Metrics export unavailable: no trade store attached";
            OnPropertyChanged(nameof(StatusMessage));
            return;
        }

        try
        {
            var trades = _tradeStore.Trades;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
            var exportDir = _metricsExportDirectory
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Directory.CreateDirectory(exportDir);
            var csvPath = Path.Combine(exportDir, $"tf_metrics_{stamp}.csv");
            var jsonPath = Path.Combine(exportDir, $"tf_metrics_{stamp}.json");

            File.WriteAllText(csvPath, MetricsExporter.ToCsv(trades));
            File.WriteAllText(jsonPath, MetricsExporter.ToJson(
                trades,
                latency: _metrics?.Latency,
                errors: _metrics?.Errors));

            statusMessage = $"Exported {trades.Count} trade(s) to {csvPath} + .json";
        }
        catch (Exception ex)
        {
            statusMessage = $"Metrics export failed: {ex.Message}";
        }

        OnPropertyChanged(nameof(StatusMessage));
    }

    private string statusMessage = "";
    public string StatusMessage
    {
        get => statusMessage;
        set => SetProperty(ref statusMessage, value);
    }
}

public class AccountStatsViewModel
{
    public string AccountName { get; set; } = "";
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal ROI { get; set; }
    public decimal MaxDrawdown { get; set; }
}

public class StrategyStatsViewModel
{
    public string Strategy { get; set; } = "";
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal ROI { get; set; }
    public decimal MaxDrawdown { get; set; }
}

public class DailyPerformanceViewModel
{
    public string Date { get; set; } = "";
    public int Trades { get; set; }
    public decimal Profit { get; set; }
    public decimal WinRate { get; set; }
    public decimal RunningPnl { get; set; }
}

public class EquityPoint
{
    public string Day { get; set; } = "";
    public decimal Value { get; set; }
}

public class StrategyComparisonItem
{
    public string Strategy { get; set; } = "";
    public int TotalTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalProfit { get; set; }
    public bool IsBest { get; set; }
    public string Rank { get; set; } = "";
}
