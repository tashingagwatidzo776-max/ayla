#!/usr/bin/env python3
"""Unit tests for scripts/fx_outcome_coverage.py.

The dashboard is the daily tier-1 vs unknown tracker, so what it counts
must be exactly what the journal proves: payload sources verbatim,
legacy rows as (no-payload), non-close FX_EXIT rows ignored, and the
writer matrix that surfaces a floor-confirm landing unknown.
Synthetic journal dirs keep every test offline and instant.

Run: python scripts/test_fx_outcome_coverage.py   (exit 0 = all pass)
"""
import importlib.util
import json
import os
import sys
import tempfile
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location(
    "fx_outcome_coverage", os.path.join(HERE, "fx_outcome_coverage.py"))
fx = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fx)


def row(ts, category, details):
    return {
        "Timestamp": ts.isoformat(),
        "AccountId": "00000000-0000-0000-0000-000000000000",
        "Category": category,
        "Details": details,
    }


def make_journal(tmp, rows):
    d = os.path.join(tmp, "journal")
    os.makedirs(d, exist_ok=True)
    by_day = {}
    for ts, category, details in rows:
        day = ts.astimezone(timezone.utc).strftime("%Y%m%d")
        by_day.setdefault(day, []).append(row(ts, category, details))
    for day, entries in by_day.items():
        with open(os.path.join(d, f"journal_{day}.jsonl"), "a", encoding="utf-8") as f:
            for e in entries:
                f.write(json.dumps(e) + "\n")
    return d


def close_row(ticket, source=None, realized_r=None):
    payload = {"Ticket": ticket, "Partial": False, "Lots": None, "Retcode": 10009}
    if source is not None:
        payload["RealizedR"] = realized_r
        payload["OutcomeSource"] = source
    return payload


def main():
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

    noon = datetime.now(timezone.utc).replace(hour=12, minute=0, second=0, microsecond=0)
    yesterday = noon - timedelta(days=1)

    with tempfile.TemporaryDirectory() as tmp:
        rows = [
            # Day -1: tier-1 ensemble close, legacy no-payload row.
            (yesterday, "FX_EXIT",
             "XAUUSDmicro #101: closed #101 — deal 5: " +
             json.dumps(close_row(101, "close-price", 1.5))),
            (yesterday, "FX_EXIT",
             "EURUSD #102: closed #102 — broker no longer holds the ticket: " +
             json.dumps({"Ticket": 102})),
            # Today: floor confirm landing unknown (the regression the
            # dashboard exists to surface), a healthy stale retire with
            # a snapshot, an ops reconcile unknown.
            (noon, "FX_EXIT",
             "XAUUSDmicro #103: closed #103 — deal 9 (profit floor exit confirmed " +
             "by broker reconciliation): " +
             json.dumps(close_row(103, "unknown", None))),
            (noon, "FX_EXIT",
             "XAGUSD #104: closed #104 — broker no longer holds the ticket; " +
             "stale tracking retired (healthy positions read): " +
             json.dumps(close_row(104, "profit-snapshot", -0.7))),
            (noon, "FX_EXIT",
             "EURUSD #105: closed #105 — broker no longer holds the ticket; " +
             "journaled by ops: " + json.dumps(close_row(105, "unknown", None))),
            # Non-close FX_EXIT rows must be ignored.
            (noon, "FX_EXIT",
             "XAUUSDmicro #106: close refused for #106: Retcode"),
            (noon, "FX_EXIT",
             "XAUUSDmicro #107: closed 0.1 lots of #107 — deal 2: " +
             json.dumps({"Ticket": 107, "Partial": True, "Lots": 0.1})),
            # Operator reconcile (no payload), unrelated category.
            (noon, "FX_EXIT",
             "closed #108 - operator reconcile: venue flat verified"),
            (noon, "FX_ORDER", "buy 0.1 lots XAUUSDmicro @ 4100 — ticket 109: {}"),
        ]
        d = make_journal(tmp, rows)
        with open(os.path.join(d, "journal_19990101.jsonl"), "w", encoding="utf-8") as f:
            f.write("{not json}\n")

        closes = fx.load_closes(d)
        check("six close rows loaded", len(closes) == 6)

        by_ticket = {c["ticket"]: c for c in closes}
        check("tier-1 close-price counted",
              by_ticket[101]["source"] == "close-price")
        check("legacy row is (no-payload)",
              by_ticket[102]["source"] == "(no-payload)")
        check("floor confirm unknown counted",
              by_ticket[103]["source"] == "unknown"
              and by_ticket[103]["writer"] == "floor-confirm")
        check("stale retire snapshot counted",
              by_ticket[104]["source"] == "profit-snapshot"
              and by_ticket[104]["writer"] == "stale-retire")
        check("ops reconcile writer classified",
              by_ticket[105]["writer"] == "ops-reconcile")
        check("operator reconcile writer classified",
              by_ticket[108]["writer"] == "operator-reconcile"
              and by_ticket[108]["source"] == "(no-payload)")
        check("refused close ignored", 106 not in by_ticket)
        check("partial close ignored", 107 not in by_ticket)

        # Windowing on close time.
        check("--days 1 drops yesterday's rows",
              len(fx.windowed(closes, 1)) == 4)
        check("--days 0 keeps everything", len(fx.windowed(closes, 0)) == 6)

        # Counts and the rendered tables.
        total = fx._counts(closes)
        check("total closes", total["closes"] == 6)
        check("measured = close-price + snapshot",
              total["measured"] == 2 and total["close-price"] == 1
              and total["profit-snapshot"] == 1)
        check("unknown + no-payload counted, never measured",
              total["unknown"] == 2 and total["(no-payload)"] == 2)

        out = fx.render(closes, "test window")
        check("daily table has TOTAL row", "TOTAL" in out)
        check("tier-1 line present", "tier-1 (close-price) 1/6" in out)
        check("writer matrix lists floor-confirm",
              "floor-confirm" in out)
        day_lines = [l for l in out.splitlines()
                     if l.strip().startswith(str(noon.date()))]
        check("per-day row for today", len(day_lines) == 1)
        check("per-day row counts today's 4 closes",
              day_lines and day_lines[0].split()[1] == "4")

        # Floor-confirm unknowns must be VISIBLE in the matrix row.
        fc_line = next(l for l in out.splitlines()
                       if l.strip().startswith("floor-confirm"))
        check("matrix floor-confirm: 1 close, 0 tier-1, 1 unknown",
              fc_line.split()[1] == "1" and fc_line.split()[2] == "0"
              and fc_line.split()[4] == "1")

        # Empty window renders without crashing.
        check("empty render safe", "no close rows in window"
              in fx.render([], "empty"))

    # Usage error path.
    sys.argv = ["fx_outcome_coverage.py", "--days", "0"]
    check("--days 0 rejected", fx.main() == 1)

    print(f"\n{ok} passed, {failed} failed")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
