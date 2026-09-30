# Hard profit-floor enforcement — first live engagement (2026-09-30)

**Verdict: WORKING.** The enforcement layer (PRs #139 + #142) commanded two
real exits on its first day and both closed at the venue on attempt 1. The
engagement also exposed one genuine bug — reconcile starvation with sibling
positions — which was fixed and regression-tested the same day.

## Timeline (journal_20260930.jsonl, UTC)

| Time | Event |
|---|---|
| 06:05–08:57 | Giveback **override saves** ×3 (PR #133 tier): #9820814920 (9.3R→+1.62R), #9820161383 (36.1R→+9.01R), #9820781127 (12.4R→+1.89R — the screenshot ticket). Ensemble's prior posture was HOLD each time; override score 100 executed. |
| 11:26 | New build (PR #139) deployed, PID 9592. |
| 11:26:41 | **First hard-floor breach ever detected on executable price**: #9820712902, executable +20.31R through the 25.3R floor. Exit commanded. |
| 11:27:41 | Ensemble evaluated the same ticket: **hold, score 0/100** — and was outranked. Close submitted; venue accepted (attempt 1, retcode 10009). |
| 11:27:41–43 | Second breach: #9820719898, +16.60R through the 21.0R floor. Same chain: commanded → submitted attempt 1. |
| ~11:28 | **Bug discovered**: both positions verifiably gone from the venue, but no `EXIT CONFIRMED` row. Guards stayed in `ExitSubmitted`. |
| 11:29–12:20 | Root cause: the reconcile gate demanded the whole-account read to SetEquals this symbol's managed book; sibling positions on other symbols kept the sets apart forever. The App test missed it because its fake book went fully flat. Fix: a non-empty confirm read is definitionally not the degraded-empty lie, so per-ticket absence confirms; a read still holding a submitted ticket downgrades to EXIT_FAILED after a grace window (§13.4). PR #142. |
| 12:20 | Fix merged (73a17c9), app redeployed, PID 13964. |
| 12:32:41–42 | **`PROFIT FLOOR EXIT CONFIRMED` ×2** — the state machine completed end-to-end on live traffic: PROTECTED → BREACH_DETECTED → EXIT_PENDING → EXIT_SUBMITTED → EXIT_CONFIRMED → RESET. |

## Outcome

- **~46R of giveback prevented** across the five saves of the day
  (3 override + 2 guard): 7.7 + 27.1 + 10.5 + ~5 + ~4.
- Daily capture rolled **0% → 22.0%** (15.44R realized of 70.23R MFE,
  4 decisive trades) — the first non-zero day since tracking began.
- Zero floor-bypass fills; zero breach→HOLD regressions; retry ladder and
  §13.4 downgrade never needed to fire for real (all closes accepted
  attempt 1).

## What the bug taught us

The reconcile logic was safe-by-conservatism (it never invents a
confirmation) but starved in the exact shape production provided on day
one: mixed books. The fix keeps both §13 guarantees — degraded empty
reads still can't confirm anything, and a still-listed ticket still
downgrades to failed — while non-empty reads now carry per-ticket
evidence. Two regression tests pin the live shapes:
`Hard_Floor_Confirms_Despite_Sibling_Positions_On_Other_Symbols` and
`Hard_Floor_Downgrades_When_The_Broker_Still_Holds_The_Ticket`.

## Gates

Core 304/304 · App 538/538 · watcher 16/16 · lifecycle 7/7 · CI green
(#139, #142) · engine weights untouched (the guard is a separate command
layer; the ensemble still journals its votes as evidence).
