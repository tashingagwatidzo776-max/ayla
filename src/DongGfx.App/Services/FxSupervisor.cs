using DongGfx.App.Infrastructure;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>Why the supervisor stopped or flattened the FX brain.</summary>
public enum FxHaltReason
{
    None,
    /// <summary>Session P/L fell below the configured daily-loss cap.</summary>
    DailyLossCap,
    /// <summary>Equity fell below the configured absolute floor.</summary>
    EquityFloor,
    /// <summary>The global kill switch was engaged.</summary>
    KillSwitch,
    /// <summary>The portfolio drawdown governor tripped.</summary>
    Governor,
    /// <summary>The MT5 bridge went unreachable while the brain was live.</summary>
    BridgeDown,
}

/// <summary>Outcome of one supervisor evaluation.</summary>
public sealed record FxSupervisorVerdict(bool TradingAllowed, FxHaltReason Halt, string Reason);

/// <summary>
/// The safety brain around the trading brain. The FxEngine decides WHAT to
/// trade; this decides WHETHER trading is allowed at all right now —
/// session loss caps, an absolute equity floor, the global kill switch, the
/// portfolio governor, and bridge health. Halt reasons are journaled and
/// (for engagement events) pushed to the webhook so monitoring sees stops.
/// </summary>
public sealed class FxSupervisor
{
    private readonly TradeJournal _journal;
    private readonly WebhookService? _webhook;
    private readonly Func<bool> _killSwitchEngaged;
    private readonly Func<bool> _governorTripped;
    private readonly Func<decimal> _dailyLossCap;
    private readonly Func<decimal> _equityFloor;

    private decimal? _sessionStartBalance;
    private bool _halted;
    private FxHaltReason _halt = FxHaltReason.None;

    public FxSupervisor(
        TradeJournal journal,
        Func<bool> killSwitchEngaged,
        Func<bool> governorTripped,
        Func<decimal> dailyLossCap,
        Func<decimal> equityFloor,
        WebhookService? webhook = null)
    {
        _journal = journal;
        _killSwitchEngaged = killSwitchEngaged;
        _governorTripped = governorTripped;
        _dailyLossCap = dailyLossCap;
        _equityFloor = equityFloor;
        _webhook = webhook;
    }

    /// <summary>Latest session-start balance the supervisor anchored to.</summary>
    public decimal? SessionStartBalance => _sessionStartBalance;

    public bool IsHalted => _halted;

    public FxHaltReason HaltReason => _halt;

    /// <summary>Anchor the loss baseline to the current balance (called when
    /// the brain starts, and after a flatten/re-arm).</summary>
    public void AnchorSession(decimal balance)
    {
        _sessionStartBalance = balance;
        Journal("FX_RISK", $"session loss baseline anchored at {balance:0.00} (cap {_dailyLossCap():0.##}, floor {_equityFloor():0.##})");
    }

    /// <summary>Evaluate all stop conditions against the current account
    /// snapshot. Pure decision — no transport here.</summary>
    public FxSupervisorVerdict Evaluate(bool bridgeUp, decimal balance, decimal equity)
    {
        // Loss stops latch: once tripped they stay tripped until ReAnchor,
        // even if the account recovers on its own (that is the point).
        if (_halted && _halt is FxHaltReason.DailyLossCap or FxHaltReason.EquityFloor)
        {
            return new FxSupervisorVerdict(false, _halt, $"halted (latched): {_halt}");
        }

        if (!bridgeUp)
        {
            return EnterHalt(FxHaltReason.BridgeDown, "MT5 bridge unreachable — trading halted");
        }

        if (_killSwitchEngaged())
        {
            return EnterHalt(FxHaltReason.KillSwitch, "global kill switch engaged — FX trading halted");
        }

        if (_governorTripped())
        {
            return EnterHalt(FxHaltReason.Governor, "portfolio drawdown governor tripped — FX trading halted");
        }

        var start = _sessionStartBalance ?? balance;
        _sessionStartBalance ??= balance;

        if (start - balance > _dailyLossCap())
        {
            return EnterHalt(FxHaltReason.DailyLossCap,
                $"daily loss cap hit — session P/L {balance - start:+0.00;-0.00} vs cap -{_dailyLossCap():0.##}");
        }

        var floor = _equityFloor();
        if (floor > 0 && equity < floor)
        {
            return EnterHalt(FxHaltReason.EquityFloor,
                $"equity {equity:0.00} below floor {floor:0.00} — FX trading halted");
        }

        return new FxSupervisorVerdict(true, FxHaltReason.None, "clear");
    }

    /// <summary>Clear a halt that came from a bridge/kill-switch/Governor
    /// transient (loss stops stay latched until explicitly re-anchored).
    /// </summary>
    public void ClearTransientHalts()
    {
        if (_halted && _halt is FxHaltReason.BridgeDown or FxHaltReason.KillSwitch or FxHaltReason.Governor)
        {
            _halted = false;
            _halt = FxHaltReason.None;
            Journal("FX_RISK", "transient halt cleared — conditions recovered");
        }
    }

    /// <summary>Forget the loss latch and re-anchor to the current balance
    /// (the operator's explicit "re-arm" after reviewing a loss stop).</summary>
    public void ReAnchor(decimal balance)
    {
        _halted = false;
        _halt = FxHaltReason.None;
        AnchorSession(balance);
        Journal("FX_RISK", $"FX loss stop re-armed, baseline {balance:0.00}");
    }

    private FxSupervisorVerdict EnterHalt(FxHaltReason reason, string message)
    {
        var first = !_halted || _halt != reason;
        _halted = true;
        _halt = reason;
        if (first)
        {
            Journal("FX_RISK", message);
            _webhook?.PostRiskRail("DON G FX — FX brain halted", message);
        }

        return new FxSupervisorVerdict(false, reason, message);
    }

    private void Journal(string category, string detail) =>
        _journal.Log(Guid.Empty, category, detail, "{}");
}
