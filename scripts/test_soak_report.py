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


def settlement(profit=1.0, bankroll=6.0, won=True, ts="2026-09-17T10:00:00+00:00"):
    return entry("TRADE_SETTLEMENT", json.dumps({
        "ContractId": "c1", "Won": won, "Payout": 1.9 if won else 0.0,
        "Profit": profit, "NewBankroll": bankroll}), ts=ts)


def settlements_above_floor(n=12):
    """Enough settlements to clear the 10-entry NO DATA floor."""
    return [settlement(profit=1.0 if i % 2 == 0 else -1.0, bankroll=5.0 + i * 0.1,
                       won=i % 2 == 0, ts=f"2026-09-17T10:{i:02d}:00+00:00")
            for i in range(n)]


def heartbeat(frm, to, name="DemoAcc"):
    return {"Timestamp": "2026-09-17T10:05:00+00:00",
            "Details": json.dumps({"AccountName": name, "From": frm, "To": to})}


GATE_REFUSAL = entry("GROWTH_STATE", json.dumps({
    "State": "real-money-gate", "Bankroll": 0, "LossStreak": 0, "Reason": "start refused"}))


def test_clean_soak_passes():
    out, rc = run_report(
        settlements_above_floor(),
        [heartbeat("Connecting…", "Connected"), heartbeat("Reconnecting…", "Connected")])
    assert rc == 0, out
    assert "SOAK CLEAN" in out
    assert "PASS - gate refusals" in out
    assert "12 settled trades, 6 won / 6 lost" in out
    assert "12 journal entries" in out
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
    entries = settlements_above_floor() # 10 in-window
    old = settlement()
    old["Timestamp"] = "2026-08-01T10:00:00+00:00"
    out, rc = run_report(entries + [old], [], extra_args=["--since", "2026-09-17"])
    # the old settlement is filtered out; the twelve in-window ones remain
    assert rc == 0, out
    assert "12 settled trades" in out


def test_empty_soak_reports_no_data_not_clean():
    # An empty soak must never read as evidence: below the entry floor a
    # clean run exits 3 (NO DATA), not 0.
    out, rc = run_report([], [])
    assert rc == 3, out
    assert "NO DATA" in out
    assert "SOAK CLEAN" not in out


def test_thin_but_clean_soak_degrades_to_no_data():
    out, rc = run_report([settlement() for _ in range(10)], [])
    assert rc == 0, out  # exactly at the floor of 10
    out, rc = run_report([settlement() for _ in range(5)], [])
    assert rc == 3, out
    assert "NO DATA" in out


def test_findings_outrank_no_data():
    # A gate refusal in a tiny journal is a FINDING (exit 1), not NO DATA —
    # emptiness must not mask a real problem.
    out, rc = run_report([GATE_REFUSAL], [])
    assert rc == 1, out
    assert "FAIL - gate refusals" in out
    assert "FINDINGS" in out


def test_record_flag_writes_dated_evidence():
    entries = settlements_above_floor()
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = run_report(entries, [], extra_args=["--record", tmp])
        assert rc == 0, out
        files = list(Path(tmp).glob("SOAK-*.md"))
        assert len(files) == 1, files
        assert files[0].read_text(encoding="utf-8").startswith("# Demo soak report")
        # the summary line is embedded for machine consumption
        assert "SOAK-SUMMARY: entries=12 settlements=12 refusals=0 verdict=CLEAN" in \
            files[0].read_text(encoding="utf-8")


def test_record_regenerates_trend_table():
    with tempfile.TemporaryDirectory() as tmp:
        Path(tmp, "README.md").write_text("# Demo soak evidence\n\nbody\n", encoding="utf-8")
        out, rc = run_report(settlements_above_floor(), [], extra_args=["--record", tmp])
        assert rc == 0, out
        readme = Path(tmp, "README.md").read_text(encoding="utf-8")
        assert "<!-- soak-trend:start -->" in readme and "<!-- soak-trend:end -->" in readme
        assert "| Date | Entries | Settlements | Refusals | Verdict |" in readme
        date = sorted(p.name for p in Path(tmp).glob("SOAK-*.md"))[0][len("SOAK-"):-len(".md")]
        assert f"| {date} | 12 | 12 | 0 | CLEAN |" in readme
        # the original body survives outside the markers
        assert "body" in readme


def test_trend_table_orders_newest_first_and_reads_summaries():
    with tempfile.TemporaryDirectory() as tmp:
        Path(tmp, "README.md").write_text("x", encoding="utf-8")
        rec = lambda: run_report(settlements_above_floor(), [], extra_args=["--record", tmp])
        rec()  # records SOAK-<today>.md using the machine clock
        today_name = sorted(p.name for p in Path(tmp).glob("SOAK-*.md"))[0]
        today_date = today_name[len("SOAK-"):-len(".md")]
        older = Path(tmp, "SOAK-2026-09-10.md")
        # a legacy report: prose verdict only, no summary line
        older.write_text("# Demo soak report\n\n## Verdict\n\nFINDINGS - something\n", encoding="utf-8")
        rec()
        readme = Path(tmp, "README.md").read_text(encoding="utf-8")
        block = readme.split("<!-- soak-trend:start -->")[1].split("<!-- soak-trend:end -->")[0]
        assert block.index(today_date) < block.index("2026-09-10"), block
        assert f"| {today_date} | 12 | 12 | 0 | CLEAN |" in block
        assert "| 2026-09-10 | ? | ? | ? | FINDINGS |" in block


def test_trend_table_is_idempotent_and_survives_no_readme():
    with tempfile.TemporaryDirectory() as tmp:
        # no README: recording works, no trend update attempted
        out, rc = run_report(settlements_above_floor(), [], extra_args=["--record", tmp])
        assert rc == 0, out
        assert not Path(tmp, "README.md").exists()
        # with a README, re-runs do not duplicate the block
        Path(tmp, "README.md").write_text("r\n", encoding="utf-8")
        run_report(settlements_above_floor(), [], extra_args=["--record", tmp])
        run_report(settlements_above_floor(), [], extra_args=["--record", tmp])
        readme = Path(tmp, "README.md").read_text(encoding="utf-8")
        assert readme.count("<!-- soak-trend:start -->") == 1


def test_min_entries_flag_lowers_the_floor():
    out, rc = run_report([settlement(), settlement()], [], extra_args=["--min-entries", "2"])
    assert rc == 0, out
    assert "SOAK CLEAN" in out


def test_governor_and_arms_are_informational():
    gov = entry("GROWTH_STATE", json.dumps({
        "State": "portfolio-governor", "Bankroll": -6.0, "LossStreak": 0, "Reason": "cap breached"}))
    arm = entry("REAL_MONEY_UNLOCK_ARMED", "manual surfaces joined")
    out, rc = run_report([gov, arm] + settlements_above_floor(), [])
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
