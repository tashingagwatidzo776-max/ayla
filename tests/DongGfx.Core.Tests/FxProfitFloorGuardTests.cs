using DongGfx.Core.Fx;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// The HARD PROFIT FLOOR's state machine (docs spec §14): a breached
/// never-down floor COMMANDS an exit exactly once per event, measured on
/// the executable price against the original risk — immune to HOLD votes,
/// partial closes, and restarts. Every case the spec names is pinned here.
/// </summary>
[Trait("Category", "Unit")]
public class FxProfitFloorGuardTests
{
    private static FxFloorVerdict Step(
        FxProfitFloorGuard g, double r, double floor = 0, double peak = 0) =>
        g.Advance(r, floor, peak == 0 ? Math.Max(r, floor) : peak);

    /// <summary>Arm the guard into PROTECTED at the given floor.</summary>
    private static FxProfitFloorGuard Protected(double floorR, double peakR)
    {
        var g = new FxProfitFloorGuard(1, "EURUSD", "buy");
        g.Advance(peakR, floorR, peakR);   // locks the floor
        Assert.Equal(FxFloorState.Protected, g.State);
        return g;
    }

    // ── spec case A (and the screenshot regression): 12.4 → 8.2 → 8.1 ──

    [Fact]
    public void CaseA_Breach_Commands_Hard_Exit_Exactly_Once()
    {
        var g = Protected(8.2, 12.4);

        var v = Step(g, 8.05);   // through the floor (tolerance 0.05R)
        Assert.Equal(FxFloorCommand.ExecuteExit, v.Command);
        Assert.Equal(FxFloorState.ExitPending, v.State);
        Assert.Equal(8.05, v.ExecutableR, 6);
        Assert.NotNull(v.EventId);

        // Host contract: the command is submitted immediately.
        g.MarkSubmitted("deal-1");

        // The same breach re-ticked (price bounces to 8.1) must NOT re-fire.
        var again = Step(g, 8.1);
        Assert.True(again.Command == FxFloorCommand.None);
        Assert.True(again.State != FxFloorState.Protected);
    }

    // ── spec case B: 12.4 → 10 → 9 stays open ──────────────────────────

    [Fact]
    public void CaseB_Above_The_Floor_Never_Commands()
    {
        var g = Protected(8.2, 12.4);
        Assert.True(Step(g, 10.0).Command == FxFloorCommand.None);
        Assert.True(Step(g, 9.0).Command == FxFloorCommand.None);
        Assert.True(g.State == FxFloorState.Protected);
    }

    // ── spec case C: 12.4 → 8.2 → 7 — one event, not repeated alerts ──

    [Fact]
    public void CaseC_Deep_Through_The_Floor_Is_Still_One_Event()
    {
        var g = Protected(8.2, 12.4);
        Assert.True(Step(g, 8.1).Command == FxFloorCommand.ExecuteExit);
        g.MarkSubmitted("deal-1");   // host submits immediately
        Assert.True(Step(g, 7.0).Command == FxFloorCommand.None);
        Assert.True(Step(g, 6.0).Command == FxFloorCommand.None);
        Assert.Equal(1, g.BreachSequence);
    }

    // ── spec case D: the ensemble says HOLD — the command stands ────────

    [Fact]
    public void CaseD_The_Command_Is_A_Command_Not_A_Vote()
    {
        var g = Protected(8.2, 12.4);
        var v = Step(g, 5.4);   // the screenshot's +5.4R
        Assert.Equal(FxFloorCommand.ExecuteExit, v.Command);
        // The guard carries no HOLD pathway: nothing in its state space can
        // retract the command short of broker confirmation or reset.
        Assert.True(v.State is FxFloorState.ExitPending or FxFloorState.ExitSubmitted);
    }

    // ── spec case E: broker rejects — retry, protection retained ────────

    [Fact]
    public void CaseE_Rejection_Retries_And_Never_Reverts_To_Hold()
    {
        var g = Protected(8.2, 12.4);
        Step(g, 8.0);                       // breach → command
        g.MarkSubmitted("deal-1");          // venue accepted request #1
        Assert.Equal(FxFloorState.ExitSubmitted, g.State);

        g.MarkFailed();                     // reconciliation: still open
        Assert.Equal(FxFloorState.ExitFailed, g.State);

        var retry = Step(g, 7.9);
        Assert.Equal(FxFloorCommand.ExecuteExit, retry.Command);   // re-queued
        Assert.Equal(FxFloorState.ExitPending, retry.State);
    }

    // ── spec case F: restart after activation restores protection ───────

