#!/usr/bin/env python3
"""Tests for scripts/watch_profit_floor.py.

The watcher's verdicts and its one-shot alert contract must be exact:
the save chain (FX_EXIT override/vote -> FX_RISK request -> close
confirmation) is exercised against synthetic journals in temp TF_DATA_DIRs,
and the webhook leg against a local fake endpoint — no real journal, no
real webhook, no trading.

Run: python scripts/test_watch_profit_floor.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "watch_profit_floor.py"


class TmpData:
    """A temp TF_DATA_DIR with journal/ and a settings.json."""

    def __init__(self, webhook_url=""):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        (self.root / "journal").mkdir()
        self.settings(self.root, webhook_url)

    @staticmethod
    def settings(root: Path, webhook_url: str):
        (root / "settings.json").write_text(json.dumps({
            "WebhookUrl": webhook_url, "IsDiscordWebhook": True,
        }), encoding="utf-8")

    def journal(self, rows):
        """Append rows (dicts with Category/Details/Timestamp) in order."""
        day = "20261001"
        path = self.root / "journal" / f"journal_{day}.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            for r in rows:
                env = {"Timestamp": r.get("ts", "2026-10-01T10:00:00.0+00:00"),
                       "Category": r["cat"],
                       "Details": r["details"]}
                fh.write(json.dumps(env) + "\n")

    def state(self):
        p = self.root / "watcher" / "profit-floor-alerts.json"
        if not p.exists():
            return set()
        return set(json.load(open(p, encoding="utf-8")))

    def run(self, *args):
        env = dict(os.environ, TF_DATA_DIR=str(self.root),
                   PYTHONIOENCODING="utf-8")
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args], capture_output=True,
            text=True, encoding="utf-8", env=env, timeout=60)


def exit_row(ts, ticket, mfe, cur, override=None, votes=None, action="hold"):
    payload = {"Ticket": ticket, "Symbol": "EURUSD", "Side": "buy",
               "Action": action, "Score": 10.0, "MfeR": mfe,
               "MaeR": 0.0, "ProfitR": cur, "Override": override,
               "Votes": votes or []}
    return {"cat": "FX_EXIT",
            "details": f"EURUSD #{ticket}: {action} — " + json.dumps(payload),
            "ts": ts}


def risk_row(ts, ticket):
    return {"cat": "FX_RISK",
            "details": f"EURUSD #{ticket}: profit floor breached — exit "
                       f"evaluation requested: " + json.dumps({"Ticket": ticket}),
            "ts": ts}


def close_row(ts, ticket):
    return {"cat": "FX_EXIT",
            "details": f"EURUSD: closed #{ticket} — deal 555 (profit-floor)",
            "ts": ts}


def profit_row(ts, ticket, peak, cur, gb):
    payload = {"Ticket": ticket, "State": "PROFIT_PROTECTED", "CurrentR": cur,
               "PeakR": peak, "MaeR": 0.0, "GivebackPct": gb,
               "GivebackClass": "NORMAL", "FloorR": 0.9 * peak,
               "FloorBreached": False, "RecommendedAction": "HOLD"}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: report — " + json.dumps(payload),
            "ts": ts}


def vote_save(mfe=8.0, cur=1.5):
    # Mirrors FxExitBrain.DrawdownVote's deep-giveback reason verbatim.
    return {"Engine": "drawdown", "Exit": 0.85, "Weight": 1.0,
            "Reason": f"give-back {(mfe - cur) / mfe:.0%} of a {mfe:.1f}R peak "
                      "— protect what remains"}


def test_healthy_book_exits_zero():
    """No saves, posture prints, exit 0."""
    d = TmpData()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 7.0, 12),
        exit_row("2026-10-01T10:01:00.0+00:00", 42, 8.0, 7.0,
                 votes=[{"Engine": "structure", "Exit": 0, "Weight": 1.0,
                         "Reason": "holding"}]),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "giveback event(s)" not in r.stdout
    assert "#42" in r.stdout and "12%" in r.stdout


def test_partial_chain_is_not_verified():
    """Override + risk but NO close confirmation -> found, not VERIFIED."""
    d = TmpData()
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "giveback event(s) found" in r.stdout
    assert "<== VERIFIED" not in r.stdout


def test_full_chain_verifies_exit_2():
    """Override -> FX_RISK -> close confirmation: exit 2, VERIFIED printed."""
    d = TmpData()
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    r = d.run("--no-alert")
    assert r.returncode == 2, r.stdout + r.stderr
    assert "<== VERIFIED" in r.stdout


def test_deep_giveback_vote_counts_as_save():
    """A 0.85 drawdown vote with a gave-back reason is a stage-1 event."""
    d = TmpData()
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 6.0, 1.4,
                 votes=[vote_save()]),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    r = d.run("--no-alert")
    assert r.returncode == 2, r.stdout + r.stderr
    assert "[vote]" in r.stdout


def test_giveback_below_vote_bar_is_not_a_save():
    """A 0.65 watch vote (or a non-giveback reason) never counts."""
    d = TmpData()
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 6.0, 2.4, votes=[
            {"Engine": "drawdown", "Exit": 0.65, "Weight": 1.0,
             "Reason": "watch the giveback"},
        ]),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "giveback event(s)" not in r.stdout


def test_events_before_save_do_not_verify():
    """A risk/close row stamped BEFORE the save row must not complete the
    chain (the chain is forward-looking from the event)."""
    d = TmpData()
    d.journal([
        risk_row("2026-10-01T09:00:00.0+00:00", 42),
        close_row("2026-10-01T09:00:30.0+00:00", 42),
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
    ])
    r = d.run("--no-alert")
    assert r.returncode == 0, r.stdout + r.stderr


def test_daily_capture_rolls_up_decisive_exits_per_day():
    """The daily capture rollup: decisive exits with MFE >= 0.5R bucket per
    UTC day; sub-noise MFE, holds and close-rows never count; zero-trade
    days are dropped (absence of evidence is not 0%)."""
    d = TmpData()
    d.journal([
        # Day 1: banks 2R of 5R -> 40%.
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 5.0, 2.0, action="full"),
        # Day 1 noise: a hold, and a decisive exit with 0.4R MFE.
        exit_row("2026-10-01T11:00:00.0+00:00", 42, 9.0, 0.1, action="hold"),
        exit_row("2026-10-01T12:00:00.0+00:00", 43, 0.4, 0.4, action="full"),
        # Day 2: a drawdown override banks 4R of 4R -> 100%.
        exit_row("2026-10-02T10:00:00.0+00:00", 44, 4.0, 4.0,
                 override="profit-floor"),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "daily profit capture" in r.stdout
    # Capture lines (not the giveback-event lines above them) carry
    # 'trade(s)'.
    day1 = next(ln for ln in r.stdout.splitlines()
                if ln.strip().startswith("2026-10-01") and "trade(s)" in ln)
    day2 = next(ln for ln in r.stdout.splitlines()
                if ln.strip().startswith("2026-10-02") and "trade(s)" in ln)
    assert "40.0%" in day1 and "2.00R" in day1 and "/" in day1 and "5.00R" in day1
    assert "1 trade(s)" in day1
    assert "100.0%" in day2 and "4.00R /" in day2 and "1 trade(s)" in day2
    # The gap day (no trades) must be absent.
    assert "2026-10-03" not in r.stdout


def test_daily_capture_silent_when_no_decisive_exits():
    d = TmpData()
    d.journal([exit_row("2026-10-01T10:00:00.0+00:00", 42, 9.0, 0.1,
                        action="hold")])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "daily profit capture" not in r.stdout


def test_utf8_output_survives_cp1252_scheduled_env():
    """REGRESSION (2026-09-30): the scheduled task runs this script with
    output captured to a file and NO PYTHONIOENCODING, so Windows defaulted
    stdout to cp1252 — and the first non-zero capture day made the █ bar
    (U+2588) crash every pass with UnicodeEncodeError AFTER the posture
    print, silently killing the TP1 section a scheduler would never see.
    Reproduce the broken env exactly: piped stdout, ANSI code page, no
    encoding overrides — the watcher must reconfigure its own streams and
    print the full pass including the TP1 section."""
    if os.name != "nt":
        return
    d = TmpData()
    d.journal([
        # A non-zero capture day: the bar must survive cp1252 stdout.
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 5.0, 2.0, action="full"),
        # A TP1 arm row: the section must print after the capture section.
        {"cat": "FX_PROFIT",
         "details": "EURUSD #42: TP1-ARM: rung prev-day-high 1.1025 (+2.1R) "
                    "armed for #42 (25% plan) — executes when price crosses: "
                    + json.dumps({"Ticket": 42, "Target": "prev-day-high", "PlanPct": 25}),
         "ts": "2026-10-01T10:05:00.0+00:00"},
    ])
    env = dict(os.environ, TF_DATA_DIR=str(d.root))
    env.pop("PYTHONIOENCODING", None)   # the scheduled task has none
    env["PYTHONLEGACYWINDOWSSTDIO"] = "1"  # force the legacy cp1252 path
    r = subprocess.run(
        [sys.executable, str(SCRIPT)], env=env,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,  # piped = not a console
        timeout=60)
    out = r.stdout.decode("utf-8", errors="replace")
    assert r.returncode == 0, out + r.stderr.decode("utf-8", errors="replace")
    assert "daily profit capture" in out, out
    assert "█" in out, out                  # the bar itself, not mojibake
    assert "TP1 prototype" in out and "TP1-ARM" in out, out
    assert "UnicodeEncodeError" not in r.stderr.decode("utf-8", errors="replace")


def test_posture_flags_watch_and_override_bands():
    """40% on a 3R peak is sub-watch; 62% on a 2R peak hits the watch band;
    76% on a 5R peak hits the override bar."""
    d = TmpData()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 1, 3.0, 1.8, 40),
        profit_row("2026-10-01T10:00:01.0+00:00", 2, 2.0, 0.76, 62),
        profit_row("2026-10-01T10:00:02.0+00:00", 3, 5.0, 1.2, 76),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "AT OVERRIDE BAR" in r.stdout
    assert "watch band" in r.stdout


class FakeWebhook(BaseHTTPRequestHandler):
    status = 204
    hits = []

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        FakeWebhook.hits.append(json.loads(self.rfile.read(length) or b"{}"))
        self.send_response(FakeWebhook.status)
        self.end_headers()

    def log_message(self, *args):
        pass


def serve_once(path="/discord/hook"):
    """A fresh fake webhook; the path contains 'discord' by default so the
    watcher's URL sniffing picks the Discord payload shape (the Slack test
    passes its own real-slack-shaped URL)."""
    FakeWebhook.hits = []
    server = HTTPServer(("127.0.0.1", 0), FakeWebhook)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_address[1]}{path}"


def test_alert_posts_once_and_dedups():
    """Verified save -> one POST, state remembers; second pass is silent."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    try:
        r1 = d.run()
        assert r1.returncode == 2, r1.stdout + r1.stderr
        assert "alert: HTTP 204" in r1.stdout, r1.stdout
        assert len(FakeWebhook.hits) == 1
        embed = FakeWebhook.hits[0]["embeds"][0]
        assert "Profit-floor save" in embed["title"]
        assert "42" in embed["title"]
        assert "gave back" not in embed["title"]
        assert "protected what was earned" in embed["description"]

        # Dedup: same journal, second run, no new POST.
        r2 = d.run()
        assert r2.returncode == 2  # still a verified save, just not re-alerted
        assert len(FakeWebhook.hits) == 1
        assert "alert:" not in r2.stdout
        assert d.state(), "state file must record the alerted event"
    finally:
        server.shutdown()


