#!/usr/bin/env python3
"""Behavioural tests for scripts/register-tp1-first-arm-pager.ps1.

The register script's exact shape is the contract (same text-level
convention as test_register_profit_floor_watch.py): the registered
task points at the pager through the encoding-wrapped cmd shim with
stdout/stderr captured — the silent-failure fix for a task that used
to be registered by a stray .git\\ script — and is idempotent, bounded
and single-instanced. The real registration is an explicit human act
(run the register script yourself), never a side effect of the test
suite.

Run: python scripts/test_register_tp1_first_arm_pager.py   (exit 0 = pass)
"""
from __future__ import annotations

from pathlib import Path

HERE = Path(__file__).resolve().parent
REGISTER = HERE / "register-tp1-first-arm-pager.ps1"
WATCHER = HERE / "watch_tp1_first_arm.py"

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")


def main() -> int:
    src = REGISTER.read_text(encoding="utf-8", errors="replace")
    watcher = WATCHER.read_text(encoding="utf-8", errors="replace")

    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "Unregister-ScheduledTask" in src)
    check("task name is the TP1 first-arm pager",
          "DongGfx TP1 first-arm pager" in src)
    check("registered action points at the pager script",
          "watch_tp1_first_arm.py" in src)
    check("pager exists next to the register script", WATCHER.exists())
    check("default cadence is 5 minutes", "$EveryMinutes = 5" in src)
    check("repetition interval uses the cadence parameter",
          "New-TimeSpan -Minutes $EveryMinutes" in src)
    check("task runs at logon", "AtLogOn" in src)
    check("execution time limit is bounded",
          "ExecutionTimeLimit" in src and "-Minutes 5" in src)
    check("concurrent instances are ignored", "-MultipleInstances IgnoreNew" in src)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in src and "StartWhenAvailable" in src)
    check("supports -Remove", "[switch]$Remove" in src)
    check("register script fails closed on a missing pager",
          "Watcher not found" in src)
    check("cmd wrapper sets PYTHONIOENCODING (the fleet audit's rule)",
          "set PYTHONIOENCODING=utf-8&&" in src)
    check("stdout/stderr captured to a log (Task Scheduler discards output)",
          "watch-tp1-first-arm.log" in src and "[char]62" in src)
    check("action executes via cmd.exe (stdout-bearing python, no pythonw)",
          "-Execute 'cmd.exe'" in src)
    check("pager pages the configured webhook",
          "webhook" in watcher)

    failed = [name for name, ok in results if not ok]
    print(f"{'FAIL' if failed else 'PASS'}: "
          f"{len(results) - len(failed)}/{len(results)}")
    return 1 if failed else 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
