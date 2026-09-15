using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Models;

namespace Tf.Core.Tests;

[Trait("Category", "Unit")]
public class BankrollCsvExporterTests : IDisposable
{
    private readonly string _dir;

    public BankrollCsvExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bankroll-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Trade MakeTrade(
        decimal profit,
        DateTimeOffset settledAt,
        string source = TradeSource.Growth,
        string? accountName = "Alpha",
        ContractStatus outcome = ContractStatus.Won) =>
        new(Guid.NewGuid(), "frxEURUSD", Direction.Rise, 1m, "USD", 1.1, 0,
            "C-" + Guid.NewGuid().ToString("N")[..8], outcome, profit, 1.2, null,
            settledAt, AccountName: accountName, Source: source);

    private static readonly DateTimeOffset Day1 = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reduction_MatchesThePythonExport()
    {
        // Only settled Growth trades with an account name count; same-day
        // trades sum into one row stamped with the day's last settlement;
        // days compound per account from a base of 0.
        var trades = new List<Trade>
        {
            // Day 1, Alpha: +0.90 then -1.00 → closing bankroll -0.10.
            MakeTrade(0.90m, Day1.AddMinutes(5)),
            MakeTrade(-1.00m, Day1.AddMinutes(30), outcome: ContractStatus.Lost),
            MakeTrade(0.50m, Day1, accountName: "Beta"),

            // Excluded: not Growth, in-flight, unknown outcome, no account.
            MakeTrade(5.00m, Day1, source: TradeSource.Manual),
            MakeTrade(0.99m, Day1, outcome: ContractStatus.Open),
            MakeTrade(1.23m, Day1, outcome: ContractStatus.Unknown),
            MakeTrade(0.70m, Day1, accountName: null),

            // Day 2, Alpha: Sold -0.25 → closing bankroll -0.35.
            MakeTrade(-0.25m, Day2, outcome: ContractStatus.Sold),
        };

        var rows = BankrollCsvExporter.DailyClosingBankroll(trades);

        var expected = new[]
        {
            new BankrollPoint(Day1.AddMinutes(30).ToUnixTimeSeconds(), "Alpha", -0.10m),
            new BankrollPoint(Day2.ToUnixTimeSeconds(), "Alpha", -0.35m),
            new BankrollPoint(Day1.ToUnixTimeSeconds(), "Beta", 0.50m),
        };
        Assert.Equal(expected.Select(e => (e.EpochSeconds, e.Account, e.Bankroll)),
            rows.Select(r => (r.EpochSeconds, r.Account, r.Bankroll)));
    }

    [Fact]
    public void Render_EmitsHeaderAndInvariantRows()
    {
        var rows = new[]
        {
            new BankrollPoint(1789111200, "Alpha", -0.10m),
            new BankrollPoint(1789197600, "Alpha", -0.35m),
        };

        var csv = BankrollCsvExporter.Render(rows);

        Assert.Equal(
            "epoch_seconds,account,bankroll\n" +
            "1789111200,Alpha,-0.10\n" +
            "1789197600,Alpha,-0.35\n",
            csv);
    }

    [Fact]
    public async Task FileHook_RefreshesOnEverySettledTrade_AndWritesDocsCopy()
    {
        var store = new TradeStore(Path.Combine(_dir, "store"));
        var docsDir = Path.Combine(_dir, "repo", "docs");
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));
        var appDataPath = Path.Combine(_dir, "appdata", "growth-bankroll.csv");
        var docsPath = Path.Combine(docsDir, "growth-bankroll.csv");

        using var hook = new BankrollCsvFile(store, docsPath, appDataPath);

        // Empty store → header-only CSV at both paths.
        var headerOnly = "epoch_seconds,account,bankroll\n";
        Assert.Equal(headerOnly, await File.ReadAllTextAsync(appDataPath));
        Assert.Equal(headerOnly, await File.ReadAllTextAsync(docsPath));

        // First settled trade → recomputed row (not appended).
        store.Add(MakeTrade(0.90m, Day1));
        Assert.Equal($"{headerOnly}{Day1.ToUnixTimeSeconds()},Alpha,0.90\n",
            await File.ReadAllTextAsync(appDataPath));

        // Second trade, same day → the row is rewritten, not duplicated.
        store.Add(MakeTrade(-1.00m, Day1.AddMinutes(30), outcome: ContractStatus.Lost));
        Assert.Equal($"{headerOnly}{Day1.AddMinutes(30).ToUnixTimeSeconds()},Alpha,-0.10\n",
            await File.ReadAllTextAsync(appDataPath));
        Assert.Equal(await File.ReadAllTextAsync(appDataPath), await File.ReadAllTextAsync(docsPath));

        // After dispose, new settlements no longer touch the files.
        hook.Dispose();
        store.Add(MakeTrade(5m, Day2));
        Assert.Equal($"{headerOnly}{Day1.AddMinutes(30).ToUnixTimeSeconds()},Alpha,-0.10\n",
            await File.ReadAllTextAsync(appDataPath));
    }

    [Fact]
    public async Task FileHook_DocsCopyIsSkippedOutsideARepoCheckout()
    {
        var store = new TradeStore(Path.Combine(_dir, "store"));
        var appDataPath = Path.Combine(_dir, "appdata", "growth-bankroll.csv");

        using var hook = new BankrollCsvFile(store, docsPath: null, appDataPath);
        store.Add(MakeTrade(0.90m, Day1));

        Assert.Contains("Alpha", await File.ReadAllTextAsync(appDataPath));
    }

    [Fact]
    public void FindRepoDocsPath_WalksUpToTheCheckout_AndReturnsNullOutsideOne()
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "docs"));

        Assert.Equal(
            Path.Combine(repo, "docs", "growth-bankroll.csv"),
            BankrollCsvFile.FindRepoDocsPath(Path.Combine(repo, "src", "Tf.App", "bin")));

        var plain = Path.Combine(_dir, "no-repo");
        Directory.CreateDirectory(plain);
        Assert.Null(BankrollCsvFile.FindRepoDocsPath(Path.Combine(plain, "app")));
    }
}
