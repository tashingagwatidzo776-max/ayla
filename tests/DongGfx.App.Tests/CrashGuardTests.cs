using System;
using System.IO;
using DongGfx.App.Infrastructure;
using DongGfx.App.ViewModels;
using DongGfx.Core.Logging;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The global crash guards: an unhandled fault (UI thread, process, or an
/// unobserved Task) is recorded to the logger and the journal and contained,
/// so a stray exception degrades the app instead of ending the session. The
/// reporting path itself must never throw — not with a null journal, not with
/// a null exception, not if logging faults.
/// </summary>
[Trait("Category", "Unit")]
public class CrashGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"tf_crash_{Guid.NewGuid():N}");

    public CrashGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ReportFatal_Journals_And_Never_Throws()
    {
        using var logger = new AppLogger(_dir);
        using var journal = new TradeJournal(Path.Combine(_dir, "journal"));

        App.ReportFatal(logger, journal, "UI", new InvalidOperationException("boom"));
        journal.Flush();

        var rows = journal.GetRecent(null, 10);
        Assert.Contains(rows, r => r.Category == "APP_FAULT");
        Assert.Contains(rows, r =>
            r.Category == "APP_FAULT" && r.Details.Contains("InvalidOperationException: boom"));

        // Degenerate inputs are tolerated: no journal, no exception at all.
        App.ReportFatal(logger, null, "task", null);
        App.ReportFatal(logger, journal, "process", null);
    }

    [Fact]
    public void Faults_Count_Only_Recent_App_Faults()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new JournalEntry { Timestamp = now.AddMinutes(-5), Category = CrashFaults.Category, Details = "a" },
            new JournalEntry { Timestamp = now.AddHours(-2), Category = CrashFaults.Category, Details = "b" },
            new JournalEntry { Timestamp = now.AddHours(-30), Category = CrashFaults.Category, Details = "old" },
            new JournalEntry { Timestamp = now, Category = "FX_MODE", Details = "not a fault" },
        };

        var summary = CrashFaults.FromJournal(entries, now);
        Assert.Equal(2, summary.Count);
        Assert.Equal("a", summary.Latest);            // the newest of the two
        Assert.NotNull(summary.LatestAt);

        // The safe-mode notice uses its own category, so it cannot itself
        // count as a fault (which would make safe mode sticky forever).
        Assert.NotEqual(CrashFaults.Category, CrashFaults.SafeModeCategory);

        Assert.Equal(0, CrashFaults.FromJournal(null, now).Count);
        Assert.Equal(string.Empty, CrashFaults.FromJournal(null, now).Latest);
    }

    [Fact]
    public void SafeModeNeeded_Latches_On_Fault_And_Releases_On_Operator_Ack()
    {
        var now = DateTimeOffset.UtcNow;
        var fault = new JournalEntry
        {
            Timestamp = now.AddMinutes(-30),
            Category = CrashFaults.Category,
            Details = "IOException: journal is being used by another process",
        };
        var release = new JournalEntry
        {
            Timestamp = now.AddMinutes(-10),
            Category = CrashFaults.SafeModeCategory,
            Details = "safe mode released by operator — engine loop started after contained fault(s)",
        };
        var holdBack = new JournalEntry
        {
            Timestamp = now.AddMinutes(-20),
            Category = CrashFaults.SafeModeCategory,
            Details = "safe mode: engine loop held back after 1 contained fault(s) in the last 24h",
        };

        // Fault, nobody acknowledged it yet → the hold-back engages.
        Assert.True(CrashFaults.SafeModeNeeded(new[] { fault }, now));

        // The operator release acknowledges every OLDER fault: the next
        // launch auto-starts the brain instead of re-latching safe mode.
        Assert.False(CrashFaults.SafeModeNeeded(new[] { fault, release }, now));

        // A hold-back notice is not an acknowledgment — only the release is.
        Assert.True(CrashFaults.SafeModeNeeded(new[] { fault, holdBack }, now));

        // A NEW fault after the release re-arms the hold-back (the release
        // cannot blind future faults).
        var freshFault = new JournalEntry
        {
            Timestamp = now.AddMinutes(-1),
            Category = CrashFaults.Category,
            Details = "InvalidOperationException: boom",
        };
        Assert.True(CrashFaults.SafeModeNeeded(
            new[] { fault, release, freshFault }, now));

        // Release with no faults at all → nothing to hold back.
        Assert.False(CrashFaults.SafeModeNeeded(new[] { release }, now));

        // A fault outside the 24h window is history either way.
        var stale = new JournalEntry
        {
            Timestamp = now.AddHours(-30),
            Category = CrashFaults.Category,
            Details = "old fault",
        };
        Assert.False(CrashFaults.SafeModeNeeded(new[] { stale }, now));
        Assert.False(CrashFaults.SafeModeNeeded(new[] { stale, release }, now));
        Assert.False(CrashFaults.SafeModeNeeded(null, now));
    }

    [Fact]
    public void FaultNotice_Is_Silent_When_Healthy_And_Names_Safe_Mode()
    {
        Assert.Null(DashboardViewModel.FaultNoticeFor(null, safeMode: false));
        Assert.Null(DashboardViewModel.FaultNoticeFor(new CrashFaults.Summary(0, null, ""), safeMode: true));

        var summary = new CrashFaults.Summary(2, DateTimeOffset.UtcNow, "InvalidOperationException: boom");

        var safe = DashboardViewModel.FaultNoticeFor(summary, safeMode: true);
        Assert.NotNull(safe);
        Assert.Contains("SAFE MODE", safe);
        Assert.Contains("InvalidOperationException: boom", safe);

        var watch = DashboardViewModel.FaultNoticeFor(summary, safeMode: false);
        Assert.NotNull(watch);
        Assert.Contains("2 contained fault(s) in the last 24h", watch);
        Assert.DoesNotContain("SAFE MODE", watch);
    }

    [Fact]
    public void Dashboard_Resume_From_Safe_Mode_Starts_And_Clears()
    {
        var dashboard = new DashboardViewModel();
        var safe = true;
        dashboard.ConfigureFaultNotice(() =>
            new DashboardViewModel.FaultNotice("SAFE MODE — held back", safe));
        Assert.True(dashboard.HasFaultNotice);
        Assert.True(dashboard.IsSafeMode);

        dashboard.ResumeAfterFault = () => { safe = false; return true; };
        dashboard.ResumeFromSafeModeCommand.Execute(null);

        Assert.Contains("engine loop started", dashboard.FaultStatus);
        Assert.False(dashboard.IsSafeMode);            // provider now reports released
        Assert.True(dashboard.HasFaultNotice);         // fault history still shown

        // A provider that throws must not take the notice down with it.
        dashboard.ConfigureFaultNotice(() => throw new InvalidOperationException("provider"));
        Assert.False(dashboard.HasFaultNotice);
    }

    [Fact]
    public void Dashboard_View_Faults_Navigates_To_The_Journal()
    {
        var dashboard = new DashboardViewModel();
        var opened = 0;
        dashboard.OpenFaultJournal = () => opened++;

        dashboard.ViewFaultsCommand.Execute(null);
        Assert.Equal(1, opened);

        // Navigation is best-effort: a throwing shell callback must not fault
        // the dashboard that is reporting the fault.
        dashboard.OpenFaultJournal = () => throw new InvalidOperationException("nav");
        dashboard.ViewFaultsCommand.Execute(null);
    }
}
