#!/usr/bin/env python3
"""Watch the journal for the profit-floor tier's first live save.

The verification chain for one event (evidence, not hope):
  1. FX_EXIT row with Override == "profit-floor", or a drawdown vote with
     Exit >= 0.85 whose reason says the peak was given back.
  2. FX_RISK row "exit evaluation requested" for the same ticket.
  3. FX_ORDER close confirmation for the same ticket.

Each pass also prints the open book's giveback posture (newest FX_PROFIT
row per ticket) so the approach to the 60% watch bar and the 75% override
bar is visible before anything fires.

Journal-only by construction: reads, never trades. Exit code 0 normally,
2 when a verified save is found (so a scheduled task can alert).

Usage:
  python scripts/watch_profit_floor.py            # one pass
  python scripts/watch_profit_floor.py --loop 60  # poll every 60s
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import time

GIVEBACK_VOTE_MIN = 0.85     # the deep-giveback vote bar (GivebackVoteRatio)
GIVEBACK_WATCH = 0.60        # the watch bar (GivebackWatchRatio)
GIVEBACK_OVERRIDE = 0.75     # the override bar (GivebackOverrideRatio)


def journal_files() -> list[str]:
    base = os.path.expandvars(r"%APPDATA%\tf\data\journal")
    return sorted(glob.glob(os.path.join(base, "journal_*.jsonl")))


def rows() -> list[dict]:
    out = []
    for path in journal_files():
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "FX_" not in line:
                    continue
                try:
                    env = json.loads(line)
                except json.JSONDecodeError:
                    continue
                details = env.get("Details", "")
                brace = details.find("{")
                payload = {}
                if brace >= 0:
                    try:
                        payload = json.loads(details[brace:])
                    except json.JSONDecodeError:
                        payload = {}
                out.append({
                    "ts": str(env.get("Timestamp", "")),
                    "cat": env.get("Category", ""),
                    "details": details,
                    "payload": payload,
                })
    out.sort(key=lambda r: r["ts"])
    return out


def payload_has_ticket(p: dict, ticket) -> bool:
    try:
        return int(p.get("Ticket", -1)) == int(ticket)
    except (TypeError, ValueError):
        return False


def find_saves(rs: list[dict]) -> list[dict]:
    """Stage-1 events: profit-floor overrides or deep giveback votes."""
    saves = []
    for r in rs:
        p = r["payload"]
        if r["cat"] != "FX_EXIT":
            continue
        if p.get("Override") == "profit-floor":
            saves.append({**r, "ticket": p.get("Ticket"), "kind": "override"})
            continue
        for v in p.get("Votes", []):
            if (v.get("Engine") == "drawdown"
                    and isinstance(v.get("Exit"), (int, float))
                    and v["Exit"] >= GIVEBACK_VOTE_MIN
                    and "gave back" in str(v.get("Reason", "")).lower()):
                saves.append({**r, "ticket": p.get("Ticket"), "kind": "vote"})
                break
    return saves


def verify_chain(rs: list[dict], save: dict) -> dict:
    """Stages 2 and 3 for one save: FX_RISK request + FX_ORDER close."""
    ticket = save["ticket"]
    risk = any(
        r["cat"] == "FX_RISK"
        and "exit evaluation requested" in r["details"]
        and payload_has_ticket(r["payload"], ticket)
        and r["ts"] >= save["ts"]
        for r in rs
    )
    close = any(
        r["cat"] == "FX_ORDER"
        and f"#{ticket}" in r["details"]
        and "closed" in r["details"]
        and r["ts"] >= save["ts"]
        for r in rs
    )
    return {"risk": risk, "close": close}


def book_posture(rs: list[dict]) -> dict[int, dict]:
    """Newest FX_PROFIT row per ticket: the giveback posture."""
    latest: dict[int, dict] = {}
    for r in rs:
        if r["cat"] != "FX_PROFIT":
            continue
        p = r["payload"]
        try:
            ticket = int(p.get("Ticket", -1))
        except (TypeError, ValueError):
            continue
        if ticket > 0:
            latest[ticket] = p
    return latest


def one_pass() -> int:
    rs = rows()
    saves = find_saves(rs)
    verified = 0

    if saves:
        print(f"=== {len(saves)} giveback event(s) found ===")
        for s in saves[-10:]:
            chain = verify_chain(rs, s)
            p = s["payload"]
            ok = chain["risk"] and chain["close"]
            verified += 1 if ok else 0
            print(f"{s['ts'][:19]} [{s['kind']}] ticket {s['ticket']} "
                  f"peak {p.get('MfeR', 0):.1f}R -> {p.get('ProfitR', 0):.2f}R "
                  f"| FX_RISK request: {chain['risk']} | close: {chain['close']}"
                  f"{'  <== VERIFIED' if ok else ''}")

    posture = book_posture(rs)
    print(f"=== open book posture ({len(posture)} tracked) ===")
    for ticket, p in sorted(posture.items(), key=lambda kv: -kv[1].get("PeakR", 0)):
        peak = p.get("PeakR", 0)
        cur = p.get("CurrentR", 0)
        gb = p.get("GivebackPct", 0)
        flag = ""
        if peak >= 2.0 and gb >= GIVEBACK_OVERRIDE * 100:
            flag = "  <== AT OVERRIDE BAR"
        elif peak >= 1.5 and gb >= GIVEBACK_WATCH * 100:
            flag = "  <== watch band"
        print(f"  #{ticket}: peak {peak:.1f}R cur {cur:+.2f}R "
              f"giveback {gb:.0f}% floor {p.get('FloorR', 0):.1f}R "
              f"{p.get('GivebackClass', '')}{flag}")
    return 2 if verified else 0


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--loop", type=int, default=0, metavar="SEC",
                    help="poll every SEC seconds instead of one pass")
    args = ap.parse_args()

    if args.loop <= 0:
        sys.exit(one_pass())

    while True:
        try:
            one_pass()
        except Exception as exc:  # a watcher never crashes the watch
            print(f"watch error (continuing): {exc}")
        print("-" * 60)
        time.sleep(args.loop)


if __name__ == "__main__":
    main()
