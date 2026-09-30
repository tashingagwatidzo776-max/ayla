#!/usr/bin/env python3
"""Tests for scripts/dd_weight_backtest.py.

The backtest replays journaled FX_EXIT evaluations at drawdown weight 1.0
vs 2.0, so its arithmetic and its journal parsing must be exact: the
resolver bands, the override pass-through, and the score-reproduction
sanity probe are pinned here against synthetic journals in a temp dir —
no real journal, no live stack.

Run: python scripts/test_dd_weight_backtest.py   (exit 0 = all pass)
"""
from __future__ import annotations

import importlib.util
import json
import os
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent


def _load_module():
    spec = importlib.util.spec_from_file_location(
        "dd_weight_backtest", HERE / "dd_weight_backtest.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


MOD = _load_module()


def vote(engine, exit_, weight):
    return {"Engine": engine, "Exit": exit_, "Weight": weight, "Reason": "r"}


def decision_row(ticket, votes, action="hold", score=None, override=None,
                 timestamp="2026-09-30T12:00:00"):
    if score is None:
        score = round(sum(v["Exit"] * v["Weight"] for v in votes)
                      / sum(v["Weight"] for v in votes) * 100, 2)
    dec = {"Ticket": ticket, "Symbol": "EURUSD", "Side": "buy",
           "Action": action, "Score": score, "Votes": votes,
           "MfeR": 3.0, "MaeR": 0.0, "ProfitR": 1.5,
           "Override": override}
    envelope = {"Timestamp": timestamp,
                "AccountId": "00000000-0000-0000-0000-000000000000",
                "Category": "FX_EXIT",
                "Details": f"EURUSD #{ticket}: hold score {score} — test: "
                           + json.dumps(dec)}
    # The app's journal writer emits compact JSON (no space after ':') —
    # match it exactly or the category fast-path skips the row.
    return json.dumps(envelope, separators=(",", ":"))


def floor_row(ticket):
    envelope = {"Timestamp": "2026-09-30T12:00:00",
                "AccountId": "00000000-0000-0000-0000-000000000000",
                "Category": "FX_FLOOR",
                "Details": f"floor breach on XAUUSDmicro #{ticket}: "
                           "peak 25.3R floor breached +20.31R — commanding close"}
    return json.dumps(envelope, separators=(",", ":"))


def write_journal(rows, appdata):
    jdir = Path(appdata) / "tf" / "data" / "journal"
    jdir.mkdir(parents=True, exist_ok=True)
    with open(jdir / "journal_20260930.jsonl", "w", encoding="utf-8") as fh:
        fh.write("\n".join(rows) + "\n")


def run_backtest(appdata, capsys_like):
    import io
    from contextlib import redirect_stdout
    old = os.environ.get("APPDATA")
    os.environ["APPDATA"] = appdata
    buf = io.StringIO()
    try:
        with redirect_stdout(buf):
            rc = MOD.main()
    finally:
        if old is None:
            os.environ.pop("APPDATA", None)
        else:
            os.environ["APPDATA"] = old
    capsys_like.append(buf.getvalue())
    return rc


def test_resolve_bands():
    # Edges pinned in FxExitBrain.Resolve: 85/70/55/35.
    assert MOD.resolve(85.0) == "full"
    assert MOD.resolve(84.99) == "partial"
    assert MOD.resolve(70.0) == "partial"
    assert MOD.resolve(69.99) == "tighten"
    assert MOD.resolve(55.0) == "tighten"
    assert MOD.resolve(54.99) == "monitor"
    assert MOD.resolve(35.0) == "monitor"
    assert MOD.resolve(34.99) == "hold"
    print("PASS resolve bands pin 85/70/55/35")


def test_replay_reweights_drawdown_only():
    votes = [vote("structure", 0.0, 1.6), vote("momentum", 0.0, 1.0),
             vote("volatility", 0.0, 1.2), vote("time", 0.0, 1.1),
             vote("thesis", 0.0, 1.4), vote("drawdown", 1.0, 2.0)]
    s20 = MOD.replay(votes, 2.0)
    s10 = MOD.replay(votes, 1.0)
    # drawdown alone at exit 1: score = 2.0/8.3 = 24.10 at w2, 1.0/7.3 = 13.70 at w1
    assert abs(s20 - 2.0 / 8.3 * 100) < 1e-9
    assert abs(s10 - 1.0 / 7.3 * 100) < 1e-9
    print("PASS replay moves only the drawdown weight (8.3 -> 7.3 denominator)")


def test_override_rows_never_flip():
    votes = [vote("structure", 0.0, 1.6), vote("momentum", 0.0, 1.0),
             vote("volatility", 0.0, 1.2), vote("time", 0.0, 1.1),
             vote("thesis", 0.0, 1.4), vote("drawdown", 1.0, 2.0)]
    with tempfile.TemporaryDirectory() as tmp:
        write_journal([
            decision_row(111, votes, action="full", score=100, override="drawdown"),
            floor_row(111),
        ], tmp)
        out = []
        rc = run_backtest(tmp, out)
        assert rc == 0
        text = out[0]
        assert "override-tier rows (replayed unchanged): 1" in text
        assert "drawdown: 1" in text
        assert "decision flips 2.0 -> 1.0: 0 of 0" in text
        assert "[111]" in text.split("guard-save tickets seen")[1]
    print("PASS override rows counted, never flipped; floor ticket joined")


def test_exit_boundary_flip_detected():
    # Craft a decision sitting just inside the partial band at w2 that
    # drops into tighten at w1: heavy drawdown exit vote, moderate rest.
    votes = [vote("structure", 0.65, 1.6), vote("momentum", 0.6, 1.0),
             vote("volatility", 0.6, 1.2), vote("time", 0.6, 1.1),
             vote("thesis", 0.6, 1.4), vote("drawdown", 1.0, 2.0)]
    s20 = sum(v["Exit"] * v["Weight"] for v in votes) / 8.3 * 100
    s10 = sum(v["Exit"] * (1.0 if v["Engine"] == "drawdown" else v["Weight"])
              for v in votes) / 7.3 * 100
    # drawdown exit 1.0 means w1 score < w2 score; make w2 partial, w1 tighten
    assert 70.0 <= s20 < 85.0 and 55.0 <= s10 < 70.0, (s20, s10)
    with tempfile.TemporaryDirectory() as tmp:
        write_journal([decision_row(222, votes)], tmp)
        out = []
        rc = run_backtest(tmp, out)
        assert rc == 0
        text = out[0]
        assert "EXIT-boundary flips (touch partial/full): 1" in text
        assert "partial@" in text and "tighten@" in text
    print("PASS exit-boundary flip (partial -> tighten) detected")


def test_advisory_flip_and_mismatch_skip():
    # monitor->hold flip: a moderate drawdown vote (0.5) drags the w2
    # score into monitor while w1's lighter weight drops it back to hold.
    # Zero-exit engines still count toward the denominator: wexit(w2) =
    # 0.56+0.66+0.70+1.00 = 2.92 / 8.3 = 35.18 monitor; wexit(w1) =
    # 2.42 / 7.3 = 33.15 hold.
    votes = [vote("structure", 0.35, 1.6), vote("momentum", 0.0, 1.0),
             vote("volatility", 0.55, 1.2), vote("time", 0.0, 1.1),
             vote("thesis", 0.5, 1.4), vote("drawdown", 0.5, 2.0)]
    s20 = MOD.replay(votes, 2.0)
    s10 = MOD.replay(votes, 1.0)
    assert 35.0 <= s20 < 55.0 and s10 < 35.0, (s20, s10)
    with tempfile.TemporaryDirectory() as tmp:
        write_journal([
            decision_row(333, votes),                          # flips monitor->hold
            decision_row(444, votes, score=91.0),              # logged score wrong -> skipped
            decision_row(555, votes, override="profit-floor",  # override: unchanged
                         action="full", score=100),
        ], tmp)
        out = []
        rc = run_backtest(tmp, out)
        assert rc == 0
        text = out[0]
        assert "advisory-only flips (hold/monitor/tighten): 1 across 1 tickets" in text
        assert "#333: 1 rows, monitor->hold" in text
        assert "score-reproduction mismatches (schema drift probe): 1" in text
        assert "advisory-only flips (hold/monitor/tighten): 1 across 1 tickets" in text
        assert "override-tier rows (replayed unchanged): 1" in text
    print("PASS advisory flip counted; score mismatch skipped; override unchanged")


def test_pre_roster_and_garbage_skipped():
    votes = [vote("structure", 0.0, 1.6), vote("momentum", 0.0, 1.0),
             vote("volatility", 0.0, 1.2), vote("time", 0.0, 1.1),
             vote("thesis", 0.0, 1.4), vote("drawdown", 0.9, 1.5)]  # wrong weight
    with tempfile.TemporaryDirectory() as tmp:
        write_journal([
            decision_row(666, votes),                 # dd weight 1.5 -> skipped
            # A line claiming FX_EXIT but not parseable JSON -> skipped.
            json.dumps({"Category": "FX_EXIT"}, separators=(",", ":"))
            + " trailing garbage",
            json.dumps({"Timestamp": "t", "Category": "FX_EXIT",
                        "Details": "no payload here"},
                       separators=(",", ":")),  # no votes -> skipped
        ], tmp)
        out = []
        rc = run_backtest(tmp, out)
        assert rc == 0
        text = out[0]
        assert "decision flips 2.0 -> 1.0: 0 of 0" in text
        assert "(skipped 3" in text
    print("PASS pre-roster weights and garbage rows skipped, never counterfeited")


if __name__ == "__main__":
    test_resolve_bands()
    test_replay_reweights_drawdown_only()
    test_override_rows_never_flip()
    test_exit_boundary_flip_detected()
    test_advisory_flip_and_mismatch_skip()
    test_pre_roster_and_garbage_skipped()
    print("ALL PASS")
