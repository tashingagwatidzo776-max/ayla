using System;
using System.IO;
using DongGfx.App.Infrastructure;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The durable paper-soak counters: the GO LIVE bar (10 signals per symbol,
/// all-or-nothing across the portfolio) must survive an app or brain
/// restart, and must NOT survive a build change — a bar met by one engine is
/// no evidence for another. It must not survive an MT5 account switch either:
/// the bar is the evidence base for the go-live gate on the account about to
/// trade.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PaperSoakLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dg-soak-ledger-tests", Guid.NewGuid().ToString("N"));
    private readonly string _ledger;

    public PaperSoakLedgerTests() => _ledger = PaperSoakLedger.PathFor(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void Counters_Survive_A_New_Instance()
    {
        var first = new PaperSoakLedger(_ledger, "build-a");
        Assert.Equal(0, first.Seen("XAUUSD"));           // cold start
        Assert.Equal(1, first.Record("XAUUSD", 0));
        Assert.Equal(2, first.Record("XAUUSD", 1));
        first.Record("EURUSD", 0);

        // A fresh instance — the next launch — reads what the last wrote,
        // per symbol.
        var restarted = new PaperSoakLedger(_ledger, "build-a");
        Assert.Equal(2, restarted.Seen("XAUUSD"));
        Assert.Equal(1, restarted.Seen("EURUSD"));
        Assert.Equal(0, restarted.Seen("GBPUSD"));
        Assert.Null(restarted.InvalidatedScope);
    }

    [Fact]
    public void Record_Takes_The_Higher_Of_Memory_And_Disk()
    {
        // A write that failed, or a restore that raced one, must never be
        // able to talk the counter DOWN — the soak may only ever accrue.
        var ledger = new PaperSoakLedger(_ledger, "build-a");
        ledger.Record("XAUUSD", 0);                       // disk: 1
        Assert.Equal(9, ledger.Record("XAUUSD", 8));      // memory ahead of disk
        Assert.Equal(10, ledger.Record("XAUUSD", 1));     // disk ahead of memory
        Assert.Equal(10, new PaperSoakLedger(_ledger, "build-a").Seen("XAUUSD"));
    }

    [Fact]
    public void A_Different_Build_Drops_The_Counters_And_Says_So()
    {
        new PaperSoakLedger(_ledger, "build-a").Record("XAUUSD", 0);
        new PaperSoakLedger(_ledger, "build-a").Record("XAUUSD", 1);

        var rebuilt = new PaperSoakLedger(_ledger, "build-b");
        Assert.Equal(0, rebuilt.Seen("XAUUSD"));          // not inherited
        Assert.Equal("build-a", rebuilt.InvalidatedScope); // and not silent

        // Writing under the new scope takes over the file: going back to
        // build-a no longer resurrects the old evidence.
        rebuilt.Record("XAUUSD", 0);
        Assert.Equal(1, new PaperSoakLedger(_ledger, "build-b").Seen("XAUUSD"));
        Assert.Equal(0, new PaperSoakLedger(_ledger, "build-a").Seen("XAUUSD"));
    }

    [Fact]
    public void Corrupt_Or_Empty_Ledger_Reads_As_A_Cold_Start()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_ledger, "{ this is not json");
        var ledger = new PaperSoakLedger(_ledger, "build-a");
        Assert.Equal(0, ledger.Seen("XAUUSD"));
        Assert.Null(ledger.InvalidatedScope);   // corrupt, not another build

        // Still usable: the next signal persists over the wreckage.
        Assert.Equal(1, ledger.Record("XAUUSD", 0));
        Assert.Equal(1, new PaperSoakLedger(_ledger, "build-a").Seen("XAUUSD"));

        // A ledger naming no symbols is simply empty.
        File.WriteAllText(_ledger, "{\"scope\":\"build-a\",\"symbols\":{}}");
        Assert.Equal(0, new PaperSoakLedger(_ledger, "build-a").Seen("XAUUSD"));
    }

    [Fact]
    public void Reset_Clears_Every_Symbol()
    {
        var ledger = new PaperSoakLedger(_ledger, "build-a");
        ledger.Record("XAUUSD", 0);
        ledger.Record("EURUSD", 0);

        ledger.Reset();

        Assert.Equal(0, ledger.Seen("XAUUSD"));
        Assert.Equal(0, new PaperSoakLedger(_ledger, "build-a").Seen("EURUSD"));
    }

    [Fact]
    public void The_Ledger_Lives_Beside_The_Other_Fx_State()
    {
        var path = PaperSoakLedger.PathFor(@"C:\data");
        Assert.Equal(Path.Combine(@"C:\data", "fx-paper-soak.json"), path);

        // Missing directory is not a failure: the first write creates it.
        var nested = Path.Combine(_dir, "deep", "ledger.json");
        Assert.Equal(3, new PaperSoakLedger(nested, "build-a").Record("XAUUSD", 2));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void A_Switched_Account_Drops_The_Counters_And_Says_So()
    {
        new PaperSoakLedger(_ledger, "build-a", () => "111").Record("XAUUSD", 0);
        new PaperSoakLedger(_ledger, "build-a", () => "111").Record("XAUUSD", 1);

        var switched = new PaperSoakLedger(_ledger, "build-a", () => "222");
        Assert.Equal(0, switched.Seen("XAUUSD"));            // not inherited
        Assert.Equal("111", switched.InvalidatedAccount);    // and not silent
        Assert.Null(switched.InvalidatedScope);              // the build is unchanged

        // Writing under the new account takes over the file.
        switched.Record("XAUUSD", 0);
        Assert.Equal(1, new PaperSoakLedger(_ledger, "build-a", () => "222").Seen("XAUUSD"));
        Assert.Equal(0, new PaperSoakLedger(_ledger, "build-a", () => "111").Seen("XAUUSD"));
    }

    [Fact]
    public void Same_Account_Carries_Over_And_A_Build_Change_Outranks_A_Switch()
    {
        new PaperSoakLedger(_ledger, "build-a", () => "111").Record("XAUUSD", 0);
        new PaperSoakLedger(_ledger, "build-a", () => "111").Record("XAUUSD", 1);

        var same = new PaperSoakLedger(_ledger, "build-a", () => "111");
        Assert.Equal(2, same.Seen("XAUUSD"));
        Assert.Null(same.InvalidatedAccount);

        // When both changed, the build is the more fundamental reset and is
        // what the report names.
        var both = new PaperSoakLedger(_ledger, "build-b", () => "222");
        Assert.Equal(0, both.Seen("XAUUSD"));      // forces the load
        Assert.Equal("build-a", both.InvalidatedScope);
        Assert.Null(both.InvalidatedAccount);
    }

    [Fact]
    public void An_Unstamped_Ledger_Is_Not_Friendly_Fired_By_The_Account_Dimension()
    {
        // A ledger written before the account dimension existed carries no
        // account field: it keeps accruing rather than being dropped.
        new PaperSoakLedger(_ledger, "build-a").Record("XAUUSD", 0);

        var reader = new PaperSoakLedger(_ledger, "build-a", () => "111");
        Assert.Equal(1, reader.Seen("XAUUSD"));
        Assert.Null(reader.InvalidatedAccount);
    }

    [Fact]
    public void The_Account_Comes_From_The_Live_Provider()
    {
        // The provider is read at load time, so a switch made in the MT5
        // terminal's own GUI — which never touches the login the app stored
        // in settings — still restarts the bar: the provider simply starts
        // returning the new login.
        var login = "111";
        new PaperSoakLedger(_ledger, "build-a", () => login).Record("XAUUSD", 0);
        Assert.Equal(1, new PaperSoakLedger(_ledger, "build-a", () => login).Seen("XAUUSD"));

        login = "222";   // the terminal GUI switched accounts
        var after = new PaperSoakLedger(_ledger, "build-a", () => login);
        Assert.Equal(0, after.Seen("XAUUSD"));
        Assert.Equal("111", after.InvalidatedAccount);
    }
}
