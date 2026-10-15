#!/usr/bin/env python3
"""Soak-exit watch: page when a soak_evening run never wrote its exit line.

soak_evening.ps1 brackets every run with two lines in
%LOCALAPPDATA%\\tf\\soak_evening.log:

    === soak_evening 2026-10-12 20:30:01 ===                    (start)
    exit: 0 at 2026-10-12 21:05:33 (duration 35.5 min)          (exit)

A start WITHOUT a matching exit line before the next start (or EOF) IS
a termination: the Monday alpha task died as SCHED_S_TASK_TERMINATED
(0x41306) leaving exactly that signature, and a killed or crashed
process can never write its own exit line. This watcher pairs starts
with exits in line order and pages the webhook once per orphan start
once it is older than the grace window (default 270 min > the alpha
task's 4 h ExecutionTimeLimit — no legitimate run can still be
executing past the limit, the scheduler would have killed it). Both
soak tasks (nightly tf-soak-evening and the Monday alpha rerun) write
the same log, so one watch covers both. Legacy exit lines (pre-2026-10
format, no timestamp) still close a run — pairing is order-based.

Alerted starts are deduped by a state file under data/watcher/ (survives
restarts; a fresh state file re-alerts each open orphan at most once).
Reports, never gates: exit 0 = the pass completed (alerted or not),
exit 1 = an alert was needed and the POST failed — state is saved only
after a confirmed post, so the next pass retries the same orphan.

Usage:
  python scripts/soak_exit_watch.py                        # one pass
  python scripts/soak_exit_watch.py --no-alert             # dry run
  python scripts/soak_exit_watch.py --grace-minutes 60 --log path\\to\\log
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

# soak_evening.ps1 writes Get-Date strings — LOCAL wall-clock time, no
# zone. Ages are therefore computed against local now() too (a naive-vs-
# aware comparison would raise on every pass).
START_RE = re.compile(r"^=== soak_evening (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")
EXIT_RE = re.compile(r"^exit: .*? at (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")
LEGACY_EXIT_RE = re.compile(r"^exit: ")   # pre-marker lines: no timestamp
TS_FMT = "%Y-%m-%d %H:%M:%S"
DEFAULT_GRACE_MINUTES = 270    # > the alpha task's 4 h ExecutionTimeLimit


def line_variants(text: str) -> list[str]:
    """The utf-8 reading plus, for UTF-16LE regions, its NUL-stripped
    form. The pre-2026-10 wrapper wrote python output (and its exit
    line) through a bare `>>` = UTF-16LE; under a utf-8 read those lines
    arrive as 'e\\x00x\\x00i\\x00t\\x00...' — stripping the NULs
    recovers 'exit: 0' so a COMPLETED legacy run does not false-alert
    (the 09-26 lesson from the first live pass)."""
    return [text] if "\x00" not in text else [text, text.replace("\x00", "")]


def data_dir() -> str:
    """TF_DATA_DIR override, else %APPDATA%\\tf\\data — the fleet's own
    convention (digest, health check, close-price watcher)."""
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def default_log_path() -> str:
    """Where soak_evening.ps1 writes (TF_SOAK_LOG override for tests)."""
    override = os.environ.get("TF_SOAK_LOG")
    if override:
        return override
    local = os.environ.get("LOCALAPPDATA") or os.path.expandvars(
        r"%USERPROFILE%\AppData\Local")
    return os.path.join(local, "tf", "soak_evening.log")


def load_settings() -> dict:
    try:
        with open(os.path.join(data_dir(), "settings.json"),
                  encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def parse_events(lines: list[str]) -> list[tuple[str, object]]:
    """Log lines -> ordered (kind, timestamp-or-None) events.

    Tolerant by design: unparsable lines are skipped, a legacy exit line
    (no timestamp) still counts as an exit — the timestamp is only
    evidence, pairing is order-based — and garbage must never kill the
    watch."""
    events: list[tuple[str, object]] = []
    for line in lines:
        matched = False
        for text in line_variants(line):
            m = START_RE.match(text)
            if m:
                events.append(("start", datetime.strptime(m.group(1), TS_FMT)))
                matched = True
                break
            if EXIT_RE.match(text) or LEGACY_EXIT_RE.match(text):
                events.append(("exit", None))
                matched = True
                break
        if matched:
            continue
    return events


def find_orphans(events: list[tuple[str, object]]) -> list[datetime]:
    """(kind, ts) events in line order -> starts with no matching exit.

    A second start while one is still pending closes the first as an
    orphan — the 10-12 alpha signature (start, nothing, next day's
    start). A start still open at EOF is an orphan too: that IS the
    watcher's own reason to exist."""
    orphans: list[datetime] = []
    pending = None
    for kind, ts in events:
        if kind == "start":
            if pending is not None:
                orphans.append(pending)
            pending = ts
        else:
            pending = None
    if pending is not None:
        orphans.append(pending)
    return orphans


def minutes_since(then: datetime, now: datetime) -> float:
    return (now - then).total_seconds() / 60.0


