# ROSTER-POLICY §5 — weekly re-validation (2026-10-08)

**Method:** recent-tape cells (new win-rate pipeline: `scripts/fx_win_rate.py`,
last 30 days of closes, tier-1 `RealizedR` / tier-2 snapshot outcomes, never
fabricated) compared against the cumulative memory cells
(`%APPDATA%/tf/data/fx-brain-memory.json`, last trained 2026-10-07 13:13 UTC,
558 runs, 321 cells). This is §5 read through policy items 2–4: the *recent*
per-(symbol, family) expectancy is the authority; cumulative cells may only
tilt, never cut.

**Headline:** 98/109 closes measured (coverage 90%), 38.8% win, avg +0.49R,
total +47.7R. 23 recent cells measured. **No cell is excluded** — the in-app
recent-tape gate is armed with **0 refusals** so far, so the live filtered
roster equals the full roster.

## §5 two-week negative-delta rule

Two metrics: the **live gate** (does the in-app recent-tape filter cut
anything?) and the **mode-1 sweep A/B** — the original §5 recipe, re-run
2026-10-08 with the recorded `roster-ab.sh` / `xau-ab.sh` on a refreshed
tape copy against the same live memory snapshot (last trained 2026-10-07
13:13 UTC). Isolation check: 0.000R moved outside GBPUSD/XAUUSDmicro.

| Run | Metric | GBPUSD filtered−full | XAUUSDmicro filtered−full |
|---|---|---|---|
| 2026-10-07 sweep (week-1) | mode-1 sweep A/B | **−267.8R** (+178.5 full vs −89.3 filtered) | +101.3R (+3065.7 vs +3167.0) |
| 2026-10-08 sweep (week-2) | mode-1 sweep A/B | **−267.8R** (+178.5 vs −89.3; net +$20.35 vs −$7.81) | +101.3R (+3065.7 vs +3167.0; net +$64,934 vs +$67,466) |
| 2026-10-08 live | in-app gate | 0 — 0 refusals, filter never engaged | 0 |

**Trigger: met on the sweep metric.** GBPUSD filtered−full is negative
for the second consecutive run → §5's mandate is **“fix the cells, don't
widen the cut.”** Two caveats, stated plainly: (1) the runs are one day
apart, not seven — with an unchanged memory snapshot the A/B is
deterministic and week-2 largely reproduces week-1 instead of offering an
independent sample; (2) live behaviour is untouched either way — mode-1
stays off (rule 1) and the live recent-tape gate has cut nothing. The
cell fix the mandate points at is the standing follow-up: port recent-
tape expectancy cells into FxTrain mode-1 so the offline filter stops
reading lifetime cells.

## Recent tape vs cumulative cells (all 23 measured cells)

Flags: `STALE-KEEP` = cumulative says keep but recent n≥10 is losing (the
mode-1 wrong-keep pattern) · `WRONG-CUT` = cumulative says cut but recent
n≥10 is winning (≥ +0.10R) · `AT/NEAR-BAR` = recent n≥10 and mean ≤ 0.

| symbol / family | n | avgR | totR | cum n | cum R | flag |
|---|---:|---:|---:|---:|---:|---|
| XAUUSDmicro / vol-breakout | 20 | −0.08 | −1.64 | 10282 | +754.3 | **STALE-KEEP** |
| XAUUSDmicro / kalman-trend | 24 | +1.22 | +29.17 | 79470 | +7842.4 | |
| EURUSD / ema-slope | 6 | −1.05 | −6.29 | 2365 | +30.1 | |
| XAUUSDmicro / hurst-trend | 6 | +1.03 | +6.16 | 91673 | +6095.5 | |
| XAGUSD / vol-breakout | 5 | −0.00 | −0.02 | 3642 | −50.8 | |
| XAUUSDmicro / ema-slope | 5 | +3.35 | +16.73 | 6974 | +1369.5 | |
| EURUSD / vol-breakout | 4 | +2.73 | +10.92 | 3489 | −318.9 | |
| GBPUSD / vol-breakout | 3 | −0.62 | −1.87 | 39308 | +531.8 | |
| USDJPY / ema-slope | 3 | −0.84 | −2.53 | 3783 | +34.8 | |
| USDJPY / vol-breakout | 3 | +0.23 | +0.70 | 10448 | −208.8 | |
| XAUUSDmicro / rsi2-rev | 3 | +0.09 | +0.27 | 208 | +72.2 | |
| XAGUSD / kalman-trend | 2 | −0.31 | −0.62 | 60683 | +4436.5 | |
| XAGUSD / ou-rev | 2 | +1.12 | +2.24 | 68 | +56.1 | |
| XAUUSDmicro / bb-rev | 2 | −0.08 | −0.17 | 300 | −64.6 | |
| XAUUSDmicro / ou-rev | 2 | +0.15 | +0.30 | 111 | −15.1 | |
| GBPUSD / donchian-pullback | 1 | −3.02 | −3.02 | 693 | −13.9 | |
| GBPUSD / ema-slope | 1 | −1.81 | −1.81 | 18528 | +325.7 | |
| GBPUSD / z-rev | 1 | −1.72 | −1.72 | 3663 | −113.3 | |
| USDJPY / kalman-trend | 1 | −0.90 | −0.90 | 75529 | +4536.6 | |
| XAGUSD / ema-slope | 1 | −0.79 | −0.79 | 5315 | +373.4 | |
| XAGUSD / hurst-trend | 1 | −0.21 | −0.21 | 97878 | +1155.1 | |
| XAGUSD / open-range | 1 | +2.51 | +2.51 | 32 | +51.9 | |
| XAUUSDmicro / donchian | 1 | +0.26 | +0.26 | 529 | +79.3 | |

