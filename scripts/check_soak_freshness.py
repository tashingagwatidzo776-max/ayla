#!/usr/bin/env python3
"""Soak evidence freshness check: the demo soak must be recent, not ancient.

The weekly drill runs on GitHub runners, far from the machine that runs the
app — so it cannot read the journal. Instead it checks the COMMITTED
evidence: docs/soak/SOAK-<date>.md files written by
`soak_report.py --record docs/soak`. This script verifies the newest report
carries a SOAK CLEAN verdict and is within --max-age days.

Exit codes:
  0 = fresh (or no evidence yet — informational, per --allow-empty)
  1 = stale evidence, or the newest report is not SOAK CLEAN
  2 = usage/IO error

The one-tolerance rule: evidence may be ABSENT (nobody has soaked yet) but
never STALE (soaked once, then gone quiet). Staleness is the silent failure
this check exists to catch.
"""
import argparse
import glob
import os
import re
import sys
from datetime import datetime, timezone

SOAK_FILE_RE = re.compile(r"^SOAK-(\d{4}-\d{2}-\d{2})\.md$")


def parse_args(argv):
    p = argparse.ArgumentParser(description="Soak evidence freshness check")
    p.add_argument("--evidence-dir", default=os.path.join("docs", "soak"),
                   help="directory of committed SOAK-<date>.md reports")
    p.add_argument("--max-age", type=int, default=14,
                   help="maximum age in days for the newest SOAK CLEAN report")
    p.add_argument("--allow-empty", choices=("yes", "no"), default="yes",
                   help="whether absent evidence is informational (yes) or a failure (no)")
    args = p.parse_args(argv)
    if not os.path.isdir(args.evidence_dir):
        if args.allow_empty == "yes":
            return None
        print(f"::error::no soak evidence directory: {args.evidence_dir}")
        sys.exit(1)
    return args


def newest_clean_report(evidence_dir):
    """Returns the (path, date) of the newest SOAK-*.md carrying a
    'SOAK CLEAN' verdict, or (None, None)."""
    candidates = []
    for path in glob.glob(os.path.join(evidence_dir, "SOAK-*.md")):
        name = os.path.basename(path)
        m = SOAK_FILE_RE.match(name)
        if not m:
            continue
        candidates.append((m.group(1), path))
    for date, path in sorted(candidates, reverse=True):
        with open(path, encoding="utf-8") as f:
            text = f.read()
        if "SOAK CLEAN" in text:
            return path, date
    return None, None


def main(argv):
    args = parse_args(argv)
    if args is None:
        print("OK: no soak evidence committed yet — informational "
              "(run soak_report.py --record docs/soak after a demo session)")
        return 0

    path, date = newest_clean_report(args.evidence_dir)
    if path is None:
        print(f"::error::no SOAK CLEAN report in {args.evidence_dir} — "
              "every committed report has findings or NO DATA")
        return 1

    age = (datetime.now(timezone.utc).date()
           - datetime.fromisoformat(date).date()).days
    if age < 0:
        print(f"::error::report dated in the future: {os.path.basename(path)}")
        return 1
    if age > args.max_age:
        print(f"::error::soak evidence is STALE: newest SOAK CLEAN is {age}d old "
              f"({os.path.basename(path)}, max {args.max_age}d). Run a demo session, "
              "then: python scripts/soak_report.py --record docs/soak")
        return 1

    print(f"OK: soak evidence fresh ({os.path.basename(path)}, {age}d old, max {args.max_age}d)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
