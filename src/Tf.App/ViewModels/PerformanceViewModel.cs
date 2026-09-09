using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.Core.Analytics;

namespace Tf.App.ViewModels;

/// <summary>
/// ViewModel for the performance dashboard. Shows P&L charts,
/// account comparison, and strategy analysis.
/// </summary>
public partial class PerformanceViewModel : ObservableObject
{
    private readonly PerformanceTracker _tracker;

    [ObservableProperty]
    private string summaryText = "No trades recorded yet";

    [ObservableProperty]
    private string selectedAccountId = "ALL";

    [ObservableProperty]
    private int selectedDays = 30;

    public ObservableCollection<AccountStatsViewModel> AccountStats { get; } = new();
    public ObservableCollection<StrategyStatsViewModel> StrategyStats { get; } = new();
    public ObservableCollection<DailyPerformanceViewModel> DailyHistory { get; } = new();
    public ObservableCollection<EquityPoint> EquityCurve { get; } = new();

    public PerformanceViewModel(PerformanceTracker tracker)
    {
        _tracker = tracker;
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

        // Strategy stats
        foreach (var stat in _tracker.GetAllStrategyStats())
        {
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
