namespace DongGfx.Core.Analytics;

/// <summary>Headless input describing one account's live health snapshot.</summary>
public sealed record AccountHealthInput(
    Guid AccountId,
    string Name,
    string LoginId,
    string Status,
    string Balance,
    bool IsConnected,
    bool IsDegraded,
    bool IsPaused,
    bool HasRunner,
    string CircuitStatus,
    int TickBufferCount,
    int TickBufferCapacity,
    int CachedTickCount,
    string Symbol,
    string Brain);

/// <summary>Computed Health tab model: counters, overall status, cache summary, rows.</summary>
public sealed record HealthSummary(
    string OverallStatus,
    int ConnectedCount,
    int TotalAccounts,
    int DegradedCount,
    int PausedCount,
    int RunningEngines,
    string CacheStats,
    IReadOnlyList<AccountHealthRowData> Rows);

/// <summary>One display-ready per-account health row.</summary>
public sealed record AccountHealthRowData(
    string Name,
    string LoginId,
    string Status,
    string Balance,
    bool IsConnected,
    bool IsDegraded,
    bool IsPaused,
    bool HasRunner,
    string CircuitStatus,
    string TickBuffer,
    double TickBufferFill,
    string CachedTicks,
    string Symbol,
    string Brain);

/// <summary>
/// Headless builder for the Health tab's per-account rows, overall status,
/// and cache summary. Extracted from <see cref="DongGfx.App.ViewModels.HealthViewModel"/>
/// so the rules are unit-testable without WPF; production behavior is
/// unchanged — except that an empty hub now honestly reports
/// "No accounts connected" instead of "✓ All 0 accounts connected".
/// </summary>
public static class HealthSummaryBuilder
{
    public static HealthSummary Build(
        IReadOnlyList<AccountHealthInput> accounts,
        int cachedSymbolCount,
        long cachedTickTotal)
    {
        var connected = 0;
        var degraded = 0;
        var paused = 0;
        var running = 0;

        var rows = new List<AccountHealthRowData>(accounts.Count);
        foreach (var acct in accounts)
        {
            if (acct.IsConnected) connected++;
            if (acct.IsDegraded) degraded++;
            if (acct.IsPaused) paused++;
            if (acct.HasRunner) running++;

            rows.Add(new AccountHealthRowData(
                acct.Name,
                acct.LoginId,
                acct.Status,
                acct.Balance,
                acct.IsConnected,
                acct.IsDegraded,
                acct.IsPaused,
                acct.HasRunner,
                acct.CircuitStatus,
                $"{acct.TickBufferCount}/{acct.TickBufferCapacity}",
                acct.TickBufferCapacity > 0 ? acct.TickBufferCount / (double)acct.TickBufferCapacity : 0,
                acct.CachedTickCount.ToString("N0"),
                acct.Symbol,
                acct.Brain));
        }

        // Overall status — the all-connected branch requires at least one
        // account so an empty hub cannot claim "All 0 accounts connected".
        string overall;
        if (degraded > 0)
            overall = $"⚠ {degraded} degraded";
        else if (connected > 0 && connected == accounts.Count)
            overall = $"✓ All {accounts.Count} accounts connected";
        else if (connected > 0)
            overall = $"{connected}/{accounts.Count} connected";
        else
            overall = "No accounts connected";

        var cacheStats = cachedSymbolCount > 0
            ? $"{cachedTickTotal:N0} ticks cached across {cachedSymbolCount} symbol(s)"
            : "No tick data cached yet";

        return new HealthSummary(overall, connected, accounts.Count, degraded, paused, running, cacheStats, rows);
    }
}
