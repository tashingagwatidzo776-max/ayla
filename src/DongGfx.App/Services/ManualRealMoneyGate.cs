using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>
/// Real-money gate for the manual trading surfaces (Terminal order card and
/// any other one-off order path). Evaluation is the shared
/// <see cref="RealMoneyGate"/>; the session unlock is instance-lifetime
/// and armed only by typing the confirmation phrase into the Terminal's
/// unlock panel — one arm covers every remaining trade path.
///
/// Instance-scoped by design (was static): the DI container registers one
/// singleton that every manual surface and the shutdown path share; tests
/// construct their own scope instead of mutating process-wide state, which
/// is what let parallel test classes race the latch.
/// </summary>
public sealed class ManualRealMoneyGate
{
    // Written by the UI thread (Arm/Reset), read from broker callbacks —
    // volatile so an arm is visible to in-flight gate evaluations.
    private volatile bool _unlocked;
    private DateTimeOffset? _armedAt;

    /// <summary>True once the confirmation phrase was typed this session.</summary>
    public bool IsUnlocked => _unlocked;

    /// <summary>When the session unlock was FIRST armed this session (null
    /// = locked). Re-arming an already-armed gate keeps the original
    /// timestamp — repeat unlock clicks must not reset the staleness clock
    /// that the unlock-staleness watch reads.</summary>
    public DateTimeOffset? ArmedAt => _armedAt;

    /// <summary>Evaluates the gate for the manual trade surfaces. The
    /// API-verified account type comes from the venue's own account state:
    /// not verified means unverified, which fails closed.</summary>
    public RealMoneyDecision Evaluate(bool configIsDemo, bool? apiVerifiedVirtual) =>
        RealMoneyGate.Evaluate(configIsDemo, apiVerifiedVirtual, _unlocked);

    /// <summary>Arms the session unlock (after the phrase was verified).
    /// The first arm stamps <see cref="ArmedAt"/>; repeat arms keep it.</summary>
    public void Arm()
    {
        _unlocked = true;
        _armedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>Clears the session unlock (called on app shutdown; unlocks
    /// are session-scoped by design).</summary>
    public void Reset()
    {
        _unlocked = false;
        _armedAt = null;
    }
}
