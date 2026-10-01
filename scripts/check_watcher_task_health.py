#!/usr/bin/env python3
"""Soak check: is the profit-floor watcher's scheduled task actually healthy?

"Watch the watcher": the watcher can be perfectly correct in its suite and
still be failing on the machine — the scheduled task can exit 3 (TP1 gate
regression), keep crashing with an arbitrary code, or stop being triggered
altogether. This check fails when

  * the task is STALE — its last run is older than 10 minutes, though the
    trigger repeats every 4 minutes (the pass itself takes <1 min, so
    Ready-between-triggers is normal; staleness is not), or
  * the task's completions show a PERSISTENT FAILURE code — the same
    failure-class code (nonzero and not exit 2) on the two most recent
    completions. Exit 2 is a DESIGNED outcome (a verified save fired) and
    never counts as a failure, not even twice in a row. A single failure
    sample is not persistent either.

Completion history comes from the TaskScheduler operational event log
(event 201) when that log is enabled; otherwise — the common case, the
log is off by default — the check keeps its own history in
data/watcher/task-health-history.json (one sample per run, deduped by
LastRunTime, so consecutive runs that saw the same completion count once).

The current completion (LastTaskResult/LastRunTime) is always known from
the task itself, so a first-ever run sees exactly one sample — not enough
to judge persistence (healthy, single-sample rule). Indeterminate (exit
2) is reserved for a BROKEN PROBE: never green, never a false alarm.
Exit 0 healthy, 1 unhealthy.

Journal-only by construction: reads task state and its own history file,
never touches the book.

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

TASK = "DongGfx profit-floor watcher"
STALE_MINUTES = 10          # trigger repeats every 4 min; <1 min pass
DESIGNED_EXIT = 2           # a verified save fired — never a failure
HISTORY_MAX = 20

# Event 201 = "action completed with return code" in the TaskScheduler
# operational log; the return code is event property [0]. Empty when the
# log is disabled (the machine default) — the caller falls back to its
# own history file. NOTE: the JSON must go through -InputObject @(...):
# a bare `... | ConvertTo-Json` EXITS 1 when the pipeline is empty (the
# disabled-log case) instead of printing [].
_PS_HISTORY = (
    "ConvertTo-Json -InputObject @($e = Get-WinEvent -FilterHashtable "
    "@{LogName='Microsoft-Windows-TaskScheduler/Operational';"
    "Id=201} -MaxEvents 400 -ErrorAction SilentlyContinue | "
    f"Where-Object {{ $_.Message -like '*{TASK}*' }} | "
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


def task_info() -> tuple[str, int]:
    """(LastRunTime string, LastTaskResult int) from the task itself."""
    raw = _ps("$i = Get-ScheduledTaskInfo -TaskName "
              f"'{TASK}'; \"$($i.LastRunTime.ToString('o'))|$($i.LastTaskResult)\"")
    ts, code = raw.strip().split("|")
    return ts, int(code)


def event_log_results() -> list[int]:
    """Return codes of the two most recent completions, newest first, from
    the event log — empty when the log is disabled (not an error)."""
    raw = _ps(_PS_HISTORY)
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
            json.dump(samples[-HISTORY_MAX:], fh)
    except OSError:
        pass   # best-effort: a lost history means one indeterminate check


def completion_history(last_run: str, last_code: int) -> tuple[list[int], str]:
    """Codes of the two most recent DISTINCT completions, newest first,
    with the source used. Event log first; otherwise record this run's
    sample (deduped by LastRunTime) into the history file."""
    codes = event_log_results()
    if codes:
        return codes[:2], "event-log"

    path = history_path()
    samples = [s for s in load_history(path)
               if isinstance(s, dict) and s.get("run") != last_run]
    samples.append({"ts": datetime.now(timezone.utc).isoformat(),
                    "run": last_run, "code": last_code})
    samples.sort(key=lambda s: str(s.get("run", "")))
    save_history(path, samples[-HISTORY_MAX:])
    return [s["code"] for s in samples[-2:]], "history-file"


def minutes_since(iso_ts: str) -> float:
    try:
        then = datetime.fromisoformat(iso_ts.replace("Z", "+00:00"))
    except ValueError:
        return 1e9   # unparseable = stale
    if then.tzinfo is None:
        then = then.replace(tzinfo=timezone.utc)
    return (datetime.now(timezone.utc) - then).total_seconds() / 60.0


def check() -> tuple[bool, list[str]]:
    """(healthy, findings)."""
    last_run, last_code = task_info()
    age = minutes_since(last_run)
    if age > STALE_MINUTES:
        return False, [f"task '{TASK}' last ran {age:.0f} min ago "
                       f"(trigger repeats every 4 min) — the schedule is not firing"]

    codes, source = completion_history(last_run, last_code)

    def failure(c: int) -> bool:
        return c != 0 and c != DESIGNED_EXIT

    if (len(codes) >= 2 and failure(codes[0]) and codes[0] == codes[1]):
        kind = ("TP1 gate regression firing (exit 3)" if codes[0] == 3
                else f"pass failing (exit {codes[0]})")
        return False, [f"last two completions both exited {codes[0]} "
                       f"({source}) — persistent nonzero: {kind}"]
    return True, []


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", action="store_true", help="machine summary")
    args = ap.parse_args()
    try:
        healthy, findings = check()
        codes, source = completion_history(*task_info()) if healthy else ([], "")
    except Exception as exc:  # noqa: BLE001 — a broken probe is not green
        print(f"watcher-task-health: INDETERMINATE ({exc.__class__.__name__}: {exc})")
        sys.exit(2)
    if args.json:
        print(json.dumps({"task": TASK, "healthy": healthy,
                          "findings": findings}, indent=2))
    if healthy:
        print(f"watcher-task-health: OK — '{TASK}' ran recently "
              f"(history via {source}: {codes})")
        sys.exit(0)
    for f in findings:
        print(f"watcher-task-health: FAIL — {f}")
    sys.exit(1)


if __name__ == "__main__":
    main()
