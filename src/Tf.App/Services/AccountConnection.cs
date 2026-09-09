using CommunityToolkit.Mvvm.ComponentModel;
using Tf.App.Infrastructure;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.Services;

/// <summary>
/// One Deriv account session: its own <see cref="DerivClient"/> (one account
/// per WebSocket connection on the Deriv API), live status/balance, and a
/// rolling tick buffer the growth brain reads for signals.
/// </summary>
public sealed partial class AccountConnection : ObservableObject, IAsyncDisposable
{
    private readonly DerivClient _client;
    private readonly List<Tick> _ticks = new();
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Raised whenever connection state or balance changes materially.</summary>
    public event Action<AccountConnection>? StateChanged;

    [ObservableProperty]
    private string statusText = "Not connected";

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string loginIdText = "";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string lastError = "";

    public AccountConnection(AccountConfig config)
    {
        Config = config;
        _client = new DerivClient { AppId = AppSettings.DefaultAppId };
        BalanceText = config.IsDemo ? "demo" : "REAL";
        LoginIdText = "";

        _client.StatusChanged += OnStatusChanged;
        _client.BalanceUpdated += OnBalanceUpdated;
        _client.TickReceived += OnTickReceived;
        _client.ErrorReceived += OnErrorReceived;
    }

    public AccountConfig Config { get; }
    public DerivClient Client => _client;
    public AccountBalance Balance => _client.Balance;
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Config.Label) ? Config.Label
        : !string.IsNullOrWhiteSpace(LoginIdText) ? LoginIdText
        : "Account";

    /// <summary>Rolling tick window (newest last) for signal computation.</summary>
    public IReadOnlyList<Tick> Ticks
    {
        get
        {
            lock (_sync)
            {
                return _ticks.ToArray();
            }
        }
    }

    public async Task ConnectAsync()
    {
        if (IsConnected || IsBusy)
        {
            return;
        }

        IsBusy = true;
        LastError = "";
        StatusText = "Connecting…";
        try
        {
            await _client.ConnectAsync(Config.ApiToken);

            // Backfill the signal window fast, then keep it live.
            try
            {
                var history = await _client.GetTicksHistoryAsync(Config.Symbol, 160);
                lock (_sync)
                {
                    _ticks.Clear();
                    _ticks.AddRange(history);
                }
            }
            catch
            {
                // History is best-effort; live ticks will populate the buffer.
            }

            await _client.SubscribeTicksAsync(Config.Symbol);
            StatusText = string.IsNullOrEmpty(LoginIdText)
                ? "Connected (not authorized)"
                : $"Connected · {LoginIdText}";
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusText = "Connect failed";
            RaiseStateChanged();
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _client.DisconnectAsync();
        StatusText = "Not connected";
        IsConnected = false;
        BalanceText = Config.IsDemo ? "demo" : "REAL";
        RaiseStateChanged();
    }

    private void OnStatusChanged(ConnectionStatus status)
    {
        IsConnected = status is ConnectionStatus.Connected or ConnectionStatus.Reconnecting;
        StatusText = status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Reconnecting => "Reconnecting…",
            ConnectionStatus.Error => "Error",
            _ => "Not connected"
        };
        RaiseStateChanged();
    }

    private void OnBalanceUpdated(AccountBalance balance)
    {
        BalanceText = $"{balance.Balance:0.##} {balance.Currency}";
        if (!string.IsNullOrEmpty(balance.LoginId))
        {
            LoginIdText = balance.LoginId;
            StatusText = $"Connected · {balance.LoginId}";
        }
        RaiseStateChanged();
    }

    private void OnTickReceived(Tick tick)
    {
        lock (_sync)
        {
            _ticks.Add(tick);
            if (_ticks.Count > 400)
            {
                _ticks.RemoveRange(0, _ticks.Count - 400);
            }
        }
    }

    private void OnErrorReceived(string message)
    {
        if (message.StartsWith("Connection lost"))
        {
            LastError = message;
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        // Usually arrives on a background socket thread; subscribers marshal.
        StateChanged?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.StatusChanged -= OnStatusChanged;
        _client.BalanceUpdated -= OnBalanceUpdated;
        _client.TickReceived -= OnTickReceived;
        _client.ErrorReceived -= OnErrorReceived;
        await _client.DisposeAsync();
    }
}
