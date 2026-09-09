using System.Collections.Concurrent;
using System.Text.Json;
using Tf.Core.Models;

namespace Tf.Core.Analytics;

/// <summary>
/// Tracks performance metrics across all accounts and strategies.
/// Maintains rolling statistics and historical data for charting.
/// </summary>
public sealed class PerformanceTracker
{
    private readonly string _dataDir;
    private readonly ConcurrentDictionary<Guid, AccountStats> _accountStats = new();
    private readonly ConcurrentDictionary<string, StrategyStats> _strategyStats = new();
    private readonly List<DailyPerformance> _dailyHistory = new();
    private readonly object _lock = new();

    public PerformanceTracker(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        LoadHistory();
    }

    /// <summary>Record a completed trade.</summary>
    public void RecordTrade(Trade trade)
    {
        // Account stats
        var accountStats = _accountStats.GetOrAdd(
            trade.AccountId ?? Guid.Empty,
            _ => new AccountStats { AccountId = trade.AccountId ?? Guid.Empty, AccountName = trade.AccountName ?? "Primary" });

        lock (accountStats)
        {
            accountStats.TotalTrades++;
            accountStats.TotalStake += trade.Stake;
            accountStats.TotalProfit += trade.Profit;
            if (trade.IsWin)
                accountStats.Wins++;
            else
                accountStats.Losses++;

            accountStats.MaxDrawdown = Math.Min(accountStats.MaxDrawdown, trade.Profit);
            accountStats.LastTradeAt = trade.SettledAt;
        }

        // Strategy stats
        var strategy = trade.Source ?? "Unknown";
        var strategyStats = _strategyStats.GetOrAdd(strategy, _ => new StrategyStats { Strategy = strategy });

        lock (strategyStats)
        {
            strategyStats.TotalTrades++;
            strategyStats.TotalStake += trade.Stake;
            strategyStats.TotalProfit += trade.Profit;
            if (trade.IsWin)
                strategyStats.Wins++;
            else
                strategyStats.Losses++;

            strategyStats.MaxDrawdown = Math.Min(strategyStats.MaxDrawdown, trade.Profit);
            strategyStats.LastTradeAt = trade.SettledAt;
        }

        // Daily stats
        lock (_lock)
        {
            var date = trade.SettledAt.Date;
            var daily = _dailyHistory.FirstOrDefault(d => d.Date == date && d.AccountId == trade.AccountId);
            if (daily == null)
            {
                daily = new DailyPerformance
                {
                    Date = date,
                    AccountId = trade.AccountId ?? Guid.Empty,
                    AccountName = trade.AccountName ?? "Primary"
                };
                _dailyHistory.Add(daily);
            }

            daily.Trades++;
            daily.Stake += trade.Stake;
            daily.Profit += trade.Profit;
            if (trade.IsWin) daily.Wins++;
            else daily.Losses++;
        }
    }

    /// <summary>Get stats for a specific account.</summary>
    public AccountStats? GetAccountStats(Guid accountId)
    {
        return _accountStats.TryGetValue(accountId, out var stats) ? stats : null;
    }

    /// <summary>Get stats for a specific strategy.</summary>
    public StrategyStats? GetStrategyStats(string strategy)
    {
        return _strategyStats.TryGetValue(strategy, out var stats) ? stats : null;
    }

    /// <summary>Get all account stats.</summary>
    public IReadOnlyList<AccountStats> GetAllAccountStats()
    {
        return _accountStats.Values.ToArray();
    }

    /// <summary>Get all strategy stats.</summary>
    public IReadOnlyList<StrategyStats> GetAllStrategyStats()
    {
        return _strategyStats.Values.ToArray();
    }

