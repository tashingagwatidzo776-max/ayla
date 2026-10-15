# DON G FX — scheduled-task fleet inventory

Every Windows scheduled task that runs this trading system, what it does,
when it fires, where its output lands and who owns it. Regenerated from a
live dump (`Get-ScheduledTask` filtered to `^DongGfx|^tf-`); the fleet
audit (`scripts/audit_donggfx_tasks.py`, weekly Monday 08:30) is the
enforcement side of this document — this file is the map.

Two repos own tasks:

* **tf repo** (`C:\Users\DELL\Desktop\tf`) — soak pipeline, alerting
  watchers, fleet self-observation. Every task here has a versioned,
  idempotent registrar under `scripts\register-*.ps1` (re-run it to
  replace the live task; `-Remove` deletes it).
* **app repo** (`C:\Users\DELL\Desktop\DongGfx`) — the trading app and
  its keep-alives. Owned by the app repo; several logs still land in the
  shared `%APPDATA%\tf\data\logs` directory.

Fleet-wide invariants (enforced by the audit where the task is a python
task): stdout-bearing python runs behind the `cmd /c set
PYTHONIOENCODING=utf-8&&` shim with stdout/stderr captured to
`data\logs\*.log` (Task Scheduler discards console output — the Discord
403 lesson); pythonw daemons self-log and need no capture; every task is
`IgnoreNew` for concurrent instances, starts on batteries and survives
`StartWhenAvailable`. Times are local.

## tf repo tasks (14)

| Task | Cadence | Runs | Logs | Limit | Registrar |
|---|---|---|---|---|---|
| DongGfx alpha validation Monday | Weekly Mon 06:10 | `soak_evening.ps1` (alpha leg: propose → ticks → backtest over the weekday archive) | `%LOCALAPPDATA%\tf\soak_evening.log` + evidence in `docs/soak/` (left uncommitted by design) | 4 h | `scripts\register-alpha-monday.ps1` |
| tf-soak-evening | Daily 20:30 | `soak_evening.ps1` (analyst + alpha rerun) | same soak log; evidence in `docs/soak/` | 72 h | ad-hoc (no registrar yet) |
| DongGfx daily soak evidence | Daily 23:55 | `daily_soak_evidence.py` (pythonw, self-contained) | writes the evidence doc in `docs/soak/`; no separate log | 72 h | ad-hoc |
| DongGfx weekly market-open watcher | Weekly Mon 00:01 | `open-watcher.ps1 -MaxHours 16` (first decision + first settlement of the week) | `data\logs\watch-open-weekly.log` + self-log `data\logs\open-watcher.log` | 17 h | `scripts\register-open-task.ps1` |
| DongGfx profit-floor watcher | AtLogOn + every 4 min | `watch_profit_floor.py` (pages the webhook when a position breaches the profit floor) | `data\logs\watch-profit-floor.log` | 3 min | `scripts\register-profit-floor-watch.ps1` |
| DongGfx close-price watcher | AtLogOn + every 5 min | `watch_close_price.py` (pages on a NEW close row with OutcomeSource=unknown) | `data\logs\watch-close-price.log` | 3 min | `scripts\register-close-price-watch.ps1` |
| DongGfx closed-row watch | AtLogOn + every 5 min | `watch_closed_row.py` (pages when a close row lands without its payload) | `data\logs\watch-closed-row.log` | 3 min | `scripts\register-closed-row-watch.ps1` |
| DongGfx TP1 first-arm pager | AtLogOn + every 5 min | `watch_tp1_first_arm.py` (pages on first TP1 arm; TP1 gate regression exits 3) | `data\logs\watch-tp1-first-arm.log` | 5 min | `scripts\register-tp1-first-arm-pager.ps1` |
| DongGfx watcher task health | AtLogOn + hourly | `check_watcher_task_health.py` (watch-the-watcher: staleness + persistent-nonzero gate) | `data\logs\watch-task-health.log` | 10 min | `scripts\register-watcher-task-health.ps1` |
| DongGfx task-fleet audit | Weekly Mon 08:30 | `audit_donggfx_tasks.py` (encoding wrapper + git-HEAD drift audit) | `data\logs\task-fleet-audit.log` | 15 min | `scripts\register-audit-task.ps1` |
| DongGfx fleet status digest | Weekly Mon 09:00 | `fleet_status_digest.py` (audit + health verdicts as one embed; reports, never gates) | `data\logs\fleet-digest.log` | 10 min | `scripts\register-fleet-digest.ps1` |
| **DongGfx fleet red alert** | AtLogOn + every 5 min | `fleet_status_digest.py --watch` (same probes; posts ONLY on a GREEN/RED flip) | `data\logs\fleet-alert.log`; state `data\watcher\fleet-alert-state.json` | 10 min | `scripts\register-fleet-alert.ps1` |
| **DongGfx soak-exit watch** | AtLogOn + every 15 min | `soak_exit_watch.py --grace-minutes 270` (pages once per soak start with no matching exit line — a termination as it happens) | `data\logs\soak-exit-watch.log`; state `data\watcher\soak-exit-watch-state.json` | 3 min | `scripts\register-soak-exit-watch.ps1` |
| DongGfx exit cycle watch | AtLogOn + every 5 min | `exit_cycle_watch.py` (pythonw daemon: XAU position exit-cycle + recovery/brain log tail) | `data\logs\exit-cycle-watch.log` | 72 h | `scripts\register-exit-cycle-watch.ps1` |

