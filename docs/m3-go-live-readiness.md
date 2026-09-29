# M3 go-live readiness — week review (2026-09-27)

Week-status refresh of the readiness review ([initial review of
2026-09-25](m3-go-live-readiness.md), runbook:
[`soak-session-plan.md`](soak-session-plan.md)). Same milestone, one week
further: what moved, what regressed, and what the next session must produce.

## 1. Where the soak stands (merged week)

Committed evidence in `docs/soak/`:

| Report | Verdict | Summary |
|---|---|---|
| `SOAK-2026-09-26.md` | CLEAN | **3103 entries, 56 settlements** (re-imported week-old deals, freshness gate held), 0 refusals; first production AI narrative |
| `SOAK-2026-09-22.md` | CLEAN | 296 journal entries, 0 settlements, 0 gate refusals |
| `SOAK-2026-09-21.md` | CLEAN | earlier session, same shape |
| `SOAK-2026-09-19.md` | CLEAN | first recorded evidence (73 entries) |

- **Freshness:** newest evidence is 1 day old — well inside the weekly
  drill's 14-day `--max-age`. The Monday-open watcher
  (`open-watcher.ps1`, registered task) records the next report
  automatically; the `--record` output still needs the human
  `git add docs/soak && git commit`.
- **The gap that matters — still the gap:** the 56 settlements in the
  2026-09-26 count are **re-imported week-old deals** from the venue's
  7-day window, not session trades (the report says so itself, and the
  first-settlement milestone correctly did not fire on the re-import). The
  *trading* loop remains unevidenced: M3's bar is settled demo trades from
  the session's own decisions through `TRADE_SETTLEMENT`, the scorecard,
  and the performance dashboard.

## 2. What shipped this week (code)

- **The demo account IS the paper account** — paper-mode FX signals now
  execute as real MT5 market orders on the connected demo venue
  (`PAPER-EXEC` in the journal, ticket + price recorded, supervisor/rails
  fully applied, demo equity moves with paper P/L). Hard guard: execution
  requires the bridge to VERIFY the account is demo (`trade_mode`, else the
  demo-server heuristic) — a real account, even with the session unlock
  armed, or an unverified one refuses fail-closed. Paper practice can never
  leak into real money. Soak counting is unchanged: signals in paper still
  advance the same go-live bar; demo fills are execution practice, not
  soak evidence.
- **AI agents 1 & 2 live** — `JournalAnalystService` (scheduled 🤖 narrative,
  local Qwen3, guarded prediction-memory loop) and `RiskNarratorService`
  (halt explanations within seconds, cannot re-arm). Evidence: the
  SOAK-2026-09-26 narrative section was produced by the production analyst,
  not by hand.
- **Alpha validation pipeline** — `soak_evening.py --alpha` accumulates
  per-run sections; every proposal so far lands at **0 trades /
  insufficient-trades verdicts** (see SOAK-2026-09-26): the 5-trade bar has
  not been cleared by anything, which is the harness being honest, not idle.
- **Genetic lab** — `FxLabService` (nightly, journal-only walk-forward with
  the `FxWalkForward` approval gate; evidence journaled as `FX_LAB`, no
  promotion path). Needs ≥120 journaled decisions per symbol before it
  speaks — the current thin journals keep it silent by design.
- **Maps tab** — volume surface + seven heatmaps (liquidity, order flow,
  volatility, correlation, SMC/ICT sweeps, AI probability) on one
  selector-driven canvas, computed from bridge bars + the tick archive.
- **Hygiene** — `check_script_hygiene.py` CI linter keeps the
  subprocess-decode / UTF-16-log mojibake classes extinct (both had shipped
  bugs here before).

## 3. What regressed / new risks this week

- **The demo data directory was wiped again** (second time this week) —
  **cause identified**: a bare `dotnet test` invocation run directly (not
  through `scripts/ci-local.ps1`, so no `TF_DATA_DIR` redirect) executed
  the DataDir-deleting teardown suites while the app was closed; the
  LiveSoakGuard only blocks *live* sessions, so the stale-journal history
  sailed past it. Fix shipped: `RealDataDirDeleteGuard` — those teardowns
  now refuse to delete the real `%APPDATA%	f\data` unless the redirect
  is in effect or `TF_TESTS_ALLOW_LIVE_DATADIR=1` is set deliberately.
  Committed evidence was intact both times; only in-app state (soak
  counters) reset.
- **Watchdog loop is failing honestly:** the scheduled watchdog keeps
  reporting `sidecar restart did not become healthy on port 53190` — correct
  behavior while nothing to attach to exists. The 2026-09-27 diagnosis went
  further: the terminal is running but **not re-authorized** (last Deriv-Demo
  login 04:19; every later restart never logged in), and `mt5.initialize()`
  answers `-10005 (IPC timeout)` to every attach attempt. A manual terminal
  login is the unblock for the whole session stack.
- **Ollama daemon was down** on 2026-09-27 morning: the scheduled analyst
  falls back to the template narrative (by design), and the alpha propose
  leg records `propose unavailable`. Restart Ollama before the next evening
  run so the narrative stays LLM-grade.

## 4. The alpha-validation dependency

The 5-trade validation bar cannot be cleared while the tick archive holds 3
ticks. The chain is: app runs → bridge feeds quotes → `TickArchive` grows →
`ticks_to_bars` clears the 50-bar floor → `backtest.py` produces trades.
Every current proposal verdict (`insufficient trades (< 5) — do not
promote`) is the correct verdict for a 3-tick tape. Nothing needs fixing in
the harness; the tape needs to exist.

## 5. Verdict — **Not ready to go live. On track, data dir reset.**

The rails are green, rehearsed, and now AI-narrated; the missing ingredient
is unchanged — settled demo trades — and this week's wipe resets the soak
counters that were slowly accumulating toward them.

Next concrete steps, in order:

1. **Restart the session stack:** start the app (it launches the pinned MT5
   terminal), confirm the sidecar's `/health` goes green, restart Ollama
   (`ollama serve`) so tonight's narrative is LLM-grade.
2. **Run demo sessions until the badge stops refusing GO LIVE** — the
   per-symbol soak pills restart from 0 after the wipe; record evidence each
   session (`soak_report.py --since <date> --record docs/soak`).
3. **After the first settled demo trades:** re-run the bankroll drill
   (#44 unblocks), then re-issue this readiness review with a verdict that
   can finally cite real settlements.
4. **Only then** review the real-account gate path (typed unlock, venue
   verdict, caps) — unchanged from the initial review.
