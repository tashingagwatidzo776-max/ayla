using System.Collections.Concurrent;
using System.Text.Json;

namespace Tf.Core.Logging;

/// <summary>
/// Comprehensive trade journal that captures every brain decision, trade
/// execution, and account activity. Thread-safe and file-backed.
/// </summary>
public sealed class TradeJournal : IDisposable
{
    private readonly string _journalDir;
    private readonly ConcurrentQueue<JournalEntry> _pending = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();
    private bool _disposed;

    public TradeJournal(string journalDir)
    {
        _journalDir = journalDir;
        Directory.CreateDirectory(journalDir);
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Log a brain decision.</summary>
    public void LogBrainDecision(Guid accountId, string brainType, string symbol, 
        string direction, decimal stake, double confidence, string reasoning, string source = "")
    {
        Enqueue(new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            Category = "BRAIN_DECISION",
            Details = JsonSerializer.Serialize(new
            {
                BrainType = brainType,
                Symbol = symbol,
                Direction = direction,
                Stake = stake,
                Confidence = confidence,
                Reasoning = reasoning,
                Source = source
            })
        });
    }

    /// <summary>Log a trade execution attempt.</summary>
    public void LogTradeExecution(Guid accountId, string proposalId, string symbol,
        string direction, decimal stake, string status, string error = "")
    {
        Enqueue(new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            Category = "TRADE_EXECUTION",
            Details = JsonSerializer.Serialize(new
            {
                ProposalId = proposalId,
                Symbol = symbol,
                Direction = direction,
                Stake = stake,
                Status = status,
                Error = error
            })
        });
    }

    /// <summary>Log a trade settlement.</summary>
    public void LogTradeSettlement(Guid accountId, string contractId, bool won,
        decimal payout, decimal profit, decimal newBankroll)
    {
        Enqueue(new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            Category = "TRADE_SETTLEMENT",
            Details = JsonSerializer.Serialize(new
            {
                ContractId = contractId,
                Won = won,
                Payout = payout,
                Profit = profit,
                NewBankroll = newBankroll
            })
        });
    }

    /// <summary>Log growth engine state change.</summary>
    public void LogGrowthState(Guid accountId, string state, decimal bankroll,
        int lossStreak, string reason = "")
    {
        Enqueue(new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            Category = "GROWTH_STATE",
            Details = JsonSerializer.Serialize(new
            {
                State = state,
                Bankroll = bankroll,
                LossStreak = lossStreak,
                Reason = reason
            })
        });
    }

    /// <summary>Log account connection event.</summary>
    public void LogAccountEvent(Guid accountId, string accountName, string @event, string details = "")
    {
        Enqueue(new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            Category = "ACCOUNT_EVENT",
            Details = JsonSerializer.Serialize(new
            {
                AccountName = accountName,
                Event = @event,
                Details = details
            })
        });
    }

    /// <summary>Log a general message.</summary>
    public void Log(Guid accountId, string category, string message, string details = "")
    {
        Enqueue(new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            AccountId = accountId,
            Category = category,
            Details = string.IsNullOrEmpty(details) ? message : $"{message}: {details}"
        });
    }

    /// <summary>Get recent journal entries for an account.</summary>
    public IReadOnlyList<JournalEntry> GetRecent(Guid? accountId = null, int count = 100)
    {
        var entries = new List<JournalEntry>();
        var files = Directory.GetFiles(_journalDir, "journal_*.jsonl")
            .OrderByDescending(f => f)
            .Take(10);

        foreach (var file in files)
        {
            // Read under the write lock: the flush timer may append to the
            // newest file while we read it, and Windows forbids concurrent
            // openers regardless of share mode (IOException).
            string[] lines;
            lock (_writeLock)
            {
                lines = File.ReadAllLines(file);
            }
            foreach (var line in lines.Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                    if (entry != null && (accountId == null || entry.AccountId == accountId))
                    {
                        entries.Add(entry);
                        if (entries.Count >= count) break;
                    }
                }
                catch { /* skip malformed lines */ }
            }
            if (entries.Count >= count) break;
        }

        return entries;
    }

    /// <summary>Get journal statistics for an account.</summary>
    public JournalStats GetStats(Guid accountId, DateTimeOffset? since = null)
    {
        var entries = GetRecent(accountId, 10000);
        if (since.HasValue)
            entries = entries.Where(e => e.Timestamp >= since.Value).ToList();

        var decisions = entries.Where(e => e.Category == "BRAIN_DECISION").ToList();
        var settlements = entries.Where(e => e.Category == "TRADE_SETTLEMENT").ToList();

        var wins = 0;
        var losses = 0;
        foreach (var s in settlements)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(s.Details);
                if (doc.RootElement.TryGetProperty("Won", out var wonEl))
                {
                    if (wonEl.GetBoolean()) wins++;
                    else losses++;
                }
            }
            catch
            {
                // Fallback to string matching if JSON parsing fails
                if (s.Details.Contains("\"Won\":true")) wins++;
                else if (s.Details.Contains("\"Won\":false")) losses++;
            }
        }

        return new JournalStats
        {
            TotalDecisions = decisions.Count,
            TotalSettlements = settlements.Count,
            WinningTrades = wins,
            LosingTrades = losses,
            AccountId = accountId,
            PeriodStart = entries.LastOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow,
            PeriodEnd = entries.FirstOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow
        };
    }

    private void Enqueue(JournalEntry entry)
    {
        if (_disposed) return;
        _pending.Enqueue(entry);
    }

    /// <summary>Writes any pending buffered entries to disk immediately
    /// (the timer calls this every few seconds; readers may call it to force
    /// durability before reading the journal back).</summary>
    public void Flush()
    {
        if (_pending.IsEmpty) return;

        var fileName = $"journal_{DateTime.UtcNow:yyyyMMdd}.jsonl";
        var filePath = Path.Combine(_journalDir, fileName);

        // Dequeue and write under one lock: a timer flush overlapping the
        // final flush from Dispose must not race two appenders onto the same
        // file (IOException: file used by another process).
        lock (_writeLock)
        {
            if (_pending.IsEmpty) return;

            var batch = new List<JournalEntry>();
            while (_pending.TryDequeue(out var entry))
                batch.Add(entry);

            if (batch.Count == 0) return;

            try
            {
                using var writer = new StreamWriter(filePath, append: true);
                foreach (var entry in batch)
                {
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The journal directory vanished (e.g. cleaned up while a
                // flush timer was still in flight). Journaling is best-effort:
                // never let the background flush kill the process.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer?.Dispose();
        Flush();
    }
}

public sealed class JournalEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public Guid AccountId { get; set; }
    public string Category { get; set; } = "";
    public string Details { get; set; } = "";
}

public sealed class JournalStats
{
    public Guid AccountId { get; set; }
    public int TotalDecisions { get; set; }
    public int TotalSettlements { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }

    public decimal WinRate => TotalSettlements > 0 ? (decimal)WinningTrades / TotalSettlements : 0;
}
