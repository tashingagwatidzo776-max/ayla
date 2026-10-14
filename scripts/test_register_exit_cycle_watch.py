#!/usr/bin/env python3
"""Behavioural tests for scripts/register-exit-cycle-watch.ps1.

The register script's exact shape is the contract (same text-level
convention as test_register_profit_floor_watch.py): the registered
task points at the repo copy of the daemon — the fix for the fleet
audit's "python task with no repo script path" finding — runs it
stdout-less through pythonw (it self-logs), and is idempotent,
bounded and single-instanced. The real registration is an explicit
human act (run the register script yourself), never a side effect of
the test suite.

Run: python scripts/test_register_exit_cycle_watch.py   (exit 0 = pass)
"""
from __future__ import annotations

from pathlib import Path

HERE = Path(__file__).resolve().parent
REGISTER = HERE / "register-exit-cycle-watch.ps1"
WATCHER = HERE / "exit_cycle_watch.py"

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")


def main() -> int:
    src = REGISTER.read_text(encoding="utf-8", errors="replace")
    watcher = WATCHER.read_text(encoding="utf-8", errors="replace")

    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "Unregister-ScheduledTask" in src)
    check("task name is the exit-cycle watch",
          "DongGfx exit cycle watch" in src)
    check("registered action points at the repo copy of the daemon",
          "exit_cycle_watch.py" in src)
    check("daemon lives in the repo (the fleet audit's finding)",
          WATCHER.exists())
    check("runs through pythonw (self-logging daemon, no stdout)",
          "pythonw" in src and "-Execute $pythonw" in src)
    check("default cadence is 5 minutes", "$EveryMinutes = 5" in src)
    check("repetition interval uses the cadence parameter",
          "New-TimeSpan -Minutes $EveryMinutes" in src)
    check("execution time limit matches the original 72 h cap",
          "New-TimeSpan -Hours 72" in src)
    check("task runs at logon", "AtLogOn" in src)
    check("concurrent instances are ignored", "-MultipleInstances IgnoreNew" in src)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in src and "StartWhenAvailable" in src)
    check("supports -Remove", "[switch]$Remove" in src)
    check("register script fails closed on a missing daemon",
          "Watcher not found" in src)
    check("daemon self-logs to exit-cycle-watch.log",
          "exit-cycle-watch.log" in watcher)
    check("daemon keeps a single-instance PID lock",
          "exit-cycle-watch.pid" in watcher)
    check("daemon reads the sidecar with a bounded timeout",
          "timeout" in watcher)

    failed = [name for name, ok in results if not ok]
    print(f"{'FAIL' if failed else 'PASS'}: "
          f"{len(results) - len(failed)}/{len(results)}")
    return 1 if failed else 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
