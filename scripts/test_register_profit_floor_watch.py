#!/usr/bin/env python3
"""Behavioural tests for scripts/register-profit-floor-watch.ps1.

The register script's exact shape is the contract: the registered task
points at the watcher, is idempotent, is bounded, and runs at logon plus a
repeat interval. Text-level checks like the open-watcher suite — the real
registration is an explicit human act (run the register script yourself),
never a side effect of the test suite.

Run: python scripts/test_register_profit_floor_watch.py   (exit 0 = pass)
"""
from __future__ import annotations

from pathlib import Path

HERE = Path(__file__).resolve().parent
REGISTER = HERE / "register-profit-floor-watch.ps1"
WATCHER = HERE / "watch_profit_floor.py"

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")


def main() -> int:
    src = REGISTER.read_text(encoding="utf-8", errors="replace")
    watcher = WATCHER.read_text(encoding="utf-8", errors="replace")

    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "Unregister-ScheduledTask" in src)
    check("task name is the profit-floor watcher",
          "DongGfx profit-floor watcher" in src)
    check("registered action points at the watcher script",
          "watch_profit_floor.py" in src)
    check("default cadence is 4 minutes", "$EveryMinutes = 4" in src)
    check("repetition interval uses the cadence parameter",
          "New-TimeSpan -Minutes $EveryMinutes" in src)
    check("task runs at logon", "AtLogOn" in src)
    check("execution time limit is bounded", "ExecutionTimeLimit" in src)
    check("concurrent instances are ignored", "-MultipleInstances IgnoreNew" in src)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in src and "StartWhenAvailable" in src)
    check("supports -Remove", "[switch]$Remove" in src)
    check("register script fails closed on a missing watcher",
          "Watcher not found" in src)
    check("watcher exists next to the register script", WATCHER.exists())
    check("watcher is read-only (reads, never trades)",
          "reads, never trades" in watcher)
    check("watcher honors TF_DATA_DIR (hermetic tests possible)",
          "TF_DATA_DIR" in watcher)
    check("watcher has a --no-alert observe mode", "--no-alert" in watcher)
    check("watcher dedups alerts across restarts",
          "profit-floor-alerts.json" in watcher)

    failed = [name for name, ok in results if not ok]
    print(f"{'FAIL' if failed else 'PASS'}: "
          f"{len(results) - len(failed)}/{len(results)}")
    return 1 if failed else 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
