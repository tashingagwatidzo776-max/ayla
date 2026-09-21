#!/usr/bin/env python3
"""Weekly P&L summary from the app's own journal.

Reads TRADE_SETTLEMENT entries from %APPDATA%/tf/data/journal/*.jsonl
(any window you like) and prints per-day and total win/loss/profit so
demo performance since a release is visible at a glance. No network,
no vault access — the journal is plain JSON lines.

Usage:
  python scripts/weekly_pnl.py                     # last 7 days
  python scripts/weekly_pnl.py --days 30
  python scripts/weekly_pnl.py --since v0.0.10     # labels the window

Exit codes: 0 = report produced, 1 = usage error.
"""
import argparse
import glob
import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

DATA_DIR = os.path.expandvars(r"%APPDATA%\tf\data\journal")


def parse_entry(line):
    try:
        e = json.loads(line)
    except Exception:
        return None
    if e.get("Category") != "TRADE_SETTLEMENT":
        return None
    try:
        det = json.loads(e.get("Details", "{}"))
        ts = datetime.fromisoformat(e["Timestamp"])
        return {
            "ts": ts,
            "won": bool(det["Won"]),
            "profit": float(det.get("Profit", 0.0)),
            "payout": float(det.get("Payout", 0.0)),
            "contract": str(det.get("ContractId", "")),
        }
    except Exception:
        return None


def load_settlements(days, data_dir=DATA_DIR):
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    out = []
    for path in sorted(glob.glob(os.path.join(data_dir, "journal_*.jsonl"))):
        with open(path, encoding="utf-8") as f:
            for line in f:
                e = parse_entry(line)
                if e and e["ts"] >= cutoff:
                    out.append(e)
    return sorted(out, key=lambda x: x["ts"])


def summarize(settlements):
    by_day = defaultdict(lambda: {"n": 0, "wins": 0, "profit": 0.0})
    for s in settlements:
        d = s["ts"].astimezone().strftime("%Y-%m-%d")
        by_day[d]["n"] += 1
        by_day[d]["wins"] += 1 if s["won"] else 0
        by_day[d]["profit"] += s["profit"]
    total = {
        "n": sum(v["n"] for v in by_day.values()),
        "wins": sum(v["wins"] for v in by_day.values()),
        "profit": sum(v["profit"] for v in by_day.values()),
    }
    total["win_rate"] = total["wins"] / total["n"] if total["n"] else 0.0
    return by_day, total


def render(by_day, total, label):
    lines = [f"=== Weekly P&L — {label} ==="]
    for d in sorted(by_day):
        v = by_day[d]
        lines.append(
            f"  {d}  trades {v['n']:3}  wins {v['wins']:3}  "
            f"profit {v['profit']:+8.2f}")
    if not by_day:
        lines.append("  (no settled trades in the window)")
    wr = f"{total['win_rate'] * 100:.0f}%" if total["n"] else "n/a"
    lines.append(f"  TOTAL   trades {total['n']:3}  wins {total['wins']:3}  "
                 f"profit {total['profit']:+8.2f}  win-rate {wr}")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--days", type=int, default=7, help="look-back window in days")
    ap.add_argument("--since", type=str, default=None, help="label for the window")
    ap.add_argument("--data-dir", type=str, default=DATA_DIR)
    args = ap.parse_args()
    if args.days < 1:
        print("--days must be >= 1", file=sys.stderr)
        return 1
    settlements = load_settlements(args.days, args.data_dir)
    by_day, total = summarize(settlements)
    label = args.since or f"last {args.days} days"
    print(render(by_day, total, label))
    return 0


if __name__ == "__main__":
    sys.exit(main())
