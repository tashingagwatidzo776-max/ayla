# Drawdown engine — promotion case (independent, as of the hard floor)

Status: **weighted 2.0, earning** · ledger rows began only when the hard
floor started crediting guard saves (PR #143, 2026-09-30) — before that,
its evidence lived in FX_EXIT payloads, not the promotion substrate.

## What this engine is

The `drawdown` voice is the ensemble's own loss-side engine: its
deep-giveback vote (≥ 0.85, `GivebackVoteRatio` 0.75 on a ≥2R peak) and
the profit-floor override (override score 100) are the mechanisms that
drove every save before the hard floor existed. It is NOT the hard floor
— the guard commands exits before the ensemble runs; drawdown is the
ensemble-side evidence layer that outranks consensus inside it.

## Why its case is now separable from giveback's

Two structural changes made drawdown's ledger row a real record of its
own behavior, not just a mirror:

1. **Guard saves credit it directly** (PR #143): a reconciled hard-floor
   exit appends a `floor-exit` row for the drawdown voice (conviction
   0.95, the guard's own peak→breach evidence), independent of any
   ensemble evaluation.
2. **Ensemble closes grade its settled votes**: every ensemble full close
   appends the weight-0 shadow voices AND the giveback mirror — but the
   drawdown engine's own votes arrive with the FX_EXIT payload votes and
   are graded on the same settlement.

The giveback shadow row remains a **mirror** of drawdown's final evidence
(`FxEngineHost.GivebackShadowVote`) — see the case doc's "Drawdown vs
giveback" section for why the two must not be read as independent
confirmation of each other.

## Evidence bar (same gates as the giveback case)

1. **100 settled trades** observed in the ledger (now: see the dashboard
   promotion card / `fx-shadow-*.jsonl` rollup).
2. **60% hit rate** — `Helped` = save evidence (override, deep vote, or
   `floor-exit` with won) or conviction ≥0.5 on a held winner.
3. **Monte-Carlo STABLE** at promotion time (latest: 6.27% flips,
   2026-09-30 — but that run predates most of drawdown's rows; a fresh
   run at the bar is mandatory).
4. **Shipped as a reviewed PR.** Note: drawdown is ALREADY weighted 2.0 —
   the promotion question for drawdown is not "earn a weight" but
   **"keep it, adjust it, or demote it"** based on the same hit-rate
   evidence. A hit rate far below the other engines' is grounds to
   rebalance DOWN, with a fresh McDrill before and after.

## Current substrate (live, 2026-09-30)

- drawdown: rows only from the two guard saves of 2026-09-30 onward —
  effectively **starting its ledger now**. Its pre-ledger history lives
  in FX_EXIT payloads (override saves 06:05/06:46/08:57 + every
  evaluation), not in `fx-shadow-*.jsonl`.

## What to watch

- The first `floor-exit` credits land per guard save (two on 2026-09-30,
  both won).
- If the ensemble's drawdown votes and the guard's saves disagree in
  direction over time (guard saves winning while ensemble drawdown votes
  would have held), that is evidence the ENSEMBLE's drawdown thresholds
  are looser than the guard's — the input to a threshold-tuning PR,
  which must rerun McDrill and is NOT this case.

## Keep / adjust / demote — the decision matrix

Evaluated when the ledger shows ≥30 decisive settlements with drawdown
rows (guard saves + graded ensemble votes). One row per state of the
world; every weight change reruns McDrill before and after.

| Evidence at the bar | Verdict | Action | Rebalance trigger |
|---|---|---|---|
| Hit ≥ 60%, capture trend up or flat, McDrill STABLE at 2.0 | **KEEP** | no change; extend observation to 100 | none — next review at 100 settled |
| Hit 45–60% with wins concentrated in guard saves (ensemble votes near coin-flip) | **ADJUST ↓** | weight 2.0 → 1.0 in a reviewed PR; the guard keeps the save behavior | fresh McDrill must be STABLE; capture must not degrade for 20 settlements after |
| Hit ≥ 60% AND saves increasing week-over-week with capture trend up | **ADJUST ↑** | consider 2.0 → 2.5, only alongside the giveback weight-1.0 PR's fresh MC run | requires TWO consecutive STABLE runs; never crosses drawdown-grade for the giveback mirror |
| Hit < 45%, or McDrill NON-STABLE attributable to drawdown votes | **DEMOTE** | weight 2.0 → 0 (shadow) in a reviewed PR; the hard floor is unaffected — it is a separate command layer | McDrill before/after; the giveback mirror's meaning changes (its settled rows would go all-shadow) — re-review both cases together |
| Ledger rows diverge from FX_EXIT payload evidence (mirror staleness) | **FIX FIRST** | no weight change until `GivebackShadowVote`'s coupling is repaired | n/a — substrate integrity gates everything |

The matrix's guard rails: the hard floor is never touched by any row
(it is not an ensemble citizen), every demotion is recoverable (weights
are data, reviewed back in through the same evidence bar), and no row
fires on a sample smaller than 30 decisive settlements.
