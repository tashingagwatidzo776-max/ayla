using Tf.Core.Models;

namespace Tf.App.Services;

/// <summary>
/// Real-money gate for the manual trading surfaces (Brain tab cycles and the
/// Trades tab's manual trade) that run on the app's primary Deriv client —
/// the one account outside the multi-account hub's per-account unlocks.
/// Evaluation is the shared <see cref="RealMoneyGate"/>; the session unlock
/// is instance-lifetime (like the hub's) and armed only by typing the
/// confirmation phrase into the Growth tab's in-tab unlock panel — one
/// phrase arms the hub accounts listed there and this shared gate.
///
/// Instance-scoped by design (was static): the DI container registers one
/// singleton that every manual surface, the hub, and the shutdown path
/// share; tests construct their own scope instead of mutating process-wide
/// state, which is what let parallel test classes race the latch.
/// </summary>
public sealed class ManualRealMoneyGate
{
    // Written by the UI thread (Arm/Reset), read from broker callbacks —
    // volatile so an arm is visible to in-flight gate evaluations.
    private volatile bool _unlocked;

    /// <summary>True once the confirmation phrase was typed this session.</summary>
    public bool IsUnlocked => _unlocked;

    /// <summary>Evaluates the gate for the primary client. The API-verified
    /// account type comes from the client's own authorize/balance state:
    /// not authorized (no login id) means unverified, which fails closed.</summary>
    public RealMoneyDecision Evaluate(bool configIsDemo, bool? apiVerifiedVirtual) =>
        RealMoneyGate.Evaluate(configIsDemo, apiVerifiedVirtual, _unlocked);

    /// <summary>Arms the session unlock (after the phrase was verified).</summary>
    public void Arm() => _unlocked = true;

    /// <summary>Clears the session unlock (called on app shutdown; unlocks
    /// are session-scoped by design).</summary>
    public void Reset() => _unlocked = false;
}