Bold rows were added 2026-10-15 (fleet is 21 tasks total; the pre-change
count of 19 is what earlier notes reference).

## app repo tasks (7)

| Task | Cadence | Runs | Logs | Limit |
|---|---|---|---|---|
| DongGfx app autostart | AtLogOn + every 5 min | `app\DongGfx.exe` (the trading app; respawns if dead) | app's own log under the app repo | none |
| DongGfx badge watcher | AtLogOn + every 5 min | `pythonw scripts\watch_badge.py` | `<app repo>\badge-watch.log` | none |
| DongGfx brain watchdog | every 5 min | `pythonw scripts\brain_watchdog.py` | `data\logs\brain-watchdog.log` | 72 h |
| DongGfx book recovery | every 10 min | `scripts\book_recovery.ps1` (re-points the book after a venue reset) | `data\logs\book-recovery.log` | 72 h |
| DongGfx MT5 bridge watchdog | AtLogOn + every 5 min | `scripts\mt5-watchdog.ps1 -Port 53190 -Quiet` | `data\logs\mt5-watchdog.log` | 5 min |
| DongGfx deploy guard | every 2 min | `scripts\deploy-guard.ps1` (blocks a deploy while the app runs) | `data\logs\deploy.log` | 10 min |
| DongGfx twin killer | every 5 min | `scripts\kill_twins.ps1` (kills duplicate app instances) | `data\logs\twin-killer.log` | 72 h |

The MT5 watchdog can also be re-registered from this repo via
`scripts\register-mt5-watchdog.ps1` (it points at the app-repo copy).

## Exit-code semantics (what "failing" means per task)

* **Alerting watchers** (profit-floor, close-price, closed-row, TP1
  pager): 0 = clean pass, **2 = designed** (an alert paged / a verified
  save fired — never counted as failure), 3 = TP1 gate regression,
  anything else = a real failure. The health check fails a task only on
  the SAME nonzero code twice in a row (persistence), or on staleness.
* **open-watcher**: 0 = first settlement seen, 2 = deadline reached
  (designed), 3 = app not running.
* **watcher task health**: 0 healthy, 1 unhealthy, 2 indeterminate
  (broken probe — never fake green).
