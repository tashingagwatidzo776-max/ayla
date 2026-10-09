#!/usr/bin/env python3
"""Alert when a close row lands with OutcomeSource=unknown — a tier-1
coverage regression (docs/superpowers/specs/2026-10-08-fx-win-rate-design.md §1).

Every FX_EXIT "closed #N" row must carry a settled outcome:
  close-price      R from the close/executable/fill price (tier 1)
  profit-snapshot  stale-retire / ops snapshots
  unknown          the writer had no trustworthy R — THIS watcher's signal
  (no-payload)     legacy row from before the outcome feature existed

The 2026-10-08 live defect wrote unknown closes (floor confirms before the
DeferredCloseState fix, ops-reconcile rows with no fill evidence). Backfill
repairs history; this watcher pages on REGRESSIONS so a new unknown never
waits for a human to notice it in the dashboard.

Two finding rules (journal evidence only — reads, never trades):

  unknown-close   an FX_EXIT "closed #N" row whose payload says
                  OutcomeSource == "unknown" and is NOT Backfilled
                  (a backfilled row is the repair, not the regression).
  no-payload      an FX_EXIT "closed #N" row with no OutcomeSource key at
                  all — a close writer that lost the outcome payload.

The FIRST pass primes a baseline: every finding that already exists is
recorded silently, so today's repair history never pages — only rows
written AFTER the baseline alert. Alerts are deduped per finding key in
data/watcher/close-price-alerts.json, surviving restarts.

Each pass also prints the coverage table (per-day source counts) so a
drift toward unknown is visible before the page fires.

Alert body follows the watch_profit_floor convention (Discord embed /
Slack attachment from settings.json WebhookUrl).

Exit codes: 0 = clean (or findings already alerted), 2 = NEW finding this
pass (webhook paged unless --no-alert).

Usage:
  python scripts/watch_close_price.py                # one pass (scheduler)
  python scripts/watch_close_price.py --loop 60      # poll every 60s
  python scripts/watch_close_price.py --no-alert     # observe, never post
  python scripts/watch_close_price.py --dry-run      # show alert, no POST

Journal lives under %APPDATA%\\tf\\data\\journal, or $TF_DATA_DIR\\journal
when the tests set the app's data-dir override.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import time
import urllib.error
import urllib.request

SOURCES = ("close-price", "profit-snapshot", "unknown", "(no-payload)")


def data_dir() -> str:
    """The app's data dir: TF_DATA_DIR override, else the real %APPDATA%
    location. Same convention the app itself honors."""
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def journal_files() -> list[str]:
    return sorted(glob.glob(os.path.join(data_dir(), "journal", "journal_*.jsonl")))


def default_state_path() -> str:
    return os.path.join(data_dir(), "watcher", "close-price-alerts.json")


def load_settings() -> dict:
    try:
        with open(os.path.join(data_dir(), "settings.json"), encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def rows() -> list[dict]:
    """Journal envelopes as {ts, cat, details, payload} (FX_ rows only)."""
    out = []
    for path in journal_files():
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "FX_" not in line:
                    continue
                try:
                    env = json.loads(line)
                except json.JSONDecodeError:
                    continue
                details = env.get("Details", "")
                brace = details.find("{")
                payload = {}
                if brace >= 0:
                    try:
                        payload = json.loads(details[brace:])
                    except json.JSONDecodeError:
                        payload = {}
                out.append({
                    "ts": str(env.get("Timestamp", "")),
                    "cat": env.get("Category", ""),
                    "details": details,
                    "payload": payload,
                })
    out.sort(key=lambda r: r["ts"])
    return out


def close_rows(rs: list[dict]) -> list[dict]:
    """Every FX_EXIT 'closed #N' row the coverage dashboard counts, with its
    source classification (same rules as scripts/fx_outcome_coverage.py)."""
    out = []
    for r in rs:
        if r["cat"] != "FX_EXIT" or "closed #" not in r["details"]:
            continue
        payload = r["payload"]
        if not payload or "OutcomeSource" not in payload:
            source = "(no-payload)"
        else:
            source = str(payload.get("OutcomeSource") or "unknown")
            if source not in SOURCES:
                source = "unknown"
        ticket = _ticket_after(r["details"])
        out.append({**r, "source": source, "ticket": ticket})
    return out


def _ticket_after(details: str) -> int:
    marker = "closed #"
    i = details.find(marker)
    if i < 0:
        return 0
    j = i + len(marker)
    k = j
    while k < len(details) and details[k].isdigit():
        k += 1
    try:
        return int(details[j:k])
    except ValueError:
        return 0


def findings(closes: list[dict]) -> list[dict]:
    """New-shaped unknown closes. Backfilled rows are the repair, not the
    regression — they never page. (no-payload) rows page too: a close
    writer that lost the payload writes the same unknown R, silently."""
    out = []
    for c in closes:
        p = c["payload"] or {}
        if p.get("Backfilled"):
            continue
        if c["source"] == "unknown":
            out.append({**c, "kind": "unknown-close"})
        elif c["source"] == "(no-payload)":
            out.append({**c, "kind": "no-payload"})
    return out


def coverage_table(rs: list[dict], days: int = 7) -> list[tuple[str, dict]]:
    """Per-UTC-day source counts over the last `days` days."""
    buckets: dict[str, dict] = {}
    for c in close_rows(rs):
        day = c["ts"][:10]
        b = buckets.setdefault(day, {s: 0 for s in SOURCES})
        b[c["source"]] += 1
    return sorted(buckets.items())[-days:]


# ── webhook alerting (Discord/Slack, payload shape per WebhookService) ──

def build_alert_payload(f: dict) -> tuple[str, dict]:
    """The alert body for one new finding. Discord detection matches the
    metrics-digest convention ('discord' in the URL). Returns (kind, body)."""
    title = ("\U0001f4c5 FX outcome coverage regression — "
             f"ticket {f['ticket'] or '?'}")
    if f["kind"] == "unknown-close":
        text = (
            f"A close row landed with OutcomeSource=unknown (tier-1 miss): "
            f"#{f['ticket']} at {f['ts'][:19]} UTC.\n"
            f"Every close needs a settled R (win-rate design spec §1) — "
            f"backfill from broker deal evidence or fix the writer, then "
            f"re-check scripts/fx_outcome_coverage.py."
        )
    else:
        text = (
            f"A close row has NO outcome payload (writer regression): "
            f"#{f['ticket']} at {f['ts'][:19]} UTC.\n"
            f"The close writer dropped the outcome keys — spec §1 requires "
            f"RealizedR/OutcomeSource on every close."
        )
    return "discord", {
        "embeds": [{"title": title, "description": text, "color": 0xB71C1C}]}


def build_alert_payload_slack(f: dict) -> dict:
    _, discord = build_alert_payload(f)
    embed = discord["embeds"][0]
    return {"attachments": [{"color": f"#{embed['color']:06x}",
                             "title": embed["title"],
                             "text": embed["description"]}]}


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


# ── dedup state: one alert per event, across restarts ──────────────

def load_alerted(state_path: str) -> set[str]:
    try:
        with open(state_path, encoding="utf-8") as fh:
            return set(json.load(fh))
    except (OSError, ValueError):
        return set()


def save_alerted(state_path: str, keys: set[str]) -> None:
    try:
        os.makedirs(os.path.dirname(state_path), exist_ok=True)
        with open(state_path, "w", encoding="utf-8") as fh:
            json.dump(sorted(keys), fh)
    except OSError:
        pass   # dedup is best-effort; a lost state file means one re-alert


def event_key(f: dict) -> str:
    return f"{f['kind']}|{f['ts']}|{f['ticket']}"


def alert(f: dict, webhook_url: str, state_path: str,
          dry_run: bool = False) -> bool:
    """Post the alert for one new finding unless already alerted. Returns
    True when an alert was sent (or would have been, under --dry-run)."""
    key = event_key(f)
    seen = load_alerted(state_path)
    if key in seen or ("prime|" + key) in seen:
        return False

    is_discord = "discord" in webhook_url
    body = (build_alert_payload(f)[1] if is_discord
            else build_alert_payload_slack(f))
    if dry_run:
        print(f"dry-run: would alert ({'discord' if is_discord else 'slack'}): "
              f"{body['embeds'][0]['title'] if is_discord else body['attachments'][0]['title']}")
        return True

    status = post_webhook(webhook_url, body)
    print(f"alert: HTTP {status} for {key}")
    seen.add(key)
    save_alerted(state_path, seen)
    return True


# ── the pass ────────────────────────────────────────────────────────

def prime_baseline(fs: list[dict], state_path: str) -> None:
    """First pass: record every pre-existing finding silently so the
    repair history never pages (watch_closed_row's contract)."""
    seen = load_alerted(state_path)
    fresh = [event_key(f) for f in fs
             if event_key(f) not in seen and ("prime|" + event_key(f)) not in seen]
    seen.update("prime|" + k for k in fresh)
    # Always persist, even with zero findings: the state file's EXISTENCE
    # is the "baseline primed" marker — without it a genuinely new unknown
    # on the next pass would be primed silently instead of paged.
    save_alerted(state_path, seen)
    if fresh:
        print(f"baseline: {len(fresh)} pre-existing finding(s) recorded silently")


def one_pass(alert_webhook: bool = True, webhook_url: str = "",
             state_path: str = "", dry_run: bool = False) -> int:
    rs = rows()
    closes = close_rows(rs)
    fs = findings(closes)

    state_exists = bool(state_path) and os.path.exists(state_path)
    if not state_exists:
        prime_baseline(fs, state_path)

    new = []
    for f in fs:
        key = event_key(f)
        seen = load_alerted(state_path)
        if key in seen or ("prime|" + key) in seen:
            continue
        new.append(f)

    if closes:
        print(f"=== {len(closes)} close row(s) tracked, "
              f"{len(fs)} finding shape(s), {len(new)} new ===")
        for f in (new if new else fs[-3:]):
            print(f"  {f['ts'][:19]} [{f['kind']}] ticket {f['ticket']} "
                  f"{f['details'][:80]}")

    if new and alert_webhook and webhook_url:
        for f in new:
            try:
                alert(f, webhook_url, state_path, dry_run)
            except RuntimeError as exc:
                print(f"alert FAILED (will retry next pass, state untouched): {exc}")

    table = coverage_table(rs)
    if table:
        print("=== coverage by day (close-price / profit-snap / unknown / no-payload) ===")
        for day, b in table:
            total = sum(b.values())
            measured = b["close-price"] + b["profit-snapshot"]
            pct = (measured * 100 // total) if total else 100
            print(f"  {day}  {b['close-price']:3d} / {b['profit-snapshot']:3d} / "
                  f"{b['unknown']:3d} / {b['(no-payload)']:3d}   {pct}% measured"
                  f"{'  <== NEW UNKNOWN' if b['unknown'] and new else ''}")

    return 2 if new else 0


def main() -> None:
    # Same UTF-8 guard as watch_profit_floor: the scheduled task captures
    # stdout on the ANSI code page, and a stray journal character would
    # otherwise kill the pass before it reports anything.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, OSError):
            pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--loop", type=int, default=0, metavar="SEC",
                    help="poll every SEC seconds instead of one pass")
    ap.add_argument("--no-alert", action="store_true",
                    help="observe only: never post to the webhook")
    ap.add_argument("--webhook-url", default=None,
                    help="webhook URL override (default: settings.json)")
    ap.add_argument("--state", default=None,
                    help="dedup state file override (default: data/watcher/)")
    ap.add_argument("--dry-run", action="store_true",
                    help="build and show the alert, never POST, never remember")
    args = ap.parse_args()

    url = args.webhook_url
    if url is None and not args.no_alert:
        url = load_settings().get("WebhookUrl") or ""
    state = args.state or default_state_path()

    if args.loop <= 0:
        sys.exit(one_pass(not args.no_alert, url or "", state, args.dry_run))

    while True:
        try:
            one_pass(not args.no_alert, url or "", state, args.dry_run)
        except Exception as exc:  # a watcher never crashes the watch
            print(f"watch error (continuing): {exc}")
        print("-" * 60)
        time.sleep(args.loop)


if __name__ == "__main__":
    main()
