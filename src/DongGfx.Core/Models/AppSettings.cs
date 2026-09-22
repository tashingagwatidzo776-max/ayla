commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.Core/Models/AppSettings.cs b/src/DongGfx.Core/Models/AppSettings.cs
index e7777f7..605b2b7 100644
--- a/src/DongGfx.Core/Models/AppSettings.cs
+++ b/src/DongGfx.Core/Models/AppSettings.cs
@@ -73,6 +73,10 @@ public sealed class AppSettings
     /// the bridge. 0 disables MT5 order placement entirely (fail-closed).</summary>
     public decimal Mt5MaxLots { get; set; } = 1.00m;
 
+    /// <summary>The MT5 symbol the forex brain trades (bridge catalog name,
+    /// not a Deriv symbol).</summary>
+    public string FxSymbol { get; set; } = "XAUUSD";
+
     /// <summary>One-click session start: when set, launching the app
     /// auto-connects the demo row, arms the brain, selects R_100 and starts
     /// the engines. Demo automation only — real accounts still need the
