#!/usr/bin/env python3
"""Unit tests for scripts/weekly_pnl.py.

The summary gates nobody's money but it does gate confidence: day
bucketing must respect the entry timestamps, wins/profit aggregation
must be exact, malformed lines must be skipped silently, and an empty
window must render the "(no settled trades)" placeholder. Synthetic
journal dirs keep every test offline and instant.

Run: python scripts/test_weekly_pnl.py   (exit 0 = all pass)
"""
import importlib.util
import json
import os
import sys
import tempfile
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("weekly_pnl", os.path.join(HERE, "weekly_pnl.py"))
wp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wp)


def make_journal(tmp, entries):
    d = os.path.join(tmp, "journal")
    os.makedirs(d, exist_ok=True)
    for i, (ts, det) in enumerate(entries):
        day = ts.astimezone(timezone.utc).strftime("%Y%m%d")
        path = os.path.join(d, f"journal_{day}.jsonl")
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps({
                "Timestamp": ts.isoformat(),
                "AccountId": "a",
                "Category": "TRADE_SETTLEMENT",
                "Details": json.dumps(det),
            }) + "\n")
    return d


def main():
    now = datetime.now(timezone.utc)
    ok = 0
    failed = 0

    def check(name, cond):
        nonlocal ok, failed
        if cond:
            ok += 1
            print(f"  ok: {name}")
        else:
            failed += 1
            print(f"  FAIL: {name}")

    with tempfile.TemporaryDirectory() as tmp:
        # Window: last 7 days → two settled today, one 3 days ago,
        # one 10 days ago (outside), plus junk lines to ignore.
        # Anchored to local noon so every entry lands cleanly inside one
        # local day regardless of when the test runs.
        noon = datetime.now().astimezone().replace(hour=12, minute=0, second=0, microsecond=0)
        entries = [
            (noon, {"Won": True, "Profit": 0.85, "Payout": 1.85, "ContractId": "c1"}),
            (noon + timedelta(hours=1), {"Won": False, "Profit": -1.0, "Payout": 0.0, "ContractId": "c2"}),
            (noon - timedelta(days=3), {"Won": True, "Profit": 0.9, "Payout": 1.9, "ContractId": "c3"}),
            (noon - timedelta(days=10), {"Won": True, "Profit": 99.0, "Payout": 100.0, "ContractId": "old"}),
        ]
        d = make_journal(tmp, entries)
        # malformed lines must be skipped
        with open(os.path.join(d, "journal_19990101.jsonl"), "w", encoding="utf-8") as f:
            f.write("{not json}\n" + json.dumps({"Category": "OTHER"}) + "\n")

        s = wp.load_settlements(7, d)
        check("window excludes 10-day-old trade", all(e["contract"] != "old" for e in s))
        check("window includes 3 trades", len(s) == 3)

        by_day, total = wp.summarize(s)
        check("two days bucketed", len(by_day) == 2)
        check("total count", total["n"] == 3)
        check("total wins", total["wins"] == 2)
        check("total profit", abs(total["profit"] - 0.75) < 1e-9)
        check("win rate", abs(total["win_rate"] - 2 / 3) < 1e-9)

        out = wp.render(by_day, total, "test window")
        check("render has TOTAL", "TOTAL" in out)

        by_day0, total0 = wp.summarize([])
        out0 = wp.render(by_day0, total0, "empty")
        check("empty renders placeholder", "no settled trades" in out0)
        check("empty win rate is n/a", "n/a" in out0)

    print(f"\n{ok} passed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
