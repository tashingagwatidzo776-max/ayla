#!/usr/bin/env python3
"""Soak check: are the alerting watchers' scheduled tasks actually healthy?

"Watch the watcher": a watcher can be perfectly correct in its suite
and still be failing on the machine — the scheduled task can exit 3
(TP1 gate regression), keep crashing with an arbitrary code, or stop
being triggered altogether. Every task that pages a webhook is checked
(profit-floor, close-price, closed-row, TP1 first-arm pager), each on
its own schedule, and the check fails when

  * a task is STALE — its last run is older than its staleness
    window, though its trigger repeats every few minutes (the pass
    itself takes <1 min, so Ready-between-triggers is normal;
    staleness is not), or
  * a task's completions show a PERSISTENT FAILURE code — the same
    failure-class code (nonzero and not exit 2) on the two most recent
    completions FOR THAT TASK. Exit 2 is a DESIGNED outcome (an alert
    paged / a verified save fired) and never counts as a failure, not
    even twice in a row. A single failure sample is not persistent
    either.

Completion history comes from the TaskScheduler operational event log
(event 201) when that log is enabled; otherwise — the common case, the
log is off by default — the check keeps its own history in
data/watcher/task-health-history.json (one sample per run per task,
deduped by LastRunTime, so consecutive runs that saw the same
completion count once; legacy single-task samples count as the first
watcher).

The current completion (LastTaskResult/LastRunTime) is always known
from the task itself, so a first-ever run sees exactly one sample —
not enough to judge persistence (healthy, single-sample rule).
Indeterminate (exit 2) is reserved for a BROKEN PROBE: never green,
never a false alarm. On a machine without the tasks (CI) the probe
raises and main exits 2 — the gate skips.
Exit 0 healthy, 1 unhealthy.

Journal-only by construction: reads task state and its own history
file, never touches the book.

Usage:
  python scripts/check_watcher_task_health.py            # one check
  python scripts/check_watcher_task_health.py --json     # machine summary
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone

# (task name, trigger cadence in minutes, staleness window in minutes)
WATCHERS: tuple[tuple[str, int, int], ...] = (
    ("DongGfx profit-floor watcher", 4, 10),
    ("DongGfx close-price watcher", 5, 10),
    ("DongGfx closed-row watch", 5, 10),
    ("DongGfx TP1 first-arm pager", 5, 10),
)
TASK = WATCHERS[0][0]             # legacy alias: the first watched task
STALE_MINUTES = WATCHERS[0][2]    # legacy alias: its staleness window
DESIGNED_EXIT = 2        # an alert paged / verified save — never a failure
HISTORY_MAX = 20          # samples kept per task in the history file


def _ps_history(task: str) -> str:
    """Event-201 return codes for the two most recent completions of
    `task`, newest first, as JSON. Empty when the log is disabled (the
    machine default) — the caller falls back to its own history file.
    NOTE: the JSON must go through -InputObject @(...): a bare
    `... | ConvertTo-Json` EXITS 1 when the pipeline is empty (the
    disabled-log case) instead of printing []."""
    return (
        "ConvertTo-Json -InputObject @($e = Get-WinEvent -FilterHashtable "
        "@{LogName='Microsoft-Windows-TaskScheduler/Operational';"
        "Id=201} -MaxEvents 400 -ErrorAction SilentlyContinue | "
        f"Where-Object {{ $_.Message -like '*{task}*' }} | "
        "Select-Object -First 2 TimeCreated, Message) -Compress"
    )


def _ps(command: str) -> str:
    out = _run_ps(command)
    if out.returncode != 0:
        raise RuntimeError(f"powershell failed rc={out.returncode}: "
                           f"{out.stderr.strip()[:200]}")
    return out.stdout.strip()


def _run_ps(command: str):
    import subprocess
    return subprocess.run(
        ["powershell", "-NoProfile", "-Command", command],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=90, check=False)


def task_info(task: str = TASK) -> tuple[str, int]:
    """(LastRunTime string, LastTaskResult int) from the task itself."""
    raw = _ps("$i = Get-ScheduledTaskInfo -TaskName "
              f"'{task}'; \"$($i.LastRunTime.ToString('o'))|$($i.LastTaskResult)\"")
    ts, code = raw.strip().split("|")
    return ts, int(code)


def event_log_results(task: str = TASK) -> list[int]:
    """Return codes of the two most recent completions of `task`,
    newest first, from the event log — empty when the log is disabled
    (not an error)."""
    raw = _ps(_ps_history(task))
    if not raw or raw == "null":
        return []
    events = json.loads(raw)
    if isinstance(events, dict):   # a single event still comes back flat
        events = [events]
    out: list[int] = []
    for ev in events:
        try:
            out.append(int(ev["Properties"][0]))
        except (KeyError, TypeError, ValueError, IndexError):
            continue
    return out


def history_path() -> str:
    """The app's data dir (TF_DATA_DIR override, else %APPDATA%), same
    convention watch_profit_floor.py honors — the check's own history
    lives beside the watcher's alert-dedup state."""
    override = os.environ.get("TF_DATA_DIR")
    root = override if override else os.path.expandvars(r"%APPDATA%\tf\data")
    return os.path.join(root, "watcher", "task-health-history.json")


