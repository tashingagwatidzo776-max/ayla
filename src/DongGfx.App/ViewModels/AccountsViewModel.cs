using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Infrastructure;
using DongGfx.Deriv;
using DongGfx.App.Services;
using DongGfx.Core.Brain;
using DongGfx.Core.Models;

namespace DongGfx.App.ViewModels;

/// <summary>
/// Multi-account management: paste one Deriv API token per account row,
/// connect all simultaneously (one WebSocket per account), and remove them.
/// Also hosts the PAT-split guided flow: when two accounts share one token,
/// it walks the user through creating a per-account token and re-imports the
/// account in place (same id — history preserved).
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly MultiAccountHub _hub;
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private readonly PatSplitWizard _patSplit;

    [ObservableProperty]
    private string newToken = "";

    [ObservableProperty]
    private string newLabel = "";

    [ObservableProperty]
    private string newSymbol = AppSettings.DefaultSymbol;

    [ObservableProperty]
    private int newDurationMinutes = 5;

    [ObservableProperty]
    private decimal newBudget = 5.00m;

    [ObservableProperty]
    private string newBrainKey = "Growth";

    /// <summary>Ensemble sub-brains with optional weights, e.g.
    /// "Growth:1.5 TrendFollowing Breakout:0.5". Only read when the brain
    /// combo selects Ensemble; blank means all known rules brains vote equally.</summary>
    [ObservableProperty]
    private string newEnsembleConfig = "";

    /// <summary>When true, the token is a new-platform Personal Access
    /// Token (developers.deriv.com PAT app): connects through the OTP flow
    /// with the PAT app's registered id instead of the classic authorize.</summary>
    [ObservableProperty]
    private bool newIsPat;

    /// <summary>The PAT app's registered Deriv application id (required
    /// for PAT auth — the Deriv-App-ID header; NOT the token itself).</summary>
    [ObservableProperty]
    private string newDerivAppId = "";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "Add one Deriv API token per account. Demo tokens only — real money is blocked.";

    public ObservableCollection<AccountConnection> Accounts => _hub.Accounts;

    public IReadOnlyList<string> AvailableBrains { get; } = new BrainRegistry().GetBrainKeys().ToList();

    public AccountsViewModel(MultiAccountHub hub, Func<AppSettings> settings,
        SettingsService? settingsService = null)
    {
        _hub = hub;
        _settings = settings;
        _settingsService = settingsService ?? new SettingsService();
        _patSplit = new PatSplitWizard(hub, VerifyTokenViaDiscovery);
        // Persisted across restarts: the registration is one-time, the id
        // should not need re-pasting every session.
        oauthClientId = _settings().OAuthClientId;
    }

    /// <summary>Non-disruptive token verification: Deriv's discovery endpoint
    /// over plain HTTPS — the live WebSocket sessions are never touched. Uses
    /// the primary client's PAT app id when configured (the token was created
    /// under that app), else the classic default.</summary>
    private async Task<IReadOnlyList<NewPlatformAccount>> VerifyTokenViaDiscovery(string token)
    {
        if (DiscoveryOverrideForTests is not null)
        {
            return await DiscoveryOverrideForTests(token).ConfigureAwait(true);
        }

        var settings = _settings();
        var appId = settings.PrimaryNewPlatform && settings.PrimaryDerivAppId.Length > 0
            ? settings.PrimaryDerivAppId
            : settings.AppId;
        var auth = new NewPlatformAuth(appId);
        return await auth.ListAccountsAsync(token).ConfigureAwait(false);
    }

    public string DemoNotice =>
        "Connect all opens one live WebSocket per account; each row shows its own login, balance and feed.";

    [RelayCommand]
    private void AddAccount()
    {
        var token = NewToken.Trim();
        if (token.Length < 8)
        {
            StatusMessage = "Paste a full Deriv API token (it looks like a long random string).";
            return;
        }

        if (NewIsPat && string.IsNullOrWhiteSpace(NewDerivAppId))
        {
            StatusMessage = "A PAT needs its app's registered Deriv App ID (developers.deriv.com dashboard, not the token).";
            return;
        }

        var baseSettings = _settings();
        var config = new AccountConfig
        {
            Label = string.IsNullOrWhiteSpace(NewLabel) ? $"Account {Accounts.Count + 1}" : NewLabel.Trim(),
            ApiToken = token,
            IsDemo = true,
            Symbol = string.IsNullOrWhiteSpace(NewSymbol) ? AppSettings.DefaultSymbol : NewSymbol.Trim(),
            Currency = baseSettings.Currency,
            DurationMinutes = Math.Max(1, NewDurationMinutes),
            StartBudget = Math.Max(0.50m, NewBudget),
            BrainKey = string.IsNullOrWhiteSpace(NewBrainKey) ? "Growth" : NewBrainKey.Trim(),
            EnsembleConfig = NewEnsembleConfig.Trim(),
            NewPlatform = NewIsPat,
            DerivAppId = NewDerivAppId.Trim(),
            DerivAccountId = ""
        };

        try
        {
            var connection = _hub.AddAccount(config);
            StatusMessage = $"Added {connection.DisplayName} ({config.Symbol}). Press Connect on its row (or Connect all).";
            NewToken = "";
            NewLabel = "";
            NewIsPat = false;
            NewDerivAppId = "";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ConnectAccountAsync(AccountConnection? connection)
    {
        if (connection is null || connection.IsBusy)
        {
            return;
        }

        try
        {
            if (connection.IsConnected)
            {
                await connection.DisconnectAsync();
                StatusMessage = $"{connection.DisplayName} disconnected.";
                return;
            }

            await connection.ConnectAsync();
            StatusMessage = $"{connection.DisplayName} connected · {connection.LoginIdText} · {connection.BalanceText}";
        }
        catch (DerivApiException ex) when (ex.Code == "MarketIsClosed")
        {
            // Expected weekend/holiday state — never an error. The engine
            // idles until Deriv reopens; saying "failed" here made a normal
            // closure look like a broken connection.
            StatusMessage = $"{connection.DisplayName}: market closed — resumes automatically when Deriv reopens.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"{connection.DisplayName} failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePause(AccountConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        connection.IsPaused = !connection.IsPaused;
        StatusMessage = connection.IsPaused
            ? $"{connection.DisplayName} paused — growth engine will skip cycles."
            : $"{connection.DisplayName} resumed.";
    }

    [RelayCommand]
    private void ResetCircuitBreaker(AccountConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        connection.ResetCircuitBreaker();
        StatusMessage = $"{connection.DisplayName} circuit breaker reset — ready to reconnect.";
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(AccountConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        await _hub.RemoveAccountAsync(connection);
        StatusMessage = "Account removed.";
    }

    [RelayCommand]
    private async Task ConnectAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Connecting {Accounts.Count(a => !a.IsConnected)} account(s)…";
            await _hub.ConnectAllAsync();

            var connected = Accounts.Count(a => a.IsConnected);
            var failed = Accounts.Count(a => !a.IsConnected && !string.IsNullOrEmpty(a.LastError));
            StatusMessage = failed > 0
                ? $"{connected} connected, {failed} failed (check token / network on each row)."
                : $"{connected}/{Accounts.Count} accounts connected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        await _hub.DisconnectAllAsync();
        StatusMessage = "All accounts disconnected — growth runners stopped.";
    }

    [RelayCommand]
    private void ExportAccounts()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = ".json",
                FileName = $"tf_accounts_{DateTime.UtcNow:yyyyMMdd}.json"
            };

            if (dialog.ShowDialog() == true)
            {
                var vault = new AccountVault();
                vault.Export(dialog.FileName, _hub.Accounts.Select(a => a.Config).ToArray());
                StatusMessage = $"Exported {Accounts.Count} account(s) to {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // ── PAT split guided flow ──────────────────────────────────────────
    // Two accounts on one token churn each other's sessions (the measured
    // reconnect-storm root cause). This flow walks the user through minting
    // one token per account and re-imports them in place.

    /// <summary>Token groups shared by more than one account row, refreshed
    /// by <see cref="DetectSharedTokens"/>.</summary>
    public ObservableCollection<string> SharedTokens { get; } = new();

    /// <summary>The shared token currently being split.</summary>
    [ObservableProperty]
    private string splitTargetToken = "";

    /// <summary>The account (by label) the pending token will be applied to.</summary>
    [ObservableProperty]
    private string splitAccountLabel = "";

    /// <summary>The new per-account token the user pasted for the pending split.</summary>
    [ObservableProperty]
    private string splitNewToken = "";

    /// <summary>The discovery verdict for the pending token — shown to the
    /// user so they can confirm the token opens the right account before it
    /// replaces the shared one.</summary>
    [ObservableProperty]
    private string splitVerifyResult = "";

    // Hand-rolled observables (not [ObservableProperty]): keeps the OAuth
    // state independent of source-generator quirks.
    private string oauthClientId = "";

    /// <summary>The OAuth client_id of the Deriv OAuth-type app registration
    /// (developers.deriv.com dashboard). Empty until the user registers one
    /// — PAT remains the default path meanwhile.</summary>
    public string OAuthClientId
    {
        get => oauthClientId;
        set
        {
            if (oauthClientId != value)
            {
                oauthClientId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOAuthClientId));
            }
        }
    }

    private string oauthStatus = "";

    /// <summary>Live progress text for the browser round-trip.</summary>
    public string OAuthStatus
    {
        get => oauthStatus;
        set
        {
            if (oauthStatus != value)
            {
                oauthStatus = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private bool hasSharedToken;

    [RelayCommand]
    private void DetectSharedTokens()
    {
        var groups = _patSplit.DetectSharedTokens();
        SharedTokens.Clear();
        foreach (var g in groups)
        {
            SharedTokens.Add($"{g.Labels.Count} accounts share one token: {string.Join(", ", g.Labels)}");
        }
        HasSharedToken = groups.Count > 0;
        if (groups.Count > 0)
        {
            SplitTargetToken = groups[0].Token;
            SplitAccountLabel = groups[0].Labels[0];
            StatusMessage = $"Shared token detected — create a fresh token in Deriv for '{SplitAccountLabel}', paste it below, then Verify.";
        }
        else
        {
            SplitTargetToken = "";
            SplitAccountLabel = "";
            StatusMessage = "No shared tokens — every account already has its own token. 🎉";
        }
    }

    [RelayCommand]
    private async Task VerifySplitTokenAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SplitNewToken))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var accounts = await _patSplit.VerifyNewTokenAsync(SplitNewToken.Trim());
            var summary = string.Join(", ", accounts.Select(a =>
                $"{a.AccountId} ({(a.IsDemoAccount ? "demo" : "REAL")}, {a.Balance:0.00} {a.Currency})"));
            SplitVerifyResult = $"Token verified — opens: {summary}";
            StatusMessage = "Token verified. Check the account above is the right one, then Apply.";
        }
        catch (Exception ex)
        {
            SplitVerifyResult = "";
            StatusMessage = $"Token rejected: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ApplySplitToken()
    {
        if (string.IsNullOrWhiteSpace(SplitNewToken))
        {
            StatusMessage = "Verify a token first — nothing to apply.";
            return;
        }

        try
        {
            // The pending token was verified for the first account of the
            // group (SplitAccountLabel); find it by label + shared token.
            var target = Accounts.FirstOrDefault(a =>
                a.Config.Label == SplitAccountLabel &&
                a.Config.ApiToken == SplitTargetToken)
                ?? throw new InvalidOperationException(
                    $"No account '{SplitAccountLabel}' on the shared token — re-run Detect.");

            _patSplit.ApplyToken(target.Config.Id, SplitNewToken);
            StatusMessage = $"'{SplitAccountLabel}' now has its own token (id preserved). " +
                "Press Connect on its row to use it — no re-import needed.";
            SplitNewToken = "";
            SplitVerifyResult = "";
            DetectSharedTokens();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Split failed: {ex.Message}";
        }
    }

    // ── OAuth 2.0 sign-in (second sign-in path; PAT stays default) ─────
    // Desktop variant of Deriv's OAuth: browser consent → loopback capture
    // → token exchange. The resulting short-lived bearer feeds the same
    // import path as a pasted PAT, so every rail downstream is unchanged.

    private OAuthSignIn _oauth = new();

    /// <summary>Test seam: when set, token verification (discovery) uses this
    /// delegate instead of the real network client.</summary>
    internal Func<string, Task<IReadOnlyList<NewPlatformAccount>>>? DiscoveryOverrideForTests { get; set; }

    /// <summary>Test seam: replaces the OAuth flow with a fake (canned
    /// result) so the VM command runs without a browser.</summary>
    internal void SetOAuthForTests(OAuthSignIn oauth) => _oauth = oauth;

    /// <summary>True once an OAuth client id is configured — XAML band
    /// visibility helper.</summary>
    public bool HasOAuthClientId => OAuthClientId.Trim().Length > 0;

    [RelayCommand]
    private async Task SignInWithDerivAsync()
    {
        var clientId = OAuthClientId.Trim();
        if (IsBusy)
        {
            return;
        }

        if (clientId.Length == 0)
        {
            OAuthStatus = "Register an OAuth-type app at developers.deriv.com first, then paste its client id here.";
            return;
        }

        IsBusy = true;
        try
        {
            // The registered redirect must match EXACTLY, so real sign-ins use
        // the fixed registrable loopback port (tests pass their own).
        var result = await _oauth.SignInAsync(
                clientId,
                loopbackPort: OAuthSignIn.DefaultLoopbackPort,
                onWaiting: msg => OAuthStatus = msg).ConfigureAwait(true);

            var mins = Math.Max(1, result.ExpiresInSeconds / 60);
            OAuthStatus = $"Signed in — token received (expires in ~{mins} min).";

            // Keep the registration for next session — one-time setup.
            var liveSettings = _settings();
            if (liveSettings.OAuthClientId != clientId)
            {
                liveSettings.OAuthClientId = clientId;
                _settingsService.Save(liveSettings);
            }

            // Same import path as a pasted PAT: discovery verifies, then the
            // account lands as a new-platform row with the OAuth bearer. The
            // refresh token (when the server issued one) rides along so the
            // connection renews the ~1-hour access token itself — no second
            // browser round-trip when it expires.
            var added = await ImportTokenAsAccountsAsync(
                result.AccessToken, "OAuth session",
                refreshToken: result.RefreshToken,
                expiresInSeconds: result.ExpiresInSeconds,
                clientId: clientId).ConfigureAwait(true);

            OAuthStatus = added > 0 && result.RefreshToken is not null
                ? $"Signed in — imported accounts from the OAuth session. " +
                  $"The access token expires in ~{mins} min, but it renews itself " +
                  "from the refresh token before that (PATs remain the default)."
                : added > 0
                    ? $"Signed in — imported accounts from the OAuth session. " +
                      $"No refresh token was issued: the token expires in ~{mins} min, " +
                      $"then re-sign-in is required (PATs remain the default for long sessions)."
                    : "Signed in — this token's account is already in the list.";
        }
        catch (Exception ex)
        {
            OAuthStatus = $"OAuth sign-in failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shared by PAT paste and OAuth sign-in: verifies the bearer
    /// via discovery (plain HTTPS, live sessions untouched) and adds ONE
    /// account row for the token — the demo account when discovery lists
    /// several (an OAuth consent covers every account with one bearer, but
    /// this app's architecture is one token per row: rows sharing a token
    /// churn each other's sessions — the measured reconnect-storm root
    /// cause). Additional simultaneous accounts need their own PATs.
    /// Returns the number added (0 when the token is already imported).</summary>
    private async Task<int> ImportTokenAsAccountsAsync(
        string token,
        string labelPrefix,
        string? refreshToken = null,
        int expiresInSeconds = 0,
        string? clientId = null)
    {
        var accounts = await VerifyTokenViaDiscovery(token).ConfigureAwait(true);
        if (accounts.Count == 0)
        {
            throw new InvalidOperationException("Deriv listed no accounts for this token.");
        }

        // Demo preferred, first-listed as fallback — fail closed toward virtual funds.
        var chosen = accounts.FirstOrDefault(a => a.IsDemoAccount) ?? accounts[0];

        var baseSettings = _settings();
        var config = new AccountConfig
        {
            Label = $"{labelPrefix} · {(chosen.IsDemoAccount ? "demo" : "REAL")}",
            ApiToken = token,
            IsDemo = chosen.IsDemoAccount,
            Symbol = AppSettings.DefaultSymbol,
            Currency = chosen.Currency,
            DurationMinutes = 5,
            StartBudget = 5.00m,
            BrainKey = "Growth",
            NewPlatform = true,
            DerivAppId = baseSettings.PrimaryNewPlatform && baseSettings.PrimaryDerivAppId.Length > 0
                ? baseSettings.PrimaryDerivAppId
                : baseSettings.AppId,
            DerivAccountId = chosen.AccountId,

            // OAuth-only: the refresh grant needs the client id + refresh
            // token + deadline; a PAT import leaves all three empty.
            OAuthRefreshToken = refreshToken ?? "",
            OAuthClientId = clientId ?? "",
            TokenExpiresAtUtc = expiresInSeconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds)
                : null
        };

        var added = 0;
        try
        {
            _hub.AddAccount(config);
            added = 1;
        }
        catch (InvalidOperationException)
        {
            // Duplicate token — this token's row already exists.
        }

        StatusMessage = added > 0
            ? $"Imported the {chosen.AccountId} {chosen.AccountType} row from the token " +
              "(one row per token — this app never shares a token across rows). " +
              "For additional simultaneous accounts, paste a dedicated PAT per account."
            : "This token's account is already in the list.";
        return added;
    }

    [RelayCommand]
    private void ImportAccounts()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog() == true)
            {
                var vault = new AccountVault();
                var imported = vault.Import(dialog.FileName);
                var added = 0;

                foreach (var config in imported)
                {
                    try
                    {
                        _hub.AddAccount(config);
                        added++;
                    }
                    catch (InvalidOperationException)
                    {
                        // Duplicate token — skip silently.
                    }
                }

                StatusMessage = $"Imported {added} new account(s) from {Path.GetFileName(dialog.FileName)}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }
}
