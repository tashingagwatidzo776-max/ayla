using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Services;

/// <summary>Wires the primary DerivClient to the connection described by
/// AppSettings: classic authorize by default, or the new-platform OTP flow
/// when the primary is a PAT. For PAT primaries, discovery is the demo/real
/// verification and MUST succeed — its verdict patches the client's
/// balances so the real-money gate always sees a verified account, never
/// an unverified one.</summary>
public static class PrimaryClientWiring
{
    public static async Task ApplyAsync(DerivClient client, AppSettings settings)
    {
        client.IsVirtualOverride = null;
        if (settings.PrimaryNewPlatform)
        {
            var auth = new NewPlatformAuth(
                string.IsNullOrWhiteSpace(settings.PrimaryDerivAppId)
                    ? settings.AppId : settings.PrimaryDerivAppId);

            var accounts = await auth.ListAccountsAsync(settings.ApiToken);
            var match = accounts.FirstOrDefault(a =>
                            a.AccountId == settings.PrimaryDerivAccountId)
                        ?? accounts.FirstOrDefault(a => a.IsDemoAccount)
                        ?? accounts.FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            "The new platform listed no accounts for this token.");

            settings.PrimaryDerivAccountId = match.AccountId;
            client.NewPlatform = auth;
            client.NewPlatformToken = settings.ApiToken;
            client.NewPlatformAccountId = match.AccountId;
            client.IsVirtualOverride = match.IsDemoAccount;
        }
        else
        {
            client.NewPlatform = null;
            client.NewPlatformToken = null;
            client.NewPlatformAccountId = null;
        }

        client.AppId = settings.AppId;
    }
}
