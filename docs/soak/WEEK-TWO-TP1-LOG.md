# Week two — TP1 partials live (opens 2026-09-30)

**Standing:** watching for the first live TP1-ARM row. Nothing to grade yet —
the book has been empty since the enforcement saves, and paper-soak entries
never reach the venue book, so the first sighting comes from real traffic.

## What is already autonomous (no operator needed)

- `scripts/watch_profit_floor.py` (4-min scheduled) prints a TP1 telemetry
  section the moment ARM/EXEC rows appear in the journal.
- The weekly digest auto-posts ~3 min after app start and carries
  `Tp1CaptureMarkdown`: the graded ticket's captured R beside the
  fleet-without-TP1 capture.
- `scripts/trade_lifecycle.py --ticket <n>` builds the per-ticket timeline
  from all six journal row families when a ticket exists.

## The grading runbook (execute when the first ticket settles)

Baseline for comparison: the round-trip losses that started the arc —
peaks handed back in full. Grade the ticket on three numbers:

1. **Rung banked** — lots closed at the TP1 cross × R distance, from the
   EXEC row (`FxExecuteTp1Partials` rows carry the rung price/size).
2. **Live peak** — the FX_PROFIT PeakR the position reached.
3. **Final settlement** — closed R (TRADE_SETTLEMENT), so capture =
   banked / (banked + remaining move).

Then: does the rung beat the counterfactual (same ticket with the rung
never banked, i.e. giveback baseline)? Log one line below.

Watch commands:

```bash
python scripts/watch_profit_floor.py | grep -A4 TP1     # ARM/EXEC rows
curl -s 127.0.0.1:53190/positions                        # open book
python scripts/trade_lifecycle.py --ticket <n> --json    # full timeline
```

## Graded tickets

| Ticket | Symbol | TrailingMode | Armed (UTC) | Exec (UTC) | Rung banked | Live peak | Settled | Capture | vs giveback baseline |
|---|---|---|---|---|---|---|---|---|---|
| — | — | — | — | — | — | — | — | — | — |

Gate note (TP1-FLOOR-INTERACTION.md, now implemented): STRUCTURE_TRAIL
tickets never arm (TP1-SKIP row instead — the floor owns the partial);
graded rows should therefore only ever appear for hybrid/ATR tickets.
If a STRUCTURE_TRAIL ticket ever shows an ARM row, the gate regressed.

## Backtest: TP1 rung on top of the five 2026-09-30 saves

Counterfactual replay (`scripts/tp1_save_backtest.py`): arm each save
ticket's TP1 rung exactly as the live gate would (first FX_PROFIT row
with a rung, plan ≥ 10%, peak ≥ 1R — all five armed 2026-09-29 ~18:11),
execute at the first later row whose CurrentR reaches the rung, then
apply the journaled save price to the remainder.

| Ticket | Save | Plan | Rung R | Crossed | Rung R banked | Save R | Total R | Δ vs save alone |
|---|---|---|---|---|---|---|---|---|
| #9820814920 | override | 25% | 10.14 | no | 0 | 1.62 | 1.62 | 0 |
| #9820161383 | override | 25% | 16.11 | **yes** | 4.03 | 9.01 | 10.78 | **+1.78** |
| #9820781127 | override | 25% | 13.30 | no | 0 | 1.89 | 1.89 | 0 |
| #9820712902 | guard | 25% | 12.72 | yes | 3.18 | 20.30 | 18.40 | **−1.90** |
| #9820719898 | guard | 25% | 10.02 | yes | 2.51 | 16.60 | 14.95 | **−1.65** |

**Net: −1.76R across the five saves.** The sign split is the finding:

- **Override saves (giveback): the rung is free upside or neutral.**
  #9820161383 banked 25% at 16.11R before the giveback to 9.01R —
  exactly the rung's design case (bank early, before the crash the
  override catches). The other two overrides never crossed (rung above
  the price path) and lost nothing.
- **Guard saves: the rung is dominated.** Both guard tickets crossed
  their rungs (12.72R / 10.02R) hours before the floors (25.3R / 21.0R)
  commanded exits at 20.30R / 16.60R — the guard out-executed the rung,
  so banking 25% early would have surrendered 1.90R + 1.65R of peak
  capture. On tickets the floor layer saves, the floor IS the better
  partial-exit.

**Read for week two:** this validates the layering rather than
contradicting TP1 — the rung's value case is the round-trip population
(tickets no layer catches, the 09-28/29 +20.25R failure mode), not
guard-save tickets. Expected live signature when the first TP1-EXEC
lands: rung capture beats the giveback baseline on override-class
tickets and trails the floor on guard-class tickets. Caveats: the arm
uses profit-row PeakR (engine MfeR equivalent — same 2.44 risk distance,
verified two ways on #9820712902); the live arm gate also requires the
ensemble not to be partial/full that cycle, which the replay cannot see
per row; rung banking shrinks the book the floor protects (second-order
on these five, all single-position saves).
