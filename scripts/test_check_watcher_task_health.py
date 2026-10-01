#!/usr/bin/env python3
"""Tests for scripts/check_watcher_task_health.py.

The verdict logic is exercised against stubbed PowerShell answers and a
temp TF_DATA_DIR history file — no real scheduled task, no event log, no
flaky fixture. Run:
python scripts/test_check_watcher_task_health.py   (exit 0 = all pass)
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location(
    "check_watcher_task_health", HERE / "check_watcher_task_health.py")
mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mod)


def iso(minutes_ago: float) -> str:
    return (datetime.now(timezone.utc)
            - timedelta(minutes=minutes_ago)).isoformat()


class Stub:
    """Patches the module's PowerShell probes and points TF_DATA_DIR at a
    temp dir so the history file never touches the real data dir.

    The CURRENT completion is (last_run, last_code) — exactly what
    Get-ScheduledTaskInfo answers. `seed()` writes OLDER completions into
    the history file as offsets before that run, so fixtures can never
    double-book the current completion (the bug that failed three tests
    on the first draft)."""

    def __init__(self, last_run_minutes_ago=2.0, last_code=0,
                 event_log_codes=None):
        self.last_run = iso(last_run_minutes_ago)
        self.last_code = last_code
        ev_json = json.dumps([
            {"Properties": [c]} for c in (event_log_codes or [])])

        def fake_ps(command: str) -> str:
            if "Get-ScheduledTaskInfo" in command:
                return f"{self.last_run}|{self.last_code}"
            return ev_json
        mod._ps = fake_ps
        self._tmp = tempfile.TemporaryDirectory()
        self._old = os.environ.get("TF_DATA_DIR")
        os.environ["TF_DATA_DIR"] = self._tmp.name

    def seed(self, older: list[tuple[float, int]]) -> None:
        """(minutes_before_last_run, code) pairs, oldest first."""
        base = datetime.fromisoformat(self.last_run)
        samples = [{"run": (base - timedelta(minutes=m)).isoformat(),
                    "code": c} for m, c in older]
        path = mod.history_path()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(samples, fh)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        if self._old is None:
            os.environ.pop("TF_DATA_DIR", None)
        else:
            os.environ["TF_DATA_DIR"] = self._old
        self._tmp.cleanup()


def test_first_ever_run_is_single_sample_healthy():
    """No history file yet: the current completion is the only sample —
    not enough to judge persistence (single-sample rule), so healthy."""
    with Stub(last_code=0):
        healthy, findings = mod.check()
    assert healthy, findings


def test_healthy_task_zero_exit():
    with Stub(last_code=0) as s:
        s.seed([(8, 0)])
        healthy, findings = mod.check()
    assert healthy, findings
    assert findings == []


def test_stale_task_fails_even_with_zero_exits():
    with Stub(last_run_minutes_ago=25, last_code=0) as s:
        s.seed([(30, 0)])
        healthy, findings = mod.check()
    assert not healthy
    assert any("not firing" in f for f in findings)


def test_unparseable_lastrun_counts_as_stale():
    with Stub() as s:
        mod._ps = lambda command: "not-a-date|0"
        healthy, findings = mod.check()
    assert not healthy


def test_persistent_exit3_is_gate_regression():
    """Two DISTINCT completions (an older one in the file, the current one
    from the task) both exiting 3: the gate regression is firing."""
    with Stub(last_code=3) as s:
        s.seed([(9, 3)])
        healthy, findings = mod.check()
    assert not healthy
    assert any("exit 3" in f and "gate regression" in f for f in findings)


def test_persistent_crash_code_fails():
    code = -1073741510
    with Stub(last_code=code) as s:
        s.seed([(9, code)])
        healthy, findings = mod.check()
    assert not healthy
    assert any("persistent nonzero" in f for f in findings)


def test_exit2_twice_is_two_saves_never_a_failure():
    """Exit 2 is a DESIGNED outcome (a verified save fired): twice in a
    row is good news, not 'persistent nonzero'."""
    with Stub(last_code=2) as s:
        s.seed([(9, 2)])
        healthy, findings = mod.check()
    assert healthy, findings


def test_single_failure_sample_is_not_persistent():
    """One failing completion (the current one, no older sample) is a
    fluke — the next pass fixes it or the next check escalates."""
    with Stub(last_code=3):
        healthy, _ = mod.check()
    assert healthy


def test_failure_over_zero_recovers():
    """Older 0, current 3: one failure after a clean pass — healthy."""
    with Stub(last_code=3) as s:
        s.seed([(9, 0)])
        healthy, _ = mod.check()
    assert healthy


def test_event_log_preferred_over_history_file():
    """The history file says 3,3 but the enabled event log says 0,0 — the
    event log wins (it is ground truth, the file is a fallback)."""
    with Stub(last_code=0, event_log_codes=[0, 0]) as s:
        s.seed([(9, 3)])
        codes, source = mod.completion_history(iso(1), 0)
    assert source == "event-log"
    assert codes == [0, 0]


def test_history_dedups_same_lastrun():
    """Two checks that saw the same completion (same LastRunTime) record
    one sample — persistence needs two DISTINCT completions."""
    run = iso(2)
    with Stub(last_code=0):
        mod.completion_history(run, 0)
        mod.completion_history(run, 0)
        with open(mod.history_path(), encoding="utf-8") as fh:
            samples = json.load(fh)
    assert len(samples) == 1, samples


def test_history_file_corrupt_is_empty_not_crash():
    with Stub(last_code=0) as s:
        path = mod.history_path()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("{not json")
        codes, source = mod.completion_history(iso(1), 0)
    assert source == "history-file"
    assert codes == [0]


def test_probe_failure_maps_to_exit2_not_false_green():
    """main() must exit 2 (indeterminate) when the PowerShell probe dies —
    never 0 (false green), never 1 (false alarm)."""
    def boom(command: str) -> str:
        raise RuntimeError("powershell exploded")
    mod._ps = boom
    buf = io.StringIO()
    code = None
    try:
        with contextlib.redirect_stdout(buf):
            mod.main()
    except SystemExit as ex:
        code = ex.code
    assert code == 2, (code, buf.getvalue())


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"  ok  {t.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"FAIL  {t.__name__}: {exc}")
        except Exception as exc:  # noqa: BLE001
            failed += 1
            print(f"ERROR {t.__name__}: {exc.__class__.__name__}: {exc}")
    print(f"{'FAIL' if failed else 'PASS'}: {len(tests) - failed}/{len(tests)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
