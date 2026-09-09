using CommunityToolkit.Mvvm.ComponentModel;
using Tf.App.Infrastructure;
using Tf.Core.Analytics;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.Services;

/// <summary>
/// One Deriv account session: its own <see cref="DerivClient"/> (one account
/// per WebSocket connection on the Deriv API), live status/balance, and a
/// rolling tick buffer the growth brain reads for signals.
/// 
/// Includes a circuit breaker that pauses reconnection after repeated failures
/// and a per-account pause/resume toggle independent of the global kill switch.
/// </summary>
public sealed partial class AccountConnection : ObservableObject, IAsyncDisposable
{
    private readonly DerivClient _client;
    private readonly List<Tick> _ticks = new();
    private readonly object _sync = new();
    private readonly TickHistoryCache? _tickCache;
    private readonly HeartbeatLog? _heartbeat;
    private bool _disposed;
    private string _lastState = "Not connected";

    // ── Circuit breaker ────────────────────────────────────────────
    private const int MaxConsecutiveFailures = 5;
    private const int CircuitOpenMinutes = 10;
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

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

    /// <summary>
    /// Per-account pause toggle. When true, the growth runner skips this
    /// account even if the global kill switch is off.
    /// </summary>
    [ObservableProperty]
    private bool isPaused;

    /// <summary>True when the circuit breaker has tripped (too many failures).</summary>
    [ObservableProperty]
    private bool isDegraded;

    /// <summary>Human-readable circuit breaker status.</summary>
    [ObservableProperty]
    private string circuitStatus = "";

    public AccountConnection(AccountConfig config, TickHistoryCache? tickCache = null, HeartbeatLog? heartbeat = null)
    {
        Config = config;
        _client = new DerivClient { AppId = AppSettings.DefaultAppId };
        _tickCache = tickCache;
        _heartbeat = heartbeat;
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

        // Circuit breaker: refuse to connect if too many consecutive failures.
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            if (DateTimeOffset.UtcNow < _circuitOpenUntil)
            {
                var remaining = (_circuitOpenUntil - DateTimeOffset.UtcNow).TotalMinutes;
                StatusText = $"Degraded — retry in {remaining:0} min ({_consecutiveFailures} failures)";
                IsDegraded = true;
                CircuitStatus = $"Open ({_consecutiveFailures} failures, {remaining:0} min left)";
                RaiseStateChanged();
                return;
            }

            // Cooldown expired — allow a retry attempt.
            _consecutiveFailures = 0;
            IsDegraded = false;
            CircuitStatus = "";
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

                // Cache history for backtesting.
                _tickCache?.AddTicks(Config.Symbol, history);
            }
            catch
            {
                // History is best-effort; live ticks will populate the buffer.
            }

            await _client.SubscribeTicksAsync(Config.Symbol);

            // Success — reset circuit breaker.
            _consecutiveFailures = 0;
            IsDegraded = false;
            CircuitStatus = "";

            StatusText = string.IsNullOrEmpty(LoginIdText)
                ? "Connected (not authorized)"
                : $"Connected · {LoginIdText}";
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddMinutes(CircuitOpenMinutes);
                IsDegraded = true;
                CircuitStatus = $"Open ({_consecutiveFailures} failures, {CircuitOpenMinutes} min cooldown)";
                StatusText = $"Degraded — too many failures, pausing {CircuitOpenMinutes} min";
            }
            else
            {
                StatusText = $"Connect failed ({_consecutiveFailures}/{MaxConsecutiveFailures})";
            }

            LastError = ex.Message;
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
        _consecutiveFailures = 0;
        IsDegraded = false;
        CircuitStatus = "";
        RaiseStateChanged();
    }

    /// <summary>Manually reset the circuit breaker (e.g. after fixing a token).</summary>
    public void ResetCircuitBreaker()
    {
        _consecutiveFailures = 0;
        _circuitOpenUntil = DateTimeOffset.MinValue;
        IsDegraded = false;
        CircuitStatus = "";
        RaiseStateChanged();
    }

    private void OnStatusChanged(ConnectionStatus status)
    {
        IsConnected = status is ConnectionStatus.Connected or ConnectionStatus.Reconnecting;
        var newState = status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Reconnecting => "Reconnecting…",
            ConnectionStatus.Error => "Error",
            _ => "Not connected"
        };

        if (newState != _lastState)
        {
            _heartbeat?.Record(Config.Id, DisplayName, _lastState, newState);
            _lastState = newState;
        }

        StatusText = newState;
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

        // Feed the persistent cache for backtesting (batched by caller).
        _tickCache?.AddTicks(Config.Symbol, new[] { tick });
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
