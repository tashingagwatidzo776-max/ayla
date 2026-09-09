using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.Core.Brain;
using Tf.Core.Models;
using Tf.Core.Optimization;

namespace Tf.App.ViewModels;

/// <summary>
/// ViewModel for the strategy optimizer. Allows backtesting different
/// brain strategies and finding optimal parameters.
/// </summary>
public partial class OptimizerViewModel : ObservableObject
{
    private readonly StrategyOptimizer _optimizer;

    [ObservableProperty]
    private string selectedStrategy = "TrendFollowing";

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
    private BacktestResultViewModel? lastResult;

    public IReadOnlyList<string> Strategies { get; } = new[]
    {
        "TrendFollowing", "Breakout", "MeanReversion", "Growth"
    };

    public ObservableCollection<OptimizationResultViewModel> Results { get; } = new();

    public OptimizerViewModel(StrategyOptimizer optimizer)
    {
        _optimizer = optimizer;
    }

    [RelayCommand]
    private async Task RunBacktestAsync()
    {
        if (IsRunning) return;

        try
        {
            IsRunning = true;
            StatusMessage = "Running backtest...";

            // Generate synthetic data for demo (in production, use real historical data)
            var data = GenerateSyntheticData(500);

            Func<IReadOnlyList<Tick>, LlmDecision> decideFunc = SelectedStrategy switch
            {
                "TrendFollowing" => window => TrendFollowingBrain.Decide(window, TrendFollowingBrain.TrendConfig.Default),
                "Breakout" => window => BreakoutBrain.Decide(window, BreakoutBrain.BreakoutConfig.Default),
                "MeanReversion" => window => MeanReversionBrain.Decide(window, MeanReversionBrain.MeanReversionConfig.Default),
                "Growth" => window => LlmDecision.Hold("Growth brain requires session engine"),
                _ => window => LlmDecision.Hold("Unknown strategy")
            };

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

            var data = GenerateSyntheticData(1000);

            // Define parameter ranges for each strategy
            var ranges = SelectedStrategy switch
            {
                "TrendFollowing" => new Dictionary<string, (double min, double max, double step)>
                {
                    ["FastEma"] = (5, 15, 1),
                    ["SlowEma"] = (15, 30, 1),
                    ["MinAdx"] = (20, 35, 5)
                },
                "Breakout" => new Dictionary<string, (double min, double max, double step)>
                {
                    ["BollingerPeriod"] = (15, 25, 2),
                    ["BollingerStdDev"] = (1.5, 2.5, 0.25),
                    ["BreakoutThreshold"] = (0.3, 0.8, 0.1)
                },
                "MeanReversion" => new Dictionary<string, (double min, double max, double step)>
                {
                    ["RsiPeriod"] = (10, 20, 2),
                    ["OversoldRsi"] = (25, 35, 2),
                    ["OverboughtRsi"] = (65, 75, 2),
                    ["MinZScore"] = (1.0, 2.0, 0.25)
                },
                _ => new Dictionary<string, (double min, double max, double step)>()
            };

            if (ranges.Count == 0)
            {
                StatusMessage = "Optimization not available for this strategy";
                return;
            }

            Func<IReadOnlyList<Tick>, Dictionary<string, double>, LlmDecision> decideFunc =
                (window, parameters) => SelectedStrategy switch
                {
                    "TrendFollowing" => TrendFollowingBrain.Decide(window,
                        new TrendFollowingBrain.TrendConfig(
                            FastEma: (int)parameters["FastEma"],
                            SlowEma: (int)parameters["SlowEma"],
                            MinAdx: parameters["MinAdx"])),
                    "Breakout" => BreakoutBrain.Decide(window,
                        new BreakoutBrain.BreakoutConfig(
                            BollingerPeriod: (int)parameters["BollingerPeriod"],
                            BollingerStdDev: parameters["BollingerStdDev"],
                            BreakoutThreshold: parameters["BreakoutThreshold"])),
                    "MeanReversion" => MeanReversionBrain.Decide(window,
                        new MeanReversionBrain.MeanReversionConfig(
                            RsiPeriod: (int)parameters["RsiPeriod"],
                            OversoldRsi: parameters["OversoldRsi"],
                            OverboughtRsi: parameters["OverboughtRsi"],
                            MinZScore: parameters["MinZScore"])),
                    _ => LlmDecision.Hold("Unknown")
                };

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

    private IReadOnlyList<Tick> GenerateSyntheticData(int count)
    {
        var random = new Random(42);
        var ticks = new List<Tick>();
        var price = 1.10000;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-count);

        for (int i = 0; i < count; i++)
        {
            // Random walk with mean reversion
            price += (random.NextDouble() - 0.5) * 0.0001;
            price = Math.Max(1.05, Math.Min(1.15, price));

            ticks.Add(new Tick(
                "frxEURUSD",
                price,
                price + random.NextDouble() * 0.00001,
                price - random.NextDouble() * 0.00001,
                timestamp.ToUnixTimeMilliseconds(),
                5));

            timestamp = timestamp.AddSeconds(5);
        }

        return ticks;
    }
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
