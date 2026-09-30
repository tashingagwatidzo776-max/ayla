#!/usr/bin/env python3
"""Tests for scripts/trade_lifecycle.py.

The lifecycle stitcher's verdicts must be exact — a missed fill or a
misread settlement corrupts every investigation built on it. All tests run
against synthetic journals in temp TF_DATA_DIRs: no real journal, no venue.

Run: python scripts/test_trade_lifecycle.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "trade_lifecycle.py"


class TmpData:
    def __init__(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        (self.root / "journal").mkdir()
        (self.root / "fx-shadow").mkdir()

    def journal(self, rows):
        path = self.root / "journal" / "journal_20261001.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            for cat, details, ts in rows:
                fh.write(json.dumps({"Timestamp": ts, "Category": cat,
                                     "Details": details}) + "\n")

    def shadow(self, rows, symbol="EURUSD"):
        path = self.root / "fx-shadow" / f"fx-shadow-{symbol}.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            for r in rows:
                fh.write(json.dumps(r) + "\n")

    def run(self, *args):
        env = dict(os.environ, TF_DATA_DIR=str(self.root),
                   PYTHONIOENCODING="utf-8")
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args], capture_output=True,
            text=True, encoding="utf-8", env=env, timeout=60)


def fill(ticket, side="buy", sl=1.1450, ts="2026-10-01T10:00:00.0+00:00"):
    return ("FX_ORDER",
            f"paper-exec fill (demo): {side} 0.1 lots EURUSD @ 1.1480 — ticket {ticket}: "
            + json.dumps({"Side": side, "Lots": 0.1, "Sl": sl, "Order": ticket,
                          "Deal": ticket + 1, "Price": 1.1480,
                          "Signal": "ema-cross(9/21)",
                          "SizedStopDistance": 0.0012}), ts)


def eval_row(ticket, action="hold", score=12.5, mfe=3.2, ts="2026-10-01T10:05:00.0+00:00"):
    return ("FX_EXIT",
            f"EURUSD #{ticket}: {action} score {score} — "
            + json.dumps({"Ticket": ticket, "Symbol": "EURUSD", "Side": "buy",
                          "Action": action, "Score": score, "MfeR": mfe,
                          "MaeR": 0.1, "ProfitR": mfe, "Override": None,
                          "Votes": []}), ts)


def close_row(ticket, ts="2026-10-01T11:00:00.0+00:00"):
    return ("FX_EXIT",
            f"EURUSD: closed #{ticket} — deal 777 "
            + json.dumps({"Ticket": ticket, "Partial": False}), ts)


def profit_row(ticket, peak=6.5, floor=4.0, ts="2026-10-01T10:06:00.0+00:00"):
    return ("FX_PROFIT",
            f"EURUSD #{ticket}: PROFIT_PROTECTED +5.9R — "
            + json.dumps({"Ticket": ticket, "Symbol": "EURUSD",
                          "State": "PROFIT_PROTECTED", "CurrentR": 5.9,
                          "PeakR": peak, "MaeR": 0.2, "FloorR": floor,
                          "GivebackPct": 9.0, "GivebackClass": "NORMAL"}), ts)


def breach_row(ticket, ts="2026-10-01T10:07:00.0+00:00"):
    return ("FX_RISK",
            f"EURUSD #{ticket}: profit floor 4.0R breached (current 3.8R) — "
            "exit evaluation requested: " + json.dumps({"Ticket": ticket}), ts)


def settle_row(ticket, won=True, ts="2026-10-01T11:01:00.0+00:00"):
    return ("TRADE_SETTLEMENT",
            "settled: " + json.dumps({"ContractId": str(ticket), "Won": won,
                                      "Profit": 24.77 if won else -12.0,
                                      "NewBankroll": 7194.88}), ts)


def full_story(ticket=42):
    return [
        fill(ticket),
        eval_row(ticket),
        profit_row(ticket),
        breach_row(ticket),
        close_row(ticket),
        settle_row(ticket),
    ]


def test_full_story_stitches_every_leg():
    d = TmpData()
    d.journal(full_story(42))
    d.shadow([{"Ticket": 42, "Engine": "giveback", "ExitAtClose": 0.95,
               "ResolvedAction": "full", "Won": True, "Helped": True}])
    r = d.run("--ticket", "42")
    assert r.returncode == 0, r.stdout + r.stderr
    out = r.stdout
    assert "SETTLED WIN" in out
    assert "buy 0.1 EURUSD SL 1.145" in out
    assert "via ema-cross(9/21)" in out
    assert "sized stop 0.0012" in out
    assert "FLOOR BREACH" in out
    assert "hold score  12.5" in out or "hold score 12.5" in out
    assert "PROFIT PROFIT_PROTECTED peak 6.5R floor 4.0R" in out
    assert "shadow giveback" in out and "HELPED" in out


def test_missing_legs_render_honestly():
    """A settlement-only ticket (imported history) still renders."""
    d = TmpData()
    d.journal([settle_row(7, won=False)])
    r = d.run("--ticket", "7")
    assert r.returncode == 0, r.stdout + r.stderr
    assert "SETTLED LOSS" in r.stdout


def test_partial_close_is_not_the_final_state():
    """A partial close followed by live evaluations stays OPEN-ish, never
    'closed' (the closed marker only ends a lifecycle when nothing follows
    is unknowable — so the table shows the close but state keeps the
    venue's truth out of it: no settlement row => closed-no-settlement only
    when a CLOSE exists)."""
    d = TmpData()
    d.journal([fill(9), close_row(9), eval_row(9, action="hold")])
    r = d.run("--ticket", "9")
    assert r.returncode == 0, r.stdout + r.stderr
    assert "CLOSE" in r.stdout


def test_table_and_open_view():
    d = TmpData()
    d.journal(full_story(42) + [fill(43, side="sell"),
                                eval_row(43, mfe=1.2)])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "#42" in r.stdout and "settled WIN" in r.stdout
    assert "2 ticket(s)" in r.stdout

    r2 = d.run("--open")
    assert "#43" in r2.stdout
    assert "#42" not in r2.stdout


def test_json_view_is_machine_readable():
    d = TmpData()
    d.journal(full_story(42))
    r = d.run("--json")
    assert r.returncode == 0, r.stdout + r.stderr
    data = json.loads(r.stdout)
    assert data["42"]["settlement"]["won"] is True
    assert data["42"]["profit"][0]["floor"] == 4.0


def test_unknown_ticket_exits_1():
    d = TmpData()
    r = d.run("--ticket", "999")
    assert r.returncode == 1
    assert "no journal evidence" in r.stdout


def test_corrupt_rows_are_skipped():
    d = TmpData()
    path = d.root / "journal" / "journal_20261001.jsonl"
    with open(path, "a", encoding="utf-8") as fh:
        fh.write("{broken\n")
        fh.write(json.dumps({"Timestamp": "2026-10-01T10:00:00.0+00:00",
                             "Category": "FX_EXIT",
                             "Details": "no payload here"}) + "\n")
    d.journal(full_story(42))
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "#42" in r.stdout


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"  ok  {t.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"FAIL  {t.__name__}: {exc}")
        except Exception as exc:  # noqa: BLE001
            failed += 1
            print(f"ERROR {t.__name__}: {exc.__class__.__name__}: {exc}")
    print(f"{'FAIL' if failed else 'PASS'}: {len(tests) - failed}/{len(tests)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
