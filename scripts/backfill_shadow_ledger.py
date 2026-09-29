#!/usr/bin/env python3
"""Backfill the shadow engines' promotion ledger from journal history.

The shadow engines (counterfactual, giveback) journal weight-0 votes on
every exit evaluation, and the engine host appends their settled-trade
rows to fx-shadow-<SYMBOL>.jsonl on each full close. But the ledger only
started collecting on 2026-09-29 — the journals already hold days of
evaluations + closes that the engines "observed" without a ledger to
receive them. This script replays that history so the 100-trade promotion
bar measures evidence, not calendar luck.

Reconstruction rule (mirrors the live append in FxEngineHost):
  for every close-confirmation FX_EXIT row ("closed #T — deal ..."):
    find the ticket's last evaluation row at or before the close time
    for each shadow vote (counterfactual, giveback) in that evaluation:
      row = {At, Ticket, Symbol, Engine, ExitAtClose, ResolvedAction,
             Won, Helped}
      Won    = ProfitR > 0 at close
      Helped = Won and ExitAtClose >= 0.5 and action is hold/monitor
  rows already present in the ledger (Ticket + Engine) are skipped.

Read-only by default; --write appends the missing rows to the ledger.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
from datetime import datetime, timezone

SHADOW_ENGINES = ("counterfactual", "giveback")
LEDGER_FIELDS = ("At", "Ticket", "Symbol", "Engine", "ExitAtClose",
                 "ResolvedAction", "Won", "Helped")


def default_data_dir() -> str:
    appdata = os.environ.get("APPDATA")
    if appdata:
        return os.path.join(appdata, "tf", "data")
    return os.path.expanduser("~/.local/share/tf/data")


def parse_args(argv):
    p = argparse.ArgumentParser(description="Shadow-ledger backfill from journals")
    p.add_argument("--data", default=default_data_dir(),
                   help="app data dir (default: %%APPDATA%%\\tf\\data)")
    p.add_argument("--ledger-dir", default=None,
                   help="ledger dir (default: <data>/fx-shadow)")
    p.add_argument("--write", action="store_true",
                   help="append missing rows (default: dry run)")
    return p.parse_args(argv)


def parse_ts(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def decode_exit(details: str):
    """Split an FX_EXIT Details line into (text_prefix, payload|None)."""
    brace = details.find("{")
    if brace < 0:
        return details, None
    try:
        return details[:brace], json.loads(details[brace:])
    except json.JSONDecodeError:
        return details, None


def load_rows(data_dir: str):
    rows = []
    for path in sorted(glob.glob(os.path.join(data_dir, "journal", "journal_*.jsonl"))):
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    rows.sort(key=lambda r: r.get("Timestamp", ""))
    return rows


def reconstruct(rows):
    """Yield ledger rows the live host would have written, in order."""
    last_eval = {}   # ticket -> (timestamp, payload) latest evaluation
    for r in rows:
        if r.get("Category") != "FX_EXIT":
            continue
        text, payload = decode_exit(r.get("Details", ""))
        if payload is None:
            continue
        ticket = payload.get("Ticket")
        if not ticket:
            continue
        if payload.get("Action"):
            last_eval[ticket] = (r["Timestamp"], payload)
            continue

        # Close-confirmation line: the settlement moment.
        close_ts = r["Timestamp"]
        ev_ts, ev = last_eval.get(ticket, (None, None))
        if ev is None:
            continue
        won = ev.get("ProfitR", 0) > 0
        for v in ev.get("Votes", []):
            engine = v.get("Engine", "")
            if engine not in SHADOW_ENGINES:
                continue
            exit_at_close = v.get("Exit", 0)
            helped = (won and exit_at_close >= 0.5
                      and ev.get("Action") in ("hold", "monitor"))
            yield {
                "At": close_ts,
                "Ticket": ticket,
                "Symbol": ev.get("Symbol", ""),
                "Engine": engine,
                "ExitAtClose": 1 if exit_at_close >= 0.5 else 0,
                "ResolvedAction": ev.get("Action", ""),
                "Won": won,
                "Helped": helped,
                "_ev_ts": ev_ts,
            }


def existing_keys(ledger_dir: str):
    keys = set()
    if not os.path.isdir(ledger_dir):
        return keys
    for path in glob.glob(os.path.join(ledger_dir, "fx-shadow-*.jsonl")):
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                    keys.add((row.get("Ticket"), row.get("Engine")))
                except json.JSONDecodeError:
                    continue
    return keys


def main(argv) -> int:
    args = parse_args(argv)
    ledger_dir = args.ledger_dir or os.path.join(args.data, "fx-shadow")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")

    rows = load_rows(args.data)
    have = existing_keys(ledger_dir)
    missing = [r for r in reconstruct(rows)
               if (r["Ticket"], r["Engine"]) not in have]

    by_symbol = {}
    for r in missing:
        by_symbol.setdefault(r["Symbol"], []).append(r)

    print(f"journal rows read:      {len(rows)}")
    print(f"ledger rows on disk:    {len(have)}")
    print(f"missing rows to append: {len(missing)}")
    for sym, rs in sorted(by_symbol.items()):
        helped = sum(1 for r in rs if r["Helped"])
        won = sum(1 for r in rs if r["Won"])
        print(f"  {sym:12s} +{len(rs)} rows ({won} won, {helped} helped)")

    if not args.write:
        print("\ndry run — pass --write to append these rows")
        return 0

    for sym, rs in sorted(by_symbol.items()):
        path = os.path.join(ledger_dir, f"fx-shadow-{sym}.jsonl")
        os.makedirs(ledger_dir, exist_ok=True)
        with open(path, "a", encoding="utf-8") as f:
            for r in rs:
                f.write(json.dumps({k: r[k] for k in LEDGER_FIELDS},
                                   separators=(",", ":")) + "\n")
        print(f"appended {len(rs)} rows -> {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
