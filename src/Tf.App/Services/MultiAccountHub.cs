using System.Collections.ObjectModel;
using Tf.App.Infrastructure;
using Tf.Core;
using Tf.Core.Analytics;
using Tf.Core.Brain;
using Tf.Core.Logging;
using Tf.Core.Models;

namespace Tf.App.Services;

/// <summary>
/// Owns every Deriv account connection plus its optional growth runner. One
/// <see cref="AccountConnection"/> per API token (Deriv authorizes one account
/// per WebSocket). The WPF layer binds to <see cref="Accounts"/>; the growth
/// brains are started/stopped here so the kill switch can halt everything.
/// </summary>
public sealed class MultiAccountHub
{
    private readonly AccountVault _vault;
    private readonly TradeStore _store;
    private readonly TradeJournal _journal;
    private readonly PerformanceTracker? _tracker;
    private readonly TickHistoryCache? _tickCache;
    private readonly HeartbeatLog? _heartbeat;
    private readonly NotificationService? _notifications;
    private readonly WebhookService? _webhook;
    private readonly Dictionary<Guid, GrowthRunner> _runners = new();
    private readonly object _runnerLock = new();

    public MultiAccountHub(AccountVault vault, TradeStore store, TradeJournal journal,
        PerformanceTracker? tracker = null, TickHistoryCache? tickCache = null,
        HeartbeatLog? heartbeat = null, NotificationService? notifications = null,
        WebhookService? webhook = null)
    {
        _vault = vault;
        _store = store;
        _journal = journal;
        _tracker = tracker;
        _tickCache = tickCache;
        _heartbeat = heartbeat;
        _notifications = notifications;
        _webhook = webhook;

        foreach (var config in vault.Load())
        {
            Accounts.Add(CreateConnection(config));
        }
    }

    /// <summary>All known accounts (bound directly by the UI).</summary>
    public ObservableCollection<AccountConnection> Accounts { get; } = new();

    /// <summary>Raised on every growth runner activity line (background thread).</summary>
    public event Action<GrowthRunner, string>? GrowthActivity;

    /// <summary>Raised after an account is added or removed.</summary>
    public event Action? AccountsChanged;

    public IReadOnlyDictionary<Guid, GrowthRunner> Runners => _runners;

    public AccountConnection AddAccount(AccountConfig config)
    {
        var existing = Accounts.FirstOrDefault(a =>
            string.Equals(a.Config.ApiToken, config.ApiToken, StringComparison.Ordinal));
        if (existing is not null)
        {
            throw new InvalidOperationException("That API token is already in the account list.");
        }

        var connection = CreateConnection(config);
        Accounts.Add(connection);
        Save();
        AccountsChanged?.Invoke();
        return connection;
    }

    public async Task RemoveAccountAsync(AccountConnection connection)
    {
        if (connection is null)
        {
            return;
        }

        GrowthRunner? runner;
        lock (_runnerLock)
        {
            _runners.TryGetValue(connection.Config.Id, out runner);
            if (runner is not null)
            {
                runner.Connection.StateChanged -= OnConnectionStateChanged;
                _runners.Remove(connection.Config.Id);
            }
        }

        if (runner is not null)
        {
            await runner.DisposeAsync();
        }

        Accounts.Remove(connection);
        await connection.DisposeAsync();
        Save();
        AccountsChanged?.Invoke();
    }

    public async Task ConnectAllAsync()
    {
        foreach (var account in Accounts.ToArray())
        {
            if (!account.IsConnected && !account.IsBusy)
            {
                try
                {
                    await account.ConnectAsync();
                }
                catch
                {
                    // Row surfaces the error; keep connecting the others.
                }
            }
        }
    }

    public async Task DisconnectAllAsync()
    {
        StopAllRunners();

        foreach (var account in Accounts.ToArray())
        {
            await account.DisconnectAsync();
        }
    }

    /// <summary>
    /// Starts the deterministic growth brain on one connected demo account.
    /// Returns the runner, or null when the account cannot run.
    /// </summary>
    public GrowthRunner? StartGrowth(AccountConnection connection, GrowthPlan plan,
        Func<AppSettings> settings, Func<bool> killSwitch)
    {
        if (connection is null || !connection.IsConnected || !connection.Config.IsDemo)
        {
            return null;
        }

        lock (_runnerLock)
        {
            if (_runners.TryGetValue(connection.Config.Id, out var running))
            {
                return running;
            }

            var runner = new GrowthRunner(connection, _store, settings, killSwitch, _journal, _tracker, _notifications, _webhook);
            runner.Activity += line => GrowthActivity?.Invoke(runner, line);
            runner.Connection.StateChanged += OnConnectionStateChanged;

            _runners[connection.Config.Id] = runner;
            _ = runner.StartAsync(plan);
            return runner;
        }
    }

    public Task StopGrowthAllAsync()
    {
        StopAllRunners();
        return Task.CompletedTask;
    }

    private void StopAllRunners()
    {
        GrowthRunner[] runners;
        lock (_runnerLock)
        {
            runners = _runners.Values.ToArray();
            _runners.Clear();
        }

        foreach (var runner in runners)
        {
            runner.Connection.StateChanged -= OnConnectionStateChanged;
            runner.Stop();
        }
    }

    public void StopGrowth(Guid accountId)
    {
        GrowthRunner? runner;
        lock (_runnerLock)
        {
            _runners.Remove(accountId, out runner);
        }
        runner?.Stop();
    }

    private AccountConnection CreateConnection(AccountConfig config) =>
        new(config, _tickCache, _heartbeat);

    private void OnConnectionStateChanged(AccountConnection connection)
    {
        // A lost connection ends its runner (the scheduler cannot trade anyway).
        GrowthRunner? runner;
        lock (_runnerLock)
        {
            if (!connection.IsConnected &&
                _runners.TryGetValue(connection.Config.Id, out var r))
            {
                _runners.Remove(connection.Config.Id);
                runner = r;
            }
            else
            {
                runner = null;
            }
        }

        if (runner is not null)
        {
            runner.Connection.StateChanged -= OnConnectionStateChanged;
            runner.Stop();
            runner.LastActivity = "Stopped — account disconnected.";
        }
    }

    private void Save()
    {
        try
        {
            _vault.Save(Accounts.Select(a => a.Config).ToArray());
        }
        catch
        {
            // Persistence is best-effort; the in-memory list still works.
        }
    }
}
