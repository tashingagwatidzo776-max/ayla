# Week two — TP1 partials live (opens 2026-09-30)

**Standing:** watching for the first live TP1-ARM row. Nothing to grade yet —
the book has been empty since the enforcement saves, and paper-soak entries
never reach the venue book, so the first sighting comes from real traffic.

## What is already autonomous (no operator needed)

- `scripts/watch_profit_floor.py` (4-min scheduled, task action pinned to
  `PYTHONIOENCODING=utf-8` so it never depends on the in-process stream
  reconfigure) prints a TP1 telemetry section the moment ARM/EXEC rows
  appear in the journal.
- **Gate-regression drill**: the watcher flags any TP1-ARM row on a ticket
  whose trailing mode was STRUCTURE_TRAIL at arm time (`!!! TP1 GATE
  REGRESSION` banner) and exits 3 — persistent, every pass, because the
  journal keeps the evidence, and PAGES the webhook once per regression
  row (deduped across restarts; a failed POST retries next pass).
  Exit codes: 0 clean, 2 verified save, 3 gate regression
  (docs/soak/TP1-FLOOR-INTERACTION.md).
- **Watch-the-watcher**: `scripts/check_watcher_task_health.py` fails (rc 1)
  when the scheduled task is stale (last run > 10 min despite the 4-min
  trigger) or the last two DISTINCT completions share a failure-class exit
  code (persistent exit 3 = the gate regression firing; exit 2 twice is two
  verified saves — never a failure). rc 2 = broken probe (never green).
  Runs hourly as task "DongGfx watcher task health" AND as a `ci-local.ps1`
  gate step (skips on machines without the task; rc 2 skips, never
  false-alarms the gate).
- **First-ARM pager**: task "DongGfx TP1 first-arm pager" runs
  `scripts/watch_tp1_first_arm.py` every 5 min — on the first TP1-ARM row
  it pages the webhook (🎯 once per arm row, deduped across restarts;
  exit 2 scheduler-visible), on the ticket's TP1-EXEC it pages again with
  the graded number in the message (banked lots + rung R resolved from
  the ARM row; refusals page with the retcode — grading needs to see
  them), and it dumps the grading evidence (every TP1 row plus every
  FX_* row for the referenced tickets) to
  `data/watcher/tp1-first-arm-evidence.jsonl`. TP1-SKIP rows are dumped
  as evidence but never page. Replaces session babysitting: the graded
  table below is generated from the journals, no operator watching.
- **TP1-EXEC digest (auto)**: every pass of the pager regenerates the
  `### Live TP1-EXEC digest (auto)` block above the graded table — one
  row per banked (or refused) rung, mechanically derived from the
  journals: rung R and plan % from the ARM/EXEC payloads, Banked R =
  plan × rung R, live peak from the newest FX_PROFIT report at-or-before
  the exec, closed R from the newest decisive FX_EXIT after it,
  TrailingMode reconstructed with the gate drill's rule (unknown → —).
  The block is REPLACED in place each pass (markers
  `TP1-EXEC-DIGEST:START/END`), so the log stays hand-editable and
  never accumulates stale rows.
