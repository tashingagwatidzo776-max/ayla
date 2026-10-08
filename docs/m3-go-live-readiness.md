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
   per-symbol soak pills (10 signals each, all-or-nothing across the book)
   now ACCRUE ACROSS RESTARTS (`data/fx-paper-soak.json`), so a session can
   be resumed instead of thrown away; the counters are scoped to the build
   stamp, and a new commit clears them deliberately (the bar is evidence
   about THAT engine). The dashboard's paper-soak line (and the account-bar
   pill's tooltip) states plainly whether the bar was CARRIED OVER from an
   earlier session or RESTARTED on a build change — a resumed count must
   never be mistaken for one earned since this brain started. Record
   evidence each session
   (`soak_report.py --since <date> --record docs/soak`).
3. **After the first settled demo trades:** re-run the bankroll drill
   (#44 unblocks), then re-issue this readiness review with a verdict that
   can finally cite real settlements.
4. **Only then** review the real-account gate path (typed unlock, venue
   verdict, caps) — unchanged from the initial review.

## 6. Readiness re-issue — 2026-10-05 (full gate audit)

### Gates re-audited — green

| Gate | Evidence | Status |
|---|---|---|
| CI on PR #159 (head `a94c1e3`) | run 37319980381: unit, integration, coverage-report, uia-smoke, workflow-lint, safety-audit-diff — all pass | GREEN |
| Local unit lane | 972/972 (343 Core + 629 App), `Category=Unit`, data dir redirected | GREEN |
| Guards | `check_script_hygiene`, `check_rail_traits`, `check_test_traits` — RC=0 | GREEN |
| Gate drill (real-money lane) | scheduled run 2026-10-03 success | GREEN |
| Weekly bankroll drill (#44) | scheduled run 2026-10-03 success — first run AFTER the 10-01/10-02 settlements, so the drill has been re-run post-settlements as step 3 required | GREEN |
| Weekly drills | 2026-09-30 success (next due ~10-07) | GREEN |
| Flake tracker | **was RED**: `gh issue create --label ci-flakes` failed on every scheduled run (9-26, 10-03) because the label did not exist in the repo. Repaired 2026-10-05 — created `ci-flakes` (matching sibling `ci-*` color), reran run 37123945469 → success | GREEN (fixed) |
| Monte-Carlo fire drill | 7.3% flip rate ≤ 10% stability bar (2026-09-29) | GREEN |
| Session stack | sidecar `/health` green (Deriv-Demo 32353037, terminal_connected, trade_allowed); Ollama up; terminal64 running; bridge watchdog re-enabled (state Ready) | GREEN |
| Soak rails | SOAK-2026-10-04 CLEAN — 147 entries, 0 refusals, 0 unparseable, 0 reconnects | GREEN |

### What still blocks going live

1. **The GO LIVE badge refuses: paper soak 0/40.**
   `data/fx-paper-soak.json` (scope `0.9.0+4236c93`) was cleared 2026-10-03
   (closed market / stale-feed hold) and holds zero counted symbols. The
   portfolio is 4 symbols (`FxSymbols = XAUUSDmicro,EURUSD,GBPUSD,USDJPY`),
   each needing 10 paper signals — the bar is all-or-nothing
   (`FxEngineHost.PaperSoakSignalsRequired = 10`, portfolio
   `PaperSoakComplete` = all hosts complete). Nothing accrues while the app
   is closed.
2. **The app is not running and today has no evidence.**
   `journal_20261005.jsonl` holds a single startup line (08:39Z); the
   2026-10-05 soak report is NO DATA (0 entries). Run a session through
   today's open market, then record it
   (`soak_report.py --since 2026-10-05 --record docs/soak`).
3. **Build-stamp invalidation is coming.** The ledger scope is
   `0.9.0+4236c93` (an old commit); rebuilding the app at current HEAD
   changes `VersionInfo.Stamp` and the counters restart deliberately — the
   bar must be earned on the shipping build, so accrue it AFTER the final
   build, not before.
4. **The alpha 5-trade bar is still unverified.** The dependency that made
   every proposal `insufficient trades` is gone — the tick archive now
   holds 144 files / 13 MB across all 18 symbols (vs 3 ticks at the last
   review) — but no alpha-proposal entries were journaled in October. Run
   `soak_evening.py --alpha` against the completed tape and read the new
   verdicts.
5. **Settlement provenance — CHECKED, and it FAILS: all 241 "settlements"
   are re-imports, not session trades.** Ticket-level analysis of the
   journal (2026-10-05) shows every TRADE_SETTLEMENT on 10-01/10-02 landed
   in one of exactly three same-second bursts — 83 entries at 03:42:11,
   83 at 07:23:16 (an IDENTICAL ticket set: the same window journaled
   twice), and 75 at 04:52:00 (73 of 75 tickets overlapping the previous
   window) — the signature of `FxTradeFeed`'s startup re-ingest of the
   venue's 7-day deal history, with zero live-poll journaling in between.
   The soak reports' `settlements=166` / `settlements=75` counts are
   therefore import artifacts (the exact trap the 2026-09-27 review
   flagged), and the M3 chain — session decision → TRADE_SETTLEMENT →
   scorecard — remains **unevidenced**. The 423 PAPER-EXEC decisions on
   10-01 are real session decisions; their closes never journaled live
   (they were only ever seen by a startup import). Note also: those
   settlements belong to account `MT5 201587365`, while the bridge now
   reports login 32353037 — an account switch since.
6. **Real-account gate path review** (typed unlock, venue verdict, caps) —
   correctly not started; only after items 1–5 land.

### Verdict — **Not ready to go live. Rails green, badge empty.**

Every automation gate is green (flake-tracker repaired today), the session
stack is healthy, and settlements have been observed — but the hard bar is
unchanged in kind: the GO LIVE badge is all-or-nothing at 0/40 on the
shipping build, and no session evidence exists for today. Sections 1 and 5
above are superseded in part: the trading loop's 10-01/10-02 settlements
are confirmed re-imports at ticket level (item 5), so the M3 settlement
milestone is still open.

### 2026-10-05 update — session executed after the audit

- **Session launched 15:31Z** (Release build at `a94c1e3`, brain running,
  paper mode). Evidence recorded: `SOAK-2026-10-05.md` **SOAK CLEAN — 106
  entries, 0 refusals, 0 telemetry errors** (trend table regenerated).
  Blocker 2 resolved for today.
- **Paper-soak accrual started** (blocker 1): the ledger re-scoped to the
  shipping build exactly as predicted — `0.9.0+a94c1e3` / account 32353037
  — and XAUUSDmicro cleared its bar within the hour (11 signals by 15:33Z,
  56 by 16:38Z). EURUSD/GBPUSD/USDJPY remain at 0: the day's regime veto
  reads `LowLiquidity — dead tape` on them, so the all-or-nothing bar waits
  on their first volatile session.
- **Autostart registered**: scheduled task `DongGfx app autostart`
  (AtLogOn, current user, state Ready) starts the app on logon so the bar
  accrues without a manual launch.
- **Alpha propose leg was broken and is now fixed** (blocker 4):
  `propose.py` hard-coded a 60s HTTP timeout, but a completion measures
  ~194–250s of generation plus ~180s of cold model load on this 2-core box
  (measured) — every run reported `propose unavailable or failed`. Timeout
  raised to 540s (inside soak_evening's 600s subprocess budget), with the
  measurement in the comment. Rerun at 09:53 local: **+1 new proposal,
  183 backtest lines across all 18 symbols on today's tape** — and every
  verdict is `insufficient trades (< 5) — do not promote` (best: 3 trades,
  USDCAD/USDJPY/XAGEUR/XAUEUR). The harness is honest: one session-day of
  bars cannot clear the 5-trade bar; the evening runs accumulate it.
- **Ollama**: was evicting the model on the 5-minute default keep-alive
  between runs; cold loads now fit inside the propose timeout, no daemon
  change needed.
- Still open: settlements must happen LIVE (item 5's re-import trap is now
  permanent knowledge — journal a settlement within 15 minutes of the deal
  closing before citing it), and items 1/4 need the tape to accumulate.
