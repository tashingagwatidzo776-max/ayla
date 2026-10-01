#!/usr/bin/env python3
"""Tests for scripts/watch_tp1_first_arm.py.

Real subprocess runs against temp TF_DATA_DIRs and local fake webhooks —
no real journal, no real webhook, no trading. Run:
python scripts/test_watch_tp1_first_arm.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "watch_tp1_first_arm.py"


class TmpData:
    def __init__(self, webhook_url=""):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        (self.root / "journal").mkdir()
        (self.root / "settings.json").write_text(json.dumps({
            "WebhookUrl": webhook_url, "IsDiscordWebhook": True,
        }), encoding="utf-8")

    def journal(self, rows):
        path = self.root / "journal" / "journal_20261001.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            for r in rows:
                fh.write(json.dumps({
                    "Timestamp": r["ts"], "Category": r["cat"],
                    "Details": r["details"],
                }) + "\n")

    def state(self) -> dict:
        try:
            with open(self.root / "watcher" / "tp1-first-arm-state.json",
                      encoding="utf-8") as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return {}

    def evidence_lines(self) -> list[str]:
        p = self.root / "watcher" / "tp1-first-arm-evidence.jsonl"
        if not p.exists():
            return []
        return p.read_text(encoding="utf-8").splitlines()

    def run(self, *args):
        env = dict(os.environ, TF_DATA_DIR=str(self.root),
                   PYTHONIOENCODING="utf-8")
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args], capture_output=True,
            text=True, encoding="utf-8", env=env, timeout=60)


def profit_row(ts, ticket, peak, cur, gb, mode="HYBRID_STRUCTURE_ATR"):
    payload = {"Ticket": ticket, "PeakR": peak, "CurrentR": cur,
               "GivebackPct": gb, "TrailingMode": mode}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: report — " + json.dumps(payload),
            "ts": ts}


def arm_row(ts, ticket, target="prev-day-high", price=1.1025, r=2.1):
    payload = {"Ticket": ticket, "Target": target, "TargetPrice": price,
               "TargetR": r, "PlanPct": 25}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: TP1-ARM: rung {target} {price} "
                       f"(+{r}R) armed for #{ticket} (25% plan) — executes "
                       f"when price crosses: " + json.dumps(payload),
            "ts": ts}


def skip_row(ts, ticket):
    payload = {"Ticket": ticket, "TrailingMode": "STRUCTURE_TRAIL",
               "PlanPct": 25}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: TP1-SKIP: STRUCTURE_TRAIL floor "
                       f"owns the partial on #{ticket} — rung not armed: "
                       + json.dumps(payload), "ts": ts}


def close_row(ts, ticket):
    payload = {"Ticket": ticket}
    return {"cat": "FX_EXIT",
            "details": f"EURUSD: closed #{ticket} — deal 901: " + json.dumps(payload),
            "ts": ts}


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
    FakeWebhook.hits = []
    server = HTTPServer(("127.0.0.1", 0), FakeWebhook)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_address[1]}{path}"


def test_quiet_journal_exits_zero():
    d = TmpData()
    d.journal([profit_row("2026-10-01T10:00:00.0+00:00", 42, 4.0, 3.0, 20)])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert r.stdout.strip() == "", r.stdout


def test_skip_rows_dump_but_never_page():
    """A TP1-SKIP is the gate answering — evidence-dumped (the finding is
    real) but NOT the first-sighting event: no page, exit 0."""
    d = TmpData()
    d.journal([skip_row("2026-10-01T10:00:00.0+00:00", 777)])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    lines = d.evidence_lines()
    assert len(lines) == 1 and "TP1-SKIP" in lines[0], lines
    assert not d.state().get("paged")


def test_arm_pages_once_dumps_evidence_dedups():
    """The full contract: first pass pages (exit 2) and dumps every FX_*
    row for the ticket; second pass is silent (exit 0) and appends
    nothing."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        profit_row("2026-10-01T10:06:00.0+00:00", 555, 4.0, 3.0, 20),
    ])
    try:
        r1 = d.run()
        assert r1.returncode == 2, r1.stdout + r1.stderr
        assert len(FakeWebhook.hits) == 1, r1.stdout
        embed = FakeWebhook.hits[0]["embeds"][0]
        assert embed["title"].startswith("\U0001f3af")
        assert "555" in embed["title"]
        assert "prev-day-high" in embed["description"]
        assert "WEEK-TWO-TP1-LOG" in embed["description"]
        lines = d.evidence_lines()
        assert lines, "evidence file must exist"
        assert sum(1 for ln in lines if "TP1-ARM" in ln) == 1
        assert len(lines) == 3, lines   # report + ARM + report

        r2 = d.run()
        assert r2.returncode == 0, r2.stdout + r2.stderr
        assert len(FakeWebhook.hits) == 1
        assert d.evidence_lines() == lines
    finally:
        server.shutdown()


