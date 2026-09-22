commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.App/App.xaml.cs b/src/DongGfx.App/App.xaml.cs
index bdfdf1a..716533b 100644
--- a/src/DongGfx.App/App.xaml.cs
+++ b/src/DongGfx.App/App.xaml.cs
@@ -178,6 +178,7 @@ public partial class App : System.Windows.Application
                 sp.GetRequiredService<StrategyOptimizer>(),
                 sp.GetRequiredService<TickHistoryCache>()));
         services.AddSingleton<HealthViewModel>();
+        services.AddSingleton<TickArchive>();
         services.AddSingleton(sp =>
             new TerminalViewModel(
                 () => sp.GetRequiredService<DerivClient>(),
@@ -188,7 +189,21 @@ public partial class App : System.Windows.Application
                 isRealMoneyUnlocked: () => sp.GetRequiredService<ManualRealMoneyGate>().IsUnlocked,
                 dashboard: sp.GetRequiredService<DashboardViewModel>(),
                 setAutonomyBound: v => sp.GetRequiredService<SettingsViewModel>().AutonomyEnabled = v,
-                setSymbolBound: s => sp.GetRequiredService<SettingsViewModel>().Symbol = s));
+                setSymbolBound: s => sp.GetRequiredService<SettingsViewModel>().Symbol = s,
+                tickArchive: sp.GetRequiredService<TickArchive>(),
+                accounts: () => sp.GetRequiredService<AccountsViewModel>(),
+                fxHostFactory: () =>
+                {
+                    var settings = sp.GetRequiredService<Func<AppSettings>>()();
+                    return new FxEngineHost(
+                        sp.GetRequiredService<Mt5BridgeClient>(),
+                        sp.GetRequiredService<TradeJournal>(),
+                        settings.FxSymbol,
+                        () => sp.GetRequiredService<DashboardViewModel>().IsKillSwitchEngaged,
+                        () => sp.GetRequiredService<Func<AppSettings>>()().Mt5MaxLots,
+                        () => sp.GetRequiredService<MultiAccountHub>().Accounts.Any(a => !a.Config.IsDemo && sp.GetRequiredService<MultiAccountHub>().IsRealMoneyUnlocked(a.Config.Id))
+                              || sp.GetRequiredService<ManualRealMoneyGate>().IsUnlocked);
+                }));
         services.AddSingleton(sp => new MainViewModel(
             sp.GetRequiredService<SettingsService>(),
             sp.GetRequiredService<DerivClient>(),
