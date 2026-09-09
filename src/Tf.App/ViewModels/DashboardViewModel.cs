using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tf.App.Controls;
using Tf.App.Services;
using Tf.Core.Models;
using Tf.Deriv;

namespace Tf.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DerivClient _client;
    private readonly MultiAccountHub? _hub;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private string statusText = "Disconnected";

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string symbolText = "—";

    [ObservableProperty]
    private string brainStatus = "Idle — brain arrives in M3";

    [ObservableProperty]
    private string lastPriceText = "—";

    [ObservableProperty]
    private string tickCountText = "0 ticks";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isKillSwitchEngaged;

    /// <summary>The live chart view (attached by MainWindow; VM stays UI-agnostic).</summary>
    public TickChartControl? Chart { get; set; }

    public DashboardViewModel(DerivClient client, MultiAccountHub? hub = null)
    {
        _client = client;
        _hub = hub;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _client.StatusChanged += OnStatusChanged;
        _client.TickReceived += OnTickReceived;
        _client.BalanceUpdated += OnBalanceUpdated;
        _client.ErrorReceived += OnErrorReceived;
    }

    public void SetSymbol(string symbol) => OnUiThread(() => SymbolText = symbol);

    public void AddHistory(IReadOnlyList<Tick> ticks)
    {
        OnUiThread(() =>
        {
            Chart?.Clear();
            if (ticks.Count > 0)
            {
                Chart?.AddTicks(ticks);
                LastPriceText = ticks[^1].Quote.ToString("0.00000");
                TickCountText = $"{ticks.Count} ticks";
            }
        });
    }

    [RelayCommand]
    private void ToggleKillSwitch()
    {
        IsKillSwitchEngaged = !IsKillSwitchEngaged;
        if (IsKillSwitchEngaged)
        {
            StatusText = "KILL SWITCH ENGAGED — all feeds stopped";
            _ = _client.DisconnectAsync();

            // Halt every account connection and growth runner immediately.
            if (_hub is not null)
            {
                _ = _hub.DisconnectAllAsync();
            }
        }
        else
        {
            StatusText = "Disconnected — press Connect to resume";
        }
    }

    private void OnStatusChanged(ConnectionStatus status) => OnUiThread(() =>
    {
        if (IsKillSwitchEngaged)
        {
            return;
        }

        IsConnected = status is ConnectionStatus.Connected or ConnectionStatus.Reconnecting;
        StatusText = status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Reconnecting => "Reconnecting…",
            ConnectionStatus.Error => "Error",
            _ => "Disconnected"
        };
    });

    private int _tickCount;

    private void OnTickReceived(Tick tick) => OnUiThread(() =>
    {
        LastPriceText = tick.Quote.ToString("0.00000");
        _tickCount++;
        TickCountText = $"{_tickCount} ticks";
        Chart?.AddTicks(new[] { tick });
    });

    private void OnBalanceUpdated(AccountBalance balance) => OnUiThread(() =>
    {
        BalanceText = $"{balance.Balance:0.##} {balance.Currency}";
        if (!string.IsNullOrEmpty(balance.LoginId))
        {
            SymbolText = SymbolText == "—" ? balance.LoginId : $"{SymbolText} · {balance.LoginId}";
        }
    });

    private void OnErrorReceived(string message) => OnUiThread(() =>
    {
        if (message.StartsWith("Connection lost"))
        {
            StatusText = message;
        }
    });

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}