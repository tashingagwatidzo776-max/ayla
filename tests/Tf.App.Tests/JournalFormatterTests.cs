using System.IO;
using Tf.App.ViewModels;
using Tf.Core;
using Tf.Core.Logging;

namespace Tf.App.Tests;

/// <summary>
/// The Journal tab's dedicated formatting for the real-money unlock audit
/// trail: REAL_MONEY_UNLOCK_ARMED entries read like settlements do (who was
/// armed, scope, API-verified state, arming surface) and stale-unlock alerts
/// read at a glance. The VM is used read-only here (no UI refresh timer is
/// exercised), so it is safe headless — the hub's Journal tab wiring is
/// covered by the E2E tests.
/// </summary>
[Trait("Category", "Unit")]
public class JournalFormatterTests : IDisposable
{
    private readonly string _dir;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly JournalViewModel _vm;

    public JournalFormatterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tf_jfmt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new TradeStore(_dir);
        _journal = new TradeJournal(Path.Combine(_dir, "journal"));
        _vm = new JournalViewModel(_journal, _store, () => false);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ArmedEntry_FormatsAccountsScopeVerifiedStateAndSurface()
    {
        _journal.LogRealMoneyUnlockArmed(new List<(Guid, string, bool)>
        {
            (Guid.NewGuid(), "Alpha", true),
            (Guid.NewGuid(), "Beta", false)
        }, manualSurfaces: true);
        _journal.Flush();
        _vm.RefreshCommand.Execute(null);

        var line = Assert.Single(_vm.Entries).Details;
        Assert.StartsWith("🔓 REAL-MONEY UNLOCK ARMED", line);
        Assert.Contains("Alpha, Beta", line);           // both accounts named
        Assert.Contains("1/2 API-verified real", line);  // honest verification state
        Assert.Contains("manual surfaces", line);        // manual scopes joined
        Assert.Contains("via unlock panel", line);       // arming surface
    }

    [Fact]
    public void ArmedEntry_SingleUnverifiedAccount_OmitsVerifiedClause()
    {
        _journal.LogRealMoneyUnlockArmed(
            new List<(Guid, string, bool)> { (Guid.NewGuid(), "Solo", false) },
            manualSurfaces: false, armedBy: "Restart");
        _journal.Flush();
        _vm.RefreshCommand.Execute(null);

        var line = Assert.Single(_vm.Entries).Details;
        Assert.StartsWith("🔓 REAL-MONEY UNLOCK ARMED | Solo (0/1 API-verified real) | via Restart", line);
    }

    [Fact]
    public void ArmedEntry_MalformedDetails_FallsBackToRawPayload()
    {
        _journal.Log(Guid.Empty, "REAL_MONEY_UNLOCK_ARMED", "not json at all");
        _journal.Flush();

        _vm.RefreshCommand.Execute(null);

        var line = Assert.Single(_vm.Entries).Details;
        Assert.StartsWith("🔓", line);
        Assert.Contains("not json at all", line); // raw payload, never an empty arm line
    }

    [Fact]
    public void StaleEntry_FormatsWithClockIcon()
    {
        _journal.Log(Guid.Empty, "REAL_MONEY_UNLOCK_STALE",
            "real-money unlock for 'Overnight' armed 4h07m ago " +
            "(threshold 4h × 1) — still live; re-arm or let the session end to clear it");
        _journal.Flush();

        _vm.RefreshCommand.Execute(null);

        var line = Assert.Single(_vm.Entries).Details;
        Assert.StartsWith("⏰ STALE UNLOCK", line);
        Assert.Contains("'Overnight'", line);
        Assert.Contains("4h07m", line);
    }

    [Fact]
    public void Categories_ExposeTheUnlockAuditFilters()
    {
        Assert.Contains("REAL_MONEY_UNLOCK_ARMED", _vm.Categories);
        Assert.Contains("REAL_MONEY_UNLOCK_STALE", _vm.Categories);
    }

    [Fact]
    public void CategoryFilter_IsolatesUnlockAuditEntries()
    {
        _journal.Log(Guid.NewGuid(), "ACCOUNT_EVENT", "unrelated");
        _journal.LogRealMoneyUnlockArmed(
            new List<(Guid, string, bool)> { (Guid.NewGuid(), "Filt", true) },
            manualSurfaces: false);
        _journal.Flush();

        _vm.FilterCategory = "REAL_MONEY_UNLOCK_ARMED";
        _vm.RefreshCommand.Execute(null);

        var entry = Assert.Single(_vm.Entries);
        Assert.Equal("REAL_MONEY_UNLOCK_ARMED", entry.Category);
        Assert.Contains("Filt", entry.Details);
    }
}
