#!/usr/bin/env python3
"""Tests for scripts/watch_close_price.py.

The watcher's verdicts and its one-shot alert contract must be exact:
every finding rule is exercised against synthetic journals in temp
TF_DATA_DIRs, the webhook leg against a local fake endpoint — no real
journal, no real bridge, no trading.

Run: python scripts/test_watch_close_price.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "watch_close_price.py"

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")


class TmpData:
    """A temp TF_DATA_DIR with journal/ and a settings.json."""

    def __init__(self, webhook_url: str = ""):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        (self.root / "journal").mkdir()
        (self.root / "settings.json").write_text(
            json.dumps({"WebhookUrl": webhook_url}), encoding="utf-8")

    def close_row(self, ticket: int, source: str | None = "close-price",
                  backfilled: bool = False, ts: str = "2026-10-08T12:00:00+00:00"):
        """Append one FX_EXIT closed #N row with an outcome payload."""
        payload: dict = {"Ticket": ticket, "Partial": False, "Retcode": 10009}
        if source is not None:
            payload["OutcomeSource"] = source
            payload["RealizedR"] = None if source == "unknown" else 1.5
        if backfilled:
            payload["Backfilled"] = True
        details = (f"#{ticket}: closed #{ticket} — broker no longer holds the "
                   f"ticket; journal book reconcile: {json.dumps(payload)}")
        self._append(ts, "FX_EXIT", details)

    def other_row(self, ts: str = "2026-10-08T12:00:01+00:00"):
        self._append(ts, "FX_PROFIT", "XAUUSD #1: telemetry {}")

    def _append(self, ts: str, cat: str, details: str):
        path = self.root / "journal" / "journal_20261008.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"Timestamp": ts, "Category": cat,
                                 "Details": details}) + "\n")

    @property
    def state_path(self) -> Path:
        return self.root / "watcher" / "close-price-alerts.json"

    def state(self):
        if not self.state_path.exists():
            return None
        return json.loads(self.state_path.read_text(encoding="utf-8"))

    def cleanup(self):
        self._tmp.cleanup()


def run(data: TmpData, *args: str) -> subprocess.CompletedProcess:
    import os
    env = dict(os.environ)
    env["TF_DATA_DIR"] = str(data.root)
    return subprocess.run(
        [sys.executable, str(SCRIPT), *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=env, timeout=60)


class FakeWebhook(BaseHTTPRequestHandler):
    received: list[dict] = []

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        FakeWebhook.received.append(json.loads(self.rfile.read(length)))
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


def main() -> int:
    # 1. clean journal → exit 0, baseline state file created
    d = TmpData()
    d.close_row(100, "close-price")
    d.close_row(101, "profit-snapshot")
    p = run(d, "--no-alert")
    check("clean journal exits 0", p.returncode == 0)
    check("coverage table printed", "close-price" in p.stdout)
    check("baseline state file exists after first pass",
          d.state_path.exists())
    check("clean pass records no findings", d.state() == [])
    d.other_row()
    check("non-close rows ignored", run(d, "--no-alert").returncode == 0)
    d.cleanup()

    # 2. pre-existing unknown → primed silently on first pass, no page
    d = TmpData()
    d.close_row(200, "unknown")
    p = run(d, "--no-alert")
    check("first pass with pre-existing unknown exits 0 (primed)",
          p.returncode == 0)
    check("baseline primed the pre-existing unknown",
          any(str(k).startswith("prime|") for k in d.state()))
    d.cleanup()

    # 3. NEW unknown after baseline → exit 2, alert paged once
    server = HTTPServer(("127.0.0.1", 0), FakeWebhook)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{server.server_port}/discord-hook"
    FakeWebhook.received.clear()

    d = TmpData(webhook_url=url)
    d.close_row(300, "close-price")
    run(d)   # baseline
    d.close_row(301, "unknown", ts="2026-10-08T13:00:00+00:00")
    p = run(d)
    check("new unknown close exits 2", p.returncode == 2)
    check("webhook paged once", len(FakeWebhook.received) == 1)
    check("alert names the ticket", "301" in json.dumps(FakeWebhook.received[0]))
    check("alert is a discord embed",
          "embeds" in FakeWebhook.received[0])

    # re-pass → still exit 2? No: already alerted → exit 0, no second page
    p = run(d)
    check("repeat pass does not re-page", len(FakeWebhook.received) == 1)
    check("already-alerted finding exits 0", p.returncode == 0)

    # restart (state survives) → no re-page
    p = run(d)
    check("restart does not re-page", len(FakeWebhook.received) == 1)
    d.cleanup()

    # 4. Backfilled rows never find
    d = TmpData()
    d.close_row(400, "close-price", backfilled=True)
    run(d)
    d.close_row(401, "unknown", backfilled=True, ts="2026-10-08T14:00:00+00:00")
    p = run(d, "--no-alert")
    check("backfilled unknown never fires", p.returncode == 0)
    d.cleanup()

    # 5. no-payload close row is a finding (writer dropped the payload)
    d = TmpData()
    run(d)
    d.close_row(500, source=None, ts="2026-10-08T15:00:00+00:00")
    p = run(d, "--no-alert")
    check("payload-less close row fires", p.returncode == 2)
    check("payload-less finding named", "no-payload" in p.stdout)
    d.cleanup()

    # 6. --dry-run builds but never pages and never remembers
    d = TmpData(webhook_url=url)
    FakeWebhook.received.clear()
    run(d)
    d.close_row(600, "unknown", ts="2026-10-08T16:00:00+00:00")
    p = run(d, "--dry-run")
    check("dry-run shows the alert", "dry-run" in p.stdout)
    check("dry-run never POSTs", len(FakeWebhook.received) == 0)
    check("dry-run exits 2 (finding exists)", p.returncode == 2)
    d.cleanup()

    server.shutdown()

    # 7. contract checks (mirrors the register-script suites)
    src = SCRIPT.read_text(encoding="utf-8")
    check("watcher is read-only (reads, never trades)",
          "reads, never trades" in src)
    check("watcher honors TF_DATA_DIR (hermetic tests possible)",
          "TF_DATA_DIR" in src)
    check("watcher has a --no-alert observe mode", "--no-alert" in src)
    check("watcher dedups alerts across restarts",
          "close-price-alerts.json" in src)
    register = HERE / "register-close-price-watch.ps1"
    check("register script exists", register.exists())
    rsrc = register.read_text(encoding="utf-8", errors="replace") if register.exists() else ""
    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in rsrc and "Unregister-ScheduledTask" in rsrc)
    check("task name is the close-price watcher",
          "DongGfx close-price watcher" in rsrc)
    check("registered action points at the watcher script",
          "watch_close_price.py" in rsrc)
    check("repetition interval uses the cadence parameter",
          "New-TimeSpan -Minutes $EveryMinutes" in rsrc)
    check("task runs at logon", "AtLogOn" in rsrc)
    check("execution time limit is bounded", "ExecutionTimeLimit" in rsrc)
    check("concurrent instances are ignored", "-MultipleInstances IgnoreNew" in rsrc)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in rsrc and "StartWhenAvailable" in rsrc)
    check("supports -Remove", "[switch]$Remove" in rsrc)
    check("register script fails closed on a missing watcher",
          "Watcher not found" in rsrc)

    failed = [name for name, ok in results if not ok]
    print(f"{'FAIL' if failed else 'PASS'}: {len(results) - len(failed)}/{len(results)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