def new_alerts(orphans: list[datetime], alerted: set[str],
               grace_minutes: int, now: datetime) -> list[datetime]:
    """Orphan starts not yet alerted and older than the grace window."""
    return [o for o in orphans
            if minutes_since(o, now) > grace_minutes
            and o.strftime(TS_FMT) not in alerted]


def build_payload(starts: list[datetime], grace_minutes: int,
                  log_path: str, ts: str) -> dict:
    lines = [f"start {s.strftime(TS_FMT)} — no matching exit line"
             for s in starts[:5]]
    embed = {
        "title": ("Soak run terminated — no exit line" if len(starts) == 1
                  else f"Soak runs terminated — {len(starts)} starts "
                       f"without exit lines"),
        "color": 0xE74C3C,
        "fields": [
            {"name": "Orphan start(s)", "value": "\n".join(lines)[:900]},
            {"name": "Watch",
             "value": (f"log: {log_path}\n"
                       f"grace: {grace_minutes} min (alpha task limit 4 h)")[:900]},
        ],
        "footer": {"text": f"generated {ts}"},
    }
    return {"embeds": [embed]}


def load_alerted(path: str) -> set[str]:
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        vals = data.get("alerted") if isinstance(data, dict) else None
        return {str(v) for v in vals} if isinstance(vals, list) else set()
    except (OSError, ValueError):
        return set()


def save_alerted(path: str, keys: set[str]) -> None:
    """Persist the alerted set UNBOUNDED: orphan starts accumulate one
    short string per soak run (~20 bytes/day — trivial), and any cap
    would evict the oldest keys and make the next pass re-alert them
    forever (the ALERTED_MAX=10 lesson from the first live pass)."""
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump({"alerted": sorted(keys)}, fh)
    except OSError:
        pass   # best-effort: a lost state file re-alerts at most once


def post_webhook(url: str, body: dict) -> int:
    req = urllib.request.Request(
        url, data=json.dumps(body).encode("utf-8"),
        # Discord's Cloudflare edge 403s urllib's default "Python-urllib"
        # signature (error 1010); a named UA posts fine (2026-10-09).
        headers={"Content-Type": "application/json",
                 "User-Agent": "DongGfx-Watcher/1.0"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status
    except urllib.error.HTTPError as ex:
        raise RuntimeError(f"webhook POST failed: HTTP {ex.code}") from ex
    except Exception as ex:
        raise RuntimeError(f"webhook POST failed: {ex.__class__.__name__}") from ex


def main() -> int:
    # A cp1252 console must never crash the watch mid-print (the
    # scheduled task already runs with PYTHONIOENCODING=utf-8; this
    # covers manual runs from any shell).
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--no-alert", action="store_true",
                    help="print the verdict, skip the POST and state write")
    ap.add_argument("--grace-minutes", type=int, default=DEFAULT_GRACE_MINUTES,
                    help=f"alert only after an orphan start is older than "
                         f"this (default {DEFAULT_GRACE_MINUTES})")
    ap.add_argument("--log", default=default_log_path(),
                    help="soak_evening.log to watch")
    ap.add_argument("--state", default=os.path.join(
        data_dir(), "watcher", "soak-exit-watch-state.json"),
        help="alerted-start dedup state file")
    ap.add_argument("--webhook-url", default="",
                    help="override settings.json WebhookUrl")
    args = ap.parse_args()

    try:
        with open(args.log, encoding="utf-8", errors="replace") as fh:
            lines = fh.read().splitlines()
    except OSError:
        lines = []   # no log yet = no soak ever ran = nothing to watch

    now = datetime.now()
    events = parse_events(lines)
    orphans = find_orphans(events)
    overdue = [o for o in orphans
               if minutes_since(o, now) > args.grace_minutes]
    todo = new_alerts(orphans, load_alerted(args.state),
                      args.grace_minutes, now)

    starts = sum(1 for k, _ in events if k == "start")
    exits = sum(1 for k, _ in events if k == "exit")
    print(f"soak-exit-watch: log {args.log} — {starts} start line(s), "
          f"{exits} exit line(s), {len(orphans)} orphan start(s), "
          f"{len(overdue)} past grace, {len(todo)} new alert(s)")
    for o in overdue:
        marker = "ALERT" if o in todo else "already alerted"
        print(f"  orphan start {o.strftime(TS_FMT)} ({marker})")

    if not todo:
        return 0
    if args.no_alert:
        print("soak-exit-watch: --no-alert, POST skipped, state unchanged")
        return 0
    url = args.webhook_url or (load_settings().get("WebhookUrl") or "")
    if not url:
        print("soak-exit-watch: no webhook URL (settings.json WebhookUrl "
              "missing) — alert UNPOSTED, will retry next pass")
        return 1
    payload = build_payload(
        todo, args.grace_minutes, args.log,
        datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"))
    try:
        status = post_webhook(url, payload)
    except RuntimeError as ex:
        print(f"soak-exit-watch: {ex} — alert UNPOSTED, will retry next pass")
        return 1
    already = load_alerted(args.state)
    save_alerted(args.state,
                 already | {o.strftime(TS_FMT) for o in todo})
    print(f"soak-exit-watch: posted HTTP {status} "
          f"({len(todo)} orphan start(s) past grace)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
