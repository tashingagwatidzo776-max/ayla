#!/usr/bin/env python3
"""Tests for scripts/watch_closed_row.py.

The watcher's verdicts and its one-shot alert contract must be exact:
every finding rule is exercised against synthetic journals in temp
TF_DATA_DIRs, the vanish rule against a local fake bridge (/positions +
/account), and the webhook leg against a local fake endpoint — no real
journal, no real bridge, no trading.

Run: python scripts/test_watch_closed_row.py   (exit 0 = all pass)
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
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "watch_closed_row.py"


def ts_ago(minutes: float) -> str:
    return (datetime.now(timezone.utc) - timedelta(minutes=minutes)).isoformat()


class TmpData:
    """A temp TF_DATA_DIR with journal/ and a settings.json."""

    def __init__(self, webhook_url=""):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        (self.root / "journal").mkdir()
        (self.root / "settings.json").write_text(json.dumps({
            "WebhookUrl": webhook_url, "IsDiscordWebhook": True,
        }), encoding="utf-8")

    def journal(self, rows):
        """Append rows (dicts with Category/Details/Timestamp) in order."""
        path = self.root / "journal" / "journal_20261001.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            for r in rows:
                env = {"Timestamp": r.get("ts", ts_ago(0)),
                       "Category": r["cat"],
                       "Details": r["details"]}
                fh.write(json.dumps(env) + "\n")

    def state(self):
        p = self.root / "watcher" / "closed-row-alerts.json"
        if not p.exists():
            return None
        with open(p, encoding="utf-8") as fh:
            return json.load(fh)

    def run(self, *args, bridge=None, webhook=None):
        env = dict(os.environ, TF_DATA_DIR=str(self.root),
                   PYTHONIOENCODING="utf-8")
        cmd = [sys.executable, str(SCRIPT), *args]
        if bridge:
            cmd += ["--positions-url", bridge.positions_url,
                    "--account-url", bridge.account_url]
        return subprocess.run(cmd, capture_output=True, text=True,
                              encoding="utf-8", env=env, timeout=60)


# ── row fixtures (shapes the parser must recognize) ────────────────

def ops_close(ts, ticket):
    return {"cat": "FX_EXIT", "ts": ts,
            "details": f"closed #{ticket} - operator reconcile: venue flat "
                       f"verified 2026-10-06 (positions [], equity == balance, "
                       f"margin 0): " + json.dumps({"Ticket": ticket})}


def inapp_close(ts, ticket):
    return {"cat": "FX_EXIT", "ts": ts,
            "details": f"#{ticket}: closed #{ticket} — deal 777 (profit floor "
                       f"exit confirmed by broker reconciliation): "
                       + json.dumps({"Ticket": ticket})}


def floor_confirm(ts, ticket):
    return {"cat": "FX_FLOOR", "ts": ts,
            "details": f"#{ticket}: PROFIT FLOOR EXIT CONFIRMED — the broker no "
                       f"longer holds the ticket; giveback prevented: "
                       + json.dumps({"Ticket": ticket})}


def fill_row(ts, ticket):
    return {"cat": "FX_ORDER", "ts": ts,
            "details": f"paper-exec fill (demo): buy 0.1 lots XAUUSDmicro @ "
                       f"4145.53 — ticket {ticket}: "
                       + json.dumps({"Ticket": ticket})}


# ── fake bridge + fake webhook ─────────────────────────────────────

class FakeBridge:
    """Loopback /positions + /account with mutable state."""

    def __init__(self):
        self.positions: list = []
        self.account = {"balance": 1000.0, "equity": 1000.0, "margin": 0.0}
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path.startswith("/positions"):
                    body = {"positions": outer.positions}
                elif self.path.startswith("/account"):
                    body = dict(outer.account)
                else:
                    self.send_response(404)
                    self.end_headers()
                    return
                data = json.dumps(body).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(data)

            def log_message(self, *args):   # keep test output clean
                pass

        self._srv = HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self._srv.serve_forever, daemon=True).start()
        port = self._srv.server_address[1]
        self.positions_url = f"http://127.0.0.1:{port}/positions"
        self.account_url = f"http://127.0.0.1:{port}/account"

    def close(self):
        self._srv.shutdown()


class FakeWebhook:
    def __init__(self):
        self.hits: list = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("Content-Length", 0))
                body = self.rfile.read(length)
                self.server.captured.append(json.loads(body.decode("utf-8")))
                self.send_response(204)
                self.end_headers()

            def log_message(self, *args):
                pass

        self._srv = HTTPServer(("127.0.0.1", 0), Handler)
        self._srv.captured = []
        threading.Thread(target=self._srv.serve_forever, daemon=True).start()
        port = self._srv.server_address[1]
        # The "discord" in the URL selects the Discord payload shape,
        # same detection the watcher shares with the metrics digest.
        self.url = f"http://127.0.0.1:{port}/discord/hook"

    @property
    def posts(self):
        return self._srv.captured

    def close(self):
        self._srv.shutdown()


# ── tests ──────────────────────────────────────────────────────────

def test_baseline_primes_existing_history_silently():
    """Pre-fix ops-reconcile rows must never page: the first pass records
    them as baseline and exits 0."""
    d = TmpData()
    hook = FakeWebhook()
    try:
        d.journal([ops_close(ts_ago(120), 111),
                   ops_close(ts_ago(60), 222)])
        r = d.run("--webhook-url", hook.url)
        assert r.returncode == 0, r.stdout + r.stderr
        assert "baseline primed: 2" in r.stdout, r.stdout
        st = d.state()
        assert st is not None and len(st["alerted"]) == 2, st
        assert hook.posts == [], hook.posts
        # second pass: still clean, still silent
        r2 = d.run("--webhook-url", hook.url)
        assert r2.returncode == 0, r2.stdout
        assert hook.posts == [], hook.posts
    finally:
        d._tmp.cleanup()
        hook.close()


def test_new_ops_reconcile_row_pages_exactly_once():
    """A NEW operator-reconcile close row is the regression itself: the
    app failed to write its own row. Pages once, deduped afterwards."""
    d = TmpData()
    hook = FakeWebhook()
    try:
        r = d.run("--webhook-url", hook.url)          # baseline (empty)
        assert r.returncode == 0, r.stdout
        d.journal([ops_close(ts_ago(1), 333)])
        r2 = d.run("--webhook-url", hook.url)
        assert r2.returncode == 2, r2.stdout + r2.stderr
        assert len(hook.posts) == 1, hook.posts
        embed = hook.posts[0]["embeds"][0]
        assert "closed #" in embed["title"] or "closed #" in embed["description"]
        assert "#333" in embed["description"], embed
        assert "ops-reconcile-close" in embed["description"], embed
        r3 = d.run("--webhook-url", hook.url)
        assert r3.returncode == 0, r3.stdout     # deduped
        assert len(hook.posts) == 1, hook.posts
    finally:
        d._tmp.cleanup()
        hook.close()


def test_floor_confirm_with_in_app_close_is_silent():
    """The fixed path: floor EXIT CONFIRMED + in-app close row in the
    same cycle — nothing to page."""
    d = TmpData()
    hook = FakeWebhook()
    try:
        d.run("--webhook-url", hook.url)             # baseline
        d.journal([floor_confirm(ts_ago(30), 555),
                   inapp_close(ts_ago(30), 555)])
        r = d.run("--webhook-url", hook.url)
        assert r.returncode == 0, r.stdout
        assert hook.posts == [], hook.posts
    finally:
        d._tmp.cleanup()
        hook.close()


def test_floor_confirm_without_close_pages_after_grace():
    """A confirm older than the same-cycle grace with no close row is a
    miss; a just-written confirm is still within grace (never a false
    page mid-cycle)."""
    d = TmpData()
    hook = FakeWebhook()
    try:
        d.run("--webhook-url", hook.url)             # baseline
        d.journal([floor_confirm(ts_ago(2), 666)])   # inside 10-min grace
        r = d.run("--webhook-url", hook.url)
        assert r.returncode == 0, r.stdout
        assert hook.posts == [], hook.posts
        d.journal([floor_confirm(ts_ago(15), 777)])  # past grace, no close
        r2 = d.run("--webhook-url", hook.url)
        assert r2.returncode == 2, r2.stdout
        assert len(hook.posts) == 1, hook.posts
        assert "#777" in json.dumps(hook.posts[0]), hook.posts
    finally:
        d._tmp.cleanup()
        hook.close()


def test_vanish_rule_needs_two_passes_and_proven_flat_venue():
    """A fill with no close row only pages when the venue provably does
    not hold it, sustained across two passes — and never when the
    account is not settled (fail closed)."""
    d = TmpData()
    hook = FakeWebhook()
    bridge = FakeBridge()
    try:
        r = d.run("--webhook-url", hook.url, bridge=bridge)   # baseline
        assert r.returncode == 0, r.stdout

        # Young fill: too early to call it gone.
        d.journal([fill_row(ts_ago(5), 999)])
        r = d.run("--webhook-url", hook.url, bridge=bridge)
        assert r.returncode == 0, r.stdout

        # 30-min fill, venue proven flat (empty pair + settled account):
        # first sighting only arms the pending timer.
        d.journal([fill_row(ts_ago(30), 888)])
        r = d.run("--webhook-url", hook.url, bridge=bridge)
        assert r.returncode == 0, r.stdout
        st = d.state()
        assert "888" in (st or {}).get("pending", {}), st
        assert hook.posts == [], hook.posts

        # Second sustained pass: pages.
        r = d.run("--webhook-url", hook.url, bridge=bridge)
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(hook.posts) == 1, hook.posts
        assert "#888" in json.dumps(hook.posts[0]), hook.posts

        # Ticket back at the venue: pending clears, no further pages.
        bridge.positions = [{"ticket": 888, "symbol": "XAUUSDmicro"}]
        r = d.run("--webhook-url", hook.url, bridge=bridge)
        assert r.returncode == 0, r.stdout
        st = d.state()
        assert "888" not in (st or {}).get("pending", {}), st
        assert len(hook.posts) == 1, hook.posts

        # Unsettled account (live margin): vanish is never proven — two
        # passes stay silent even with the ticket absent.
        bridge.positions = []
        bridge.account = {"balance": 1000.0, "equity": 995.0, "margin": 5.0}
        d.journal([fill_row(ts_ago(30), 777)])
        r = d.run("--webhook-url", hook.url, bridge=bridge)
        assert r.returncode == 0, r.stdout
        r = d.run("--webhook-url", hook.url, bridge=bridge)
        assert r.returncode == 0, r.stdout
        assert len(hook.posts) == 1, hook.posts
    finally:
        d._tmp.cleanup()
        hook.close()
        bridge.close()


def test_no_alert_flag_reports_but_never_pages_or_forgets():
    """--no-alert observes: the finding prints, exit 2, nothing posted,
    and the state does NOT swallow it (so a later armed pass pages)."""
    d = TmpData()
    hook = FakeWebhook()
    try:
        d.run("--no-alert")                           # baseline
        d.journal([ops_close(ts_ago(1), 444)])
        r = d.run("--no-alert")
        assert r.returncode == 2, r.stdout
        assert hook.posts == [], hook.posts
        st = d.state()
        assert not any("444" in k for k in (st or {}).get("alerted", [])), st
        # now armed: the same finding pages
        r2 = d.run("--webhook-url", hook.url)
        assert r2.returncode == 2, r2.stdout
        assert len(hook.posts) == 1, hook.posts
    finally:
        d._tmp.cleanup()
        hook.close()


if __name__ == "__main__":
    failed = 0
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
                print(f"PASS {name}")
            except AssertionError as exc:
                failed += 1
                print(f"FAIL {name}: {exc}")
    sys.exit(1 if failed else 0)
