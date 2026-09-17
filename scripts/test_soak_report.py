#!/usr/bin/env python3
"""Unit tests for scripts/soak_report.py's reduction and verdicts.

The soak report is only useful if its verdicts are trustworthy: a gate
refusal on a demo account, a malformed settlement, or a reconnect storm
must flip the exit code, and a clean soak must pass. These tests run the
script against synthetic journal/heartbeat data in milliseconds — no
Deriv, no app, no network.

Run: python scripts/test_soak_report.py   (exit 0 = all pass)
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "soak_report.py"


def run_report(journal_lines, heartbeat_lines, extra_args=None):
    """Runs the script on a synthetic data dir; returns (stdout, returncode)."""
    with tempfile.TemporaryDirectory() as tmp:
        data = Path(tmp) / "data"
        (data / "journal").mkdir(parents=True)
        (data / "heartbeats").mkdir(parents=True)
        (data / "journal" / "journal_20260917.jsonl").write_text(
            "\n".join(json.dumps(e) for e in journal_lines) + "\n", encoding="utf-8")
        (data / "heartbeats" / "heartbeat_20260917.jsonl").write_text(
            "\n".join(json.dumps(e) for e in heartbeat_lines) + "\n", encoding="utf-8")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--data", str(data)] + (extra_args or []),
            capture_output=True, text=True)
        return proc.stdout, proc.returncode


def entry(category, details, ts="2026-09-17T10:00:00+00:00", account="11111111-1111-1111-1111-111111111111"):
    return {"Timestamp": ts, "AccountId": account, "Category": category, "Details": details}


def settlement(profit=1.0, bankroll=6.0, won=True):
    return entry("TRADE_SETTLEMENT", json.dumps({
        "ContractId": "c1", "Won": won, "Payout": 1.9 if won else 0.0,
        "Profit": profit, "NewBankroll": bankroll}))


def heartbeat(frm, to, name="DemoAcc"):
    return {"Timestamp": "2026-09-17T10:05:00+00:00",
            "Details": json.dumps({"AccountName": name, "From": frm, "To": to})}


GATE_REFUSAL = entry("GROWTH_STATE", json.dumps({
    "State": "real-money-gate", "Bankroll": 0, "LossStreak": 0, "Reason": "start refused"}))


def test_clean_soak_passes():
    out, rc = run_report(
        [settlement(), settlement(profit=-1.0, bankroll=5.0, won=False)],
        [heartbeat("Connecting…", "Connected"), heartbeat("Reconnecting…", "Connected")])
    assert rc == 0, out
    assert "SOAK CLEAN" in out
    assert "PASS - gate refusals" in out
    assert "2 settled trades, 1 won / 1 lost, net +0.00" in out
    assert "DemoAcc: 2 reconnect(s)" in out


def test_gate_refusal_fails_soak():
    out, rc = run_report([GATE_REFUSAL], [])
    assert rc == 1, out
    assert "FAIL - gate refusals" in out
    assert "start refused" in out
    assert "FINDINGS" in out


def test_malformed_settlement_fails_soak():
    out, rc = run_report([entry("TRADE_SETTLEMENT", "{not json")], [])
    assert rc == 1, out
    assert "FAIL - telemetry integrity" in out
    assert "unparseable settlement" in out


def test_settlement_without_bankroll_fails_soak():
    out, rc = run_report([entry("TRADE_SETTLEMENT", json.dumps({"ContractId": "c", "Won": True}))], [])
    assert rc == 1, out
    assert "FAIL - telemetry integrity" in out


def test_reconnect_storm_fails_soak():
    beats = [heartbeat("Error", "Connected") for _ in range(25)]
    out, rc = run_report([], beats)
    assert rc == 1, out
    assert "FINDINGS" in out
    assert "DemoAcc: 25 reconnect(s)" in out


def test_unparseable_journal_line_fails_soak():
    with tempfile.TemporaryDirectory() as tmp:
        data = Path(tmp) / "data"
        (data / "journal").mkdir(parents=True)
        (data / "heartbeats").mkdir(parents=True)
        (data / "journal" / "journal_20260917.jsonl").write_text(
            "{broken json line\n", encoding="utf-8")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--data", str(data)],
            capture_output=True, text=True)
        assert proc.returncode == 1, proc.stdout
        assert "1 unparseable journal lines" in proc.stdout


def test_since_filter_excludes_old_entries():
    old = settlement()
    old["Timestamp"] = "2026-08-01T10:00:00+00:00"
    out, rc = run_report([old], [], extra_args=["--since", "2026-09-17"])
    # the old settlement is filtered out; report is clean with 0 settlements
    assert rc == 0, out
    assert "0 settled trades" in out


def test_governor_and_arms_are_informational():
    gov = entry("GROWTH_STATE", json.dumps({
        "State": "portfolio-governor", "Bankroll": -6.0, "LossStreak": 0, "Reason": "cap breached"}))
    arm = entry("REAL_MONEY_UNLOCK_ARMED", "manual surfaces joined")
    out, rc = run_report([gov, arm], [])
    assert rc == 0, out
    assert "governor entries: 1" in out
    assert "unlock arms: 1" in out


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except AssertionError as e:
            failed += 1
            print(f"FAIL {t.__name__}: {e}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
