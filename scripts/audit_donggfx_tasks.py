#!/usr/bin/env python3
"""Audit every DongGfx scheduled task for encoding wrappers and drift.

One pass over the task fleet answers three questions per task:

  * ENCODING — does the action wrap python.exe in the
    `cmd /c set PYTHONIOENCODING=utf-8&&` shim (the cp1252-scheduled-
    stdout crash class, PR #153)? pythonw.exe has no stdout at all, so
    the shim is N/A there; bare python.exe is a FINDING.
  * DRIFT — does the referenced repo script still match git HEAD? (A
    hand-edited task script silently changes what the scheduler runs.)
  * EXISTENCE — is the referenced script even on disk?

Cross-task duplicates of the same script are reported as notes (the
evening drill is registered twice by design/history), not findings.

Exit 0 clean, 1 findings. Read-only: never edits the tasks — findings
are printed with the exact fix command for human approval.

Usage:
  python scripts/audit_donggfx_tasks.py            # table + verdict
  python scripts/audit_donggfx_tasks.py --fix-dry  # print fix commands
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WRAPPER = "set PYTHONIOENCODING=utf-8&&"
# Whitespace-excluded (not quote-excluded): "pythonw.exe C:\...py" blobs
# must split into two paths, or the pythonw prefix glues onto the script
# and produces a phantom 'missing on disk' finding (found on first run).
SCRIPT_RE = re.compile(r"([A-Za-z]:\\[^\s\"]*?scripts\\[A-Za-z0-9_.\-]+\.py)")


def list_tasks() -> list[dict]:
    """(name, execute, arguments) for every DongGfx/tf task, via PS."""
    ps = ("Get-ScheduledTask | Where-Object { $_.TaskName -match "
          "'^DongGfx|^tf-' } | ForEach-Object { $t = $_; $t.Actions | "
          "ForEach-Object { ConvertTo-Json -InputObject @{ Name = $t.TaskName; "
          "Execute = $_.Execute; Arguments = $_.Arguments } -Compress } }")
    out = _ps(ps)
    tasks = []
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        if d.get("Name"):
            tasks.append(d)
    return tasks


def _ps(command: str) -> str:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command", command],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=120, check=False)
    if out.returncode != 0:
        raise RuntimeError(f"powershell failed rc={out.returncode}")
    return out.stdout


def file_hash(path: str) -> str:
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read().replace(b"\r\n", b"\n")).hexdigest()


def head_hash(rel: str) -> str | None:
    """Newline-normalized sha256 of the file at git HEAD (None if not
    tracked)."""
    out = subprocess.run(
        ["git", "-C", REPO, "show", f"HEAD:{rel.replace(os.sep, '/')}"],
        capture_output=True, timeout=30, check=False)
    if out.returncode != 0:
        return None
    return hashlib.sha256(out.stdout.replace(b"\r\n", b"\n")).hexdigest()


def audit_task(task: dict) -> dict:
    name = task.get("Name", "?")
    execute = (task.get("Execute") or "").strip('"')
    args = task.get("Arguments") or ""
    blob = f"{execute} {args}"
    verdict: dict = {"name": name, "execute": execute, "args": args,
                     "findings": [], "notes": []}

    script_match = SCRIPT_RE.search(blob)
    is_python = re.search(r"python\w*\.exe", execute, re.I) or script_match
    if not is_python:
        verdict["notes"].append("not a python task — nothing to check")
        return verdict

    if not script_match:
        verdict["findings"].append("python task with no repo script path")
        return verdict
    script = script_match.group(1)
    verdict["script"] = script

    if execute.lower() == "pythonw.exe":
        verdict["notes"].append("pythonw: no stdout, encoding N/A")
    elif execute.lower() == "python.exe":
        verdict["findings"].append(
            f"bare python.exe — no '{WRAPPER}' wrapper (cp1252 stdout risk)")
    elif execute.lower() == "cmd.exe" and WRAPPER in args:
        verdict["notes"].append("cmd encoding wrapper present")
    elif execute.lower() == "cmd.exe":
        verdict["findings"].append("cmd wrapper without the encoding set")

    if not os.path.isfile(script):
        verdict["findings"].append(f"script missing on disk: {script}")
        return verdict

    rel = os.path.relpath(script, REPO)
    local = file_hash(script)
    tracked = head_hash(rel)
    if tracked is None:
        verdict["notes"].append(f"{rel} not tracked in git")
    elif local != tracked:
        verdict["findings"].append(
            f"{rel} drifted from git HEAD (scheduler runs the edited copy)")
    else:
        verdict["notes"].append(f"{rel} matches git HEAD")
    return verdict


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--fix-dry", action="store_true",
                    help="print the exact fix command per finding")
    args = ap.parse_args()
    try:
        tasks = list_tasks()
    except Exception as exc:  # noqa: BLE001 — a broken probe is not green
        print(f"task-audit: INDETERMINATE ({exc.__class__.__name__}: {exc})")
        sys.exit(2)

    verdicts = [audit_task(t) for t in tasks]
    seen: dict[str, list[str]] = {}
    for v in verdicts:
        if v.get("script"):
            seen.setdefault(v["script"], []).append(v["name"])
    for script, names in seen.items():
        if len(names) > 1:
            for v in verdicts:
                if v.get("script") == script:
                    v["notes"].append(f"script shared with: {names}")

    findings = 0
    for v in verdicts:
        print(f"{v['name']}")
        for f in v["findings"]:
            findings += 1
            print(f"  FINDING: {f}")
            if args.fix_dry and v.get("script"):
                print(f"    fix: cmd /c set PYTHONIOENCODING=utf-8&& "
                      f"\"C:\\Python314\\python.exe\" \"{v['script']}\"")
        for n in v["notes"]:
            print(f"  note: {n}")
    print(f"task-audit: {'FAIL' if findings else 'OK'} — "
          f"{len(verdicts)} task(s), {findings} finding(s)")
    sys.exit(1 if findings else 0)


if __name__ == "__main__":
    main()