def load_history(path: str) -> list[dict]:
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        return data if isinstance(data, list) else []
    except (OSError, ValueError):
        return []


def save_history(path: str, samples: list[dict]) -> None:
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(samples, fh)
    except OSError:
        pass   # best-effort: a lost history means one indeterminate check


def completion_history(last_run: str, last_code: int,
                       task: str = TASK) -> tuple[list[int], str]:
    """Codes of the two most recent DISTINCT completions of `task`,
    newest first, with the source used. Event log first; otherwise
    record this run's sample (deduped by LastRunTime) into the history
    file — samples are scoped per task, and samples written before the
    multi-watcher extension (no task field) count as the first
    watcher's."""
    codes = event_log_results(task)
    if codes:
        return codes[:2], "event-log"

    path = history_path()
    samples = [s for s in load_history(path) if isinstance(s, dict)]
    own = [s for s in samples
           if s.get("task", TASK) == task and s.get("run") != last_run]
    own.append({"ts": datetime.now(timezone.utc).isoformat(),
                "run": last_run, "code": last_code, "task": task})
    own.sort(key=lambda s: str(s.get("run", "")))
    own = own[-HISTORY_MAX:]
    others = [s for s in samples if s.get("task", TASK) != task]
    save_history(path, others + own)
    return [s["code"] for s in own[-2:]], "history-file"


def minutes_since(iso_ts: str) -> float:
    try:
        then = datetime.fromisoformat(iso_ts.replace("Z", "+00:00"))
    except ValueError:
        return 1e9   # unparseable = stale
    if then.tzinfo is None:
        then = then.replace(tzinfo=timezone.utc)
    return (datetime.now(timezone.utc) - then).total_seconds() / 60.0


def check() -> tuple[bool, list[str]]:
    """(healthy, findings) across every watched task."""
    findings: list[str] = []
    for task, cadence, stale in WATCHERS:
        last_run, last_code = task_info(task)
        age = minutes_since(last_run)
        if age > stale:
            findings.append(
                f"task '{task}' last ran {age:.0f} min ago "
                f"(trigger repeats every {cadence} min) — the schedule "
                f"is not firing")
            continue   # a schedule that is not firing has no verdict to judge
        codes, source = completion_history(last_run, last_code, task)

        def failure(c: int) -> bool:
            return c != 0 and c != DESIGNED_EXIT

        if (len(codes) >= 2 and failure(codes[0]) and codes[0] == codes[1]):
            kind = ("TP1 gate regression firing (exit 3)" if codes[0] == 3
                    else f"pass failing (exit {codes[0]})")
            findings.append(
                f"{task}: last two completions both exited {codes[0]} "
                f"({source}) — persistent nonzero: {kind}")
    return (not findings), findings


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", action="store_true", help="machine summary")
    args = ap.parse_args()
    try:
        healthy, findings = check()
    except Exception as exc:  # noqa: BLE001 — a broken probe is not green
        print(f"watcher-task-health: INDETERMINATE ({exc.__class__.__name__}: {exc})")
        sys.exit(2)
    if args.json:
        print(json.dumps({"tasks": [t for t, _, _ in WATCHERS],
                          "healthy": healthy,
                          "findings": findings}, indent=2))
        # --json is a machine summary: exit quietly with the verdict so
        # callers (the weekly fleet digest) get the code AND the JSON.
        sys.exit(0 if healthy else 1)
    if healthy:
        print(f"watcher-task-health: OK — all {len(WATCHERS)} watcher tasks "
              f"recent, no persistent nonzero (stale or 2x same nonzero "
              f"fails; exit {DESIGNED_EXIT} is designed)")
        sys.exit(0)
    for f in findings:
        print(f"watcher-task-health: FAIL — {f}")
    sys.exit(1)


if __name__ == "__main__":
    main()
