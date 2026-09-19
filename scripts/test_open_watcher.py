#!/usr/bin/env python3
"""Behavioural tests for scripts/register-open-task.ps1 and open-watcher.ps1.

These scripts gate the Monday-open verification, so their composition must be
exact: the registered task points at the watcher with sane arguments, and the
watcher's parsing/exit semantics hold (real journal → toasts + exit 0; quiet
journal → timeout exit 2; missing/unreadable journal → never crash).

Pester isn't available on this box, so the tests drive the real scripts
end-to-end: the task script is parsed and its trigger/action composition is
asserted statically, and the watcher is executed for real (with toast
suppression and a synthetic journal) — milliseconds per case.

Run: python scripts/test_open_watcher.py   (exit 0 = all pass)
"""
import subprocess
import sys
import tempfile
import os
import json
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).parent
WATCHER = HERE / "open-watcher.ps1"
REGISTER = HERE / "register-open-task.ps1"

PASS = 0
FAIL = 0
FAILURES = []


def check(name, cond, detail=""):
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok    {name}")
    else:
        FAIL += 1
        FAILURES.append((name, detail))
        print(f"  FAIL  {name}  {detail}")


def run_watcher(args, journal_lines, journal_path=None):
    if journal_path is None:
        with tempfile.NamedTemporaryFile(
            "w", suffix=".jsonl", delete=False, dir=tempfile.gettempdir()
        ) as f:
            for line in journal_lines:
                f.write(json.dumps(line) + "\n")
            journal_path = f.name
    env = dict(os.environ, DG_WATCHER_NOTOAST="1")
    try:
        # -ToastOnly skips the post-settlement follow-up (soak report +
        # ci-watch's full local gate) — minutes of work no test needs.
        proc = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(WATCHER), *args, "-ToastOnly",
             "-JournalPath", journal_path],
            capture_output=True, text=True, timeout=60, env=env,
        )
        return proc
    finally:
        if os.path.exists(journal_path):
            os.unlink(journal_path)


def journal_entry(category, minutes_ago=1, details="{}"):
    ts = (datetime.now(timezone.utc) - timedelta(minutes=minutes_ago)).strftime(
        "%Y-%m-%dT%H:%M:%S.%fZ"
    )
    return {"Timestamp": ts, "AccountId": "11111111-1111-1111-1111-111111111111",
            "Category": category, "Details": details}


def test_registered_task_points_at_watcher():
    src = REGISTER.read_text(encoding="utf-8")
    check("register script references open-watcher.ps1", "open-watcher.ps1" in src)
    check("register script uses a Monday weekly trigger",
          re.search(r"-Weekly\s+-DaysOfWeek\s+Monday", src) is not None)
    check("register script passes -MaxHours 16 to the watcher", "-MaxHours 16" in src)
    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "Unregister-ScheduledTask" in src)
    check("task runs only when logged on (toast needs a desktop)",
          "-LogonType Interactive" in src)


def test_watcher_static_contract():
    src = WATCHER.read_text(encoding="utf-8")
    check("watcher recognizes BRAIN_DECISION", "BRAIN_DECISION" in src)
    check("watcher recognizes TRADE_SETTLEMENT", "TRADE_SETTLEMENT" in src)
    check("watcher records soak evidence", "soak_report.py" in src)
    check("watcher verifies the local tree", "ci-watch.ps1" in src)
    check("watcher honors the no-toast env (headless tests)",
          "DG_WATCHER_NOTOAST" in src)


def test_real_decision_and_settlement_toasts_and_exits_zero():
    proc = run_watcher(
        ["-AssumeOpen", "-AssumeApp", "-PollSeconds", "1", "-MaxHours", "0.02"],
        [journal_entry("BRAIN_DECISION", 2, '{"Symbol":"1HZ100V","Direction":"Rise"}'),
         journal_entry("TRADE_SETTLEMENT", 1, '{"Symbol":"1HZ100V","Pnl":1.2,"Won":true}')],
    )
    check("settlement journal → exit 0", proc.returncode == 0,
          f"rc={proc.returncode} out={proc.stdout[-300:]} err={proc.stderr[-300:]}")


def test_quiet_journal_times_out_with_exit_2():
    # 0.001h ≈ 3.6s: the deadline path must be reachable inside the test
    # timeout, not after minutes of waiting.
    proc = run_watcher(
        ["-AssumeOpen", "-AssumeApp", "-PollSeconds", "1", "-MaxHours", "0.001"],
        [journal_entry("GROWTH_STATE", 2, '"started"')],
    )
    check("quiet journal → exit 2", proc.returncode == 2,
          f"rc={proc.returncode} out={proc.stdout[-300:]}")


def test_missing_journal_never_crashes():
    proc = run_watcher(
        ["-AssumeOpen", "-AssumeApp", "-PollSeconds", "1", "-MaxHours", "0.001"],
        [],
        journal_path=os.path.join(tempfile.gettempdir(), "no-such-journal.jsonl"),
    )
    # Missing file is an expected state — the watcher must exit cleanly (2),
    # never crash (1) or throw a terminating error.
    check("missing journal → clean exit 2", proc.returncode == 2,
          f"rc={proc.returncode} err={proc.stderr[-300:]}")


def test_garbage_journal_lines_are_tolerated():
    proc = run_watcher(
        ["-AssumeOpen", "-AssumeApp", "-PollSeconds", "1", "-MaxHours", "0.02"],
        [journal_entry("BRAIN_DECISION", 2, '{"Symbol":"1HZ100V"}'),
         {"Timestamp": "not-a-date", "AccountId": "", "Category": "TRADE_SETTLEMENT", "Details": "{bad json"},
         {"Timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")},
         "this line is not even json",
         journal_entry("TRADE_SETTLEMENT", 1, '{"Symbol":"1HZ100V","Pnl":-0.5,"Won":false}')],
    )
    check("garbage lines tolerated → exit 0", proc.returncode == 0,
          f"rc={proc.returncode} err={proc.stderr[-300:]}")


def main():
    # The console may be cp1252; keep the arrows and other unicode safe.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass
    tests = [
        test_registered_task_points_at_watcher,
        test_watcher_static_contract,
        test_real_decision_and_settlement_toasts_and_exits_zero,
        test_quiet_journal_times_out_with_exit_2,
        test_missing_journal_never_crashes,
        test_garbage_journal_lines_are_tolerated,
    ]
    print("open-watcher + register-open-task tests")
    for t in tests:
        t()
    print(f"\n{PASS} passed, {FAIL} failed")
    if FAILURES:
        print("\nFailures:")
        for name, detail in FAILURES:
            print(f"  - {name}: {detail}")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
