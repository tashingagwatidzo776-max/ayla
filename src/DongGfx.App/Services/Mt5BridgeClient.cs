commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/src/DongGfx.App/Services/Mt5BridgeClient.cs b/src/DongGfx.App/Services/Mt5BridgeClient.cs
index 75b76e2..a545f17 100644
--- a/src/DongGfx.App/Services/Mt5BridgeClient.cs
+++ b/src/DongGfx.App/Services/Mt5BridgeClient.cs
@@ -4,6 +4,11 @@ using System.Text.Json;
 
 namespace DongGfx.App.Services;
 
+/// <summary>One tradable symbol with a live quote (bridge /symbols).</summary>
+public sealed record Mt5Symbol(
+    string Symbol, string Description, double? Bid, double? Ask,
+    int SpreadPoints, int Digits, int TradeMode);
+
 /// <summary>One open MT5 position (bridge /positions).</summary>
 public sealed record Mt5Position(
     long Ticket, string Symbol, string Side, double Volume,
@@ -107,6 +112,29 @@ public sealed class Mt5BridgeClient : IDisposable
     }
 
     /// <summary>Live bid/ask for a symbol, or null when unavailable.</summary>
+    public async Task<IReadOnlyList<Mt5Symbol>> GetSymbolsAsync(CancellationToken ct = default)
+    {
+        using var doc = await GetJson("symbols", ct).ConfigureAwait(false);
+        if (doc is null || !doc.RootElement.TryGetProperty("symbols", out var arr) || arr.ValueKind != JsonValueKind.Array)
+        {
+            return Array.Empty<Mt5Symbol>();
+        }
+
+        var list = new List<Mt5Symbol>();
+        foreach (var e in arr.EnumerateArray())
+        {
+            list.Add(new Mt5Symbol(
+                e.GetProperty("symbol").GetString() ?? "",
+                e.TryGetProperty("description", out var d) ? d.GetString() : null,
+                e.TryGetProperty("bid", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : null,
+                e.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null,
+                e.TryGetProperty("spread_points", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0,
+                e.TryGetProperty("digits", out var dg) && dg.ValueKind == JsonValueKind.Number ? dg.GetInt32() : 5,
+                e.TryGetProperty("trade_mode", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : 0));
+        }
+        return list;
+    }
+
     public async Task<(double Bid, double Ask, long Time)?> GetTickAsync(string symbol, CancellationToken ct = default)
     {
         using var doc = await GetJson($"ticks/{Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
