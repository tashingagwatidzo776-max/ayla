using DongGfx.App;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;
using DongGfx.Core.Update;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The app's real startup wiring: builds the actual DI graph via
/// App.ConfigureServices and resolves EVERY service the runtime depends on
/// — a registration typo, a missing dependency, or a constructor throw now
/// fails here instead of crashing at launch. Also runs the real settings
/// configuration pass (webhook wiring, digest cadence + state providers)
/// and asserts the FxStateProvider reflects a running portfolio.
/// Nothing is started: no timers, no feeds, no update probes.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "RealMoney")]
// Serialized against the other DataDir-touching suites (see
// SharedDataDirCollection.cs): this graph's constructors create directories
// under the real %APPDATA% data dir, and a parallel teardown deleting that
// dir used to throw DirectoryNotFoundException out of service resolution.
[Collection("Shared-DataDir-Directory")]
public class AppStartupWiringTests
{
    internal static ServiceProvider BuildGraph(AppSettings? settings = null)
    {
        settings ??= new AppSettings
        {
            IsDemo = true,
            MetricsDigestEnabled = true,
            MetricsDigestIntervalHours = 6,
            FxSymbols = "XAUUSDmicro, EURUSD",
            FxSymbol = "XAUUSDmicro",
        };
        var provider = App.ConfigureServices(new ServiceCollection()).BuildServiceProvider();
        // Production loads persisted settings into the SettingsViewModel
        // before anything reads BuildSettings() — mirror that here.
        provider.GetRequiredService<SettingsViewModel>().Load(settings);
        App.ConfigureFromSettings(provider, settings);
        return provider;
    }

    [Theory]
    [InlineData(typeof(SettingsService))]
    [InlineData(typeof(TradeJournal))]
    [InlineData(typeof(PerformanceTracker))]
    [InlineData(typeof(AppLogger))]
    [InlineData(typeof(NotificationService))]
    [InlineData(typeof(WebhookService))]
    [InlineData(typeof(ManualRealMoneyGate))]
    [InlineData(typeof(UnlockStalenessMonitor))]
    [InlineData(typeof(Mt5BridgeClient))]
    [InlineData(typeof(TickArchive))]
    [InlineData(typeof(MetricsCollector))]
    [InlineData(typeof(FxTradeFeed))]
    [InlineData(typeof(FxScorecardService))]
    [InlineData(typeof(DashboardViewModel))]
    [InlineData(typeof(SettingsViewModel))]
    [InlineData(typeof(Func<AppSettings>))]
    [InlineData(typeof(JournalViewModel))]
    [InlineData(typeof(AutoUpdater))]
    [InlineData(typeof(UpdateViewModel))]
    [InlineData(typeof(PerformanceViewModel))]
    [InlineData(typeof(MetricsDigestService))]
    [InlineData(typeof(TerminalViewModel))]
    [InlineData(typeof(MainViewModel))]
    public void Every_Runtime_Service_Resolves_From_The_Real_Graph(Type serviceType)
    {
        using var provider = BuildGraph();
        var instance = provider.GetRequiredService(serviceType);
        Assert.NotNull(instance);
    }

    [Fact]
    public void Singleton_Contract_Holds_Across_Resolutions()
    {
        using var provider = BuildGraph();
        var journal1 = provider.GetRequiredService<TradeJournal>();
        var journal2 = provider.GetRequiredService<TradeJournal>();
        Assert.Same(journal1, journal2);

        var terminal1 = provider.GetRequiredService<TerminalViewModel>();
        var terminal2 = provider.GetRequiredService<TerminalViewModel>();
        Assert.Same(terminal1, terminal2);
    }

    [Fact]
    public void Settings_Pass_Wires_Webhook_And_Digest_Cadence()
    {
        var settings = new AppSettings { IsDemo = true, MetricsDigestEnabled = true, MetricsDigestIntervalHours = 6 };
        using var provider = BuildGraph(settings);

        var webhook = provider.GetRequiredService<WebhookService>();
        var digest = provider.GetRequiredService<MetricsDigestService>();

        // Empty webhook URL in these settings → the endpoint stays unset;
        // the digest cadence and the enabled toggle always apply.
        Assert.True(string.IsNullOrEmpty(webhook.WebhookUrl));
        Assert.Equal(TimeSpan.FromHours(6), digest.Interval);
        Assert.False(digest.Disabled);
        Assert.NotNull(digest.SafetyAuditPath);   // found in this checkout
        Assert.NotNull(digest.UnlockStateProvider);
        Assert.Null(digest.UnlockStateProvider.Invoke());   // gate not armed

        // Arming the shared gate flips the digest's unlock line.
        var gate = provider.GetRequiredService<ManualRealMoneyGate>();
        try
        {
            gate.Arm();
            Assert.Contains("ARMED", digest.UnlockStateProvider.Invoke());
        }
        finally
        {
            gate.Reset();
        }
    }

    [Fact]
    public void FxStateProvider_Reflects_A_Running_Portfolio()
    {
        using var provider = BuildGraph();
        var digest = provider.GetRequiredService<MetricsDigestService>();
        Assert.NotNull(digest.FxStateProvider);

        // No host running yet → no FX segment.
        Assert.Null(digest.FxStateProvider.Invoke());

        // Start the brain through the same factory the runtime uses.
        var terminal = provider.GetRequiredService<TerminalViewModel>();
        terminal.ToggleFxBrainCommand.Execute(null);
        try
        {
            Assert.NotNull(terminal.FxHost);
            var line = digest.FxStateProvider.Invoke();
            Assert.NotNull(line);
            Assert.Contains("PAPER", line);
            Assert.Contains("XAUUSDmicro", line);

            // The scorecard hasn't run yet — no summary segment.
            Assert.DoesNotContain("scorecard", line);
        }
        finally
        {
            terminal.FxHost!.Stop();
        }
    }

    [Fact]
    public void Terminal_FxHost_Factory_Produces_A_Correctly_Wired_Host()
    {
        using var provider = BuildGraph();
        var terminal = provider.GetRequiredService<TerminalViewModel>();

        terminal.ToggleFxBrainCommand.Execute(null);
        try
        {
            var host = Assert.IsType<FxPortfolioHost>(terminal.FxHost);
            Assert.Equal(new[] { "XAUUSDmicro", "EURUSD" }, host.Symbols);
            Assert.False(host.IsLiveEngine);   // paper until the operator goes live
            Assert.True(host.IsRunning);
        }
        finally
        {
            terminal.FxHost!.Stop();
        }
    }
}
