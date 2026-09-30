# Giveback engine — promotion case (draft, awaiting its first save)

Status: **weight 0, observation** · 10/100 settled trades · hit 20% vs the
60% bar · no verified live save yet.

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

## Current substrate (live, 2026-09-30)

- counterfactual: 9 settled, 0 helped (0%)
- giveback: 10 settled, 2 helped (20%) — both classic-rule credits; the
  save-credit path (PR #136) has fired only in tests so far.
- target-tp: 1 settled, 0 helped (0%)

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

Not promotable today. Re-run this assessment when (a) the first verified
save lands (watcher webhook) and (b) settled trades ≥ 30 — early signal
that the hit rate is climbing toward the bar rather than pinned by noise.