* **task-fleet audit**: 0 clean, 1 findings, 2 indeterminate.
* **fleet status digest**: 0 = delivered (whatever the verdict inside),
  1 = not delivered (no webhook / failed POST / both probes crashed).
* **fleet red alert**: 0 = pass completed (posted or not), 1 = a flip
  was detected and the POST failed — state is saved only after a
  confirmed post, so the next 5-minute poll retries the same flip.
* **soak-exit watch**: 0 = pass completed, 1 = an orphan start needed an
  alert and the POST failed (same retry rule).

## Alerting taxonomy: gates vs reports

* **Gates** (exit nonzero = something is wrong): ci-local, the four
  alerting watchers, watcher task health (drives nothing itself but is
  the fleet's health verdict).
* **Instant reports**: fleet red alert (any audit/health flip to RED, or
  recovery to GREEN, within one 5-minute poll), soak-exit watch (a soak
  run terminated without its exit line, within one 15-minute poll after
  the 270-minute grace window), the four alerting watchers' own webhooks.
* **Scheduled reports**: fleet status digest (weekly Monday 09:00,
  full audit + health embed, whatever the verdict).

## Termination forensics (2026-10-15)

* `Microsoft-Windows-TaskScheduler/Operational` event log is **enabled**
  — every task completion records event 201 with its return code, so
  limit-kill vs manual stop vs crash is attributable after the fact.
* `soak_evening.ps1` brackets every run with a timestamped start line
  and a timestamped exit line (with duration) — a start without its
  matching exit IS the termination marker, and the soak-exit watch now
  pages on exactly that.
* **The soak-exit watch's first live pass (2026-10-15 09:2x) found that
  12 of the 16 soak runs in the log never wrote an exit line** —
  including the 10-12 alpha kill and the 10-14 nightly run, which the
  event log confirms started (IDs 100/107/129/200) but never completed
  (no 201), while the task's `LastTaskResult=0` was a stale leftover
  from the previous night. The nightly soak has been silently dying
  early on most nights; each future occurrence now pages within one
  15-minute poll after the grace window.
* Legacy wrinkle: the pre-2026-10 wrapper wrote python output and exit
  lines through a bare `>>` (= UTF-16LE). The watcher NUL-strips
  UTF-16 regions before matching, so completed legacy runs (09-26)
  do not false-alert.
* Observed at inventory time (live facts, not verdicts): the Monday
  alpha task's last result is 267014 = `SCHED_S_TASK_TERMINATED` (the
  10-12 kill; limit since raised 2 h → 4 h and now watched); MT5 bridge
  watchdog and the TP1 first-arm pager also show a 267014 sample (pass
  hitting the 5-min execution limit — the health check's persistence
  rule decides whether that is chronic); the closed-row watch has a
  single exit-1 sample from the morning of 2026-10-15 (one sample is
  not persistent).
* Under heavy load (boot-hour contention) both alert probes' inner
  PowerShell calls can exceed their 120 s/90 s timeouts → AMBER. The
  flip alert holds the last posted state on AMBER — never fake green,
  never fake red — and resumes judging on the next clean pass.

## Retired

* `.git\register_tp1_tasks.ps1` — stray, never versioned ad-hoc
  registrar (registered watcher task health + TP1 pager without the
  capture wrapper). Superseded by `scripts\register-watcher-task-health.ps1`
  and `scripts\register-tp1-first-arm-pager.ps1`; removed 2026-10-15.
  Both live tasks already matched the versioned registrars.

## Maintenance

```powershell
# Re-register any tf-repo task (idempotent; -Remove deletes):
pwsh -File scripts\register-fleet-alert.ps1
pwsh -File scripts\register-soak-exit-watch.ps1
# …same shape for every other register-*.ps1

# Health of the fleet without a session:
python scripts\audit_donggfx_tasks.py          # drift/encoding audit
python scripts\check_watcher_task_health.py    # watcher task health
python scripts\fleet_status_digest.py --watch --no-alert   # what the alert would say
```
