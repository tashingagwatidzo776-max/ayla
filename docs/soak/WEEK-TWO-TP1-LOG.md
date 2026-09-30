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

| Ticket | Symbol | Armed (UTC) | Exec (UTC) | Rung banked | Live peak | Settled | Capture | vs giveback baseline |
|---|---|---|---|---|---|---|---|---|
| — | — | — | — | — | — | — | — | — |
