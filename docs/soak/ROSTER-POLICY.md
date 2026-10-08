# Per-Symbol Roster Policy (mode-1 A/B evidence → design)

**Date:** 2026-10-07 · **Evidence:** `C:\Users\DELL\AppData\Local\Temp\fx-sweep\`
(`mem-snapshot.json` vs `mem-gbp-roster1.json` / `mem-xau-roster1.json`, out-*.md) ·
**Question:** roster-1 (mode-1 roster filter) improved XAUUSDmicro but degraded GBPUSD — why, and what policy follows?

## What mode-1 actually reads

The mode-1 roster filter excludes a family from a symbol's roster when its
**cumulative cross-symbol memory cell** (lifetime R across all symbols in
`fx-brain-memory`) says the family is unprofitable. The filter therefore
answers *"has this family ever worked here?"* — not *"is it working on the
current tape?"*. Those two answers diverge whenever the recent regime
disagrees with the long-run history, and they diverged hard on GBP today.

## A/B results (same sweep, roster-1 vs full roster)

### XAUUSDmicro — roster-1 marginally BETTER

| Metric | Full roster | Roster-1 |
|---|---|---|
| Cell totals (sweep P&L) | $64,944 | $67,476 |
| Tape-learned total R | +3065.7R | +3167.0R |

Per-family (delta vs `mem-snapshot`, symbol-filtered):

- Nearly every family **kept and correct** — cumulative cells and the current
  tape agree on XAU.
- Correctly **excluded**: bb-rev (−9.8R full), vwap-rev (−55.5R full).
- Wrongly excluded: ou-rev (+0.2R — negligible).
- Roster-1 **gains**: hurst +22R, kalman +13R (weight tilt into survivors).

### GBPUSD — roster-1 clearly WORSE

| Metric | Full roster | Roster-1 |
|---|---|---|
| Tape-learned total R | +178.5R | **−89.3R** |

- **Excluded correctly** (family really lost on GBP): macd, open-range,
  donchian, ou-rev, z-rev, bb-squeeze, asia-break, range-drift.
- **Excluded wrongly** (dropped current-tape *winners*): bb-rev +19.3R,
  vwap-trend +9.8R, donchian-pullback +2.1R, band-fade +1.7R, keltner +1.6R.
- **Kept despite losing now** (cumulative cells still green): ema-slope
  −37.5R / −57.8R, plus mixed vol-breakout, hurst, roc, rsi2-rev.
- Biggest single casualty: **ema-cross +158.5R full vs +48.5R roster-1**
  (−110R of current-tape edge filtered away).

## Root cause

**Cumulative-cell divergence.** On XAU the lifetime memory and the live tape
point the same way, so the filter agrees with reality. On GBP the lifetime
memory still credits families that have *stopped* working (ema-slope) and
still penalizes families that have *started* working (bb-rev, vwap-trend) —
the filter is reading last month's tape. A hard roster cut amplifies every
misread: exclusion is binary, so one stale cell removes 100% of a family's
current-tape edge (−110R on ema-cross alone).

## Policy (design)

1. **Full roster live; mode-1 stays a research/offline view.** The live
   portfolio keeps every family available. Down-weight, don't delete:
   the confidence gate (0.6) and the existing memory weight-tilt already
   suppress weak families gradually, and they self-correct when the tape
   turns.
2. **If a filter is enabled, gate on RECENT per-symbol tape expectancy, not
   cumulative cells.** Rolling window over the last N settled trades per
   (symbol, family); require a minimum sample (n ≥ ~30) before any
   exclusion, and default-keep when n is short. Stale cumulative evidence
   may only *tilt weights*, never cut the roster.
3. **Asymmetric exclusion bar.** Exclude only when recent expectancy is
   clearly negative *and* n is sufficient (protects against dropping
   +110R-type winners on one bad cell); a merely mediocre family stays with
   a reduced weight.
4. **Hysteresis.** A family must fail the recent-tape bar for two
   consecutive evaluations before exclusion, and re-enter on one passing
   window — cuts flip-flopping between sweeps.
5. **Weekly re-validation.** Re-run the sweep A/B (`fx-sweep` recipe:
   cmap by (symbol, alpha[, regime]), delta vs `mem-snapshot`, excluded
   family = learned delta 0) and compare full vs filtered per symbol.
   If filtered-full delta on any symbol goes negative for two weeks, the
   filter's cell source is stale again — fix the cells, don't widen the cut.

### Why not "just exclude per symbol" today

The evidence shows per-family direction differs *within* the same symbol
(GBP: 8 correct exclusions + 5 wrong ones), so any symbol-level switch
(cut/don't-cut GBP) inherits both the wins and the losses. The information
that decides each family is its **recent** per-symbol expectancy — which
policy item 2 reads directly instead of inferring it from lifetime cells.

## Status

Evidence: **complete** (both symbols, A/B, per-family deltas). Policy above
is the design; **implementation landed 2026-10-08**: FxTrain mode 1 is now
the recent-tape roster (full roster minus the in-app gate's exclusions,
reseeded from the live journal via `FxRecentTapeReseed` — policy items
2–4 in both the offline sweep and the live dispatch gate), replacing the
old “positive cumulative record” filter that read the lifetime cells.
Modes 2/3 keep the cumulative view as research only. Rule 1 still governs
production: **roster-1 off, full roster live** — and after this landing the
distinction is moot at today's samples (no cell has reached the n ≥ 30 bar,
so the tape excludes nothing and default-keeps everything).
