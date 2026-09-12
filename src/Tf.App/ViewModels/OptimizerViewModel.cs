using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Models;
using Tf.Core.Optimization;

namespace Tf.App.ViewModels;

/// <summary>
/// ViewModel for the strategy optimizer. Allows backtesting different
/// brain strategies on real cached tick data or synthetic fallback.
/// </summary>
public partial class OptimizerViewModel : ObservableObject
{
    private readonly StrategyOptimizer _optimizer;
    private readonly TickHistoryCache _tickCache;

    [ObservableProperty]
    private string selectedStrategy = "TrendFollowing";

    [ObservableProperty]
    private string selectedSymbol = "frxEURUSD";

    [ObservableProperty]
    private decimal startBankroll = 5m;

    [ObservableProperty]
    private decimal riskFraction = 0.20m;

    [ObservableProperty]
    private int maxTrades = 100;

    [ObservableProperty]
    private int optimizationIterations = 50;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private string statusMessage = "Select a strategy and click Run Backtest";

    [ObservableProperty]
    private string dataSourceInfo = "";

    [ObservableProperty]
    private BacktestResultViewModel? lastResult;

    public IReadOnlyList<string> Strategies { get; } = new[]
    {
        "TrendFollowing", "Breakout", "MeanReversion", "Growth"
    };

    public ObservableCollection<string> AvailableSymbols { get; } = new();
    public ObservableCollection<OptimizationResultViewModel> Results { get; } = new();
    public ObservableCollection<SavedResultViewModel> SavedResults { get; } = new();

    public OptimizerViewModel(StrategyOptimizer optimizer, TickHistoryCache tickCache)
    {
        _optimizer = optimizer;
        _tickCache = tickCache;
        RefreshSymbols();
    }

    [RelayCommand]
    private void RefreshSymbols()
    {
        AvailableSymbols.Clear();
        foreach (var sym in _tickCache.GetSymbols())
            AvailableSymbols.Add(sym);
        if (!AvailableSymbols.Contains("frxEURUSD"))
            AvailableSymbols.Insert(0, "frxEURUSD");
        if (string.IsNullOrEmpty(SelectedSymbol) || !AvailableSymbols.Contains(SelectedSymbol))
            SelectedSymbol = AvailableSymbols.FirstOrDefault() ?? "frxEURUSD";
    }

    private IReadOnlyList<Tick> LoadData(int minCount)
    {
        var cached = _tickCache.GetTicks(SelectedSymbol);
        if (cached.Count >= minCount)
        {
            DataSourceInfo = $"{cached.Count:N0} real ticks for {SelectedSymbol}";
            return cached;
        }

        DataSourceInfo = $"Only {cached.Count} cached ticks — using synthetic data";
        return GenerateSyntheticData(Math.Max(minCount, 500));
    }

    [RelayCommand]
    private void LoadSavedResults()
    {
        SavedResults.Clear();
        foreach (var file in _optimizer.GetSavedResults())
        {
            var result = _optimizer.LoadResult(file);
            if (result != null)
            {
                SavedResults.Add(new SavedResultViewModel
                {
                    FilePath = file,
                    Strategy = result.StrategyName,
                    BestSharpe = result.BestResult.SharpeRatio,
                    BestPnl = result.BestResult.TotalProfit,
                    Iterations = result.TotalIterations,
                    BestParams = string.Join(", ", result.BestParameters.Select(p => $"{p.Key}={p.Value:0.##}"))
                });
            }
        }
    }

    [RelayCommand]
    private async Task RunBacktestAsync()
    {
        if (IsRunning) return;

        try
        {
            IsRunning = true;
            StatusMessage = "Running backtest...";

            var data = LoadData(50);

            Func<IReadOnlyList<Tick>, LlmDecision> decideFunc = StrategyCatalog.ResolveDecideFunc(SelectedStrategy);

            var result = await Task.Run(() =>
                _optimizer.RunBacktest(data, SelectedStrategy, decideFunc, StartBankroll, RiskFraction, MaxTrades));

            LastResult = new BacktestResultViewModel(result);
            StatusMessage = $"Backtest complete: {result.TotalTrades} trades, " +
                          $"P&L: {(result.TotalProfit >= 0 ? "+" : "")}{result.TotalProfit:0.##}, " +
                          $"Win Rate: {result.WinRate:P0}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backtest failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunOptimizationAsync()
    {
        if (IsRunning) return;

        try
        {
            IsRunning = true;
            StatusMessage = "Running optimization...";

            var data = LoadData(100);

            // Define parameter ranges for each strategy
            var ranges = StrategyCatalog.ResolveParameterRanges(SelectedStrategy);

            if (ranges.Count == 0)
            {
                StatusMessage = "Optimization not available for this strategy";
                return;
            }

            Func<IReadOnlyList<Tick>, Dictionary<string, double>, LlmDecision> decideFunc =
                StrategyCatalog.ResolveParameterizedDecideFunc(SelectedStrategy);

            var result = await Task.Run(() =>
                _optimizer.Optimize(data, SelectedStrategy, decideFunc, ranges, OptimizationIterations));

            Results.Clear();
            foreach (var (param, res) in result.AllResults)
            {
                Results.Add(new OptimizationResultViewModel
                {
                    Parameters = string.Join(", ", param.Select(p => $"{p.Key}={p.Value:0.##}")),
                    TotalProfit = res.TotalProfit,
                    SharpeRatio = res.SharpeRatio,
                    MaxDrawdown = res.MaxDrawdown
                });
            }

            StatusMessage = $"Optimization complete! Best Sharpe: {result.BestResult.SharpeRatio:0.2}, " +
                          $"Best P&L: {(result.BestResult.TotalProfit >= 0 ? "+" : "")}{result.BestResult.TotalProfit:0.##}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Optimization failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private IReadOnlyList<Tick> GenerateSyntheticData(int count) =>
        StrategyCatalog.GenerateSyntheticData(count);
}

public class BacktestResultViewModel
{
    public string Strategy { get; set; }
    public decimal StartBankroll { get; set; }
    public decimal EndBankroll { get; set; }
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal WinRate { get; set; }
    public decimal MaxDrawdown { get; set; }
    public double SharpeRatio { get; set; }

    public BacktestResultViewModel(BacktestResult result)
    {
        Strategy = result.StrategyName;
        StartBankroll = result.StartBankroll;
        EndBankroll = result.EndBankroll;
        TotalTrades = result.TotalTrades;
        Wins = result.Wins;
        Losses = result.Losses;
        TotalProfit = result.TotalProfit;
        WinRate = result.WinRate;
        MaxDrawdown = result.MaxDrawdown;
        SharpeRatio = result.SharpeRatio;
    }
}

public class OptimizationResultViewModel
{
    public string Parameters { get; set; } = "";
    public decimal TotalProfit { get; set; }
    public double SharpeRatio { get; set; }
    public decimal MaxDrawdown { get; set; }
}

public class SavedResultViewModel
{
    public string FilePath { get; set; } = "";
    public string Strategy { get; set; } = "";
    public double BestSharpe { get; set; }
    public decimal BestPnl { get; set; }
    public int Iterations { get; set; }
    public string BestParams { get; set; } = "";
}
