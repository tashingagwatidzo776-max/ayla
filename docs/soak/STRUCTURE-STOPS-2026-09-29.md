# Structure stops + capture trend — 2026-09-29

## The finding: R units were hugging the venue's minimum band

Ticket 9820781127 (EURUSD) sized its stop from the alpha's hint = ATR(14) of
quiet M1 tape: **2.7 pips**, against a venue forbidden band of 2 pips
(`stops_level: 20 × point 0.0001`). Verified honest (1.13663 − 1.13636 = the
real placed stop) — but the R unit measured spread noise, not structure:

- +14.5R = 23 pips ÷ 2.7 pips: R readings inflated ~4x vs a structural stop.
- The exit brain's MAE ruler, the profit floor schedule, and the giveback
  ratios all scale off this unit — a hypersensitive R poisons every threshold.
- Worse, a sub-band hint (e.g. 1.5 pips) was sized small and then silently
  normalized outward by the venue at SL placement: sizing and the placed stop
  **disagreed** — the R unit the brain derived from the venue SL was never the
  one the trade was sized with.

## The fix: the structural stop floor (sizing, not SL normalization)

`FxEngine.SizeWithStop` (Core) raises the alpha's hint to
**max(hint, `atrStopMult` × ATR(14), venue stops_level band)**, capped at half
the mid price. Notes:

- `atrStopMult` (constructor, default 1.5) was stored but never used — the
  engine was designed for an ATR stop multiple that never got wired. Now it is
  the floor.
- The venue band is included so sizing can never produce a stop inside the
  forbidden band; `NormalizedStopDistance` keeps its clamp semantics for the
  no-bars fallback.
- `FxDecision.EffectiveStopDistance` carries the sized stop out of the engine;
  the host anchors the SL at **exactly** the distance it sized with (the old
  hint-then-normalize divergence is structurally gone), journals it as
  `SizedStopDistance` in FX_ORDER, and remembers it per ticket.
- Restart reseed: `SizedStopFromJournal` reads the newest FX_ORDER row for a
  ticket back into the exit brain's ruler — the R unit survives process
  restarts the same way MFE/floor state already does (PR #133).
- `FxPositionState.InitialStopDistance` makes the sized stop explicit state.

## The observability: weekly profit-capture trend

`FxExitWeeklyDigest` now rolls the WHOLE journal into a per-ISO-week capture
series (decisive exits, MFE ≥ 0.5R — same population as the weekly metric):

- markdown: one line per week (`ISO 2026-W39 33% (1R of 3R, 1 trade(s))`)
  plus a block sparkline, appended with the digest;
- chart: `docs/soak/fx-capture-trend.svg` (self-contained SVG, referenced
  relatively by the soak doc) — one bar per week, labeled capture %, R banked
  of R available, trade count.

This is the series the profit-floor tier must bend upward: 0% capture on
52.95R of MFE (14 decisive exits, 0W/14L) was the score-0 indictment; the
trend makes recovery (or its absence) visible week over week.

## Tests

- Core 283/283 (+5): floor raises sub-noise hints (1.5×ATR), honest wider
  hints are kept, sub-band hints land on the venue band, legacy no-bars
  sizing unchanged, RunOnce decision carries the effective stop.
- App 520/520 (+4): FX_ORDER journals the structural R unit and the static
  journal reader reproduces it (restart reseed), capture series groups by
  ISO week, ISO week boundaries (Dec/Jan, 53-week years), sparkline + SVG
  writer, fail-silent on bad directories.

## The watch, made autonomous (same day)

`scripts/watch_profit_floor.py` now runs as a Windows scheduled task
("DongGfx profit-floor watcher", registered via
`scripts/register-profit-floor-watch.ps1`): logon + every 4 minutes. When a
save VERIFIES through the chain, the script alerts the app's configured
webhook (Discord/Slack payload shapes per WebhookService) **exactly once per
event** — dedup state in `data/watcher/profit-floor-alerts.json` survives
restarts; a failed POST leaves state untouched so the next pass retries.
One pass takes ~5 s against the live journal. Tests:
`test_watch_profit_floor.py` (14: chain verdicts, alert/dedup/retry against
a fake endpoint, corrupt-row tolerance, scheduled-invocation smoke) and
`test_register_profit_floor_watch.py` (16: task contract). Both wired into
CI's integration job.

## Follow-ups

- Watch the live journal for the first giveback vote ≥0.85 or `profit-floor`
  override (nearest candidate #9820884346 at 58% giveback vs the 60% watch bar).
- After enough settled trades, re-run McDrill (weights untouched here —
  fingerprint gate intact; expect STABLE).
