#!/usr/bin/env python3
"""Alert when a floor or SL exit closes WITHOUT an in-app `closed #` row.

The 2026-10-06 flat-fix made the engine host retire its own book: floor
EXIT CONFIRMED now writes an FX_EXIT `closed #N` row in the same cycle,
and a proven-flat prune writes one for tickets the venue dropped (SL
hits, manual flattens). Until that fix, every such exit needed
book_recovery's `operator reconcile` row by hand — this watcher keeps
the fix honest and PAGES the configured webhook when it regresses.

Three finding rules (all journal/venue evidence, never trades):

  ops-reconcile-close  a `closed #N` row written by the ops layer
                       ("operator reconcile") — the app did not write
                       its own row for that ticket.
  floor-no-close       an FX_FLOOR "EXIT CONFIRMED" row with no in-app
                       FX_EXIT `closed #N` row for the same ticket and
                       a >= the confirm, once the confirm is 10+ minutes
                       old (the app writes it in the SAME cycle, so
                       anything older is a miss).
  vanish-no-close      a journal fill (`FX_ORDER ... fill ... ticket N`)
                       whose ticket has NO close row at all while the
                       venue provably does not hold it — same fail-closed
                       venue-flat proof as the app (a healthy non-empty
                       read lacking the ticket, or two agreeing empty
                       reads + settled account), sustained across two
                       passes so one degraded read never pages.

The FIRST pass primes a baseline: every finding that already exists is
recorded silently, so the pre-fix history (48 operator-reconcile rows)
never pages — only regressions AFTER the baseline alert. Alerts are
deduped per finding key in data/watcher/closed-row-alerts.json.

Alert body follows the watch_profit_floor convention (Discord embed /
Slack attachment from settings.json WebhookUrl).

Exit codes: 0 = clean (or findings already alerted), 2 = NEW finding
this pass (webhook paged unless --no-alert).

Usage:
  python scripts/watch_closed_row.py                # one pass (scheduler)
  python scripts/watch_closed_row.py --loop 60      # poll every 60s
  python scripts/watch_closed_row.py --no-alert     # observe, never post
  python scripts/watch_closed_row.py --dry-run      # show alert, no POST

Journal lives under %APPDATA%\\tf\\data\\journal, or $TF_DATA_DIR\\journal
when the tests set the app's data-dir override.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

FLOOR_CONFIRM_GRACE_MIN = 10   # confirm -> in-app close is same-cycle
VANISH_MIN_AGE_MIN = 15        # fills younger than this are still working
DEFAULT_POSITIONS_URL = "http://127.0.0.1:53190/positions"
DEFAULT_ACCOUNT_URL = "http://127.0.0.1:53190/account"


def data_dir() -> str:
    """The app's data dir: TF_DATA_DIR override, else the real %APPDATA%
    location. Same convention the app itself honors."""
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def journal_files() -> list[str]:
    return sorted(glob.glob(os.path.join(data_dir(), "journal", "journal_*.jsonl")))


def default_state_path() -> str:
    return os.path.join(data_dir(), "watcher", "closed-row-alerts.json")


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


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _ts_dt(ts: str) -> datetime | None:
    try:
        dt = datetime.fromisoformat(ts)
        return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)
    except ValueError:
        return None


def _age_min(ts: str) -> float:
    dt = _ts_dt(ts)
    if dt is None:
        return 0.0
    return (_now() - dt).total_seconds() / 60.0


def closed_ticket(details: str) -> int | None:
    """The N in `closed #N` (any author)."""
    m = re.search(r"closed #(\d+)", details)
    return int(m.group(1)) if m else None


def fill_ticket(r: dict) -> int | None:
    """The ticket of an FX_ORDER fill row: payload first, else the
    `ticket N` text marker FxJournalBook parses (last occurrence)."""
    try:
        return int(r["payload"].get("Ticket"))
    except (TypeError, ValueError):
        pass
    idx = r["details"].rfind("ticket ")
    if idx < 0:
        return None
    m = re.match(r"(\d+)", r["details"][idx + 7:])
    return int(m.group(1)) if m else None


def confirm_ticket(r: dict) -> int | None:
    try:
        return int(r["payload"].get("Ticket"))
    except (TypeError, ValueError):
        pass
    m = re.search(r"#(\d+):", r["details"])
    return int(m.group(1)) if m else None


# ── venue-flat proof (the app's fail-closed gate, mirrored) ────────

def _get_json(url: str, timeout: float = 6):
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception:
        return None


def venue_read(url: str) -> list | None:
    """One positions read as a list, or None when unknown."""
    doc = _get_json(url)
    if not isinstance(doc, dict) or not isinstance(doc.get("positions"), list):
        return None
    return doc["positions"]


def venue_lacks_ticket(ticket: int, positions_url: str, account_url: str) -> bool | None:
    """True = proven NOT held, False = held, None = unknown (fail closed,
    never a finding). Proof: a healthy non-empty read lacking the ticket,
    or two agreeing EMPTY reads plus a settled account (equity ~= balance
    AND margin == 0 when published)."""
    first = venue_read(positions_url)
    if first is None:
        return None
    live = [int(p.get("ticket", 0)) for p in first if isinstance(p, dict)]
    if live:
        return ticket not in live
    second = venue_read(positions_url)
    if second is None:
        return None
    live2 = [int(p.get("ticket", 0)) for p in second if isinstance(p, dict)]
    if live2:
        return ticket not in live2
    acct = _get_json(account_url)
    if not isinstance(acct, dict):
        return None
    try:
        bal = float(acct.get("balance"))
        eq = float(acct.get("equity"))
    except (TypeError, ValueError):
        return None
    margin = acct.get("margin")
    if margin is not None:
        try:
            if float(margin) > 1e-6:
                return False   # live margin: venue holds something
        except (TypeError, ValueError):
            return None
    return abs(eq - bal) <= 0.01


# ── finding rules ──────────────────────────────────────────────────

def journal_findings(rs: list[dict]) -> list[dict]:
    """Journal-only misses: ops-reconcile closes and floor confirms
    whose in-app close row never came."""
    findings = []

    in_app_closes: dict[int, list[str]] = {}
    for r in rs:
        if r["cat"] == "FX_EXIT" and "closed #" in r["details"] \
                and "operator reconcile" not in r["details"]:
            t = closed_ticket(r["details"])
            if t is not None:
                in_app_closes.setdefault(t, []).append(r["ts"])

    for r in rs:
        if "closed #" in r["details"] and "operator reconcile" in r["details"]:
            t = closed_ticket(r["details"]) or 0
            findings.append({
                "kind": "ops-reconcile-close",
                "ticket": t,
                "ts": r["ts"],
                "key": f"ops|{r['ts']}|{t}",
                "detail": r["details"].split("{")[0].strip()[:180],
            })

    for r in rs:
        if r["cat"] != "FX_FLOOR" or "EXIT CONFIRMED" not in r["details"]:
            continue
        if _age_min(r["ts"]) < FLOOR_CONFIRM_GRACE_MIN:
            continue                      # the close row may still be in flight
        t = confirm_ticket(r)
        if t is None:
            continue
        closes_after = [ts for ts in in_app_closes.get(t, [])
                        if _ts_dt(ts) is not None and _ts_dt(r["ts"]) is not None
                        and _ts_dt(ts) >= _ts_dt(r["ts"])]
        if not closes_after:
            findings.append({
                "kind": "floor-no-close",
                "ticket": t,
                "ts": r["ts"],
                "key": f"floor|{r['ts']}|{t}",
                "detail": r["details"].split("{")[0].strip()[:180],
            })

    return findings


def vanish_candidates(rs: list[dict], positions_url: str,
                      account_url: str) -> tuple[list[int], str]:
    """Fill tickets with no close row at all whose venue absence is
    PROVEN. Returns (tickets, note) — empty and a skip-note when the
    venue cannot be proven flat for a ticket (fail closed)."""
    closed = {closed_ticket(r["details"]) for r in rs
              if "closed #" in r["details"]}
    closed.discard(None)
    now_missing = []
    note = ""
    for r in rs:
        if r["cat"] != "FX_ORDER" or "fill" not in r["details"]:
            continue
        t = fill_ticket(r)
        if t is None or t in closed:
            continue
        if _age_min(r["ts"]) < VANISH_MIN_AGE_MIN:
            continue                      # a live trade's fill is young
        proven = venue_lacks_ticket(t, positions_url, account_url)
        if proven is True:
            now_missing.append(t)
        elif proven is None and not note:
            note = "venue flatness unknown (bridge down or account unsettled) — vanish rule skipped"
    return now_missing, note


def evaluate(rs: list[dict], state: dict, positions_url: str,
             account_url: str) -> tuple[list[dict], dict]:
    """Findings NEW this pass. Updates the pending map for the vanish
    rule (two sustained passes before it can page)."""
    findings = [f for f in journal_findings(rs) if f["key"] not in state["alerted"]]
    missing, note = vanish_candidates(rs, positions_url, account_url)
    pending = state.get("pending", {})
    still: dict[str, str] = {}
    for t in missing:
        key = f"vanish|{t}"
        if key in state["alerted"]:
            continue
        tkey = str(t)
        if tkey in pending:
            findings.append({
                "kind": "vanish-no-close",
                "ticket": t,
                "ts": pending[tkey],
                "key": key,
                "detail": f"fill ticket #{t} has no `closed #` row and the venue provably does not hold it",
            })
        else:
            still[tkey] = datetime.now(timezone.utc).isoformat()
    state["pending"] = still
    if note and not findings:
        print(f"note: {note}")
    return findings, state


# ── alerting (watch_profit_floor conventions) ──────────────────────

def load_state(path: str) -> dict | None:
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        if isinstance(data, dict) and "alerted" in data:
            data.setdefault("pending", {})
            return data
    except (OSError, ValueError):
        pass
    return None


def save_state(path: str, state: dict) -> None:
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(state, fh, indent=1, sort_keys=True)
    except OSError:
        pass   # dedup is best-effort; a lost state file means one re-alert


def build_alert_payload(findings: list[dict]) -> tuple[str, dict]:
    f = findings[0]
    kind_text = {
        "ops-reconcile-close":
            "The ops layer (book_recovery) wrote the `closed #` row — the "
            "app did NOT write its own in-app close row for this exit.",
        "floor-no-close":
            "A profit-floor EXIT CONFIRMED with no in-app `closed #` row — "
            "the journal book keeps this fill open until ops reconciles it.",
        "vanish-no-close":
            "A fill left the venue with no `closed #` row at all — the "
            "journal book will hold this fill open forever without ops help.",
    }[f["kind"]]
    title = "\U0001f6a8 Exit closed without an in-app closed # row"
    extra = f"\n(+{len(findings) - 1} more finding(s) this pass)" if len(findings) > 1 else ""
    text = (
        f"{kind_text}\n"
        f"Ticket #{f['ticket']} — evidence at {f['ts'][:19]} UTC.\n"
        f"Rule: {f['kind']}. Evidence: {f['detail']}{extra}"
    )
    return "discord", {
        "embeds": [{"title": title, "description": text, "color": 0xB71C1C}]}


def build_slack_payload(findings: list[dict]) -> dict:
    _, discord = build_alert_payload(findings)
    embed = discord["embeds"][0]
    return {"attachments": [{"color": f"#{embed['color']:06x}",
                             "title": embed["title"],
                             "text": embed["description"]}]}


def post_webhook(url: str, body: dict) -> int:
    req = urllib.request.Request(
        url, data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status
    except urllib.error.HTTPError as ex:
        raise RuntimeError(f"webhook POST failed: HTTP {ex.code}") from ex
    except Exception as ex:
        raise RuntimeError(f"webhook POST failed: {ex.__class__.__name__}") from ex


def alert(findings: list[dict], webhook_url: str, dry_run: bool) -> None:
    """Page once per finding key. Raises on POST failure so the caller
    leaves the state untouched and retries next pass."""
    is_discord = "discord" in webhook_url
    body = (build_alert_payload(findings)[1] if is_discord
            else build_slack_payload(findings))
    if dry_run:
        title = (body["embeds"][0]["title"] if is_discord
                 else body["attachments"][0]["title"])
        print(f"dry-run: would alert ({'discord' if is_discord else 'slack'}): {title}")
        return
    status = post_webhook(webhook_url, body)
    print(f"alert: HTTP {status} for {len(findings)} finding(s)")


# ── the pass ───────────────────────────────────────────────────────

def one_pass(alert_webhook: bool = True, webhook_url: str = "",
             state_path: str = "", dry_run: bool = False,
             positions_url: str = DEFAULT_POSITIONS_URL,
             account_url: str = DEFAULT_ACCOUNT_URL) -> int:
    state = load_state(state_path) if state_path else None
    rs = rows()
    first_run = state is None

    if first_run:
        # Baseline: everything that already exists predates this watcher —
        # record it silently so only NEW regressions ever page.
        state = {"alerted": set(), "pending": {}}
        existing, _ = journal_findings(rs), None
        missing, _ = vanish_candidates(rs, positions_url, account_url)
        for f in existing:
            state["alerted"].add(f["key"])
        for t in missing:
            state["alerted"].add(f"vanish|{t}")
        n = len(state["alerted"])
        if state_path:
            save_state(state_path, {"alerted": sorted(state["alerted"]),
                                    "pending": {}})
        print(f"baseline primed: {n} pre-existing finding(s) recorded "
              "silently (only NEW regressions will page)")
        return 0

    state = {"alerted": set(state["alerted"]), "pending": dict(state.get("pending", {}))}
    findings, state = evaluate(rs, state, positions_url, account_url)

    if findings:
        print(f"=== {len(findings)} close-row finding(s) ===")
        for f in findings:
            print(f"  [{f['kind']}] ticket #{f['ticket']} "
                  f"at {f['ts'][:19]} UTC: {f['detail']}")
        if alert_webhook and webhook_url:
            try:
                alert(findings, webhook_url, dry_run)
                if not dry_run:
                    for f in findings:
                        state["alerted"].add(f["key"])
            except RuntimeError as exc:
                print(f"alert FAILED (will retry next pass, state untouched): {exc}")
                if state_path:
                    save_state(state_path, {"alerted": sorted(state["alerted"]),
                                            "pending": state["pending"]})
                return 2
        # No webhook / --no-alert / --dry-run: NEVER record — the finding
        # persists (exit 2 every pass) until it can actually be paged,
        # the same contract as the TP1 gate-regression drill.
    else:
        print("=== no close-row findings ===")

    if state_path and not dry_run:
        save_state(state_path, {"alerted": sorted(state["alerted"]),
                                "pending": state["pending"]})
    return 2 if findings else 0


def main() -> None:
    # Scheduled-task output capture has no PYTHONIOENCODING, so stdout
    # defaults to cp1252 and journal text (→, —) kills the pass with
    # UnicodeEncodeError right after the first print. Force UTF-8 on both
    # streams regardless of console or pipe (Python 3.7+; None streams
    # raise AttributeError and are skipped).
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
    ap.add_argument("--positions-url", default=DEFAULT_POSITIONS_URL,
                    help="bridge /positions URL override (tests)")
    ap.add_argument("--account-url", default=DEFAULT_ACCOUNT_URL,
                    help="bridge /account URL override (tests)")
    ap.add_argument("--dry-run", action="store_true",
                    help="build and show the alert, never POST, never remember")
    args = ap.parse_args()

    url = args.webhook_url
    if url is None and not args.no_alert:
        url = load_settings().get("WebhookUrl") or ""
    state = args.state or default_state_path()

    if args.loop <= 0:
        sys.exit(one_pass(not args.no_alert, url or "", state, args.dry_run,
                          args.positions_url, args.account_url))

    while True:
        try:
            one_pass(not args.no_alert, url or "", state, args.dry_run,
                     args.positions_url, args.account_url)
        except Exception as exc:  # a watcher never crashes the watch
            print(f"watch error (continuing): {exc}")
        print("-" * 60)
        time.sleep(args.loop)


if __name__ == "__main__":
    main()
