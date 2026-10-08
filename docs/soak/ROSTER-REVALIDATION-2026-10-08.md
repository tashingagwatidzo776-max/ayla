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

| Week | Filtered − full delta | Note |
|---|---|---|
| 2026-10-07 (sweep A/B) | GBPUSD **−267.8R** (roster-1 −89.3R vs full +178.5R) | mode-1 cumulative-cell filter; first negative week |
| 2026-10-08 (this week) | **0 on every symbol** | mode-1 off (rule 1); live recent-tape gate excluded nothing |

The rule requires *two consecutive* negative weeks. Week 2 is 0, not
negative → **trigger not met, streak reset.** Rule 1 stands: mode-1 remains
research-only; the full roster is live; the recent-tape gate (policy items
2–4) is the only active filter and it has not yet cut anything.

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

- **Cell source stale? No** in the §5 sense: no two-week negative
  filtered−full streak, and the live gate has cut nothing.
- **mode-1 stays off** (rule 1). Full roster live; confidence gate +
  memory weight-tilt continue to down-weight gradually.
- **Recent-tape gate is the live implementation of items 2–4** and is
  behaving per policy: 0 exclusions, default-keep, XAU/vol-breakout
  watched at n=20/−0.08R.
- **Open follow-up** (unchanged from 10-07): port recent-tape expectancy
  cells into FxTrain mode-1 so the offline sweep A/B stops reading
  lifetime cells; until then §5's sweep re-run remains N/A and this
  journal-cell re-validation is the weekly check.

*Next re-validation: 2026-10-15.*
