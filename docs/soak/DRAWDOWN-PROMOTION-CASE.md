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

## Backtest: would weight 1.0 have changed any decision? (2026-09-30)

The ADJUST ↓ row's central risk question, answered against the full
journal: replay every journaled FX_EXIT evaluation at drawdown weight
1.0 instead of 2.0 and diff the resolver's action.

**Method.** `scripts/dd_weight_backtest.py` re-scores each evaluation
from the votes as journaled — `score = Σ(exit·w)/Σw·100`, the resolver
bands pinned in `FxExitBrain.Resolve` (85/70/55/35) — with only the
drawdown engine's weight moved 2.0 → 1.0. No engines are re-estimated:
the journal stores every vote, so the replay is arithmetic on the
recorded evidence. Two integrity probes gate every result: (a) the
logged `Score` must reproduce from the logged weights (0 mismatches on
this sample — no schema drift), and (b) rows whose drawdown weight is
not the roster 2.0 are skipped rather than counterfeited (173 rows:
unparseable + pre-roster). Override-tier rows are counted but can never
flip — overrides bypass the resolver at score 100.

**Sample.** 2026-09-28 → 09-30 journals: 5,920 resolver-tier
evaluations + 13 override rows (9 `drawdown`, 4 `profit-floor`).

**Result: weight 1.0 would not have changed a single exit decision.**

- **0 exit-boundary flips** — no evaluation crossed into or out of
  `partial`/`full` at either weight, across all 9 tickets and 5,920
  evaluations.
- 76 advisory-band flips (1.28%), all inside hold/monitor/tighten:
  `monitor→hold` ×10 rows across 6 tickets, `hold→monitor` ×66 rows
  across 3 tickets. Advisory bands never close anything by themselves.
- **The guard-save tickets stay guard-save tickets.** 56 of the 76 flip
  rows are #9820712902 / #9820719898 moving hold→monitor (score
  30.84→35.07): with less drawdown weight their scores sit slightly
  higher, but still nowhere near an exit band. The ensemble never voted
  to exit them at 2.0 **or** 1.0 — the guard's full close was the only
  thing that closed them, exactly the separate-layer design. (These
  positions were later re-sighted under new tickets — #9821030910 rows
  — which also flip only hold→monitor.)
- The 13 override rows replay unchanged: 9 drawdown and 4 profit-floor
  emergency closes fire identically at either weight.

**Fragility read.** 11 evaluations sat within 2.0 score of a band edge —
all at the 35 monitor edge (33–35.1); none near the 70 partial edge.
The ensemble's exit decisions have historically had a wide margin over
drawdown weight: drawdown voted exit = 0 in 5,746 of 5,920 evaluations
(97%), so moving its weight mostly reshuffles a handful of nonzero
votes (174 rows) whose ensemble context never approached an exit band.

**Verdict for the matrix.** If the ADJUST ↓ trigger ever fires
(hit 45–60%, wins concentrated in guard saves), this sample says the
rebalance is decision-neutral on exits: the guard keeps every save, the
ensemble's close behavior is untouched, and only advisory labels move.
Caveats: three days of journal, an ensemble whose drawdown votes are
almost always 0 (the interesting rows are 174), and the guard-save
sample is two tickets. The replay must be rerun inside any ADJUST PR —
per the matrix — with the ledger grown by whatever settles between now
and then.

Run it: `python scripts/dd_weight_backtest.py` (reads the live journal
dir; write nothing, touch nothing).
