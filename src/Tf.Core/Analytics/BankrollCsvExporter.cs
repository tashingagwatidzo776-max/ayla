using System.Globalization;
using System.Text;
using Tf.Core.Models;

namespace Tf.Core.Analytics;

/// <summary>One row of the growth-bankroll trend CSV.</summary>
/// <param name="EpochSeconds">UTC unix epoch of the day's last settled trade.</param>
/// <param name="Account">Trading account display name.</param>
/// <param name="Bankroll">Closing bankroll for the day (compounding daily).</param>
public sealed record BankrollPoint(long EpochSeconds, string Account, decimal Bankroll);

/// <summary>
/// Reduces the trade store's settled growth trades to a per-day closing
/// bankroll per account — the same reduction as
/// <c>scripts/export_bankroll.py</c> (daily StartBudget reset: each day opens
/// at the session's opening bankroll, so the daily delta is exactly that
/// day's summed P/L, compounding across days from a base of 0; the store
/// holds deltas, not absolute balances). The CSV feeds the trend page's
/// money axis via the Pages deploy.
/// </summary>
public static class BankrollCsvExporter
{
    /// <summary>Settled outcomes that count toward the bankroll curve; the
    /// growth runner applies exactly these to its session engine.</summary>
    private static readonly ContractStatus[] Settled =
    [
        ContractStatus.Won, ContractStatus.Lost, ContractStatus.Sold, ContractStatus.Cancelled
    ];

    /// <summary>Daily closing bankroll rows in the export's column and sort
    /// order (account-major, day-ascending; one row per account+day with
    /// settled growth trades). Decimal formatting is float-parse-compatible
    /// with the Python script but may keep trailing zeros ("-0.10" vs
    /// "-0.1") — consumers parse floats, never compare bytes.</summary>
    public static IReadOnlyList<BankrollPoint> DailyClosingBankroll(IReadOnlyList<Trade> trades)
    {
        var perDay = new Dictionary<(string Account, DateOnly Day), (decimal Pnl, long LastEpoch)>();
        foreach (var t in trades)
        {
            if (t.Source != TradeSource.Growth || t.AccountName is null) continue;
            if (!Settled.Contains(t.Outcome)) continue;

            var key = (t.AccountName, DateOnly.FromDateTime(t.SettledAt.UtcDateTime));
            var (pnl, lastEpoch) = perDay.TryGetValue(key, out var e) ? e : (0m, 0L);
            perDay[key] = (pnl + t.Profit, Math.Max(lastEpoch, t.SettledAt.ToUnixTimeSeconds()));
        }

        var rows = new List<BankrollPoint>(perDay.Count);
        var currentAccount = default(string?);
        var bank = 0m;
        foreach (var kv in perDay.OrderBy(kv => kv.Key.Account, StringComparer.Ordinal)
                     .ThenBy(kv => kv.Key.Day))
        {
            if (kv.Key.Account != currentAccount)
            {
                currentAccount = kv.Key.Account;
                bank = 0;
            }

            bank = Math.Round(bank + kv.Value.Pnl, 2);
            rows.Add(new BankrollPoint(kv.Value.LastEpoch, kv.Key.Account, bank));
        }

        return rows;
    }

    /// <summary>Renders rows in the trend CSV format
    /// <c>epoch_seconds,account,bankroll</c> (header included).</summary>
    public static string Render(IReadOnlyList<BankrollPoint> rows)
    {
        var sb = new StringBuilder("epoch_seconds,account,bankroll\n");
        foreach (var r in rows)
        {
            sb.Append(r.EpochSeconds).Append(',')
                .Append(r.Account).Append(',')
                .Append(r.Bankroll.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Keeps the growth-bankroll CSV fresh automatically: rewrites it from the
/// trade store on construction and again on every settled trade
/// (<see cref="TradeStore.TradeAdded"/>), so the trend page's money axis no
/// longer depends on a manual <c>python scripts/export_bankroll.py</c> run.
/// Two copies are maintained: the canonical export under the app data
/// directory, and (best-effort, only when the app runs from a repo checkout)
/// the committed <c>docs/growth-bankroll.csv</c> the Pages deploy publishes.
/// </summary>
public sealed class BankrollCsvFile : IDisposable
{
    private readonly TradeStore _store;
    private readonly string? _docsPath;
    private readonly string _appDataPath;
    private readonly object _sync = new();

    public BankrollCsvFile(TradeStore store, string? docsPath, string appDataPath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _docsPath = docsPath is null ? null : Path.GetFullPath(docsPath);
        _appDataPath = Path.GetFullPath(appDataPath);
        _store.TradeAdded += OnTradeAdded;
        Refresh();
    }

    /// <summary>docs/growth-bankroll.csv inside the repo checkout the app is
    /// running from, or null when there is none (e.g. an installed copy).</summary>
    public string? DocsPath => _docsPath;

    public static string? FindRepoDocsPath(string startDirectory)
    {
        for (var dir = Path.GetFullPath(startDirectory); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var docs = Path.Combine(dir, "docs");
            if (Directory.Exists(docs) && Directory.Exists(Path.Combine(dir, ".git")))
            {
                return Path.Combine(docs, "growth-bankroll.csv");
            }
        }

        return null;
    }

    /// <summary>Rewrites both copies from the store's current trades. Safe to
    /// call concurrently — settled trades can arrive from several runners.</summary>
    public void Refresh()
    {
        lock (_sync)
        {
            var csv = BankrollCsvExporter.Render(
                BankrollCsvExporter.DailyClosingBankroll(_store.Trades));
            WriteAtomically(_appDataPath, csv, ensureDirectory: true);
            if (_docsPath is not null)
            {
                WriteAtomically(_docsPath, csv, ensureDirectory: false);
            }
        }
    }

    private void OnTradeAdded(Trade trade) => Refresh();

    /// <summary>Atomic write (temp + move) so a crash mid-write can never
    /// leave a truncated CSV. The docs copy is best-effort: outside a repo
    /// checkout it is simply skipped, and permission problems never break
    /// trading.</summary>
    private static void WriteAtomically(string path, string content, bool ensureDirectory)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            if (ensureDirectory)
            {
                Directory.CreateDirectory(dir);
            }
            else if (!Directory.Exists(dir))
            {
                return;
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort export; never let chart plumbing break trading.
        }
    }

    public void Dispose() => _store.TradeAdded -= OnTradeAdded;
}