- **Graded verdicts (auto)**: the same pass emits a machine-readable
  verdict per **banked** ticket to
  `data/watcher/tp1-graded-verdicts.jsonl` (one JSON object per ticket,
  rewritten each pass) and regenerates the `## Graded tickets` table
  from it (markers `TP1-GRADED:START/END`). Capture = settled R ÷ live
  peak (the weekly digest's rule); vs giveback baseline = plan % ×
  (rung R − settled R) — the R the early rung locked versus holding the
  whole position to the same exit (positive = the rung beat the
  giveback, negative = the floor/exit out-earned it). Verdict is
  `pending` until a decisive FX_EXIT settles. Refused rungs are not
  graded (they never banked). The numbers are now generated; the human
  step left is reading the verdict, not computing it.
- **Plan-% recommendation gate (auto)**: the same pass evaluates the
  settled verdicts as a portfolio — with ≥3 settled rungs and a net R at
  or below −1.0R versus the giveback baseline the rungs are collectively
  losing value (**↓ lower the plan %**), and at or above +1.0R they are
  collectively beating it (**↑ raise the plan % / widen the rungs**). The
  page carries a concrete **candidate plan %**: the mean current plan %
  across the graded rungs moved one 5-point step per full R of mean
  vs-giveback in the gate's direction (clamped 0–60%) — a graduated
  heuristic (the filled-rung sample can't be solved to an interior
  optimum), shown as e.g. `25% → 15%`. The pager posts once per direction
  (deduped across restarts, like the ARM/EXEC pages) and RE-ARMS when the
  rungs recover or flip direction; the full decision path is the runbook
  below. Every recommendation, clearance, and acted-on mark lands in an
  append-only audit ledger, `data/watcher/tp1-plan-reviews.jsonl`, where
  `read_plan_reviews()` folds the events into one **open / cleared / acted**
  status per review. An open review **auto-closes** (marked `acted` with an
  auto note) when a later plan-% observation reaches the candidate in the
  recommended direction — `mark_plan_review_acted` is only the manual
  fallback when the live plan is not the tell. `python
  scripts/watch_tp1_first_arm.py --plan-reviews` prints that status view and
  flags any OPEN review older than 7 days as `⚠ STALE`, and a review still
  open past the threshold pages an **⏰ overdue** alert once (deduped per
  review) — an unactioned suggestion is impossible to miss. While a review
  is overdue the engine's **circuit breaker** holds the rung itself
  (`FxEngineHost.Tp1PlanReviewHold` via `Tp1PlanBreaker`): no new TP1 arm
  until it is acted on or cleared, one `TP1-HOLD` row per ticket — an
  overdue review is a pause, not just a notification. Closed reviews are
  then **scored** from the rungs settled after them (did acting lift the
  graded net?), and that track record feeds the candidate step size
  (`plan_step_pct`). The weekly digest rolls the same ledger up (open /
  cleared / acted since the last post). The step feedback is bounded
  twice over — a minimum sample of 3 scored reviews, the step clamped to
  [1%, 15%], and never more than ±3 points from the base step — so the
  learning can tune the gate, not run it, and every change (and every
  return to base) is appended as an `adapted` event to the same ledger
  (`read_step_adaptations()`), so the adaptation is auditable. The app
  surfaces the hold itself: with TP1 armed the Settings toggle hint turns
  **amber with a ⚠ glyph** and reads `ARMED but HELD — an overdue plan-%
  review is open …`, listing the open review(s) with **Mark acted** /
  **Clear** buttons that append the same ledger events the CLI writes
  (`mark_plan_review_acted`) and drop the breaker cache — so the rung can
  be released in-app, without a terminal. An always-visible **dashboard
  banner** carries the same state, recomputed at load, on toggle, on tab
  change, and on the 60s promotion tick. The dashboard also renders the
  **watcher's latest recommendation** itself — read straight from the same
  ledger, never re-derived by the app: `TP1 plan-% recommendation · ↓ 25% →
  15% · net -3.20R · 3 graded (3 trailed) · open 10d`, so the candidate plan
  % and the evidence behind it are visible without a terminal. Beneath it a
  short **recommendation history** lists the last few calls with how each
  ended (`✔ acted on` / `cleared (recovered)` / `open`), so whether acting
  has been helping is visible at a glance. The recommendation line carries
  a **one-click Arm** button — labelled with the candidate it will take
  (`Arm 15%`) — that closes the advice-to-action loop without opening
  Settings: after an explicit confirm (fail-closed when declined) it
  appends the SAME `acted` ledger event the CLI runbook writes AND persists
  the plan-% override (`AppSettings.Tp1PlanPctOverride`) the engine honors
  from the next cycle — the gate's advice becomes the plan, and a restart
  keeps it. The engine uses that figure in place of the allocation plan's
  own TP1 leg for the rung's eligibility, lot sizing and journaling
  (`FxEngineHost.Tp1PlanPctOverride`); a **0%** override reads as "do not
  take a partial" (below the 10% eligibility floor) and pulls the rung
  entirely. While an override is in force a dashboard **banner** names the
  armed plan % and offers a **Revert** button that clears it back to the
  allocation plan. Arming it is a claim, not a proof — so the watcher
  **verifies it actually reached a rung**: the FX_MODE `override ARMED`
  journal row dates the override (settings.json carries only the value),
  and every pass checks whether the newest `TP1-ARM`/`TP1-EXEC` row after
  it carries that `PlanPct` — sizing rides the same figure, read at arm
  time for eligibility and again at the cross for the lot size, so only
  rows strictly after the arm are judged (a rung armed before it
  legitimately froze at the old figure). Three failures page the webhook
  once per arm — TP1 partial execution OFF (certain: no rung can ever
  fire), a rung armed at a *different* plan % (the engine sizing from the
  allocation plan instead), and an override no rung has carried past the
  grace window (3 days, `PLAN_OVERRIDE_GRACE_DAYS`) — EXCEPT a
  precondition that can never clear itself (`OVERRIDE_DEAD_REASONS`:
  `partials-off`, `engine-idle`, `app-idle`), which pages on the very
  first pass: waiting three days to report a stopped loop only delays
  the fix. Those pages are titled *will not fire* — with the arm's age
  in hours while it is hours old (`_age_label`) — not *never fired*,
  and a freshly armed override with no rung yet and no dead
  precondition is reported, not paged, while one only ever written to
  settings.json reads as *never journaled* rather than paging a guess. The verdict is always on `--plan-reviews` (`used by the
  next rung` / `waiting for a rung` / `NEVER FIRED` / `IGNORED` /
  `CANNOT FIRE`), and a rung that does carry it clears the standing flag
  so a fix followed by a regression pages again. That SAME verdict is
  rewritten to `data/watcher/tp1-override-verification.json` every pass
  and read back by the dashboard (`Tp1PlanOverrideCheck`), so
  `waiting for a rung` / `⚠ NEVER FIRED` sits on the override banner
  **where the Arm button is** — one computation, so the pager and the
  banner cannot disagree. Alongside the status the verdict carries the
  **unmet precondition**, because "never fired" and "cannot fire" need
  different fixes: `partials-off` → `engine-idle` → `app-idle` →
  `hold-blocked` → `gate-blocked` → `no-eligible-rung`, first unmet
  precondition wins. *partials-off* is TP1 partial execution off;
  *engine-idle* is the brain's loop off (`FxBrainRunning`);
  *app-idle* is no journal row for `PLAN_APP_IDLE_HOURS` (6h — measured
  against the live journals, where a running brain never goes silent past
  ~58 min and every multi-hour gap ends at a `startup` row);
  *hold-blocked* and *gate-blocked* mean the rung question was REACHED
  and answered no (`TP1-HOLD` / `TP1-SKIP` rows after the arm) rather
  than never asked; *no-eligible-rung* means nothing is unmet at all. The
  reason is not just reported, it is acted on: the banner pairs each
  fixable reason with a **button that does what the reason says** —
  *Start brain* for `engine-idle`/`app-idle`, *Arm TP1 partials* for
  `partials-off`, *Act on review* for `hold-blocked` (the one that drops
  the breaker's cache and releases the rung) — beside the existing
  **Revert**, and the reasons the app cannot fix (`gate-blocked`,
  `no-eligible-rung`) show no button at all rather than a dead control —
  an italic stand-in line says so in its place (*nothing to press — the
  trailing gate has said no; the floor owns this rung* / *nothing to
  press — nothing is unmet; the setup simply has not come*), so an absent
  button is never mistaken for a broken banner. Pressing a button reports
  its outcome as a toast — success or refusal (*Brain started…* / *Brain
  did not start…*, *…armed* / *Arming declined — TP1 partial execution
  stays OFF (fail-closed)*) — so a click is never silent, and the toast
  respects the notification setting. The hooks are fail-closed on their own terms: the brain hook only ever
  STARTS (the toggle could stop a loop the verdict calls stale), the
  partials arm rides the confirm-gated setter (declined stays OFF), the
  review act marks the oldest overdue review exactly as the Settings
  list does. The sentence itself is written by the watcher (`reason_text`) and shown in
  parentheses — `⚠ NEVER FIRED — no rung has carried it in 5.0d (the
  brain loop is off)` — so the CLI line, the webhook page and the banner
  cannot phrase one finding three ways, and it is nulled where it would
  only restate the status. The reader drops a file that is stale (the
  watcher runs every 5 min, so an hour without a refresh means it is not
  running) or one naming a different plan % than the live override, so a
  verdict left over from an earlier arm can never mislabel the banner;
  a file that does not appear at all simply leaves the line hidden — the
  banner already states the armed value from the engine. The
  gate/breaker tunables (evidence bar, net-R bands, base step, drift
  bound, stale threshold) live in ONE shared file, `config/tp1-plan-gate.json`, read by BOTH the
  watcher and the app — so the recommendation and the hold cannot drift
  apart, and a malformed edit leaves the compiled defaults standing. The
  file ships beside the exe (the published-build fallback when there is no
  checkout above it), is **editable from Settings** — every tunable is a
  validated field, and an incoherent gate (inverted bands, a floor above
  the ceiling, a negative step) is refused before it lands — and
  **hot-reloads**: an edit is taken up without restarting the app or the
  watcher, and the breaker's cache is keyed to it so retuning the stale
  threshold releases the rung immediately. The digest/log stay the record.
- **Task-fleet audit**: `scripts/audit_donggfx_tasks.py` (tests 12/12)
  checks every DongGfx/tf scheduled task for the PYTHONIOENCODING cmd
  wrapper (pythonw is N/A — no stdout), repo-script drift vs git HEAD,
  and missing files; duplicates are notes, not findings. Read-only —
  findings print with the exact fix command. Every run appends a deduped
  summary line to `data/watcher/task-audit-history.json` (TF_DATA_DIR-
  aware), and task **"DongGfx task-fleet audit"** (register:
  `scripts/register-audit-task.ps1`, tests 17/17) runs it WEEKLY — so
  drift is caught without a session: when a finding first appeared and
  whether it persisted is readable from the history file.
- The weekly digest auto-posts ~3 min after app start and carries
  `Tp1CaptureMarkdown`: the graded ticket's captured R beside the
  fleet-without-TP1 capture, with each banked rung's graded verdict and
  vs-giveback R read from `data/watcher/tp1-graded-verdicts.jsonl`
  (` — beat (+1.78R vs giveback)`). The capture-trend SVG outlines in blue
  every ISO week that banked a TP1 rung (legend on the chart), so the
  prototype's weeks read beside the captures they produced. It also carries
  a `### TP1 plan-% reviews` rollup of the plan-review ledger — how many
  recommendations the gate made since the last post and how many are open,
  cleared, or acted on.
- `scripts/trade_lifecycle.py --ticket <n>` builds the per-ticket timeline
  from all six journal row families when a ticket exists, surfacing the
  ticket's TP1 graded verdict; the portfolio table carries a `tp1` column
  beside a `rung/no-rung` column (the R with the rung banked versus the
  no-rung counterfactual that held the whole position to the same exit),
  a `TP1 grading:` rollup (beat/trailed/flat/pending + net R vs giveback),
  a callout for the rungs that TRAILED the giveback, and a 🚨 per-ticket
  **loss alert** for any single rung that surrendered ≥1R — a material loss
  is flagged on its own before it drags the portfolio net.

## The grading runbook (execute when the first ticket settles)

Baseline for comparison: the round-trip losses that started the arc —
peaks handed back in full. Grade the ticket on three numbers:

1. **Rung banked** — lots closed at the TP1 cross × R distance, from the
   EXEC row (`FxExecuteTp1Partials` rows carry the rung price/size).
2. **Live peak** — the FX_PROFIT PeakR the position reached.
3. **Final settlement** — closed R (TRADE_SETTLEMENT), so capture =
   banked / (banked + remaining move).

Then: does the rung beat the counterfactual (same ticket with the rung
never banked, i.e. giveback baseline)? The pager answers it
mechanically — the generated `## Graded tickets` table and
`data/watcher/tp1-graded-verdicts.jsonl` carry the capture and the
vs-giveback verdict per banked ticket. The remaining human step is the
judgment on top: whether the rung is worth keeping at this plan %, and
the one-line narrative below.

Watch commands:

```bash
python scripts/watch_profit_floor.py | grep -A4 TP1     # ARM/EXEC rows
python scripts/watch_profit_floor.py; echo "exit=$?"     # 2 save, 3 gate regression
curl -s 127.0.0.1:53190/positions                        # open book
python scripts/trade_lifecycle.py --ticket <n> --json    # full timeline
```

## The plan-% review (when the gate pages)

`scripts/watch_tp1_first_arm.py` pages a **TP1 plan-% review** once the
settled verdicts clear the evidence bar — **≥3 settled rungs** with a net R
versus the giveback baseline at or below **−1.0R** (↓ *lower the plan %*)
or at or above **+1.0R** (↑ *raise the plan % / widen the rungs*). It fires
once per direction and re-arms on recovery, so it is a signal, not a
stream. Turn it into a concrete proposal like this — the change ships as a
reviewed PR, never automatically:

1. **Read the evidence.** `python scripts/trade_lifecycle.py` prints the
   `TP1 grading:` rollup (net R + verdict split), the `rung/no-rung`
   column, and a `🚨` per-ticket alert for any single rung that surrendered
   ≥1R. Cross-check `data/watcher/tp1-graded-verdicts.jsonl` and the
   generated `## Graded tickets` table.
2. **Read the split, not just the net.** Guard saves are the population
   where a static rung loses (the floor out-executes it); override saves are
   the rung's design case. A net dragged down by guard-save tickets argues
   for the plan % on the override/hybrid regime, not scrapping the rung.
3. **Name the knob.** The page already suggests a candidate plan %
   (`25% → 15%`) from the rung arithmetic; sanity-check it against the
   allocation's TP1 leg (`FxProfitBrain.AllocationPlan`: 15 / 25 / 40 by
   continuation/reversal confidence) and land it as a specific regime →
   new %, with the graded tickets cited as evidence.
4. **Re-run the Monte-Carlo drill** before shipping (docs/soak/MC-DRILL.md):
   if an allocation change moves the exit-action flip rate past the 10%
   bar, the change is out.
5. **Replay the saves** with `scripts/tp1_save_backtest.py` at the proposed
   % so the counterfactual is re-graded on the known population, not just
   the live sample.
6. **Record the outcome — or act in-app.** The dashboard recommendation
   line's **Arm** button does steps 3+6 in one click: behind an explicit
   confirm it appends the `acted` event and persists the candidate plan %
   as the engine's override, so the advice takes effect without a terminal
   (the Revert banner button releases it). Shipping a plan-% change usually
   closes the review on its own too: the watcher sees the new plan % reach
   the candidate and marks it `acted` with an auto note. When the change is
   not visible in the journal (declined, or applied elsewhere) use
   `mark_plan_review_acted(<review id>, note=...)`. List the history with
   `read_plan_reviews()` — the ledger is append-only, so the original
   recommendation and its evidence stay intact. An unacted review stays
   `open` (and pages `⏰ overdue` past 7 days); one whose condition resolves
   on its own reads `cleared`.

Watch command:

```bash
python scripts/watch_tp1_first_arm.py; echo "exit=$? (2 = page fired)"
python scripts/watch_tp1_first_arm.py --plan-reviews   # lifecycle status
```

**Note:** while a review is overdue the circuit breaker holds TP1 arming
in the app — the Settings hint turns amber (`ARMED but HELD`) with the
open review listed and **Mark acted** / **Clear** buttons, a dashboard
banner carries the same state, and the `TP1-HOLD` journal row is the
evidence. Act or clear it in-app, or via `mark_plan_review_acted`, to
release the rung. When the recommended change is a plan % you are willing
to take on live, the dashboard's **Arm** button is the short path: it
persists the override the engine honors and appends the same `acted`
event, releasing the hold without a terminal. **Revert** on the override
banner returns the rung to the allocation plan's own leg.

<!-- TP1-EXEC-DIGEST:START -->
### Live TP1-EXEC digest (auto)

Regenerated every pass by `scripts/watch_tp1_first_arm.py` —
the raw EXEC ledger; the graded verdict (capture, vs giveback)
is generated below. Banked R = plan % × rung R
(position-weighted R locked at the cross). Closed R = newest
decisive FX_EXIT ProfitR after the exec ('—' while open).
TrailingMode is reconstructed from the ticket's FX_PROFIT
telemetry at-or-before the arm (unknown → '—'; the same rule
the gate-regression drill uses).

| Ticket | Symbol | Mode | Armed (UTC) | Exec (UTC) | Rung | Plan | Banked R | Live peak | Closed R | Executed |
|---|---|---|---|---|---|---|---|---|---|---|

<!-- TP1-EXEC-DIGEST:END -->

## Graded tickets

<!-- TP1-GRADED:START -->
Generated every pass by `scripts/watch_tp1_first_arm.py` — no banked rung
in the journals yet. The first TP1-EXEC writes the row here (and one
line to `data/watcher/tp1-graded-verdicts.jsonl`).
<!-- TP1-GRADED:END -->

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
