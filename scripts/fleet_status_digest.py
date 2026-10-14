#!/usr/bin/env python3
"""Weekly fleet digest: post the audit + watcher-health verdicts to the
configured webhook so green/red is visible without opening a session.

Runs the two checks that already exist (audit_donggfx_tasks.py and
check_watcher_task_health.py --json), folds their verdicts into one
embed, and posts it with the fleet's named User-Agent — Discord's
Cloudflare edge 1010s the default urllib signature (2026-10-08/09), so
a digest without it would silently retry forever.

The digest REPORTS, it never gates: exit 0 means the digest was
delivered, whatever the verdict inside it; exit 1 means it could not
be delivered (no webhook, failed POST, or both probes crashed hard
enough to leave nothing to report). Red verdicts travel INSIDE a
delivered digest — ci-local and the watcher task-health task remain
the gates.

Flags: --no-alert (print, skip the POST), --webhook-url override
(default: settings.json WebhookUrl), --json (machine summary).

Usage:
  python scripts/fleet_status_digest.py            # weekly digest
  python scripts/fleet_status_digest.py --no-alert  # dry run
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent

GREEN, RED, AMBER = 0x2ECC71, 0xE74C3C, 0xFFAA00
MAX_FIELD = 900   # embed-field cap with slack for long finding lists


def data_dir() -> str:
    """The app's data dir: TF_DATA_DIR override, else the real %APPDATA%
    location. Same convention the watchers honor."""
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def load_settings() -> dict:
    try:
        with open(os.path.join(data_dir(), "settings.json"),
                  encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def run_check(args: list[str]) -> tuple[int, str]:
    """Run one of the fleet's checks; (-1, reason) when it cannot run.
    The child gets PYTHONIOENCODING=utf-8 (the fleet's own rule): a
    cp1252 child emits bytes this parent's UTF-8 decode would turn into
    replacement characters — and those crash a cp1252 console print."""
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    try:
        proc = subprocess.run([sys.executable, *args], capture_output=True,
                              text=True, encoding="utf-8", errors="replace",
                              timeout=300, check=False, env=env)
        return proc.returncode, (proc.stdout or "") + (proc.stderr or "")
    except Exception as ex:  # noqa: BLE001 — a dead probe must not kill the digest
        return -1, f"{ex.__class__.__name__}: {ex}"


def parse_audit_output(rc: int, out: str) -> dict:
    """(rc, combined output) -> verdict line + up to 8 FINDING lines."""
    lines = [ln.strip() for ln in out.splitlines() if ln.strip()]
    verdict = next((ln for ln in reversed(lines)
                    if ln.startswith("task-audit:")),
                   f"task-audit: no verdict (rc={rc})")
    findings = [ln for ln in lines if "FINDING:" in ln]
    return {"rc": rc, "verdict": verdict, "findings": findings[:8]}


def parse_health_output(rc: int, out: str) -> dict:
    """(rc, combined output) -> the health check's --json summary. The
    human OK/FAIL lines that follow the JSON are tolerated (raw_decode
    stops at the object's end); an exception probe prints no JSON at
    all and comes back as healthy=None."""
    if rc == -1:
        return {"rc": rc, "healthy": None, "tasks": [],
                "findings": [f"probe failed: {out[:200]}"]}
    start = out.find("{")
    if start >= 0:
        try:
            data, _ = json.JSONDecoder().raw_decode(out[start:])
            if isinstance(data, dict):
                data["rc"] = rc
                return data
        except ValueError:
            pass
    return {"rc": rc, "healthy": None, "tasks": [],
            "findings": [f"unparseable health output (rc={rc}): {out[:160]}"]}


def _clip(text: str) -> str:
    text = text.strip() or "(nothing reported)"
    return text if len(text) <= MAX_FIELD else text[:MAX_FIELD - 1] + "…"


def build_payload(audit: dict, health: dict, ts: str) -> dict:
    """(probe summaries, timestamp) -> webhook payload. AMBER when a
    probe itself is broken (never fake green, never fake red), RED on a
    real finding, GREEN only when both probes ran clean."""
    audit_rc = audit.get("rc", -1)
    health_rc = health.get("rc", -1)
    healthy = health.get("healthy")

    probes_broken = audit_rc not in (0, 1) or health_rc not in (0, 1)
    if probes_broken:
        state, color = "AMBER", AMBER
    elif audit_rc == 1 or healthy is False:
        state, color = "RED", RED
    else:
        state, color = "GREEN", GREEN

    audit_lines = [audit.get("verdict", "task-audit: unknown")]
    if audit_rc == 1:
        audit_lines += list(audit.get("findings") or [])
    elif probes_broken and audit_rc not in (0, 1):
        audit_lines.append(f"probe error (rc={audit_rc})")

    if healthy is None:
        health_lines = list(health.get("findings") or ["probe indeterminate"])
    elif healthy:
        health_lines = [f"all {len(health.get('tasks') or [])} "
                        f"watcher task(s) healthy"]
    else:
        health_lines = list(health.get("findings") or ["unhealthy (no detail)"])

    embed = {
        "title": f"Fleet digest — {state}",
        "color": color,
        "fields": [
            {"name": "Task-fleet audit", "value": _clip("\n".join(audit_lines))},
            {"name": "Watcher health", "value": _clip("\n".join(health_lines))},
        ],
        "footer": {"text": f"generated {ts}"},
    }
    return {"embeds": [embed]}


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
    # A cp1252 console must never crash the digest mid-print (the
    # scheduled task already runs with PYTHONIOENCODING=utf-8; this
    # covers manual runs from any shell).
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--no-alert", action="store_true",
                    help="print the digest, skip the POST")
    ap.add_argument("--webhook-url", default="",
                    help="override settings.json WebhookUrl")
    ap.add_argument("--json", action="store_true", help="machine summary")
    args = ap.parse_args()

    audit = parse_audit_output(
        *run_check([str(HERE / "audit_donggfx_tasks.py")]))
    health = parse_health_output(
        *run_check([str(HERE / "check_watcher_task_health.py"), "--json"]))
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    payload = build_payload(audit, health, ts)
    url = args.webhook_url or (load_settings().get("WebhookUrl") or "")
    title = payload["embeds"][0]["title"]

    if args.json:
        print(json.dumps({"audit": audit, "health": health,
                          "payload": payload, "webhook": bool(url)},
                         indent=2))
    else:
        print(f"fleet-digest: {title}")
        for field in payload["embeds"][0]["fields"]:
            print(f"  {field['name']}: {field['value']}")

    if args.no_alert:
        print("fleet-digest: --no-alert, POST skipped")
        return 0
    if not url:
        print("fleet-digest: no webhook URL (settings.json WebhookUrl missing)")
        return 1
    try:
        status = post_webhook(url, payload)
    except RuntimeError as ex:
        print(f"fleet-digest: {ex}")
        return 1
    print(f"fleet-digest: posted HTTP {status} ({title})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