## Findings

1. **One STALE-KEEP cell: XAUUSDmicro/vol-breakout.** Cumulative says keep
   (+754.3R lifetime) but the recent tape is negative over n=20 (−0.08R
   mean). This is precisely the divergence §5 exists to catch, and it is
   also the only cell approaching the live exclusion bar (n≥30, mean ≤
   −0.10R, 2 consecutive fails — currently n=20, mean −0.08, so **not yet
   excludable on any of the three conditions**). No action required by
   policy: default-keep until the bar is met.
2. **No WRONG-CUT cell reaches the n≥10 floor.** EURUSD/vol-breakout
   (cum −318.9R, recent +2.73R, n=4) and ou-rev (cum −115.6R family-wide,
   recent +0.63R, n=4) are directionally divergent but samples are far
   below any exclusion minimum — with default-keep they cannot be harmed.
3. **ema-slope is directionally healthy cross-symbol (AGREE: cum +6614.5R,
   recent +0.33R over n=16) but split underneath:** negative on EURUSD
   (−1.05R, n=6), USDJPY (−0.84R, n=3), GBPUSD (−1.81R, n=1), XAGUSD
   (−0.79R, n=1) and carried entirely by XAUUSDmicro (+3.35R, n=5). The
   10-07 A/B's "kept despite losing on GBP" pattern persists at small n;
   per-symbol recent cells (what the live gate reads) would surface it
   long before a cumulative-cell filter could.
4. **The 10-07 wrongly-excluded winners have cooled off:** bb-rev recent
   n=2 at −0.08R (was +19.3R on GBP in the A/B), vwap-trend has no closes
   in the window. Cutting on stale cumulative cells would have been a
   coin-flip both ways — confirming items 2–3 (recent evidence, minimum
   sample, asymmetric bar).
5. **Cumulative cells are dominated by cross-symbol aggregates** (roc
   +55749.8R, ema-cross +45212.8R, kalman-trend +25689.0R over tens of
   thousands of training trades). They answer "has this family ever worked
   anywhere" — still not "is it working here", which is the root cause
   recorded on 10-07 and unchanged.

## Verdict

- **Cell source stale? Yes for GBPUSD on the offline metric** — two
  consecutive negative sweep deltas trigger the fix-the-cells mandate;
  **not stale on the live metric**: the recent-tape gate has cut nothing
  and its cells read recent expectancy directly (1 stale-keep cell
  watched, XAU/vol-breakout, not yet excludable).
- **mode-1 stays off** (rule 1). Full roster live; confidence gate +
  memory weight-tilt continue to down-weight gradually.
- **Recent-tape gate is the live implementation of items 2–4** and is
  behaving per policy: 0 exclusions, default-keep, XAU/vol-breakout
  watched at n=20/−0.08R.
- **Sweep A/B re-run complete** (was the open item): the mode-1 sweep
  reproduces GBP's stale-cell verdict — §5's trigger is met, so porting
  recent-tape expectancy cells into FxTrain mode-1 is now the *mandated*
  fix, not an optional follow-up. Until it lands, mode-1 stays off (rule
  1) and this journal-cell re-validation remains the live weekly check.

*Next re-validation: 2026-10-15.*
