#!/usr/bin/env python3
"""Behavioural tests for scripts/register-audit-task.ps1.

The register script's exact shape is the contract (same text-level
convention as test_register_profit_floor_watch.py): the registered task
points at the audit script through the encoding-wrapped cmd shim the
audit itself demands, is idempotent, is bounded, and runs weekly. The
real registration is an explicit human act (run the register script
yourself), never a side effect of the test suite.

Run: python scripts/test_register_audit_task.py   (exit 0 = pass)
"""
from __future__ import annotations

from pathlib import Path

HERE = Path(__file__).resolve().parent
REGISTER = HERE / "register-audit-task.ps1"
AUDIT = HERE / "audit_donggfx_tasks.py"

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")


def main() -> int:
    src = REGISTER.read_text(encoding="utf-8", errors="replace")
    audit = AUDIT.read_text(encoding="utf-8", errors="replace")

    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "-Force" in src)
    check("supports -Remove", "[switch]$Remove" in src
          and "Unregister-ScheduledTask" in src)
    check("task name is the task-fleet audit",
          "DongGfx task-fleet audit" in src)
    check("registered action points at the audit script",
          "audit_donggfx_tasks.py" in src)
    check("register script fails closed on a missing audit script",
          "Audit script not found" in src)
    check("action uses the cmd encoding wrapper (the audit's own rule)",
          "set PYTHONIOENCODING=utf-8&&" in src)
    check("action executes via cmd.exe with python behind it (no pythonw)",
          "-Execute 'cmd.exe'" in src.replace('"', "'"))
    check("default trigger is weekly", "-Weekly" in src)
    check("day and time are parameters", "$DayOfWeek" in src and "$AtLocal" in src)
    check("execution time limit is bounded", "ExecutionTimeLimit" in src)
    check("concurrent instances are ignored", "-MultipleInstances IgnoreNew" in src)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in src and "StartWhenAvailable" in src)
    check("audit script exists next to the register script", AUDIT.exists())
    check("audit is read-only (never edits tasks)",
          "never edits the tasks" in audit)
    check("audit records its verdict to the history file",
          "task-audit-history.json" in audit)
    check("audit honors TF_DATA_DIR (hermetic history in tests)",
          "TF_DATA_DIR" in audit)
    check("audit exits 1 on findings (scheduler-visible drift)",
          "sys.exit(1 if" in audit)

    failed = [name for name, ok in results if not ok]
    print(f"{'FAIL' if failed else 'PASS'}: "
          f"{len(results) - len(failed)}/{len(results)}")
    return 1 if failed else 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
