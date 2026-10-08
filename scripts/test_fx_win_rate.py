#!/usr/bin/env python3
"""Unit tests for scripts/fx_win_rate.py.

The report is the measurement half of the win-rate spec, so the outcomes
it prints must be exactly what the journal proves: tier-1 RealizedR wins,
tier-2 hold-snapshot fallbacks, and NO OUTCOME counted but never guessed.
Synthetic journal dirs keep every test offline and instant.

Run: python scripts/test_fx_win_rate.py   (exit 0 = all pass)
"""
import importlib.util
import json
import os
import sys
import tempfile
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("fx_win_rate", os.path.join(HERE, "fx_win_rate.py"))
fw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fw)


def row(ts, category, details):
    return {
        "Timestamp": ts.isoformat(),
        "AccountId": "00000000-0000-0000-0000-000000000000",
        "Category": category,
        "Details": details,
    }


def fill_details(side, symbol, ticket, family, price=4100.0, stop=2.0):
    payload = {
        "Side": side, "Lots": 0.1, "Sl": price - stop, "SizedStopDistance": stop,
        "PaperExec": False, "Retcode": 10009, "Order": ticket, "Deal": ticket - 1,
        "Price": price, "Server": "Deriv-Demo", "Signal": family,
    }
    return f"{side} 0.1 lots {symbol} @ {price} — ticket {ticket}: " + json.dumps(payload)


def signal_details(symbol, alpha, conf):
    return f"{alpha} → Buy conf {conf:.2f} — reason: " + json.dumps(
        {"Symbol": symbol, "Alpha": alpha, "Direction": 0, "Confidence": conf,
         "Reason": "reason", "MemoryWeight": 1.0})


def profit_details(ticket, current_r):
    return f"SYM #{ticket}: PROFIT_FORMING: " + json.dumps(
        {"Ticket": ticket, "State": "PROFIT_FORMING", "CurrentR": current_r, "PeakR": 0})


def close_details(ticket, realized_r=None, outcome_source=None):
    payload = {"Ticket": ticket, "Partial": False, "Lots": None, "Retcode": 10009}
    if realized_r is not None:
        payload["RealizedR"] = realized_r
        payload["OutcomeSource"] = outcome_source or "close-price"
    elif outcome_source is not None:
        payload["RealizedR"] = None
        payload["OutcomeSource"] = outcome_source
    return f"SYM #{ticket}: closed #{ticket} — deal 5: " + json.dumps(payload)


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


