#!/usr/bin/env python3
"""Unit tests for scripts/check_soak_freshness.py.

The freshness check gates the weekly drill, so its verdicts must be exact:
absent evidence is informational, stale evidence fails, a non-clean newest
report fails, and a future-dated file fails. All against synthetic
evidence dirs in milliseconds.

Run: python scripts/test_check_soak_freshness.py   (exit 0 = all pass)
"""
import subprocess
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "check_soak_freshness.py"

TODAY = datetime.now(timezone.utc).date()


def run_check(files, extra_args=None):
    """Runs the checker against a synthetic evidence dir; returns (stdout, rc)."""
    with tempfile.TemporaryDirectory() as tmp:
        ev = Path(tmp) / "soak"
        if files is not None:
            ev.mkdir(parents=True)
            for name, body in files.items():
                (ev / name).write_text(body, encoding="utf-8")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--evidence-dir", str(ev)]
            + (extra_args or []),
            capture_output=True, text=True)
        return proc.stdout + proc.stderr, proc.returncode


def report(days_ago, verdict="SOAK CLEAN - gate silent, telemetry intact."):
    date = (TODAY - timedelta(days=days_ago)).isoformat()
    return (f"# Demo soak report\n\n- generated: {date}T10:00:00+00:00\n\n"
            f"## Verdict\n\n{verdict}\n"), f"SOAK-{date}.md"


def test_no_evidence_at_all_is_informational():
    out, rc = run_check(None)
    assert rc == 0, out
    assert "no soak evidence committed yet" in out


def test_fresh_clean_report_passes():
    text, name = report(2)
    out, rc = run_check({name: text})
    assert rc == 0, out
    assert "fresh" in out and "2d old" in out


def test_exactly_max_age_passes_and_one_more_fails():
    text, name = report(14)
    out, rc = run_check({name: text})
    assert rc == 0, out
    text, name = report(15)
    out, rc = run_check({name: text})
    assert rc == 1, out
    assert "STALE" in out


def test_stale_clean_report_fails():
    text, name = report(40)
    out, rc = run_check({name: text})
    assert rc == 1, out
    assert "STALE" in out
    assert "--record docs/soak" in out  # the remedy is in the message


def test_newest_report_without_clean_verdict_fails():
    text, name = report(1, verdict="NO DATA - only 3 journal entries")
    out, rc = run_check({name: text})
    assert rc == 1, out
    assert "no SOAK CLEAN report" in out


def test_older_clean_still_counts_when_newest_is_finding_free():
    # The newest report has NO DATA, but an older one was SOAK CLEAN and
    # within max age — the check reads the newest CLEAN, not the newest file.
    clean_text, clean_name = report(3)
    nodata_text, nodata_name = report(1, verdict="NO DATA - only 3 journal entries")
    out, rc = run_check({clean_name: clean_text, nodata_name: nodata_text})
    assert rc == 0, out
    assert "3d old" in out


def test_future_dated_report_fails():
    text, name = report(-2)
    out, rc = run_check({name: text})
    assert rc == 1, out
    assert "future" in out


def test_allow_empty_no_fails_when_directory_missing():
    out, rc = run_check(None, extra_args=["--allow-empty", "no"])
    assert rc == 1, out
    assert "no soak evidence directory" in out


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
