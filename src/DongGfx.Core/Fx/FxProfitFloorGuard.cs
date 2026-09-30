using System;

namespace DongGfx.Core.Fx;

/// <summary>The profit-floor protection lifecycle. INACTIVE: no floor yet
/// (peak below activation). ARMED: tracking a forming trade. PROTECTED: a
/// never-down floor is locked. BREACH_DETECTED: the EXECUTABLE price fell
/// to/below the floor (once per event, tolerance-bounded). EXIT_PENDING →
/// EXIT_SUBMITTED → EXIT_CONFIRMED is the only sanctioned path out of a
/// breach; EXIT_FAILED keeps protection alive and retries. RESET: the
/// position is gone (closed or reconciled).</summary>
public enum FxFloorState
{
    Inactive, Armed, Protected, BreachDetected,
    ExitPending, ExitSubmitted, ExitConfirmed, ExitFailed, Reset,
}

/// <summary>What the guard wants the host to DO. ExecuteExit is a COMMAND,
/// not a recommendation: the exit evaluator may be overruled by it, never
/// the other way around.</summary>
public enum FxFloorCommand { None, ExecuteExit }

/// <summary>Persisted guard state — journaled on every transition and
/// restored on first sighting after a restart, so a protected floor never
/// dies with the process.</summary>
public sealed record FxFloorSnapshot(
    long Ticket,
    string Symbol,
    string Side,
    double RiskPerLot,
    double PeakR,
    double FloorR,
    FxFloorState State,
    int FloorVersion,
    int BreachSequence,
    double BreachR,
    string? EventId,
    string? ExitOrderId,
    int Attempts);

/// <summary>One Advance() outcome: the new state and (once per breach
/// event, and again per retry) the exit COMMAND with its idempotency key
/// trade_id|floor_version|breach_sequence.</summary>
public sealed record FxFloorVerdict(
    FxFloorState State,
    FxFloorCommand Command,
    string? EventId,
    double ExecutableR,
    double FloorR,
    double PeakR,
    string Reason);

/// <summary>
/// The HARD PROFIT FLOOR — an independent risk-control layer.
///
/// The exit evaluator (ensemble, regimes, models) RECOMMENDS exits; this
/// guard COMMANDS them once a protected floor is breached. Priority:
/// emergency/risk limits → HARD PROFIT FLOOR → hard stop loss → normal
/// exit engine → strategy consensus. No HOLD vote can dismiss it.
///
/// Design laws:
///  - the breach is measured on the EXECUTABLE price (bid for longs, ask
///    for shorts) against the trade's ORIGINAL risk baseline — lot
///    changes, partial closes and stop moves cannot reshape the R unit;
///  - the floor ratchets up (max of history) and NEVER moves down;
///  - a breach fires exactly once per event (tolerance-bounded so spread
///    noise cannot machine-gun alerts), then the exit is commanded,
///    retried on broker failure, and CONFIRMED only by broker
///    reconciliation — never by an assumed submission;
///  - the machine is snapshot-pure: the host journals every transition
///    and restores from the journal after a restart.
/// </summary>
public sealed class FxProfitFloorGuard
{
    /// <summary>Breach tolerance in R — covers spread flutter and tick
    /// granularity ONLY (spec §6); it is not a discretionary buffer.</summary>
    public const double BreachToleranceR = 0.05;

    /// <summary>In-cycle submit attempts before the host escalates and
    /// leaves the retry to the next cycle. Protection NEVER deactivates
    /// on failure.</summary>
    public const int MaxSubmitAttempts = 3;

    private readonly long _ticket;
    private readonly string _symbol;
    private readonly string _side;

    public FxFloorState State { get; private set; } = FxFloorState.Inactive;
    public double FloorR { get; private set; }
    public double PeakR { get; private set; }
    public int FloorVersion { get; private set; }
    public int BreachSequence { get; private set; }
    public double BreachR { get; private set; }
    public string? EventId { get; private set; }
    public string? ExitOrderId { get; private set; }
    public int Attempts { get; private set; }

    /// <summary>The event id whose exit was already SUBMITTED — the
    /// idempotency key: a re-issued command for the same event is refused
    /// in-process (the journal snapshot covers the cross-restart case).</summary>
    public string? SubmittedEventId { get; private set; }

    public FxProfitFloorGuard(long ticket, string symbol, string side)
    {
        _ticket = ticket;
        _symbol = symbol;
        _side = side;
    }

    public static FxProfitFloorGuard FromSnapshot(FxFloorSnapshot s) => new(s.Ticket, s.Symbol, s.Side)
    {
        State = s.State,
        FloorR = s.FloorR,
        PeakR = s.PeakR,
        FloorVersion = s.FloorVersion,
        BreachSequence = s.BreachSequence,
        BreachR = s.BreachR,
        EventId = s.EventId,
        ExitOrderId = s.ExitOrderId,
        Attempts = s.Attempts,
        // A snapshot past EXIT_SUBMITTED means the exit request left the
        // process: mark the event submitted so a restore cannot double-send.
        SubmittedEventId = s.State is FxFloorState.ExitSubmitted or FxFloorState.ExitConfirmed
            ? s.EventId
            : null,
    };

    public FxFloorSnapshot Snapshot() => new(
        _ticket, _symbol, _side, 0, PeakR, FloorR, State,
        FloorVersion, BreachSequence, BreachR, EventId, ExitOrderId, Attempts);

