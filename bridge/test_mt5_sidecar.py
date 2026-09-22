commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/bridge/test_mt5_sidecar.py b/bridge/test_mt5_sidecar.py
index 7aad9f0..c8a8e4d 100644
--- a/bridge/test_mt5_sidecar.py
+++ b/bridge/test_mt5_sidecar.py
@@ -65,6 +65,16 @@ class FakeMT5:
             return None
         return SimpleNamespace(bid=4347.61, ask=4347.88, time=1790003496)
 
+    def symbols_get(self):
+        return [
+            SimpleNamespace(name="XAUUSDmicro", description="Gold micro",
+                            spread=27, digits=2, trade_mode=4, visible=True),
+            SimpleNamespace(name="EURUSD", description="Euro vs US Dollar",
+                            spread=10, digits=5, trade_mode=4, visible=True),
+            SimpleNamespace(name="HIDDEN", description="not shown",
+                            spread=0, digits=2, trade_mode=0, visible=False),
+        ]
+
     def market_book_add(self, symbol):
         return False  # Deriv streams no depth
 
@@ -123,6 +133,29 @@ def make_handlers() -> sidecar.BridgeHandlers:
 
 # ── validation (the money paths) ──────────────────────────────────────
 
+def test_symbols_lists_visible_with_quotes():
+    """Market Watch's source: only visible symbols, each with live quote
+    fields; hidden catalog entries never leak into the list."""
+    data = make_handlers().symbols()
+    rows = data["symbols"]
+    names = [r["symbol"] for r in rows]
+    assert names == ["XAUUSDmicro", "EURUSD"], names
+    gold = rows[0]
+    assert gold["bid"] == 4347.61 and gold["ask"] == 4347.88
+    assert gold["spread_points"] == 27 and gold["digits"] == 2
+    assert gold["trade_mode"] == 4
+
+
+def test_symbols_tolerates_missing_tick():
+    """A symbol with no tick yet (session closed) still lists with null
+    quote fields — the UI shows it greyed, not missing."""
+    h = make_handlers()
+    h._m.symbol_info_tick = lambda s: None if s == "EURUSD" else SimpleNamespace(bid=1.0, ask=1.1, time=1)
+    rows = h.symbols()["symbols"]
+    eurusd = next(r for r in rows if r["symbol"] == "EURUSD")
+    assert eurusd["bid"] is None and eurusd["ask"] is None
+
+
 def test_order_rejects_bad_action_type():
     h = make_handlers()
     for action, kind in [("buy", "weird"), ("sideways", "market"), ("", "")]:
