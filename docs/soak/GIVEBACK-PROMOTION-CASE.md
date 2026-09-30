# Giveback engine — promotion case (first-save condition MET 2026-09-30)

Status: **weight 0, observation — REASSESSMENT TRIGGERED** · 14/100 settled
trades · hit 43% (was 20% yesterday) · **first verified live saves landed
2026-09-30** — five saves, all five closes confirmed at the venue.

## The first save — checklist CLOSED (2026-09-30)

| Time (UTC) | Ticket | Peak → closed near | Chain | R saved vs round-trip |
|---|---|---|---|---|
| 06:05 | #9820814920 | 9.3R → +1.62R | giveback override (0.75) → FX_RISK → close 10009 | ~7.7R |
| 06:46 | #9820161383 | 36.1R → +9.01R | giveback override → FX_RISK → close 10009 | ~27.1R |
| 08:56/08:57 | #9820781127 | 12.4R → +1.89R | giveback override ×2 → FX_RISK → close 10009 | ~10.5R |
| 11:27 | #9820712902 | 28.1R → breach +20.31R | HARD floor guard (25.3R floor) → deal attempt 1 → CONFIRMED 12:32 | ~5R+ |
| 11:27 | #9820719898 | 23.4R → breach +16.60R | HARD floor guard (21.0R floor) → deal attempt 1 → CONFIRMED 12:32 | ~4R+ |