def test_alert_slack_payload_shape():
    """A Slack-format URL (any URL without 'discord' in it) produces the
    attachments payload, not embeds."""
    server, url = serve_once(path="/hooks/slack/T000/B000/xyz")
    d = TmpData(webhook_url=url)
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 1
        att = FakeWebhook.hits[0]["attachments"][0]
        assert att["title"].startswith("\U0001f6e1")
        assert "42" in att["title"]
    finally:
        server.shutdown()


def test_failed_post_leaves_state_untouched():
    """HTTP 500 -> alert FAILED line, no state written, so the next pass
    retries (an alert is not 'sent' until the endpoint accepts it)."""
    server, url = serve_once()
    FakeWebhook.status = 500
    d = TmpData(webhook_url=url)
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert "alert FAILED" in r.stdout
        assert not d.state()
    finally:
        FakeWebhook.status = 204
        server.shutdown()


def test_dry_run_never_posts_or_remembers():
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    try:
        r = d.run("--dry-run")
        assert r.returncode == 2, r.stdout + r.stderr
        assert "dry-run: would alert" in r.stdout
        time.sleep(0.1)
        assert len(FakeWebhook.hits) == 0
        assert not d.state()
    finally:
        server.shutdown()


def test_no_webhook_configured_still_watches():
    """Empty settings -> exit 2 on a verified save, no alert attempt."""
    d = TmpData(webhook_url="")
    d.journal([
        exit_row("2026-10-01T10:00:00.0+00:00", 42, 8.0, 1.5,
                 override="profit-floor"),
        risk_row("2026-10-01T10:01:00.0+00:00", 42),
        close_row("2026-10-01T10:02:00.0+00:00", 42),
    ])
    r = d.run()
    assert r.returncode == 2, r.stdout + r.stderr
    assert "alert" not in r.stdout


def test_corrupt_rows_are_skipped_never_crash():
    d = TmpData()
    path = d.root / "journal" / "journal_20261001.jsonl"
    with open(path, "a", encoding="utf-8") as fh:
        fh.write("{not json at all\n")
        fh.write(json.dumps({"Timestamp": "2026-10-01T10:00:00.0+00:00",
                             "Category": "FX_EXIT",
                             "Details": "garbage without payload"}) + "\n")
        fh.write(json.dumps({"Timestamp": "2026-10-01T10:00:01.0+00:00",
                             "Category": "FX_PROFIT",
                             "Details": "prefix {also not json"}) + "\n")
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr


def test_scheduled_invocation_smoke():
    """The exact invocation a scheduler runs: real journal dir (the local
    machine's %APPDATA% exists even in CI on windows runners... but not on
    Linux). Guard: only assert the run completes with a sane exit code."""
    if os.name != "nt":
        return
    r = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True,
                       text=True, encoding="utf-8", timeout=60)
    assert r.returncode in (0, 2), r.stdout + r.stderr
    assert "open book posture" in r.stdout


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
