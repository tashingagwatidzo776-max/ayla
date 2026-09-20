using CommunityToolkit.Mvvm.ComponentModel;
using DongGfx.App.Infrastructure;
using DongGfx.Core.Analytics;
using DongGfx.Core.Models;
using DongGfx.Deriv;

namespace DongGfx.App.Services;

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
    private volatile string _lastState = "Not connected";

    /// <summary>Test seam: the discovery/OTP client used for new-platform
    /// accounts. Production passes null → a real <see cref="NewPlatformAuth"/>
    /// is built from the account's registered app id. Tests inject a fake
    /// (including one that throws <see cref="DerivApiException"/> with
    /// "Unauthorized" to model an expired/revoked bearer token).</summary>
    private readonly NewPlatformAuth? _newPlatformOverride;

    // ── Circuit breaker ────────────────────────────────────────────
    private const int MaxConsecutiveFailures = 5;
    private const int CircuitOpenMinutes = 10;
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    // True while a ConnectAsync body (discovery → socket → history →
    // subscribe) is running for this account; guards against storm-driven
    // re-entry so the post-connect work never runs twice in parallel.
    private bool _connectInFlight;

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

    /// <summary>Deriv's own demo/real verdict for the authorized account:
    /// null = not verified yet (no authorize/balance response seen),
    /// true = the API says the account plays with virtual funds, false = the
    /// API confirmed a real-money account. The real-money gate refuses to
    /// start engines while this is null or true.</summary>
    [ObservableProperty]
    private bool? apiVerifiedVirtual;

    /// <summary>Human-readable form of <see cref="ApiVerifiedVirtual"/> for
    /// the runners grid — what the Deriv API says this account is, as opposed
    /// to what the config claims.</summary>
    [ObservableProperty]
    private string verifiedText = "unverified";

    /// <summary>New-platform accounts: the demo/real verdict taken from the
    /// discovery record at connect time (the OTP socket carries no
    /// is_virtual). Null on the classic platform or before discovery.</summary>
    [ObservableProperty]
    private bool? newPlatformVerifiedVirtual;

    /// <summary>Human-readable circuit breaker status.</summary>
    [ObservableProperty]
    private string circuitStatus = "";

    public AccountConnection(AccountConfig config, TickHistoryCache? tickCache = null, HeartbeatLog? heartbeat = null)
        : this(config, tickCache, heartbeat, newPlatformAuth: null)
    {
    }

    /// <summary>Test constructor: allows injecting the discovery/OTP client
    /// so auth-failure paths run without any network.</summary>
    public AccountConnection(
        AccountConfig config,
        TickHistoryCache? tickCache,
        HeartbeatLog? heartbeat,
        NewPlatformAuth? newPlatformAuth)
    {
        Config = config;
        _newPlatformOverride = newPlatformAuth;
        _client = config.NewPlatform
            ? new DerivClient
            {
                AppId = string.IsNullOrWhiteSpace(config.DerivAppId)
                    ? AppSettings.DefaultAppId
                    : config.DerivAppId.Trim(),
                NewPlatform = new NewPlatformAuth(
                    string.IsNullOrWhiteSpace(config.DerivAppId)
                        ? AppSettings.DefaultAppId
                        : config.DerivAppId.Trim()),
                NewPlatformToken = config.ApiToken,
                NewPlatformAccountId = config.DerivAccountId
            }
            : new DerivClient { AppId = AppSettings.DefaultAppId };
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

    /// <summary>Re-points the app's primary client (Dashboard, Trades and
    /// Brain surfaces) at this account: persists the account's connection
    /// data into settings (token encrypted as usual) and re-wires the
    /// primary DerivClient to this account's transport and id. The caller
    /// re-authenticates; real accounts stay locked behind the session
    /// unlock exactly as before — this switch only chooses WHICH account
    /// the manual surfaces address, never whether real money flows.</summary>
    public void ApplyToPrimarySettings(AppSettings settings)
    {
        settings.ApiToken = Config.ApiToken;
        settings.IsDemo = Config.IsDemo;
        settings.PrimaryNewPlatform = Config.NewPlatform;
        settings.PrimaryDerivAppId = Config.DerivAppId;
        settings.PrimaryDerivAccountId = Config.DerivAccountId;
        if (!string.IsNullOrWhiteSpace(Config.DerivAppId))
        {
            // The classic AppId stays a market-data fallback; for a PAT
            // primary the new-platform App ID is the one that matters.
            settings.AppId = Config.DerivAppId;
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

        // Reconnect storms often re-enter here (UI retries, hub auto-connect,
        // the client's own reconnect completion). The client tolerates it, but
        // the post-connect work below (discovery, history backfill, subscribe)
        // must not run twice in parallel for one account.
        if (_connectInFlight)
        {
            return;
        }
        _connectInFlight = true;

        IsBusy = true;
        LastError = "";
        StatusText = "Connecting…";
        try
        {
        // New platform: discovery is the demo/real verification — the
        // account list names the account's type authoritatively. Patch
        // it BEFORE connecting so the real-money gate never sees an
        // unverified account, and keep the id current.
        if (_client.NewPlatform is not null && _client.NewPlatformToken is not null)
        {
            var auth = _newPlatformOverride ?? _client.NewPlatform;
            var accounts = await auth.ListAccountsAsync(_client.NewPlatformToken)
                .ConfigureAwait(true);
                var match = accounts.FirstOrDefault(a =>
                    a.AccountId == _client.NewPlatformAccountId)
                    ?? accounts.FirstOrDefault(a => a.IsDemoAccount)
                    ?? accounts.FirstOrDefault();
                if (match is null)
                {
                    throw new InvalidOperationException(
                        "The new platform listed no accounts for this token.");
                }

                _client.NewPlatformAccountId = match.AccountId;
                NewPlatformVerifiedVirtual = match.IsDemoAccount;
                // The OTP socket carries no is_virtual: patch every balance
                // parse with the discovery verdict instead.
                _client.IsVirtualOverride = match.IsDemoAccount;
            }

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

            // Success — reset circuit breaker and any stale auth-failure flag.
            _consecutiveFailures = 0;
            IsDegraded = false;
            CircuitStatus = "";
            AuthFailure = false;

            StatusText = string.IsNullOrEmpty(LoginIdText)
                ? "Connected (not authorized)"
                : $"Connected · {LoginIdText}";
            RaiseStateChanged();
        }
        catch (DerivApiException ex) when (IsTerminalAuthFailure(ex))
        {
            // Expired/revoked bearer (e.g. a 1-hour OAuth token, or a PAT
            // revoked in Deriv): re-connecting CANNOT succeed until the
            // user supplies a new token, so retrying would just churn the
            // circuit breaker forever. Surface a specific, actionable state
            // and stop — the row shows exactly what to do.
            _consecutiveFailures = 0;
            IsDegraded = false;
            CircuitStatus = "";
            IsBusy = false;
            LastError = ex.Message;
            StatusText = "Token expired — sign in again (OAuth) or paste a fresh PAT";
            AuthFailure = true;
            RaiseStateChanged();
            return; // deliberate: no rethrow, no breaker, no auto-retry
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
            _connectInFlight = false;
        }
    }

    /// <summary>True after <see cref="ConnectAsync"/> hit a terminal auth
    /// failure (401-class): the stored token can no longer authenticate and
    /// re-connecting requires a new token (re-run OAuth sign-in or paste a
    /// fresh PAT). Cleared on disconnect and on the next successful connect.</summary>
    public bool AuthFailure
    {
        get => authFailure;
        private set
        {
            if (authFailure != value)
            {
                authFailure = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAuthFailure));
            }
        }
    }

    private bool authFailure;

    /// <summary>XAML visibility binding helper for <see cref="AuthFailure"/>.</summary>
    public bool HasAuthFailure => AuthFailure;

    /// <summary>Classifies a discovery/OTP failure as terminal (token can
    /// never work again) vs transient (retry makes sense). Internal static
    /// so tests exercise it directly.</summary>
    internal static bool IsTerminalAuthFailure(DerivApiException ex)
    {
        // Unauthorized = the new platform rejected the bearer outright.
        // DerivApiException prefixes the Message with "[code] ", so match the
        // embedded status ("HTTP 401 …") anywhere — it covers OTP and any
        // other 401 raise site.
        return ex.Code == "Unauthorized"
            || ex.Message.Contains("HTTP 401 ", StringComparison.Ordinal);
    }

    public async Task DisconnectAsync()
    {
        await _client.DisconnectAsync();
        StatusText = "Not connected";
        IsConnected = false;
        BalanceText = Config.IsDemo ? "demo" : "REAL";
        ApiVerifiedVirtual = null;
        VerifiedText = "unverified";
        NewPlatformVerifiedVirtual = null;
        AuthFailure = false;
        _client.IsVirtualOverride = null;
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

    /// <summary>Test seam: raises <see cref="StateChanged"/> as if a
    /// connect/balance event had landed.</summary>
    internal void RaiseStateChangedTest() => RaiseStateChanged();

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
        ApiVerifiedVirtual = balance.IsVirtual;
        VerifiedText = balance.IsVirtual ? "demo (API)" : "REAL (API)";
        // New platform: the authorize-less OTP socket carries no is_virtual,
        // so the authoritative verdict comes from the discovery record taken
        // at connect time. Unknown still parses as virtual (fail closed) and
        // the discovery patch below corrects it when the type is known.
        if (NewPlatformVerifiedVirtual is not null)
        {
            ApiVerifiedVirtual = NewPlatformVerifiedVirtual;
            VerifiedText = NewPlatformVerifiedVirtual.Value ? "demo (new platform)" : "REAL (new platform)";
        }
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
