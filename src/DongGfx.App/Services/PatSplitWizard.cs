using DongGfx.Deriv;

namespace DongGfx.App.Services;

/// <summary>
/// Guided flow for splitting a shared Deriv PAT across accounts.
///
/// Why this exists: when several accounts reference one API token, each
/// connection mints single-use OTP URLs from that token — and the new
/// platform invalidates sibling sessions when a token's sessions churn.
/// That was the measured root cause of the reconnect storm (hundreds of
/// reconnects/minute on the shared token). The fix at the source: one token
/// per account.
///
/// The flow: <see cref="DetectAsync"/> finds accounts sharing a token (and
/// checks it against the vault for a "used elsewhere" verdict);
/// <see cref="VerifyNewTokenAsync"/> checks a freshly created per-account
/// token against Deriv's discovery endpoint — a plain HTTPS call that lists
/// the token's accounts, so the user's live WebSocket sessions are never
/// touched; <see cref="ApplyTokenAsync"/> swaps the token into an existing
/// <see cref="AccountConfig"/> <b>in place</b> (same account id → journal
/// and history stay continuous) and persists via
/// <see cref="MultiAccountHub.SaveAccounts"/>.
/// </summary>
public sealed class PatSplitWizard
{
    /// <summary>Verifies a candidate token without opening any WebSocket.
    /// Returns the discovery result (account ids, demo/real verdicts) or
    /// throws on invalid/unauthorized tokens. Tests inject a fake.</summary>
    public delegate Task<IReadOnlyList<NewPlatformAccount>> TokenVerifier(string token);

    private readonly MultiAccountHub _hub;
    private readonly TokenVerifier _verify;

    public PatSplitWizard(MultiAccountHub hub, TokenVerifier verify)
    {
        _hub = hub;
        _verify = verify;
    }

    /// <summary>One shared token per account id — the units the wizard works
    /// in. Only accounts with two or more rows sharing a token are split.</summary>
    public sealed record SharedTokenGroup(
        string Token,
        IReadOnlyList<Guid> AccountIds,
        IReadOnlyList<string> Labels);

    /// <summary>Finds every token referenced by more than one account row.
    /// Returns an empty list when each account already has its own token.</summary>
    public IReadOnlyList<SharedTokenGroup> DetectSharedTokens()
    {
        return _hub.Accounts
            .GroupBy(a => a.Config.ApiToken, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new SharedTokenGroup(
                g.Key,
                g.Select(a => a.Config.Id).ToArray(),
                g.Select(a => a.Config.Label).ToArray()))
            .ToList();
    }

    /// <summary>True when <paramref name="token"/> is referenced by more than
    /// one account row — the wizard's per-step guard.</summary>
    public bool IsShared(string token)
    {
        return _hub.Accounts.Count(a =>
            string.Equals(a.Config.ApiToken, token, StringComparison.Ordinal)) > 1;
    }

    /// <summary>Verifies a new per-account token against Deriv discovery.
    /// Also refuses a token that is already used by another row in the hub
    /// (that would recreate the sharing problem). Returns the discovery
    /// result so the UI can show the user exactly which account the token
    /// opens (and its demo/real verdict) before anything is applied.</summary>
    public async Task<IReadOnlyList<NewPlatformAccount>> VerifyNewTokenAsync(string token)
    {
        var duplicate = _hub.Accounts.FirstOrDefault(a =>
            string.Equals(a.Config.ApiToken, token, StringComparison.Ordinal));
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"That token is already assigned to '{duplicate.Config.Label}'. " +
                "Create a fresh token in Deriv for this account instead.");
        }

        return await _verify(token).ConfigureAwait(false);
    }

    /// <summary>Swaps <paramref name="newToken"/> into the account's config
    /// <b>in place</b> (same <see cref="AccountConfig.Id"/> — journal and
    /// history continuity preserved) and persists immediately. The row's
    /// display refreshes via its existing binding; reconnect on demand.</summary>
    public void ApplyToken(Guid accountId, string newToken)
    {
        var connection = _hub.Accounts.FirstOrDefault(a => a.Config.Id == accountId)
            ?? throw new InvalidOperationException("Account not found.");

        if (string.IsNullOrWhiteSpace(newToken) || newToken.Length < 8)
        {
            throw new InvalidOperationException(
                "The new token looks too short to be a Deriv API token.");
        }

        connection.Config.ApiToken = newToken.Trim();
        _hub.SaveAccounts();
    }
}
