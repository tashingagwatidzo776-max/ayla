#!/usr/bin/env python3
"""Hard-bounded subprocess.run: can never hang past its timeout.

The fleet's scheduled watchers (fleet digest, watcher-task-health, task
audit, MT5 watchdog) run unattended, so a hang is not a stuck session —
it is a silent watchdog that stops recovering the machine and never
pages. They all used `subprocess.run(..., capture_output=True,
timeout=N)`, which carries a Windows-only trap: on timeout it kills the
child and then calls communicate() with NO timeout, waiting for pipe EOF.
A grandchild that inherited the pipe write end (ollama serve, a Roslyn
VBCSCompiler server, conhost, a detached sidecar) keeps that handle open
forever, so the caller sits at zero CPU long past its own timeout. This
is the exact failure that killed the evening soak 12 of 18 nights
(2026-10-15, py-spy showed the blocked post-kill join).

Redirecting the child's output to temp files removes the EOF dependency:
after kill(), the wait is bounded by process death alone. This is the
same `run_bounded` that scripts/soak_evening.py grew, factored out so the
scheduled fleet can share it instead of each re-deriving the trap.

Semantics mirror subprocess.run's text mode (UTF-8 decode, errors=replace)
so callers keep getting `str` stdout/stderr:

  returns CompletedProcess (returncode, decoded stdout+stderr) on finish;
  returns None on timeout (child already killed) or OSError (never
  started). Callers that need to distinguish those treat None as
  "indeterminate", never as success.

Usage:
    from bounded_run import run_bounded
    r = run_bounded(["tool", "--flag"], 300)
    if r is None:
        ...  # timed out or never started — NOT a clean exit
"""
from __future__ import annotations

import subprocess
import tempfile


def run_bounded(cmd, timeout: int, *, env=None, cwd=None, raw=False):
    """Run `cmd` under a hard wall-clock bound; never blocks past `timeout`.

    `env` and `cwd` are passed through to the child (used by the digest to
    pin PYTHONIOENCODING for its probe children). Returns a CompletedProcess
    with decoded stdout/stderr, or None when the command did not finish
    inside `timeout` (it has been killed) or could not start at all.

    `raw=True` returns the child's stdout/stderr as untouched bytes instead
    of decoded text — for callers that hash or compare the exact bytes a
    command emitted (e.g. git blob content), where a UTF-8 round-trip would
    corrupt non-UTF-8 data.
    """
    with tempfile.TemporaryFile() as out, tempfile.TemporaryFile() as err:
        try:
            r = subprocess.run(cmd, stdout=out, stderr=err,
                               stdin=subprocess.DEVNULL, timeout=timeout,
                               env=env, cwd=cwd)
        except subprocess.TimeoutExpired:
            return None            # child already killed inside run()
        except OSError:
            return None            # never started (missing exe, bad path)
        out.seek(0)
        err.seek(0)
        data = (out.read(), err.read())
        if raw:
            stdout, stderr = data
        else:
            stdout, stderr = (b.decode("utf-8", errors="replace")
                              for b in data)
        return subprocess.CompletedProcess(cmd, r.returncode, stdout, stderr)