    /// <summary>Get daily performance history for charting.</summary>
    public IReadOnlyList<DailyPerformance> GetDailyHistory(Guid? accountId = null, int days = 30)
    {
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).Date;
            return _dailyHistory
                .Where(d => d.Date >= cutoff && (accountId == null || d.AccountId == accountId))
                .OrderBy(d => d.Date)
                .ToList();
        }
    }

    /// <summary>Calculate overall performance summary.</summary>
    public PerformanceSummary GetSummary()
    {
        var allStats = _accountStats.Values.ToList();
        var totalTrades = allStats.Sum(s => s.TotalTrades);
        var totalProfit = allStats.Sum(s => s.TotalProfit);
        var totalWins = allStats.Sum(s => s.Wins);

        return new PerformanceSummary
        {
            TotalAccounts = allStats.Count,
            TotalTrades = totalTrades,
            TotalProfit = totalProfit,
            WinRate = totalTrades > 0 ? (decimal)totalWins / totalTrades : 0,
            BestAccount = allStats.OrderByDescending(s => s.TotalProfit).FirstOrDefault(),
            WorstAccount = allStats.OrderBy(s => s.TotalProfit).FirstOrDefault(),
            BestStrategy = _strategyStats.Values.OrderByDescending(s => s.TotalProfit).FirstOrDefault(),
            WorstStrategy = _strategyStats.Values.OrderBy(s => s.TotalProfit).FirstOrDefault()
        };
    }

    /// <summary>Save history to disk.</summary>
    public void Save()
    {
        lock (_lock)
        {
            var path = Path.Combine(_dataDir, "performance.json");
            var data = new PerformanceData
            {
                Accounts = _accountStats.Values.ToList(),
                Strategies = _strategyStats.Values.ToList(),
                Daily = _dailyHistory.ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void LoadHistory()
    {
        try
        {
            var path = Path.Combine(_dataDir, "performance.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PerformanceData>(json);
            if (data == null) return;

            foreach (var stat in data.Accounts)
                _accountStats[stat.AccountId] = stat;

            foreach (var stat in data.Strategies)
                _strategyStats[stat.Strategy] = stat;

            lock (_lock)
            {
                _dailyHistory.AddRange(data.Daily);
            }
        }
        catch { /* start fresh */ }
    }
}

public sealed class AccountStats
{
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public int TotalTrades { get; set; }
    public decimal TotalStake { get; set; }
    public decimal TotalProfit { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal MaxDrawdown { get; set; }
    public DateTimeOffset LastTradeAt { get; set; }

    public decimal WinRate => TotalTrades > 0 ? (decimal)Wins / TotalTrades : 0;
    public decimal ROI => TotalStake > 0 ? TotalProfit / TotalStake * 100 : 0;
}

public sealed class StrategyStats
{
    public string Strategy { get; set; } = "";
    public int TotalTrades { get; set; }
    public decimal TotalStake { get; set; }
    public decimal TotalProfit { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal MaxDrawdown { get; set; }
    public DateTimeOffset LastTradeAt { get; set; }

    public decimal WinRate => TotalTrades > 0 ? (decimal)Wins / TotalTrades : 0;
    public decimal ROI => TotalStake > 0 ? TotalProfit / TotalStake * 100 : 0;
}

public sealed class DailyPerformance
{
    public DateTime Date { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public int Trades { get; set; }
    public decimal Stake { get; set; }
    public decimal Profit { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

    public decimal WinRate => Trades > 0 ? (decimal)Wins / Trades : 0;
}

public sealed class PerformanceSummary
{
    public int TotalAccounts { get; set; }
    public int TotalTrades { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal WinRate { get; set; }
    public AccountStats? BestAccount { get; set; }
    public AccountStats? WorstAccount { get; set; }
    public StrategyStats? BestStrategy { get; set; }
    public StrategyStats? WorstStrategy { get; set; }
}

internal sealed class PerformanceData
{
    public List<AccountStats> Accounts { get; set; } = new();
    public List<StrategyStats> Strategies { get; set; } = new();
    public List<DailyPerformance> Daily { get; set; } = new();
}
