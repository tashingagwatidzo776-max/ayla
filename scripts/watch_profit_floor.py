#!/usr/bin/env python3
"""Watch the journal for the profit-floor tier's first live save.

The verification chain for one event (evidence, not hope):
  1. FX_EXIT row with Override == "profit-floor", or a drawdown vote with
     Exit >= 0.85 whose reason says the peak was given back.
  2. FX_RISK row "exit evaluation requested" for the same ticket.
  3. a close-confirmation row for the same ticket ("closed #N" — the
     confirmations share the FX_EXIT category, per the digest's rule).

When a save VERIFIES (all three legs), the script alerts the configured
webhook (Settings -> notifications in the app, same Discord/Slack channel
the settlements use) exactly once per event — dedup state in
data/watcher/profit-floor-alerts.json survives restarts. Exit code 2 when
a verified save exists (0 otherwise) so any scheduler can react too.
Exit code 3 when a TP1 gate regression is detected: a TP1-ARM row on a
ticket whose trailing mode was STRUCTURE_TRAIL at arm time — the floor
owns those partials (docs/soak/TP1-FLOOR-INTERACTION.md), so an armed
rung there is the gate failing open. A regression also PAGES the
webhook (once per arm row, deduped across restarts like the save
alerts), so the drill pages instead of only failing.

Each pass also prints the open book's giveback posture (newest FX_PROFIT
row per ticket) so the approach to the 60% watch bar and the 75% override
bar is visible before anything fires.

Journal-only by construction: reads, never trades.

Usage:
  python scripts/watch_profit_floor.py                # one pass (scheduler)
  python scripts/watch_profit_floor.py --loop 60      # poll every 60s
  python scripts/watch_profit_floor.py --no-alert     # observe, never post
  python scripts/watch_profit_floor.py --dry-run      # show alert, no POST

The journal lives under %APPDATA%\\tf\\data\\journal, or
$TF_DATA_DIR\\journal when the tests set the app's data-dir override.
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

GIVEBACK_VOTE_MIN = 0.85     # the deep-giveback vote bar (GivebackVoteRatio)
GIVEBACK_WATCH = 0.60        # the watch bar (GivebackWatchRatio)
GIVEBACK_OVERRIDE = 0.75     # the override bar (GivebackOverrideRatio)


def data_dir() -> str:
    """The app's data dir: TF_DATA_DIR override, else the real %APPDATA%
    location. Same convention the app itself honors."""
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def journal_files() -> list[str]:
    return sorted(glob.glob(os.path.join(data_dir(), "journal", "journal_*.jsonl")))


def default_state_path() -> str:
    return os.path.join(data_dir(), "watcher", "profit-floor-alerts.json")


def load_settings() -> dict:
    try:
        with open(os.path.join(data_dir(), "settings.json"), encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def rows() -> list[dict]:
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


def payload_has_ticket(p: dict, ticket) -> bool:
    try:
        return int(p.get("Ticket", -1)) == int(ticket)
    except (TypeError, ValueError):
        return False


def find_saves(rs: list[dict]) -> list[dict]:
    """Stage-1 events: profit-floor overrides or deep giveback votes."""
    saves = []
    for r in rs:
        p = r["payload"]
        if r["cat"] != "FX_EXIT":
            continue
        if p.get("Override") == "profit-floor":
            saves.append({**r, "ticket": p.get("Ticket"), "kind": "override"})
            continue
        for v in p.get("Votes", []):
            reason = str(v.get("Reason", "")).lower()
            if (v.get("Engine") == "drawdown"
                    and isinstance(v.get("Exit"), (int, float))
                    and v["Exit"] >= GIVEBACK_VOTE_MIN
                    # Marker strings mirror FxExitBrain.DrawdownVote's
                    # reasons: "the move was given back" (round-trip vote)
                    # and "give-back ... of a peak" (deep-giveback vote).
                    and ("given back" in reason or "give-back" in reason)):
                saves.append({**r, "ticket": p.get("Ticket"), "kind": "vote"})
                break
    return saves


def verify_chain(rs: list[dict], save: dict) -> dict:
    """Stages 2 and 3 for one save: the FX_RISK request and the close
    confirmation. Stage 3 scans ANY row containing 'closed #<ticket>' —
    close confirmations share the FX_EXIT category (digest's rule), so
    pinning them to FX_ORDER would never match."""
    ticket = save["ticket"]
    risk = any(
        r["cat"] == "FX_RISK"
        and "exit evaluation requested" in r["details"]
        and payload_has_ticket(r["payload"], ticket)
        and r["ts"] >= save["ts"]
        for r in rs
    )
    marker = f"closed #{ticket}"
    close = any(marker in r["details"] and r["ts"] >= save["ts"] for r in rs)
    return {"risk": risk, "close": close}


def book_posture(rs: list[dict]) -> dict[int, dict]:
    """Newest FX_PROFIT row per ticket: the giveback posture."""
    latest: dict[int, dict] = {}
    for r in rs:
        if r["cat"] != "FX_PROFIT":
            continue
        p = r["payload"]
        try:
            ticket = int(p.get("Ticket", -1))
        except (TypeError, ValueError):
            continue
        if ticket > 0:
            latest[ticket] = p
    return latest


def gate_regressions(rs: list[dict]) -> list[dict]:
    """TP1-ARM rows on tickets whose trailing mode — as of the arm row —
    was STRUCTURE_TRAIL. TP1-FLOOR-INTERACTION.md: the never-down floor
    owns the partial there, so an armed rung is the gate failing open.

    The arm row's payload carries no TrailingMode (Ticket/Target/
    TargetPrice/TargetR/PlanPct), so the mode is reconstructed from the
    ticket's FX_PROFIT telemetry rows that do carry the field (plain
    profit telemetry and TP1-SKIP rows). Conservative by construction:
    only the newest mode stamped AT OR BEFORE the arm counts, and a
    ticket with no mode journaled before the arm stays silent — absence
    of evidence is not a regression."""
    modes: dict[int, list[tuple[str, str]]] = {}
    for r in rs:
        if r["cat"] != "FX_PROFIT":
            continue
        p = r["payload"]
        mode = p.get("TrailingMode")
        try:
            ticket = int(p.get("Ticket", -1))
        except (TypeError, ValueError):
            continue
        if isinstance(mode, str) and mode and ticket > 0:
            modes.setdefault(ticket, []).append((r["ts"], mode))
    out = []
    for r in rs:
        if "TP1-ARM" not in r.get("details", ""):
            continue
        try:
            ticket = int(r["payload"].get("Ticket", -1))
        except (TypeError, ValueError):
            continue
        known = [m for ts, m in modes.get(ticket, []) if ts <= r["ts"]]
        if known and known[-1] == "STRUCTURE_TRAIL":
            out.append(r)
    return out


def daily_capture(rs: list[dict], days: int = 7) -> list[tuple[str, int, float, float, float]]:
    """Per-UTC-day profit capture over the last `days` days (decisive exits
    with MFE >= 0.5R): (date, trades, capturedR, availableR, ratio). A
    zero-trade day is dropped — absence of evidence is not 0% capture.
    The weekly digest rolls up a week; THIS sees a bend within a day."""
    buckets: dict[str, list[tuple[float, float]]] = {}
    for r in rs:
        if r["cat"] != "FX_EXIT":
            continue
        p = r["payload"]
        action = p.get("Action", "")
        decisive = action in ("full", "partial") or p.get("Override") is not None
        mfe = p.get("MfeR") or 0
        if not decisive or not isinstance(mfe, (int, float)) or mfe < 0.5:
            continue
        day = r["ts"][:10]
        buckets.setdefault(day, []).append((max(p.get("ProfitR") or 0, 0), mfe))

    out = []
    for day in sorted(buckets)[-days:]:
        pairs = buckets[day]
        captured = sum(c for c, _ in pairs)
        available = sum(m for _, m in pairs)
        out.append((day, len(pairs), captured, available,
                    captured / available if available > 0 else 0.0))
    return out


# ── webhook alerting (Discord/Slack, payload shape per WebhookService) ──

def build_alert_payload(save: dict, chain: dict) -> tuple[str, dict]:
    """The alert body for one verified save. Discord detection matches the
    metrics-digest convention ('discord' in the URL). Returns (kind, body).
    """
    p = save["payload"]
    peak = p.get("MfeR", 0)
    cur = p.get("ProfitR", 0)
    saved = max(peak - max(cur, 0), 0)
    floor = p.get("FloorR", 0) or 0
    title = f"\U0001f6e1\ufe0f Profit-floor save — ticket {save['ticket']}"
    text = (
        f"Gave back a {peak:.1f}R peak to {cur:+.2f}R — the profit floor "
        f"protected what was earned (~{saved:.1f}R saved vs the round-trip). "
        f"Floor at fire time: {floor:.1f}R.\n"
        f"Chain verified: FX_EXIT {save['kind']}, FX_RISK request "
        f"{'yes' if chain['risk'] else 'NO'}, close confirmation "
        f"{'yes' if chain['close'] else 'NO'}.\n"
        f"{save['ts'][:19]} UTC"
    )
    return "discord", {
        "embeds": [{"title": title, "description": text, "color": 0x2E7D32}]}


def build_alert_payload_slack(save: dict, chain: dict) -> dict:
    _, discord = build_alert_payload(save, chain)
    embed = discord["embeds"][0]
    return {"attachments": [{"color": f"#{embed['color']:06x}",
                             "title": embed["title"],
                             "text": embed["description"]}]}


def build_gate_alert_payload(regressions: list[dict]) -> tuple[str, dict]:
    """The page for the drill: a rung armed on a STRUCTURE_TRAIL ticket
    means the trailing-mode gate failed open. One embed per regression
    row (the caller dedups rows, so one row per call is the shape)."""
    r = regressions[0]
    ticket = r["payload"].get("Ticket", "?")
    ts = r.get("ts", "?")
    title = "\U0001f6a8 TP1 gate regression — rung armed on STRUCTURE_TRAIL"
    text = (
        f"TP1-ARM on ticket #{ticket} whose trailing mode was "
        f"STRUCTURE_TRAIL at arm time — the gate failed open; the floor "
        f"owns that partial (docs/soak/TP1-FLOOR-INTERACTION.md).\n"
        f"{ts[:19]} UTC — {len(regressions)} regression row(s) in this pass"
    )
    return "discord", {
        "embeds": [{"title": title, "description": text, "color": 0xB71C1C}]}


def alert_gate(regressions: list[dict], webhook_url: str, state_path: str,
               dry_run: bool = False) -> int:
    """Page the webhook once per regression row — deduped across restarts
    in the same state file as the save alerts (a lost entry means one
    re-page). Returns the number of pages sent (or would have been,
    under --dry-run). A failed POST raises so the pass can retry next
    time with the state untouched."""
    is_discord = "discord" in webhook_url
    seen = load_alerted(state_path)
    new_keys: list[str] = []
    sent = 0
    try:
        for r in regressions:
            key = f"gate|{r.get('ts', '')}|{r['payload'].get('Ticket', '?')}"
            if key in seen:
                continue
            _, discord = build_gate_alert_payload([r])
            body = discord if is_discord else {
                "attachments": [{
                    "color": f"#{discord['embeds'][0]['color']:06x}",
                    "title": discord["embeds"][0]["title"],
                    "text": discord["embeds"][0]["description"],
                }]}
            if dry_run:
                print("dry-run: would alert (gate regression)")
            else:
                status = post_webhook(webhook_url, body)
                print(f"alert: HTTP {status} for {key}")
            new_keys.append(key)
            sent += 1
    finally:
        if new_keys and not dry_run:
            save_alerted(state_path, seen | set(new_keys))
    return sent


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


def event_key(save: dict) -> str:
    return f"{save['ts']}|{save['ticket']}|{save['kind']}"


def alert(save: dict, chain: dict, webhook_url: str, state_path: str,
          dry_run: bool = False) -> bool:
    """Post the alert for one verified save unless already alerted. Returns
    True when an alert was sent (or would have been, under --dry-run)."""
    key = event_key(save)
    seen = load_alerted(state_path)
    if key in seen:
        return False

    is_discord = "discord" in webhook_url
    body = (build_alert_payload(save, chain)[1] if is_discord
            else build_alert_payload_slack(save, chain))
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

def one_pass(alert_webhook: bool = True, webhook_url: str = "",
             state_path: str = "", dry_run: bool = False) -> int:
    rs = rows()
    saves = find_saves(rs)
    verified = 0

    if saves:
        print(f"=== {len(saves)} giveback event(s) found ===")
        for s in saves[-10:]:
            chain = verify_chain(rs, s)
            p = s["payload"]
            ok = chain["risk"] and chain["close"]
            verified += 1 if ok else 0
            print(f"{s['ts'][:19]} [{s['kind']}] ticket {s['ticket']} "
                  f"peak {p.get('MfeR', 0):.1f}R -> {p.get('ProfitR', 0):.2f}R "
                  f"| FX_RISK request: {chain['risk']} | close: {chain['close']}"
                  f"{'  <== VERIFIED' if ok else ''}")
            if ok and alert_webhook and webhook_url:
                try:
                    alert(s, chain, webhook_url, state_path, dry_run)
                except RuntimeError as exc:
                    print(f"alert FAILED (will retry next pass, state untouched): {exc}")

    posture = book_posture(rs)
    print(f"=== open book posture ({len(posture)} tracked) ===")
    for ticket, p in sorted(posture.items(), key=lambda kv: -kv[1].get("PeakR", 0)):
        peak = p.get("PeakR", 0)
        cur = p.get("CurrentR", 0)
        gb = p.get("GivebackPct", 0)
        flag = ""
        if peak >= 2.0 and gb >= GIVEBACK_OVERRIDE * 100:
            flag = "  <== AT OVERRIDE BAR"
        elif peak >= 1.5 and gb >= GIVEBACK_WATCH * 100:
            flag = "  <== watch band"
        print(f"  #{ticket}: peak {peak:.1f}R cur {cur:+.2f}R "
              f"giveback {gb:.0f}% floor {p.get('FloorR', 0):.1f}R "
              f"{p.get('GivebackClass', '')}{flag}")

    capture = daily_capture(rs)
    if capture:
        print("=== daily profit capture (decisive, MFE >= 0.5R) ===")
        for day, trades, cap_r, avail_r, ratio in capture:
            bar = "█" * round(ratio * 20)
            print(f"  {day}  {ratio * 100:5.1f}%  {cap_r:7.2f}R / {avail_r:7.2f}R  "
                  f"{trades} trade(s)  |{bar:<20}|")

    # TP1 prototype telemetry: the armed rungs and the banked execs.
    # Silent until the FxExecuteTp1Partials prototype produces rows — the
    # watcher surfaces them so the first ARM/EXEC is seen autonomously.
    # (rows() keys the raw text "details" — lowercase; an earlier version
    # read "Details" here and the section could never fire.)
    tp1 = [r for r in rs if "TP1-ARM" in r.get("details", "")
           or "TP1-EXEC" in r.get("details", "")]
    if tp1:
        arms = sum(1 for r in tp1 if "TP1-ARM" in r["details"])
        execs = [r for r in tp1 if "TP1-EXEC" in r["details"]]
        print(f"=== TP1 prototype ({arms} arm row(s), {len(execs)} exec row(s)) ===")
        for r in (tp1 if len(tp1) <= 6 else tp1[-6:]):
            print(f"  {r.get('ts', '?')[:19]}  {r['details'][:100]}")

    # The gate-regression drill (TP1-FLOOR-INTERACTION.md): an ARM on a
    # STRUCTURE_TRAIL ticket means the trailing-mode gate failed open.
    # Persistent, not deduped — the pass fails (exit 3) every minute until
    # a human looks, because the journal keeps the evidence.
    regressions = gate_regressions(rs)
    if regressions:
        print(f"!!! TP1 GATE REGRESSION — {len(regressions)} arm row(s) on "
              f"STRUCTURE_TRAIL ticket(s); the floor owns those partials "
              f"(docs/soak/TP1-FLOOR-INTERACTION.md)")
        for r in (regressions if len(regressions) <= 10 else regressions[-10:]):
            print(f"  {r.get('ts', '?')[:19]}  {r['details'][:100]}")
        if alert_webhook and webhook_url:
            try:
                alert_gate(regressions, webhook_url, state_path, dry_run)
            except RuntimeError as exc:
                print(f"gate alert FAILED (will retry next pass, state untouched): {exc}")
    return 3 if regressions else (2 if verified else 0)


def main() -> None:
    # The scheduled task runs this with output captured to a file and no
    # PYTHONIOENCODING set, so Windows defaults stdout to the ANSI code
    # page (cp1252) — and the capture-trend bar (U+2588 █) plus assorted
    # journal text crash the pass with UnicodeEncodeError right after the
    # posture print. A scheduler never sees the traceback, so every pass
    # since the first non-zero capture day (2026-09-30) has silently died
    # before the TP1 section. Force UTF-8 on both streams regardless of
    # console or pipe (Python 3.7+).
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
