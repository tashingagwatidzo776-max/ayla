#!/usr/bin/env python3
"""Tests for scripts/bounded_run.py.

Pins the pipe-EOF hazard fix the fleet's scheduled watchers now share: a
timeout must be a real wall-clock bound even when a grandchild inherits
the child's stdout handle and holds it open forever. Without the temp-file
redirect, subprocess.run(capture_output=True, timeout=N) kills the child
then blocks in a post-kill pipe wait — exactly the hang that silenced the
evening soak and that a scheduled watcher must never reproduce (it would
stop recovering the machine and never page).

The grandchild-hold test asserts on a daemon thread: a regressed
implementation FAILS instead of hanging the suite.

Run:  python scripts/test_bounded_run.py   (exit 0 = all pass)
"""
from __future__ import annotations

import os
import sys
import tempfile
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from bounded_run import run_bounded  # noqa: E402

PY = sys.executable


def test_captures_text_from_a_real_child():
    r = run_bounded(
        [PY, "-c", "import sys; print('out line'); print('err line', file=sys.stderr)"],
        30)
    assert r is not None and r.returncode == 0, r
    assert "out line" in r.stdout and "err line" in r.stderr


def test_returns_while_a_grandchild_holds_the_stream():
    """THE pipe-EOF regression. The child spawns a grandchild that outlives
    it while holding the inherited stdout handle open forever. run_bounded
    must return on time; a capture_output implementation would block here.
    Asserted on a daemon thread so a regression fails, not hangs."""
    script = (
        "import subprocess, sys\n"
        "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])\n"
        "print('child finished')\n")
    box: dict = {}

    def work():
        box["r"] = run_bounded([PY, "-c", script], 60)

    t = threading.Thread(target=work, daemon=True)
    t.start()
    t.join(timeout=20)
    assert not t.is_alive(), "run_bounded blocked waiting for the inherited stream"
    r = box.get("r")
    assert r is not None and r.returncode == 0, r
    assert "child finished" in r.stdout


def test_timeout_kills_and_returns_within_bound():
    start = time.monotonic()
    r = run_bounded([PY, "-c", "import time; time.sleep(120)"], 1)
    elapsed = time.monotonic() - start
    assert r is None, r                      # timed out: killed, not awaited
    assert elapsed < 15, f"timeout path took {elapsed:.1f}s - unbounded"


def test_missing_exe_returns_none_not_raise():
    assert run_bounded(["definitely-not-a-real-binary-xyz"], 5) is None


def test_raw_returns_bytes_for_exact_hashing():
    """raw=True must yield untouched bytes (head_hash compares them against
    a binary file read), not decoded text."""
    r = run_bounded([PY, "-c", "import sys; sys.stdout.buffer.write(b'\\xff\\xfe raw')"],
                    30, raw=True)
    assert r is not None and r.returncode == 0, r
    assert r.stdout == b"\xff\xfe raw", r.stdout


def test_env_and_cwd_are_passed_through():
    with tempfile.TemporaryDirectory() as td:
        r = run_bounded(
            [PY, "-c", "import os; print(os.environ.get('BOUNDED_VAR','unset')); print(os.getcwd())"],
            30, env=dict(os.environ, BOUNDED_VAR="seen"), cwd=td)
        assert r is not None and r.returncode == 0, r
        assert "seen" in r.stdout
        assert os.path.realpath(td) in os.path.realpath(r.stdout)


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except Exception as e:  # noqa: BLE001
            failed += 1
            print(f"FAIL {t.__name__}: {type(e).__name__}: {e}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
