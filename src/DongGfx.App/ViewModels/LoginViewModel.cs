using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Services;

namespace DongGfx.App.ViewModels;

/// <summary>
/// View model for the MT5 account login dialog (File→Login): switches the
/// terminal's signed-in account through the sidecar's POST /login over
/// loopback only. Credentials live only in this VM for the lifetime of the
/// dialog — never logged, never journaled, never echoed into status text,
/// cleared from the field on success. Input validation mirrors the sidecar's
/// so a bad field is refused BEFORE burning one of the 3 allowed attempts
/// per minute; when the sidecar rate limit does trip, its refusal surfaces
/// verbatim so the dialog tells the user to wait instead of retrying.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly Mt5BridgeClient _mt5;

    /// <summary>One entry of the dialog's Recent picker: "login · server".
    /// Built from the two persisted slots (last + previous successful
    /// switches); passwords are never persisted, so nothing to hide here.</summary>
    public sealed record RecentAccount(string Login, string Server)
    {
        public string Label => $"{Login} · {Server}";
    }

    [ObservableProperty]
    private string account = "";

    [ObservableProperty]
    private string password = "";

    [ObservableProperty]
    private string server = "";

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private bool isLoggingIn;

    /// <summary>The two persisted accounts (last, then previous) for the
    /// Recent dropdown; choosing an entry fills Account and Server.
    /// De-duplicated and blank-free.</summary>
    public IReadOnlyList<RecentAccount> RecentAccounts { get; private set; } =
        Array.Empty<RecentAccount>();

    [ObservableProperty]
    private RecentAccount? selectedRecent;

    partial void OnSelectedRecentChanged(RecentAccount? value)
    {
        if (value is null)
        {
            return;
        }
        Account = value.Login;
        Server = value.Server;
    }

    public LoginViewModel(Mt5BridgeClient mt5) => _mt5 = mt5;

    /// <summary>Raised after a successful switch (UI thread) — the dialog
    /// closes itself on this.</summary>
    public event Action? SignedIn;

    /// <summary>Post-login refresh (the Terminal account bar), invoked
    /// best-effort so a refresh failure never masks the successful login.</summary>
    public Func<Task>? OnSignedIn { get; set; }

    /// <summary>Seeds the Recent picker. Called by the dialog's owner with
    /// (last, server, prev, server) before ShowDialog — the picker then
    /// fills Account/Server when an entry is chosen.</summary>
    public void SetRecentAccounts(
        string lastLogin, string lastServer, string prevLogin, string prevServer)
    {
        var list = new List<RecentAccount>(2);
        if (!string.IsNullOrWhiteSpace(lastLogin) && !string.IsNullOrWhiteSpace(lastServer))
        {
            list.Add(new RecentAccount(lastLogin.Trim(), lastServer.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(prevLogin) && !string.IsNullOrWhiteSpace(prevServer))
        {
            var prev = new RecentAccount(prevLogin.Trim(), prevServer.Trim());
            if (!list.Any(r => r.Login == prev.Login && r.Server == prev.Server))
            {
                list.Add(prev);
            }
        }
        RecentAccounts = list;
        OnPropertyChanged(nameof(RecentAccounts));
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsLoggingIn)
        {
            return;
        }

        // Mirror the sidecar's validation: no attempt is spent on a field
        // the bridge would refuse anyway (attempts are rate-limited).
        if (!long.TryParse(Account.Trim(), out var accountId))
        {
            StatusMessage = "login must be the numeric account id";
            return;
        }
        if (string.IsNullOrEmpty(Password) || string.IsNullOrWhiteSpace(Server))
        {
            StatusMessage = "password and server are required";
            return;
        }

        try
        {
            IsLoggingIn = true;
            StatusMessage = "Signing in...";

            var result = await _mt5.LoginAsync(accountId, Password, Server.Trim())
                .ConfigureAwait(true);
            if (!result.Ok)
            {
                // Includes the sidecar's rate-limit refusal (422/429 →
                // "too many login attempts — wait a minute").
                StatusMessage = result.Error ?? "login refused";
                return;
            }

            Password = string.Empty;   // credentials leave the field here
            StatusMessage = $"Signed in: {result.Login} @ {result.Server}" +
                (result.Balance is { } b ? $" · {b:0.##} {result.Currency}" : "");

            if (OnSignedIn is { } refresh)
            {
                try
                {
                    await refresh().ConfigureAwait(true);
                }
                catch
                {
                    // The account bar degrades on its own; never mask the
                    // successful login with a refresh failure.
                }
            }

            SignedIn?.Invoke();
        }
        catch (Exception)
        {
            StatusMessage = "MT5 bridge unreachable — start the sidecar and retry";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }
}
