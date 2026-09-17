using Tf.Core.Models;

namespace Tf.App.Services;

/// <summary>
/// Real-money gate for the manual trading surfaces (Brain tab cycles and the
/// Trades tab's manual trade) that run on the app's primary Deriv client —
/// the one account outside the multi-account hub's per-account unlocks.
/// Evaluation is the shared <see cref="RealMoneyGate"/>; the session unlock
/// is process-lifetime (like the hub's) and armed only by typing the
/// confirmation phrase into the Growth tab's in-tab unlock panel — one
/// phrase arms the hub accounts listed there and this shared gate.
/// </summary>
public static class ManualRealMoneyGate
{
    private static bool _unlocked;

    /// <summary>True once the confirmation phrase was typed this session.</summary>
    public static bool IsUnlocked => _unlocked;

    /// <summary>Evaluates the gate for the primary client. The API-verified
    /// account type comes from the client's own authorize/balance state:
    /// not authorized (no login id) means unverified, which fails closed.</summary>
    public static RealMoneyDecision Evaluate(bool configIsDemo, bool? apiVerifiedVirtual) =>
        RealMoneyGate.Evaluate(configIsDemo, apiVerifiedVirtual, _unlocked);

    /// <summary>Arms the session unlock (after the phrase was verified).</summary>
    public static void Arm() => _unlocked = true;

    /// <summary>Clears the session unlock (called on app shutdown; unlocks
    /// are session-scoped by design).</summary>
    public static void Reset() => _unlocked = false;
}
