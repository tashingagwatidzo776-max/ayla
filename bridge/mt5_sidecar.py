commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/bridge/mt5_sidecar.py b/bridge/mt5_sidecar.py
index 342078f..e89b739 100644
--- a/bridge/mt5_sidecar.py
+++ b/bridge/mt5_sidecar.py
@@ -129,6 +129,26 @@ class BridgeHandlers:
             raise OrderError(f"symbol {symbol} not available on this account")
         return info
 
+    def symbols(self) -> dict:
+        """Tradable catalog with live quotes: symbol_select every visible
+        symbol once, then snapshot bid/ask/spread/digits + trade mode. The
+        Terminal's Market Watch is fed from this (MT5-native, not Deriv)."""
+        out = []
+        for info in (self._m.symbols_get() or []):
+            if not getattr(info, "visible", False):
+                continue
+            tick = self._m.symbol_info_tick(info.name)
+            out.append({
+                "symbol": info.name,
+                "description": info.description,
+                "bid": tick.bid if tick else None,
+                "ask": tick.ask if tick else None,
+                "spread_points": info.spread,
+                "digits": info.digits,
+                "trade_mode": int(info.trade_mode),
+            })
+        return {"symbols": out}
+
     def ticks(self, symbol: str) -> dict:
         self._symbol_or_404(symbol)
         tick = self._m.symbol_info_tick(symbol)
@@ -355,6 +375,8 @@ class SidecarServer:
                         self._send(200, h.health())
                     elif path == "/account":
                         self._send(200, h.account())
+                    elif path == "/symbols":
+                        self._send(200, h.symbols())
                     elif path.startswith("/ticks/"):
                         self._send(200, h.ticks(path.split("/", 2)[2]))
                     elif path.startswith("/book/"):
