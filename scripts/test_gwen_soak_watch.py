#!/usr/bin/env python3
"""Unit tests for scripts/gwen_soak_watch.py.

The report's numbers must be exactly what the journal proves: verdict
rows counted as pass/veto by their Pass payload, fail-open rows (GWEN
unavailable) counted separately and never mixed into the verdicts, and
coverage measured against FX_DECISION rows in the same window.

Run: python scripts/test_gwen_soak_watch.py   (exit 0 = all pass)
"""
import importlib.util
import json
import os
import sys
import tempfile
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location(
    "gwen_soak_watch", os.path.join(HERE, "gwen_soak_watch.py"))
gw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gw)


def row(ts, category, details, payload=None):
    body = details if payload is None else details + ": " + json.dumps(payload)
    return {
        "Timestamp": ts.isoformat(),
        "AccountId": "00000000-0000-0000-0000-000000000000",
        "Category": category,
        "Details": body,
    }


def make_journal(tmp, rows):
    d = os.path.join(tmp, "journal")
    os.makedirs(d, exist_ok=True)
    by_day = {}
    for ts, category, details, payload in rows:
        day = ts.astimezone(timezone.utc).strftime("%Y%m%d")
        by_day.setdefault(day, []).append(row(ts, category, details, payload))
    for day, entries in by_day.items():
        with open(os.path.join(d, f"journal_{day}.jsonl"), "a",
                  encoding="utf-8") as f:
            for e in entries:
                f.write(json.dumps(e) + "\n")
    return d


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

    noon = datetime.now(timezone.utc).replace(
        hour=12, minute=0, second=0, microsecond=0)

    # ── verdicts, fail-open, coverage, latency ─────────────────────────
    with tempfile.TemporaryDirectory() as tmp:
        rows = []
        # 2 pass verdicts (XAU) with latency.
        for i, lat in enumerate((120, 240)):
            t = noon + timedelta(minutes=i)
            rows.append((t, "FX_DECISION", "decision", None))
            rows.append((t, "FX_GWEN", "XAUUSDmicro: confluence ok", {
                "Symbol": "XAUUSDmicro", "Side": "buy",
                "Alpha": "vol-breakout(14)", "Bias": "long",
                "Confidence": 0.8, "RrOk": True, "Reasons": ["trend up"],
                "LatencyMs": lat, "Pass": True, "Prompt": "p"}))
        # 1 veto verdict (XAG).
        rows.append((noon + timedelta(minutes=2), "FX_DECISION",
                     "decision", None))
        rows.append((noon + timedelta(minutes=2), "FX_GWEN",
                     "XAGUSD: no confluence", {
                         "Symbol": "XAGUSD", "Side": "sell",
                         "Alpha": "kalman-trend", "Bias": "flat",
                         "Confidence": 0.4, "RrOk": False,
                         "Reasons": ["flat tape"], "LatencyMs": 300,
                         "Pass": False, "Prompt": "p"}))
        # 1 fail-open row (unavailable) — never a verdict.
        rows.append((noon + timedelta(minutes=3), "FX_DECISION",
                     "decision", None))
        rows.append((noon + timedelta(minutes=3), "FX_GWEN",
                     "XAGUSD: no verdict from the local model",
                     {"Symbol": "XAGUSD", "Unavailable": True}))
        # Garbage line must not break the load.
        d = make_journal(tmp, rows)
        with open(os.path.join(d, "journal_19990101.jsonl"), "w",
                  encoding="utf-8") as f:
            f.write("{not json}\n")

        verdicts, unavailable, decisions, by_day = gw.load_window(d, days=3)
        s = gw.summarize(verdicts, unavailable, decisions, by_day)

        check("three verdict rows loaded", s["verdicts"] == 3)
        check("one fail-open row separated", s["unavailable"] == 1
              and s["total_observations"] == 4)
        check("pass/veto split by payload", s["passed"] == 2
              and s["vetoed"] == 1)
        check("veto rate = 1/3", abs(s["veto_rate"] - 1 / 3) < 1e-9)
        check("fail-open share = 1/4", abs(s["fail_open_share"] - 0.25) < 1e-9)
        check("coverage = verdicts / decisions", s["decisions"] == 4
              and abs(s["coverage"] - 0.75) < 1e-9)
        check("latency mean/p50/max", abs(s["latency_mean"] - 220.0) < 1e-9
              and s["latency_p50"] == 240 and s["latency_max"] == 300)
        check("bias mix counted", s["bias"].get("long") == 2
              and s["bias"].get("flat") == 1)
        check("per-symbol split", s["symbols"]["XAUUSDmicro"]["pass"] == 2
              and s["symbols"]["XAGUSD"]["veto"] == 1
              and s["symbols"]["XAGUSD"]["unavailable"] == 1)

        out = gw.render(s, "test window")
        check("headline verdicts present", "verdicts: **3**" in out)
        check("fail-open line present", "fail-open (GWEN unavailable): **1**" in out)
        check("coverage line present", "75% of 4 decisions" in out)
        check("latency line present", "mean 220 ms" in out)
        check("symbol table rendered", "| XAUUSDmicro | 2 | 2 | 0 | 0 |" in out)
        check("per-day table rendered", "| " in out and "unavailable" in out)
        check("fail-neutral reminder present",
              "trades exactly as if the gate were off" in out)

    # ── no FX_GWEN rows in window → honest empty report ────────────────
    with tempfile.TemporaryDirectory() as tmp:
        d = make_journal(tmp, [
            (noon, "FX_DECISION", "decision", None),
        ])
        verdicts, unavailable, decisions, by_day = gw.load_window(d, days=3)
        s = gw.summarize(verdicts, unavailable, decisions, by_day)
        out = gw.render(s, "t")
        check("zero observations reported", s["total_observations"] == 0)
        check("empty-window line present",
              "No FX_GWEN rows in the window" in out)

    # ── stale rows outside the window are excluded ─────────────────────
    with tempfile.TemporaryDirectory() as tmp:
        d = make_journal(tmp, [
            (noon - timedelta(days=10), "FX_GWEN", "old", {
                "Symbol": "XAUUSDmicro", "Pass": True, "LatencyMs": 100}),
            (noon, "FX_GWEN", "new", {
                "Symbol": "XAUUSDmicro", "Pass": False, "LatencyMs": 100}),
        ])
        verdicts, unavailable, _, _ = gw.load_window(d, days=3)
        check("window filters stale rows", len(verdicts) == 1
              and not verdicts[0].get("Pass"))

    # ── usage ──────────────────────────────────────────────────────────
    old_argv = sys.argv
    try:
        sys.argv = ["gwen_soak_watch.py", "--days", "0"]
        check("--days 0 exits 1", gw.main() == 1)
    finally:
        sys.argv = old_argv

    print(f"\n{ok} passed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
