#!/usr/bin/env python3
"""Unit tests for scripts/pre_go_live_check.py.

The pre-go-live check gates the M3 milestone, so its verdicts must be
exact: every checklist leg is exercised against synthetic state in
milliseconds — no sidecar, no real webhook, no live journal. The script
itself is read-only; these tests only prove its verdicts.

Run: python scripts/test_pre_go_live_check.py   (exit 0 = all pass)
"""
import json
import subprocess
import sys
import tempfile
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "pre_go_live_check.py"

TODAY = datetime.now(timezone.utc).date()


def make_repo(tmp, soak_days_ago=1, verdict="SOAK CLEAN - gate silent, telemetry intact."):
    """A synthetic repo root with docs/soak evidence."""
    root = Path(tmp) / "repo"
    (root / "docs" / "soak").mkdir(parents=True)
    date = (TODAY - timedelta(days=soak_days_ago)).isoformat()
    (root / "docs" / "soak" / f"SOAK-{date}.md").write_text(
        f"# Demo soak report\n\n## Verdict\n\n{verdict}\n", encoding="utf-8")
    return root


def make_data_dir(tmp, settings=None, journal_lines=()):
    data = Path(tmp) / "data"
    data.mkdir(parents=True)
    if settings is not None:
        (data / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
    if journal_lines:
        (data / "journal").mkdir()
        (data / "journal" / "journal_20260926.jsonl").write_text(
            "\n".join(journal_lines) + "\n", encoding="utf-8")
    return data


def run_check(tmp, extra_args=None):
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--skip-sidecar", "--data-dir",
         str(Path(tmp) / "data"), "--repo-root", str(Path(tmp) / "repo")] + (extra_args or []),
        capture_output=True, text=True)
    return proc.stdout + proc.stderr, proc.returncode


def journal_line(category):
    return ('{"Timestamp":"2026-09-25T10:00:00.0000000+00:00",'
            f'"AccountId":"11111111-1111-1111-1111-111111111111",'
            f'"Category":"{category}","Details":"{{}}"}}')


GOOD_SETTINGS = {
    "Mt5MaxLots": 1.0, "Mt5DailyLossCap": 25, "Mt5EquityFloor": 0,
    "FxPortfolioMaxLots": 0.10, "NewsBlackoutMinutes": 15,
}


class _StubHook(BaseHTTPRequestHandler):
    """A webhook endpoint that exists (answers HTTP 404) but is never
    posted to — the probe's 'endpoint exists' branch, no network."""

    def do_GET(self):  # noqa: N802
        self.send_response(404)
        self.end_headers()

    def log_message(self, *args):
        pass


class LiveHook:
    """Context manager: a loopback HTTP server that always answers 404."""

    def __enter__(self):
        self.server = HTTPServer(("127.0.0.1", 0), _StubHook)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        return f"http://127.0.0.1:{self.server.server_port}/hook"

    def __exit__(self, *args):
        self.server.shutdown()
        self.server.server_close()
        return False


def test_all_green_passes():
    with tempfile.TemporaryDirectory() as tmp, LiveHook() as hook:
        make_repo(tmp, soak_days_ago=1)
        make_data_dir(tmp, {**GOOD_SETTINGS, "WebhookUrl": hook}, [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 0, out
        assert out.count("[PASS]") == 5, out   # sidecar leg skipped-but-passing
        assert "HTTP 404" in out   # exists-but-404 counts as reachable


def test_zero_lot_cap_fails():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp)
        make_data_dir(tmp, {**GOOD_SETTINGS, "Mt5MaxLots": 0}, [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "DISABLES placement" in out


def test_missing_loss_cap_fails():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp)
        make_data_dir(tmp, {k: v for k, v in GOOD_SETTINGS.items() if k != "Mt5DailyLossCap"},
                      [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "Mt5DailyLossCap" in out


def test_no_journal_signals_fails():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp)
        make_data_dir(tmp, GOOD_SETTINGS)   # no journal at all
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "0 BRAIN_DECISION" in out


def test_stale_soak_evidence_fails():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp, soak_days_ago=20)
        make_data_dir(tmp, GOOD_SETTINGS, [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "STALE" in out


def test_non_clean_newest_report_fails():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp, soak_days_ago=1, verdict="SOAK FINDINGS - 1 issue.")
        make_data_dir(tmp, GOOD_SETTINGS, [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "no SOAK CLEAN" in out


def test_missing_webhook_fails():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp)
        make_data_dir(tmp, {**GOOD_SETTINGS, "WebhookUrl": ""}, [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "no webhook configured" in out


def test_refused_webhook_fails():
    # Port 1 on loopback refuses instantly — nothing is listening, so every
    # alert would die silently. That must FAIL the checklist (the probe's
    # whole point), without touching the network.
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp)
        make_data_dir(tmp, {**GOOD_SETTINGS, "WebhookUrl": "http://127.0.0.1:1/hook"},
                      [journal_line("BRAIN_DECISION")])
        out, rc = run_check(tmp)
        assert rc == 1, out
        assert "unreachable" in out
        assert "nothing is listening" in out


def test_missing_data_dir_is_usage_error():
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--data-dir", "Z:/definitely/not/here",
         "--repo-root", str(HERE.parent)],
        capture_output=True, text=True)
    assert proc.returncode == 2
    assert "data dir not found" in proc.stdout + proc.stderr


def test_sidecar_probe_unreachable_reports_clear_hint():
    with tempfile.TemporaryDirectory() as tmp:
        make_repo(tmp)
        make_data_dir(tmp, GOOD_SETTINGS, [journal_line("BRAIN_DECISION")])
        # No --skip-sidecar: nothing listens on 53190 in the test env
        # unless the trader's real sidecar is up, in which case this test
        # still passes on the healthy branch.
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--data-dir", str(Path(tmp) / "data"),
             "--repo-root", str(Path(tmp) / "repo")],
            capture_output=True, text=True)
        combined = proc.stdout + proc.stderr
        if "sidecar not healthy" in combined:
            assert proc.returncode == 1
            assert "python bridge/mt5_sidecar.py" in combined
        else:
            assert "sidecar healthy" in combined or "terminal connected" in combined


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"ok  {t.__name__}")
        except AssertionError as ex:
            failed += 1
            print(f"FAIL {t.__name__}: {ex}")
        except Exception as ex:  # noqa: BLE001
            failed += 1
            print(f"FAIL {t.__name__}: {ex.__class__.__name__}: {ex}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