def load(d):
    _, closes, signals, _ = fw.load_journal(d)
    return closes, signals


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

    # ── tier outcomes and coverage ───────────────────────────────────────
    with tempfile.TemporaryDirectory() as tmp:
        d = make_journal(tmp, [
            # Tier 1: close carries RealizedR +1.5 → win.
            (noon, "FX_ORDER", fill_details("buy", "XAUUSDmicro", 101, "vol-breakout")),
            (noon, "FX_SIGNAL", signal_details("XAUUSDmicro", "vol-breakout(14)", 0.60)),
            (noon + timedelta(minutes=5), "FX_EXIT", close_details(101, realized_r=1.5)),
            # Tier 2: no RealizedR; last FX_PROFIT CurrentR -0.7 before close.
            (noon + timedelta(minutes=1), "FX_ORDER", fill_details("sell", "XAGUSD", 102, "kalman-trend")),
            (noon + timedelta(minutes=6), "FX_PROFIT", profit_details(102, -0.7)),
            (noon + timedelta(minutes=7), "FX_EXIT", close_details(102)),
            # No outcome anywhere: counted, never guessed.
            (noon + timedelta(minutes=2), "FX_ORDER", fill_details("buy", "EURUSD", 103, "ou-rev")),
            (noon + timedelta(minutes=8), "FX_EXIT",
             "EURUSD #103: closed #103 — broker no longer holds the ticket: " +
             json.dumps({"Ticket": 103, "RealizedR": None, "OutcomeSource": "unknown"})),
            # Orphan close with no fill: outcome still measured in ALL.
            (noon + timedelta(minutes=9), "FX_EXIT", close_details(104, realized_r=-0.25)),
            # Garbage line must not break the load.
        ])
        with open(os.path.join(d, "journal_19990101.jsonl"), "w", encoding="utf-8") as f:
            f.write("{not json}\n")

        closes, signals = load(d)
        trades = fw.windowed_trades(closes, days=30)
        st = fw.stats(trades)

        check("four closes loaded", len(trades) == 4)
        check("tier-1 win measured", any(
            t["ticket"] == 101 and t["outcome"] == 1.5 and t["source"] == "close-price"
            for t in trades))
        check("tier-2 snapshot fallback", any(
            t["ticket"] == 102 and t["outcome"] == -0.7 and t["source"] == "hold-snapshot"
            for t in trades))
        check("unknown outcome stays None", any(
            t["ticket"] == 103 and t["outcome"] is None for t in trades))
        check("coverage: 3 measured / 4 closed", st["n"] == 3 and st["closed"] == 4
              and st["no_outcome"] == 1)
        check("wins = tier-1 only", st["wins"] == 1)
        check("win rate over measured only", abs(st["win_rate"] - 1 / 3) < 1e-9)
        check("avg R = (1.5 - 0.7 - 0.25) / 3",
              abs(st["avg_r"] - (1.5 - 0.7 - 0.25) / 3) < 1e-9)

    # ── breakdowns, confidence, bar preview ──────────────────────────────
    with tempfile.TemporaryDirectory() as tmp:
        rows = []
        # 30 losing vol-breakout trades on XAU at conf 0.60 → FAIL cell.
        for i in range(30):
            t = noon + timedelta(minutes=i)
            ticket = 200 + i
            rows.append((t, "FX_SIGNAL", signal_details("XAUUSDmicro", "vol-breakout(14)", 0.60)))
            rows.append((t + timedelta(seconds=1), "FX_ORDER",
                         fill_details("buy", "XAUUSDmicro", ticket, "vol-breakout(14)")))
            rows.append((t + timedelta(minutes=4), "FX_EXIT",
                         close_details(ticket, realized_r=-0.5)))
        # 15-trade near-threshold cell (approaching).
        for i in range(15):
            t = noon + timedelta(hours=1, minutes=i)
            ticket = 300 + i
            rows.append((t, "FX_ORDER",
                         fill_details("buy", "XAGUSD", ticket, "ema-slope(21)")))
            rows.append((t + timedelta(minutes=4), "FX_EXIT",
                         close_details(ticket, realized_r=-0.4)))
        d = make_journal(tmp, rows)

        closes, signals = load(d)
        trades = fw.windowed_trades(closes, days=30)
        out = fw.render(trades, "test window", signals)

        check("overall row present", "ALL" in out)
        check("coverage line present", "coverage 100%" in out)
        check("family bucket", "vol-breakout(14)" in out)
        check("side bucket", "By side" in out and "buy" in out)
        check("confidence bucket matched", "conf 0.60" in out)
        check("failing cell flagged",
              "FAIL — would exclude" in out and "vol-breakout" in out)
        check("near-threshold listed", "approaching the bar" in out
              and "ema-slope(21)" in out)
        check("windowed cells never listed as FAIL",
              out.count("FAIL — would exclude") == 1)

    # ── confidence not derivable → conf ? bucket ─────────────────────────
    with tempfile.TemporaryDirectory() as tmp:
        d = make_journal(tmp, [
            (noon, "FX_ORDER", fill_details("buy", "EURUSD", 401, "ou-rev")),
            (noon + timedelta(minutes=5), "FX_EXIT", close_details(401, realized_r=0.5)),
        ])
        closes, signals = load(d)
        out = fw.render(fw.windowed_trades(closes, days=30), "t", signals)
        check("unmatched confidence buckets as ?", "conf ?" in out)

    # ── thin cells never surface in the bar preview ──────────────────────
    with tempfile.TemporaryDirectory() as tmp:
        rows = []
        for i in range(fw.APPROACHING_N - 1):
            t = noon + timedelta(minutes=i)
            rows.append((t, "FX_ORDER", fill_details("buy", "GBPUSD", 500 + i, "bb-rev")))
            rows.append((t + timedelta(minutes=4), "FX_EXIT",
                         close_details(500 + i, realized_r=-1.0)))
        d = make_journal(tmp, rows)
        closes, _ = load(d)
        out = fw.render(fw.windowed_trades(closes, days=30), "t", [])
        preview = out.split("-- Recent-tape bar preview")[1]
        check("thin cell hidden from the bar preview",
              "approaching the bar" not in preview and "bb-rev" not in preview)
        check("thin cell still reported in By family", "bb-rev" in out)
        check("default-keep message shown", "default-keep holds" in preview)

    # ── usage ────────────────────────────────────────────────────────────
    old_argv = sys.argv
    try:
        sys.argv = ["fx_win_rate.py", "--days", "0"]
        check("--days 0 exits 1", fw.main() == 1)
    finally:
        sys.argv = old_argv

    print(f"\n{ok} passed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
