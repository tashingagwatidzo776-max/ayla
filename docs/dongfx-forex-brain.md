commit 096264a31b8620202a52e6a64b541389a93c3bbc
Author: Tf Developer <dev@tf.local>
Date:   Tue Sep 22 02:08:00 2026 -0700

    FX brain foundation: MT5-fed terminal data, Terminal sign-in, features/regime/alphas, paper-first FxEngine

diff --git a/docs/dongfx-forex-brain.md b/docs/dongfx-forex-brain.md
new file mode 100644
index 0000000..98f2d54
--- /dev/null
+++ b/docs/dongfx-forex-brain.md
@@ -0,0 +1,68 @@
+# DON G FX — forex brain
+
+DON G FX's own forex brain: MT5 market data in, features out, a regime layer
+routing between algorithm families, volatility-normalized sizing, the app's
+existing risk rails, and MT5 execution — all journaled as `FX_*` entries.
+
+```
+MT5 data (ticks, bars, book) → features → regime detector → forex alpha family
+    → volatility-normalized sizing → DON G FX rails (gate, governor, kill switch, max-lots)
+    → MT5 execution → journal (FX_*) + webhook digest
+```
+
+## Non-negotiable guardrails
+
+1. **Walk-forward out-of-sample proof first** — no alpha family trades live
+   until it survives walk-forward OOS evaluation on archived data. In-sample
+   beauty means nothing; this is the genetic/ML trap and the harness exists
+   to prevent it.
+2. **Cost-adjusted order-book proof** — microstructure/order-book alphas must
+   clear spread + slippage out-of-sample before they can place orders.
+3. **The regime layer can veto everything** — TREND routes to momentum,
+   RANGE to mean-reversion, HIGH_VOL/NEWS/LIQUIDITY → stand down. It runs
+   upstream of every alpha.
+4. **Every order passes the existing rails** — APEX-class brains are not a
+   new trade path: real-money gate, governor, kill switch, `Mt5MaxLots`
+   all apply (audit doc carries the MT5 path).
+
+## The 14 algorithm families (build-out order)
+
+| # | Family | Status |
+|---|--------|--------|
+| 1 | Features (EMA/RSI/ATR/ADX/BB/Donchian/Z/ROC/vol) | ✅ `FxFeatures` |
+| 2 | Regime layer (trend/range/vol/session/spread + Markov + change-point) | ✅ rule-based; Markov/change-point planned |
+| 3 | Momentum (EMA cross, Donchian, ROC, MTF, Hurst, vol-breakout) | ✅ `FxAlphas` (momentum set) |
+| 4 | Mean-reversion (Z, BB, VWAP, MA-dev, OU, Kalman) | ✅ `FxAlphas` (reversion set) |
+| 5 | Tick/microstructure (order-flow imbalance, VPIN, signed volume) | needs tick archive (built) to accumulate |
+| 6 | Statistical arbitrage (cointegration/pairs, basket) | needs multi-symbol streams |
+| 7 | Order-book alphas (queue, book-pressure, iceberg, sweep) | needs real depth from the bridge |
+| 8 | Bayesian/Kalman (particle filters, state-space, regime posterior) | planned |
+| 9 | ML (gradient boosting/RF/online) | needs feature store |
+| 10 | Genetic optimization + walk-forward harness | planned (Phase 2 gate) |
+| 11 | Deep learning (LSTM/TCN/transformer) | last; ONNX in the sidecar |
+| 12 | Execution algos (TWAP/VWAP/iceberg/sniper) | planned |
+| 13 | Market-making (spread capture, inventory skew) | needs depth + rebates |
+| 14 | FX arbitrage (triangular, latency, cross-venue) | scaffold planned |
+
+## Modes
+
+- **Paper (default)** — every cycle journals `FX_REGIME` → `FX_SIGNAL` →
+  `FX_DECISION (PAPER)`; zero orders. A soak window of clean paper cycles is
+  the prerequisite for going live.
+- **Live (explicit)** — `GoLive()` is a journaled act; orders flow through
+  the bridge ticket path with rails, positions tracked, `MT5_ORDER` +
+  `FX_FILL` journal entries.
+
+## Cycle layout (one continuous build, no staged releases)
+
+- Foundation: sidecar data endpoints (`/symbols` shipped; `/ticks /book /candles`
+  existed), MT5-fed Terminal (Market Watch, chart, DOM), Terminal sign-in,
+  low-CPU architecture (event-driven, coalesced, <2% CPU / <150 MB targets)
+- Brain core: families 1–4 above, paper mode first, Terminal UI section
+- Trading starts early: first four brain pieces → one evening paper soak →
+  the brain trades the demo MT5 account by itself
+- Safety: MT5 P/L in the governor, kill-switch flattens MT5, drawdown stop,
+  session blackouts, guards + audit doc updated
+- Reliability: sidecar auto-start at login + watchdog, position recovery,
+  webhook digest for FX decisions, R_100 failure loop root-caused
+- Packaging: docs, v0.0.17 through the release chain, stamp verified
