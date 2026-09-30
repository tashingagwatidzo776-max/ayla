# Week one — the profit-capture arc (2026-09-28 → 2026-09-30)

**Headline: profit capture went from 0% to 22% in one day, five verified
saves prevented ~46R of giveback, and the hard profit floor — the arc's
centerpiece — commanded and confirmed real exits on its first day.**

## Where the week started

The exit ensemble scored **0 on every evaluation**: all six weighted
engines measure loss-side risk, so a +7.2R-MFE trade earned a 6.7/100
hold. 3,160 evaluations, 14/14 decisive exits = drawdown MAE overrides,
0W/14L, profit capture **0% of 52.95R MFE**, 6/14 round-trips. The brain
could see winners and had no language for keeping them.

## What was built, in order

| PR | Change | One-line effect |
|---|---|---|
| #132 | HWARANG Profit Brain (advisory) | targets, probabilities, EV, never-down floor schedule — journaled, never traded |
| #133 | Profit-floor tier + restart reseed | giveback votes (0.65/0.85/0.95) + the 0.75 override; peaks survive restarts |
| #134 | Structural stop sizing | honest R units — stops no longer hug the venue minimum band |
| #135 | Scheduled watcher + webhook | saves witnessed through the evidence chain automatically |
| #136 | Breach-time alert + save credits | the webhook fires in-process at the breach; the ledger counts saves |
| #137 | Promotion dashboard + pre-tighten | progress visible in-app; floors tighten on moderate reversal |
| #138 | Auto promotion review + TP1 prototype + lifecycle tool | the reassessment fires itself; the first rung can bank live; one command per ticket |
| #139/#142 | **HARD floor enforcement + reconcile fix** | a breached floor COMMANDS the close; ensemble HOLD cannot veto; confirmation survives sibling positions |
| #143 | Guard saves credit the ledger; §11/G test; TP1 armed | every layer of the save evidence reaches the promotion substrate |

## The day it all fired at once (2026-09-30)

- 06:05–08:57 — three giveback **override saves**: #9820814920 (9.3R→+1.62R),
  #9820161383 (36.1R→+9.01R), #9820781127 (12.4R→+1.89R, the screenshot
  ticket). Full evidence chain each: override row → FX_RISK → close 10009.
- 11:26–11:27 — the hard floor's **first two breaches on executable
  price**: #9820712902 (+20.31R through a 25.3R floor) and #9820719898
  (+16.60R through 21.0R). The ensemble voted **hold 0/100** on both and
  was overridden; both venues closed attempt 1.
- 11:28 — the first engagement exposed the reconcile-starvation bug
  (sibling positions blocked whole-book agreement); fixed same day
  (PR #142) with two regression tests.
- 12:32 — **`PROFIT FLOOR EXIT CONFIRMED` ×2**: the full state machine
  (PROTECTED → BREACH_DETECTED → EXIT_PENDING → EXIT_SUBMITTED →
  EXIT_CONFIRMED → RESET) completed on live traffic.

## The capture bend

| Day (UTC) | Decisive capture | Detail |
|---|---|---|
| 09-28 | 0.0% | 0.00R / 2.35R (2 trades) |
| 09-29 | 0.0% | 0.00R / 50.59R (5 trades) |
| 09-30 | **22.0%** | 15.44R / 70.23R (4 trades) |

Three-day totals: 11 decisive exits, 15.44R banked of 123.17R MFE
(12.5%), round-trips 6 (all pre-enforcement). The week-one chart
([fx-capture-trend.svg](fx-capture-trend.svg)) carries the amber saves
strip: **5 save(s)** on ISO W40, deduped across evidence families.

## What the promotion substrate shows now

- **giveback** (shadow, weight 0): 14 settled / 6 helped = **43%** — was
  20% before the saves. First-save reassessment condition MET; the
  weight-1.0 skeleton ([GIVEBACK-WEIGHT-PR-SKELETON.md](GIVEBACK-WEIGHT-PR-SKELETON.md))
  waits for 30 settled + a fresh McDrill.
- **drawdown** (weighted 2.0): ledger life began with the two guard-save
  `floor-exit` credits (both won) — its keep/adjust/demote matrix lives in
  [DRAWDOWN-PROMOTION-CASE.md](DRAWDOWN-PROMOTION-CASE.md). The two cases
  are explicitly NOT independent evidence (the mirror), and the McDrill
  gate runs with proposed weights to catch the coupling.
- **McDrill**: STABLE across the whole arc — 5.10% flips before
  enforcement, 6.27% after (population changed, verdict did not; baseline
  and proposed identical every run).
- **counterfactual** 11/0, **target-tp** 3/0 — observation continues.

## Honest notes

- The 22% day is n=4 decisive exits. It is a bend, not yet a trend; the
  digest's weekly series and the watcher's daily rollup will say which.
- TP1 is armed and tested but has zero rows — the book has been empty
  since the saves; the first ARM lands with the next managed entry that
  banks ≥1R (watcher-announced, digest-graded).
- The R-unit regime changed mid-week (structural stops): pre-#134
  settlements are weaker evidence for post-#134 thresholds.
- Round-trips 0/4 on enforcement day vs 6/11 across the three days — the
  clearest early sign the override + guard layers did their job.

## Gates at week's end

Core 305/305 · App 540/540 · watcher 16/16 · lifecycle 7/7 · CI green
through PR #148 · 30 commits this arc · engine weights untouched (the
guard is a command layer; the ensemble still journals as evidence).
