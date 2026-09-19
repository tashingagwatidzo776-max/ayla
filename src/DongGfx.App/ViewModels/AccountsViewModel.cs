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
