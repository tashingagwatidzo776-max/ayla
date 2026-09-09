using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Infrastructure;
using Tf.App.Services;
using Tf.Core.Brain;
using Tf.Core.Models;

namespace Tf.App.ViewModels;

/// <summary>
/// Multi-account management: paste one Deriv API token per account row,
/// connect all simultaneously (one WebSocket per account), and remove them.
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly MultiAccountHub _hub;
    private readonly Func<AppSettings> _settings;

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

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "Add one Deriv API token per account. Demo tokens only — real money is blocked.";

    public ObservableCollection<AccountConnection> Accounts => _hub.Accounts;

    public IReadOnlyList<string> AvailableBrains { get; } = new BrainRegistry().GetBrainKeys().ToList();

    public AccountsViewModel(MultiAccountHub hub, Func<AppSettings> settings)
    {
        _hub = hub;
        _settings = settings;
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
            BrainKey = string.IsNullOrWhiteSpace(NewBrainKey) ? "Growth" : NewBrainKey.Trim()
        };

        try
        {
            var connection = _hub.AddAccount(config);
            StatusMessage = $"Added {connection.DisplayName} ({config.Symbol}). Press Connect on its row (or Connect all).";
            NewToken = "";
            NewLabel = "";
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
        catch (Exception ex)
        {
            StatusMessage = $"{connection.DisplayName} failed: {ex.Message}";
        }
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
}
