# Profit-floor rebalance — score-0 ensemble diagnosis, backtest, and gate

2026-09-29. The settled-trade review exposed that the exit ensemble had
never once exited a trade on its own (14/14 decisive exits were drawdown
MAE overrides, 0W/14L, profit capture 0% of 52.95R MFE). This note records
the diagnosis, the data-justified fix, and the gate verdicts.

## Diagnosis: the ensemble is structurally blind on the profit side

Across 3,160 FX_EXIT evaluations, the best weighted vote on a healthy
+7R trade was 0.4 (thesis) → score 6.7/100. Every active engine measures
loss-side risk: structure/momentum/volatility/time all read a trade giving
back a large peak as "healthy", drawdown measures only adverse excursion
(MAE from entry), and the resolver bands (hold <35) sit far above anything
profit-side evidence could produce. Result: exits happen only via the MAE
emergency — after the profit is gone.

## Backtest: a from-peak giveback rule on the journal's full history

Replaying every ticket's (MfeR, ProfitR) trajectory (39 tickets) through a
round-trip rule — *peaked ≥1R and returned to ≤0.2R, or peaked ≥2R and
returned ≥75% of the peak* — fires on **6 closed trades and would have
saved +20.25R**, the largest being #9820177381 (+26.3R peak → −2.47R
actual). The rule stays silent on healthy winners (e.g. 16.9R → 14.8R,
12% giveback): it discriminates give-back from being-in-profit.

## The fix (weights unchanged → fingerprint intact)

1. **Drawdown voice gains the giveback curve** (same 2.0 weight): 0.95 on
   a round-trip, 0.85 on ≥75% give-back of a ≥2R peak, 0.65 on ≥60% of a
   ≥1.5R peak. The ensemble finally has profit-side evidence between the
   watch bar and the emergency.
2. **`profit-floor` override** (the mirror of the MAE emergency): round-trip
   or ≥75% give-back of a ≥2R peak → full exit, safety tier. Validated
   against the history: 6 saves, +10.97R on the strict override bar alone,
   silent on every non-round-trip.
3. **Restart-state reseed**: MFE/MAE/floor memory now re-seeds from the
   journal's last FX_PROFIT row on first sighting — restarts previously
   wiped 16.9R peaks (violating the never-down floor law and re-arming
   the override on invisible peaks).

## Gates

- Suites: Core 278/278 (4 new profit-floor laws), App 514/514 (reseed test).
- Monte-Carlo drill (McDrill, 39 settled evaluations × 2000 trials, ±20%
  weight noise): baseline 5.10% flips, rebalanced 5.10% — **STABLE,
  identical**, confirming the change perturbs no weight (the fingerprint
  gate stays green by construction; the new behavior is evidence inside
  one engine's vote curve).

## Standing caveat

Restart reseed reads the journal, not the venue: a restart mid-gap can
still under-see a peak formed while the app was down. The floor ratchet
(journal-backed) bounds the damage; the shadow-ledger grading continues to
accumulate the evidence that decides whether this rule earns permanent
promotion through the normal bar (100 trades @ 60% + MC).