    /// <summary>One protection step. executableR is the position's live
    /// profit in R measured at the EXECUTABLE price against the ORIGINAL
    /// risk; floorR is the protection model's current floor (this guard
    /// ratchets it never-down); peakR the trade's high-water mark.</summary>
    public FxFloorVerdict Advance(double executableR, double floorR, double peakR)
    {
        PeakR = Math.Max(PeakR, peakR);

        // The never-down law (spec §5): a ratchet upward bumps the floor
        // version (it changes the idempotency key of any future breach).
        if (floorR > FloorR + 1e-9)
        {
            FloorR = floorR;
            FloorVersion++;
        }

        var reason = string.Empty;
        var command = FxFloorCommand.None;

        switch (State)
        {
            case FxFloorState.Inactive or FxFloorState.Armed:
                State = FloorR > 0 ? FxFloorState.Protected : FxFloorState.Armed;
                reason = FloorR > 0
                    ? $"floor locked at {FloorR:0.0}R (peak {PeakR:0.0}R)"
                    : "floor forming (peak below activation)";
                break;

            case FxFloorState.Protected:
                if (executableR <= FloorR - BreachToleranceR)
                {
                    // §6: the breach fires EXACTLY ONCE per event, then the
                    // exit is commanded in the same breath (§1: detection
                    // and enforcement are one layer).
                    BreachSequence++;
                    BreachR = executableR;
                    EventId = $"{_ticket}|{FloorVersion}|{BreachSequence}";
                    Attempts = 0;
                    State = FxFloorState.ExitPending;
                    command = FxFloorCommand.ExecuteExit;
                    reason = $"HARD PROFIT FLOOR BREACH — executable {executableR:+0.00;-0.00}R " +
                             $"through the {FloorR:0.0}R floor; exit commanded for event {EventId}";
                }
                else
                {
                    reason = $"holding above the floor ({executableR:+0.00;-0.00}R vs {FloorR:0.0}R)";
                }
                break;

            case FxFloorState.BreachDetected:
                // Restored mid-breach (a snapshot taken between detection
                // and submission): re-queue the exit.
                State = FxFloorState.ExitPending;
                if (SubmittedEventId is null || SubmittedEventId != EventId)
                {
                    command = FxFloorCommand.ExecuteExit;
                    reason = $"restored mid-breach — exit re-queued for event {EventId}";
                }
                else
                {
                    reason = "exit already submitted — awaiting broker reconciliation";
                }
                break;

            case FxFloorState.ExitPending:
                // Retry/first submit: the command is issued only while the
                // event has not actually been submitted (§9 idempotency).
                if (SubmittedEventId is null || SubmittedEventId != EventId)
                {
                    command = FxFloorCommand.ExecuteExit;
                    reason = $"hard exit commanded for event {EventId} (attempt {Attempts + 1})";
                }
                else
                {
                    reason = "exit already submitted — awaiting broker reconciliation";
                }
                break;

            case FxFloorState.ExitFailed:
                // §8: NEVER silently revert to HOLD. A failed protection
                // re-queues the exit for as long as the position lives.
                State = FxFloorState.ExitPending;
                if (SubmittedEventId is null || SubmittedEventId != EventId)
                {
                    command = FxFloorCommand.ExecuteExit;
                    reason = $"retry after failed submission (attempt {Attempts + 1}) — protection remains active";
                }
                else
                {
                    reason = "exit already submitted — awaiting broker reconciliation";
                }
                break;

            case FxFloorState.ExitSubmitted:
                reason = "exit submitted — awaiting broker reconciliation";
                break;

            case FxFloorState.ExitConfirmed or FxFloorState.Reset:
                reason = "protection complete";
                break;
        }

        return new FxFloorVerdict(State, command, EventId, executableR, FloorR, PeakR, reason);
    }

    /// <summary>The broker ACCEPTED the close request (§13: this is not
    /// confirmation — reconciliation decides that).</summary>
    public void MarkSubmitted(string orderId)
    {
        if (State is FxFloorState.ExitPending or FxFloorState.BreachDetected)
        {
            State = FxFloorState.ExitSubmitted;
            ExitOrderId = orderId;
            SubmittedEventId = EventId;
            Attempts++;
        }
    }

    /// <summary>The broker CONFIRMS the position is actually closed.</summary>
    public void MarkConfirmed()
    {
        if (State == FxFloorState.ExitSubmitted)
        {
            State = FxFloorState.ExitConfirmed;
        }
    }

    /// <summary>The broker refused (or the position survived): protection
    /// stays active and the next Advance re-queues the exit. The submitted
    /// marker clears — the event's exit did NOT happen, so its command may
    /// be re-issued (idempotency guards duplicate SUBMISSIONS, and a failed
    /// submission warrants a fresh one under the same event id).</summary>
    public void MarkFailed()
    {
        if (State is FxFloorState.ExitSubmitted or FxFloorState.ExitPending)
        {
            State = FxFloorState.ExitFailed;
            SubmittedEventId = null;
        }
    }

    /// <summary>The position is verifiably gone (reconciled closed, or
    /// pruned by the trusted bookkeeping pass).</summary>
    public void MarkReset()
    {
        if (State != FxFloorState.ExitConfirmed)
        {
            State = FxFloorState.Reset;
        }
    }
}
