#!/usr/bin/env python3
"""Freeze a deterministic sample of the live journal for snapshot tests.

Reads one real journal day and writes a frozen JSONL fixture containing:

  - every FX_EXIT row whose Override is set (override-tier coverage),
  - every FX_EXIT row whose drawdown vote Exit > 0 (the rows the weight
    backtest actually moves),
  - every 50th remaining FX_EXIT row (background hold-score coverage),
  - every FX_FLOOR row for the hard-floor save tickets (guard-join
    coverage),

in original file order, byte-faithful. The fixture is FROZEN: commit it
once and never regenerate it — its whole value is that future FX_EXIT
schema changes in the wild stop reproducing the pinned expectations and
fail loudly. Provenance lives in the test, not in the file (JSONL has no
comments).

Run: python scripts/freeze_journal_sample.py [day] [out_path]
     day default 20260930; out default scripts/fixtures/journal-frozen-<day>.jsonl
"""
from __future__ import annotations

import glob
import hashlib
import json
import os
import sys

SAVE_TICKETS = {9820712902, 9820719898}


def keep(row_json: str, row: dict, lineno: int) -> bool:
    cat = row.get("Category")
    details = str(row.get("Details", ""))
    if cat == "FX_FLOOR":
        for t in SAVE_TICKETS:
            if str(t) in details:
                return True
        return False
    if cat != "FX_EXIT":
        return False
    brace = details.find("{")
    if brace < 0:
        return False
    try:
        dec = json.loads(details[brace:])
    except json.JSONDecodeError:
        return False
    if not isinstance(dec, dict):
        return False
    if dec.get("Override"):
        return True
    for v in dec.get("Votes") or []:
        if v.get("Engine") == "drawdown" and float(v.get("Exit", 0)) > 0:
            return True
    return lineno % 50 == 0


def main() -> int:
    day = sys.argv[1] if len(sys.argv) > 1 else "20260930"
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        "scripts", "fixtures", f"journal-frozen-{day}.jsonl")
    src = os.path.join(os.environ.get("APPDATA", ""), "tf", "data",
                       "journal", f"journal_{day}.jsonl")
    if not os.path.isfile(src):
        print(f"source journal not found: {src}", file=sys.stderr)
        return 2

    os.makedirs(os.path.dirname(out), exist_ok=True)
    kept = 0
    fx_exit_seen = 0
    with open(src, "r", encoding="utf-8") as fh, \
            open(out, "w", encoding="utf-8", newline="\n") as dst:
        for lineno, line in enumerate(fh):
            line = line.rstrip("\r\n")
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            if row.get("Category") == "FX_EXIT":
                fx_exit_seen += 1
            if keep(line, row, lineno):
                dst.write(line + "\n")
                kept += 1

    raw = open(out, "rb").read()
    digest = hashlib.sha256(raw).hexdigest()
    # The snapshot test pins the NEWLINE-NORMALIZED sha256 (git autocrlf
    # may rewrite LF -> CRLF on checkout; an end-line-sensitive pin
    # would then reject its own fixture — caught in PR #151's follow-up).
    norm = hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()
    print(f"source: {src}")
    print(f"FX_EXIT rows seen: {fx_exit_seen}; rows frozen: {kept}")
    print(f"fixture: {out}")
    print(f"sha256 (raw): {digest}")
    print(f"sha256 (newline-normalized — pin THIS): {norm}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