def test_no_webhook_configured_retries_without_state():
    """No WebhookUrl: the page is NOT remembered (retries next pass) and
    exit 0 keeps the scheduler calm — but evidence is still dumped."""
    d = TmpData()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "no WebhookUrl" in r.stdout
    assert d.evidence_lines(), "evidence is dumped regardless"
    assert not d.state().get("paged")


def test_failed_post_leaves_state_untouched():
    """HTTP 500 -> 'pager error', exit 1, no paged state, so the next
    pass retries the page; the evidence dump stands either way."""
    server, url = serve_once()
    FakeWebhook.status = 500
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        close_row("2026-10-01T10:06:00.0+00:00", 555),
    ])
    try:
        r = d.run()
        assert r.returncode == 1, r.stdout + r.stderr
        assert "pager error" in r.stdout
        assert not d.state().get("paged")
        assert d.evidence_lines()
    finally:
        FakeWebhook.status = 204
        server.shutdown()


def test_webhook_recovers_on_second_pass():
    """500 then 204: the first pass fails loudly (exit 1, state clean),
    the second delivers the page (exit 2) — a page is not 'sent' until
    the endpoint accepts it."""
    server, url = serve_once()
    FakeWebhook.status = 500
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
    ])
    try:
        r1 = d.run()
        assert r1.returncode == 1, r1.stdout + r1.stderr
        FakeWebhook.status = 204
        r2 = d.run()
        assert r2.returncode == 2, r2.stdout + r2.stderr
        # Two POSTs total: the failed attempt, then the retried success.
        assert len(FakeWebhook.hits) == 2
        assert d.state().get("paged")
    finally:
        FakeWebhook.status = 204
        server.shutdown()


def test_two_arms_on_different_tickets_page_twice():
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        profit_row("2026-10-01T11:00:00.0+00:00", 556, 4.0, 3.0, 20),
        arm_row("2026-10-01T11:05:00.0+00:00", 556),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 2
        assert len(d.state().get("paged", [])) == 2
    finally:
        server.shutdown()


def exec_row(ts, ticket, lots=0.25, executed=True, retcode=None):
    """The exact shape FxEngineHost journals on the TP1 close attempt:
    lots and Executed, but NO rung R — that lives only in the ARM row."""
    payload = {"Ticket": ticket, "Executed": executed, "Lots": lots,
               "PlanPct": 25, "ArmedPrice": 1.1025}
    if retcode is not None:
        payload["Retcode"] = retcode
    action = "TP1-EXEC: banked" if executed else "TP1-EXEC refused for"
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: {action} "
                       f"{lots} lots (25% plan) of #{ticket}: "
                       + json.dumps(payload), "ts": ts}


def test_exec_pages_with_banked_r_from_arm():
    """The EXEC page carries the graded number: banked lots and the rung
    R resolved from the ARM row (the EXEC payload has no R)."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, lots=0.25),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 2   # ARM page + EXEC page
        titles = [h["embeds"][0]["title"] for h in FakeWebhook.hits]
        assert any("banked" in t for t in titles)
        desc = next(h["embeds"][0]["description"] for h in FakeWebhook.hits
                    if "banked" in h["embeds"][0]["title"])
        # TargetR round-trips through JSON as a float: the C# "+2.1"
        # custom format arrives as 2.1.
        assert "0.25" in desc and "2.1R" in desc and "25% plan" in desc, desc
    finally:
        server.shutdown()


def test_exec_refusal_pages_with_retcode():
    """Executed=false pages too — a refused rung is exactly what grading
    needs to see — with the retcode in the description."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, executed=False,
                 retcode=10018),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 2
        titles = [h["embeds"][0]["title"] for h in FakeWebhook.hits]
        assert any("refused" in t for t in titles)
        desc = next(h["embeds"][0]["description"] for h in FakeWebhook.hits
                    if "refused" in h["embeds"][0]["title"])
        assert "10018" in desc
    finally:
        server.shutdown()


def test_exec_without_matching_arm_still_pages():
    """Defensive: an EXEC with no ARM row in the journal (hand-trimmed
    or rotated away) pages with '?' placeholders instead of crashing."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, lots=0.5),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 1
        desc = FakeWebhook.hits[0]["embeds"][0]["description"]
        assert "?" in desc and "0.5" in desc
    finally:
        server.shutdown()


def main() -> int:
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
