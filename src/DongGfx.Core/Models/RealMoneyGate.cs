namespace DongGfx.Core.Models;

/// <summary>Outcome of a real-money eligibility check.</summary>
public enum RealMoneyDecision
{
    /// <summary>Not a real-money request — the demo path proceeds.</summary>
    DemoPassthrough,

    /// <summary>Real money allowed (user config says real, the venue API
    /// confirmed the account is non-virtual, and the session unlock is
    /// armed).</summary>
    Allowed,

    /// <summary>Config says real but the API says the account is virtual
    /// (or unverified) — refuse.</summary>
    BlockedAccountIsVirtual,

    /// <summary>Config says real but the API has not verified the account
    /// (no authorize/balance yet) — refuse until verified.</summary>
    BlockedUnverified,

    /// <summary>The two-step unlock has not been completed for this
    /// session — refuse until the user confirms.</summary>
    BlockedLocked,

    /// <summary>Config/API mismatch: the account is marked demo but the
    /// API says it is real — refuse and demand the user fix the config.</summary>
    BlockedConfigMismatch,
}

/// <summary>
/// Headless gate for placing real-money orders. Every condition must pass:
/// the account config says real, the venue's own API state has verified the
/// account is non-virtual, and the explicit two-step unlock (user typed the
/// confirmation phrase) is active for the session. Refusal reasons are
/// specific so the UI can explain exactly which condition failed. Demo
/// accounts pass through untouched.
/// </summary>
public static class RealMoneyGate
{
    /// <summary>Phrase the user must type to arm the unlock (exact match).</summary>
    public const string ConfirmationPhrase = "TRADE REAL MONEY";

    /// <summary>Evaluates whether a growth engine may start on this account.
    /// <paramref name="apiVerifiedVirtual"/> is the venue API's own
    /// non-virtual verdict; null = not verified yet.</summary>
    public static RealMoneyDecision Evaluate(
        bool configIsDemo, bool? apiVerifiedVirtual, bool unlockArmed)
    {
        if (configIsDemo)
        {
            // A demo-flagged account whose API verification says real is a
            // config mistake worth surfacing loudly, not silently trading.
            return apiVerifiedVirtual is false
                ? RealMoneyDecision.BlockedConfigMismatch
                : RealMoneyDecision.DemoPassthrough;
        }

        if (apiVerifiedVirtual is null)
        {
            return RealMoneyDecision.BlockedUnverified;
        }

        if (apiVerifiedVirtual.Value)
        {
            return RealMoneyDecision.BlockedAccountIsVirtual;
        }

        return unlockArmed
            ? RealMoneyDecision.Allowed
            : RealMoneyDecision.BlockedLocked;
    }

    /// <summary>Human-readable explanation for a refused start.</summary>
    public static string Explain(RealMoneyDecision decision) => decision switch
    {
        RealMoneyDecision.Allowed => "Real-money trading unlocked.",
        RealMoneyDecision.DemoPassthrough => "",
        RealMoneyDecision.BlockedAccountIsVirtual =>
            "REFUSED: the venue reports this account is virtual (demo funds). " +
            "Configure a real account to trade real money.",
        RealMoneyDecision.BlockedUnverified =>
            "REFUSED: the account type has not been verified yet. " +
            "Connect and verify the account type first.",
        RealMoneyDecision.BlockedLocked =>
            $"REFUSED: real-money trading is locked. Type \"{ConfirmationPhrase}\" " +
            "in the unlock dialog to arm it for this session.",
        RealMoneyDecision.BlockedConfigMismatch =>
            "REFUSED: this account is marked demo in settings but the venue " +
            "reports a real account. Fix the account's demo/real flag first.",
        _ => "REFUSED.",
    };
}
