#!/usr/bin/env python3
"""Daily soak evidence: run the soak report into docs/soak/ and commit it.

Scheduled daily (mirrors the MT5 watchdog task). Read-only over the app's
data; the only write is the evidence markdown + the git commit. Never
pushes — pushing stays the operator's reviewed act (VPN/HTTP-1.1 command).
A NO-DATA day is not committed (an empty soak is not evidence).
"""
from __future__ import annotations

import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DATA = Path(os.environ.get("APPDATA", "")) / "tf" / "data" if os.environ.get("APPDATA") \
    else Path.home() / ".local/share/tf/data"


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=ROOT, text=True, encoding="utf-8",
                          errors="replace", capture_output=True, **kw)


def main() -> int:
    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    out = ROOT / "docs" / "soak" / f"SOAK-{today}.md"

    gen = run([sys.executable, str(ROOT / "scripts" / "soak_report.py"),
               "--data", str(DATA), "--since", today, "--record",
               str(ROOT / "docs" / "soak")])
    if gen.returncode == 3:
        print("NO DATA today — not committing an empty soak")
        return 0
    if gen.returncode != 0:
        print(f"soak report generation failed rc={gen.returncode}: {gen.stderr[-400:]}")
        return 1

    readme = ROOT / "docs" / "soak" / "README.md"
    run([git(), "add", str(out), str(readme)])
    staged = run([git(), "diff", "--cached", "--quiet"])
    if staged.returncode == 0:
        print("soak evidence unchanged — nothing to commit")
        return 0

    commit = run([git(), "commit", "-m", f"docs: daily soak evidence {today}\n\n"
                  "Generated with Codebuff\nCo-Authored-By: Codebuff <noreply@codebuff.com>"])
    print(commit.stdout or commit.stderr)
    return commit.returncode


def git() -> str:
    return "git"


if __name__ == "__main__":
    sys.exit(main())