    [Fact]
    public void CaseF_Snapshot_Restore_Keeps_Protection_And_Idempotency()
    {
        var g = Protected(8.2, 12.4);
        Step(g, 8.0);
        g.MarkSubmitted("deal-9");
        var snap = g.Snapshot();

        var reborn = FxProfitFloorGuard.FromSnapshot(snap);
        Assert.Equal(FxFloorState.ExitSubmitted, reborn.State);
        Assert.Equal(8.2, reborn.FloorR, 6);

        // The restored guard must not double-send the same event…
        var v = reborn.Advance(7.5, 8.2, 12.4);
        Assert.Equal(FxFloorCommand.None, v.Command);

        // …but a reconciled failure re-queues the exit under the SAME event.
        reborn.MarkFailed();
        Assert.Equal(FxFloorCommand.ExecuteExit, reborn.Advance(7.4, 8.2, 12.4).Command);
    }

    // ── spec case G: partial close leaves the remainder protected ───────

    [Fact]
    public void CaseG_Partial_Close_Keeps_The_Remainder_Protected()
    {
        // The guard is volume-blind BY DESIGN: R is measured against the
        // ORIGINAL risk baseline, so halving the lots cannot reshape the R
        // unit, weaken the floor, or dismiss the protection.
        var g = Protected(8.2, 12.4);
        Step(g, 9.0);                       // a partial banks at +9R
        Assert.Equal(FxFloorState.Protected, g.State);
        Assert.Equal(FxFloorCommand.ExecuteExit, Step(g, 8.0).Command);
    }

    // ── spec case H: spread flutter stays inside the tolerance ──────────

    [Fact]
    public void CaseH_Spread_Flutter_Within_Tolerance_Does_Not_Trip()
    {
        var g = Protected(8.2, 12.4);
        // 8.18R is inside the 0.05R tolerance band: no breach.
        Assert.True(Step(g, 8.18).Command == FxFloorCommand.None);
        // One tick further does trip it — the tolerance is precision-only.
        Assert.True(Step(g, 8.10).Command == FxFloorCommand.ExecuteExit);
    }

    // ── the floor never moves down, and a ratchet bumps the version ─────

    [Fact]
    public void Floor_Ratchets_Up_Never_Down_And_Bumps_The_Version()
    {
        var g = Protected(4.0, 5.0);
        Assert.Equal(1, g.FloorVersion);
        g.Advance(5.5, 5.5, 5.5);           // model ratchets up
        Assert.Equal(5.5, g.FloorR, 6);
        Assert.Equal(2, g.FloorVersion);
        g.Advance(5.0, 3.0, 5.5);           // a model glitch tries to lower
        Assert.Equal(5.5, g.FloorR, 6);     // the law holds
    }

    // ── idempotency: a submitted event is never commanded twice ─────────

    [Fact]
    public void Submitted_Event_Is_Never_Re_Commanded_In_Process()
    {
        var g = Protected(8.2, 12.4);
        var v = Step(g, 8.0);
        var eventId = v.EventId;
        g.MarkSubmitted("deal-2");
        Assert.Equal(eventId, g.SubmittedEventId);

        Assert.Equal(FxFloorCommand.None, Step(g, 7.9).Command);
        Assert.Equal(FxFloorCommand.None, Step(g, 7.0).Command);
    }

    // ── INACTIVE/ARMED: no floor, no protection, no crash ────────────────

    [Fact]
    public void No_Floor_No_Command()
    {
        var g = new FxProfitFloorGuard(2, "EURUSD", "sell");
        Assert.Equal(FxFloorCommand.None, g.Advance(3.0, 0, 3.0).Command);
        Assert.Equal(FxFloorState.Armed, g.State);
    }

    // ── the screenshot regression, verbatim ──────────────────────────────

    [Fact]
    public void Regression_Peak12p4_Floor8p2_Current5p4_Must_Exit_Not_Hold()
    {
        // THE BUG: +12.4R peak, +8.2R floor, price falls to +5.4R and the
        // system repeatedly said "exit evaluation requested" while voting
        // HOLD. The guard's answer:
        var g = new FxProfitFloorGuard(9820161383, "EURUSD", "sell");
        Assert.Equal(FxFloorState.Armed, g.Advance(12.4, 0, 12.4).State);
        Assert.Equal(FxFloorState.Protected, g.Advance(12.0, 8.2, 12.4).State);

        var breach = g.Advance(5.4, 8.2, 12.4);
        Assert.Equal(FxFloorCommand.ExecuteExit, breach.Command);

        // PROTECTED → BREACH_DETECTED → EXIT_PENDING → EXIT_SUBMITTED →
        // EXIT_CONFIRMED — never → EXIT_EVALUATION_REQUESTED → HOLD.
        g.MarkSubmitted("9820161383-exit");
        Assert.Equal(FxFloorState.ExitSubmitted, g.State);
        g.MarkConfirmed();
        Assert.Equal(FxFloorState.ExitConfirmed, g.State);
    }
}