Evidence verified in `journal_20260930.jsonl`: FX_EXIT override rows with
score 100 + close retcode 10009 (three saves), FX_FLOOR
BREACH→SUBMITTED→CONFIRMED rows (two guard saves — the hard-enforcement
layer, PR #139/#142). The ensemble voted **hold 0/100** on both guard
tickets; the override outranked it. Daily capture bent the same day:
0% (09-28/29) → **22.0%** (15.44R banked of 70.23R MFE, 4 decisive trades).

## The engine

The `giveback` shadow voice has ridden every exit evaluation at weight 0
since PR #132 (`FxExitShadowVotes`): its exit confidence is the giveback
ratio itself — `1 − P/L ÷ MFE` (capped 0.9), "returned X% of its peak". It
argues the profit-side case every weighted engine is blind to: structure,
momentum, volatility, time and thesis all read a trade that gave back half
its peak as "healthy".

## What has changed since the engine went in

| Date (2026-09-29/30) | Change | Why it matters for this case |
|---|---|---|
| PR #133 | Profit-floor override + deep giveback vote (0.85/0.95) in the Exit Brain | The giveback evidence can now DRIVE exits, not just observe |
| PR #134 | Structural stop sizing (max(hint, 1.5×ATR, venue band)) | Honest R units — giveback ratios were measured against venue-band-hugging stops before |
| PR #135 | Scheduled watcher + webhook on verified saves | Every save is now witnessed through the evidence chain automatically |
| PR #136 | Saves credited in the ledger; breach-time alert | `Helped` counts save evidence (override or deep vote) for giveback + drawdown |
| PR #139/#142 | HARD profit-floor enforcement (guard commands exits) | Saves no longer depend on ensemble consensus at all; guard saves credit the ledger via `floor-exit` |
| this PR | Moderate-reversal pre-tighten; promotion dashboard | The floor tightens before full reversal confluence; progress visible in-app |

## The evidence bar (unchanged, spec gates)

1. **100 settled trades** observed (now: 10).
2. **60% hit rate** — `Helped` = saved a winner's give-back (override/deep
   vote, since PR #136) or conviction ≥0.5 on a held winner (classic).
3. **Monte-Carlo STABLE** at the moment of promotion (latest run: 5.10%
   flips, 39 settled evaluations — no regression).
4. **Shipped only as a reviewed PR**, weight ≤ 1.0 (never drawdown-grade).

## What the first save must look like (the checklist this doc will close)

- FX_EXIT row: `Override == "profit-floor"` (score 100, "gave back a X.XR
  peak … emergency close") or a drawdown vote ≥ 0.85 with a "given back"/
  "give-back" reason the resolver acted on.
- FX_RISK row: "profit floor … breached — exit evaluation requested" for
  the same ticket (the in-process webhook fires at this moment since PR
  #136).
- FX_EXIT close confirmation ("closed #N — deal …"), Won = ProfitR > 0.
- Ledger rows appended for `giveback` + `drawdown` with `Helped: true` —
  verified by the scheduled watcher (exit 2 + webhook) and the dashboard
  promotion card.

## Current substrate (live, 2026-09-30 after the five saves)

- counterfactual: 11 settled, 0 helped (0%)
- giveback: 14 settled, 6 helped (43%) — 2 classic-rule credits + the
  2026-09-30 save credits; hit rate 20% → 43% in one day of live saves.
- target-tp: 3 settled, 0 helped (0%)

Gap closed the same day: the two guard-executed saves (#9820712902,
#9820719898) initially bypassed the ledger (the append lived only in the
ensemble's close branch) — the promotion ledger now appends a `floor-exit`
row when a guard save reconciles (PR following #142), so every future
hard-floor save counts.

## Drawdown vs giveback — keeping the two cases separable

Every save credits TWO engines, and they must not be read as independent
confirmation of each other:

- **drawdown (weighted 2.0)** — the ensemble's own loss-side engine whose
  deep-giveback vote (≥0.85) and override drive the exits. Its ledger
  rows come from two sources: ensemble full closes (settled votes graded
  at close) and, since PR #143, guard-executed `floor-exit` saves. Before
  the hard floor shipped it had ZERO ledger rows — its evidence lived in
  FX_EXIT payloads, not the promotion substrate.
- **giveback (shadow, weight 0)** — its settled row is a deliberate
  **mirror**: `FxEngineHost.GivebackShadowVote` copies the drawdown
  engine's final Exit/Reason onto a weight-0 giveback voice so the same
  evidence can be graded for the ratio-shaped hypothesis at weight 0.

**Consequence for promotion:** giveback's save-credits are NOT independent
evidence — they are the same signal family re-graded. Giving giveback
weight 1.0 means the giveback evidence family effectively votes twice
(drawdown 2.0 + giveback 1.0) on the same tick. That is acceptable ONLY
because (a) the two voices diverge in behavior — drawdown is a binary
threshold, giveback is the continuous ratio — and (b) the McDrill gate
runs with the proposed weights, so any double-counting instability shows
up as non-STABLE flips. Reviewers of the weight-1.0 PR should weigh the
case as "the ratio shape earns its own voice", not "two engines agree".

If the ledger ever shows giveback helped-rate diverging sharply from
drawdown's on identical settlements, investigate the mirror first — the
mirror is the coupling, and divergence means the copy is stale, not that
one engine got smarter.

## Honest caveats

- **20% ≠ trajectory.** The two helps came from the classic rule; zero
  saves have settled. The hit rate may fall before it rises.
- **The drawdown engine rides the same evidence.** Every save credits
  drawdown (weight 2.0, already earning) as well as giveback (weight 0) —
  the case for giveback specifically is that its ratio-shaped vote adds
  signal beyond the binary thresholds.
- **R-unit regime change.** Structural stops (PR #134) change what "R"
  means; pre-change settled trades are weaker evidence for post-change
  thresholds. Expect the 100-trade bar to effectively restart from the
  first structural-stop settlement.
- **No weight change ships without a fresh McDrill STABLE verdict** on the
  then-current settled population.

## Decision

Reassessment triggered (first save landed 2026-09-30). Early signal is
strong — 43% hit after one day, capture bent 0% → 22% — but the sample is
five saves in one session. **Not promotable yet**: hold until (b) settled
trades ≥ 30 AND a fresh McDrill STABLE verdict on the then-current
population. If the hit rate is still ≥ 40% at 30 settled, prepare the
weight ≤ 1.0 PR for review.
