# M3 go-live readiness

Status review as of 2026-09-25, after the M2 release (`v0.9.0`: MT5 login
surface, account-switch guards, updater hardening, chart trading, menu
regression suite + UIA smoke lane). M3 is the milestone this app exists for:
the FX brain leaves paper mode against the demo venue, accumulates settled
trades, and — only after that evidence exists — asks the real-money question.

## 1. Where the soak stands

Committed evidence in `docs/soak/`:

| Report | Verdict | Summary |
|---|---|---|
| `SOAK-2026-09-22.md` | **CLEAN** | 296 journal entries, **0 settlements**, 0 gate refusals, no reconnects |
| `SOAK-2026-09-21.md` | CLEAN | earlier session, same shape |
| `SOAK-2026-09-19.md` | CLEAN | first recorded evidence |

- **Freshness:** the newest report is 3 days old against the weekly drill's
  14-day `--max-age` — no drill risk yet, but the next demo session is due
  **before ~2026-10-06**. The registered `open-watcher.ps1` Windows task
  records the report automatically after the week's first Monday-open
  `BRAIN_DECISION`/settlement; the `--record` output still needs the
  `git add docs/soak && git commit` afterwards.
- **The gap that matters:** every CLEAN verdict so far carries **0 settled
  trades**. Gate silence + intact telemetry prove the *safety* plumbing, not
  the *trading* loop. M3's evidence bar is settled demo trades — wins and
  losses flowing through `TRADE_SETTLEMENT`, the scorecard, and the
  performance dashboard.

## 2. What "soak complete" means in code

The go-live refusal is implemented, not aspirational
(`FxEngineHost` / `FxPortfolioHost`):

- Per symbol: `PaperSoakComplete` ⇔ `PaperSignalsSeen ≥
  PaperSoakSignalsRequired` (default **10 paper signals per symbol engine**).
- Portfolio: `PaperSoakComplete` ⇔ **every** symbol engine has soaked —
  GO LIVE is all-or-nothing across `FxSymbols`; a refused attempt journals
  `FX_MODE` and reports `go-live refused — paper soak n/m signals` on the
  FX badge.
- Per the soak reports, sessions so far have produced decisions but not the
  10-signal-per-symbol bar — expect several more demo sessions before the
  badge stops refusing.

## 3. Pre-go-live checklist (demo first — the same list is the real gate)

Verify the machine-readable version of this checklist any time with
`python scripts/pre_go_live_check.py` (read-only; exits 0 only when every
item below passes):

1. **Safety settings fail-closed** (Settings → FX BRAIN): `Mt5MaxLots`
   (0 disables placement), `Mt5DailyLossCap`, `Mt5EquityFloor`,
   `FxPortfolioMaxLots`, `NewsBlackoutMinutes` — all bounded before start.
2. **Sidecar running** so the venue's `trade_mode` verdict reaches the
   real-money gate (`docs/mt5-bridge.md`).
3. **Paper soak complete** on every symbol (badge shows `soak n/m`), then
   the explicit GO LIVE.
4. **Webhook on** so refusals, arms and staleness reach you out-of-band;
   unlock-staleness watch (`ArmStalenessHours`) nagging when armed.
5. **Record the evidence** after each session:
   `python scripts/soak_report.py --since <date> --record docs/soak`, commit.

## 4. Open dependencies

- **Issue #44** (bankroll drill re-run) is blocked on the first *settled
  growth trade* — the demo go-live session is what unblocks it.
- **Issue #52** (gate-drill alert) is stale-open: runs 31–36 all posted
  "went green" notes; it is taken down by human review per the drift policy.
- The Monday-open watcher (`open-watcher.ps1`) is the soak-evidence
  pipeline; keep the task registered.

## 5. Verdict

**Not ready to go live — on track.** The rails are green and rehearsed; the
missing ingredient is settled-trade evidence, which only more demo soak
sessions can produce. Next concrete steps: (1) run demo sessions until the
badge stops refusing GO LIVE, recording evidence each time; (2) after the
first settled demo trades, re-run the bankroll drill (#44); (3) only then
review the real-account gate path (typed unlock, venue verdict, caps).
