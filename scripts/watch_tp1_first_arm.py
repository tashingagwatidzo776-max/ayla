#!/usr/bin/env python3
"""Page the webhook the moment the FIRST TP1-ARM rows hit the journal.

The grading arc waits on real traffic; instead of a session babysitting
the journal, the scheduler runs this every few minutes. On the first
TP1-ARM row (deduped across restarts via data/watcher/) it

  1. appends an evidence dump — every TP1 row plus every FX_* row for
     the referenced tickets — to data/watcher/tp1-first-arm-evidence.jsonl
     (once per journal file, so re-runs don't duplicate),
  2. posts the webhook (Settings -> notifications, same channel the
     saves page): "first TP1 arm" with the rung, the plan %, and the
     evidence path.
  3. on the ticket's TP1-EXEC row (the rung actually BANKED — or was
     refused), pages again with the banked size and the rung R from the
     ARM row: the graded number in the message where the operator
     reads it. Refusals (Executed=false) page too — a refused rung is
     exactly what grading needs to see.

TP1-SKIP rows are evidence-dumped too (the gate answering IS the
finding) but never page — ARM and EXEC are the paging events.

Exit codes: 0 nothing new · 2 paged (scheduler-visible). Journal-only
by construction: reads, never trades.

Usage:
  python scripts/watch_tp1_first_arm.py            # one pass (scheduler)
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request

TICKET_RE = re.compile(r"#(\d+)")


def data_dir() -> str:
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def journal_files() -> list[str]:
    return sorted(glob.glob(os.path.join(data_dir(), "journal", "journal_*.jsonl")))


def state_path() -> str:
    return os.path.join(data_dir(), "watcher", "tp1-first-arm-state.json")


def evidence_path() -> str:
    return os.path.join(data_dir(), "watcher", "tp1-first-arm-evidence.jsonl")


def load_state() -> dict:
    try:
        with open(state_path(), encoding="utf-8") as fh:
            data = json.load(fh)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def save_state(state: dict) -> None:
    try:
        os.makedirs(os.path.dirname(state_path()), exist_ok=True)
        with open(state_path(), "w", encoding="utf-8") as fh:
            json.dump(state, fh)
    except OSError:
        pass   # a lost state file means one re-page — acceptable


def load_settings() -> dict:
    try:
        with open(os.path.join(data_dir(), "settings.json"), encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def parse_rows() -> list[dict]:
    """Every journal row whose details mention TP1-, with the payload
    parsed out of the first '{' (the watcher's rows() convention)."""
    out = []
    for path in journal_files():
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "TP1-" not in line or '"FX_' not in line:
                    continue
                try:
                    env = json.loads(line)
                except json.JSONDecodeError:
                    continue
                details = str(env.get("Details", ""))
                brace = details.find("{")
                payload = {}
                if brace >= 0:
                    try:
                        payload = json.loads(details[brace:])
                    except json.JSONDecodeError:
                        payload = {}
                out.append({"file": os.path.basename(path),
                            "ts": str(env.get("Timestamp", "")),
                            "cat": env.get("Category", ""),
                            "details": details, "payload": payload})
    out.sort(key=lambda r: r["ts"])
    return out


def evidence_for(rows: list[dict], dumped: list[str]) -> list[str]:
    """Journal files with TP1 rows not yet dumped: append the TP1 rows
    plus every FX_* row for the referenced tickets. Returns newly
    dumped file names."""
    todo = [r for r in rows if r["file"] not in dumped]
    if not todo:
        return []
    tickets = set(TICKET_RE.findall("\n".join(r["details"] for r in todo)))
    appended = []
    for path in journal_files():
        name = os.path.basename(path)
        with open(path, encoding="utf-8", errors="replace") as fh:
            keep = []
            for line in fh:
                if '"FX_' not in line:
                    continue
                if "TP1-" in line or any(f"#{t}" in line for t in tickets):
                    keep.append(line if line.endswith("\n") else line + "\n")
        if keep:
            os.makedirs(os.path.dirname(evidence_path()), exist_ok=True)
            with open(evidence_path(), "a", encoding="utf-8") as out:
                out.writelines(keep)
            appended.append(name)
    return appended


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


def arm_by_key(rows: list[dict]) -> dict[object, list[tuple[str, dict]]]:
    """ARM rows indexed by ticket (ts-ordered) — the EXEC leg resolves
    the rung R and plan % from the newest ARM at-or-before the execution
    (an app restart can legitimately re-ARM the same ticket)."""
    out: dict[object, list[tuple[str, dict]]] = {}
    for r in rows:
        if "TP1-ARM" in r["details"]:
            out.setdefault(r["payload"].get("Ticket", "?"), []).append(
                (r["ts"], r))
    return out


def arm_for(exec_row: dict, arms: dict) -> dict | None:
    history = arms.get(exec_row["payload"].get("Ticket", "?"), [])
    prior = [row for ts, row in history if ts <= exec_row["ts"]]
    return prior[-1] if prior else None


def build_payload(arm: dict, ev_files: list[str]) -> tuple[str, dict]:
    p = arm["payload"]
    ticket = p.get("Ticket", "?")
    title = f"\U0001f3af First TP1 arm — ticket {ticket}"
    text = (
        f"Rung {p.get('Target', '?')} at {p.get('TargetPrice', '?')} "
        f"({p.get('TargetR', '?')}R), {p.get('PlanPct', '?')}% plan — "
        f"the partial prototype is live; grading runbook: "
        f"docs/soak/WEEK-TWO-TP1-LOG.md.\n"
        f"Evidence: {evidence_path()}"
        + (f" (+ {len(ev_files)} journal file(s))" if ev_files else "")
        + f"\n{arm['ts'][:19]} UTC"
    )
    return "discord", {
        "embeds": [{"title": title, "description": text, "color": 0x1565C0}]}


def build_exec_payload(exec_row: dict, arm: dict | None,
                       ev_files: list[str]) -> tuple[str, dict]:
    """The page for a banked (or refused) rung. The graded number —
    banked size and the rung's R from the ARM row — goes in the message
    where the operator reads it; the EXEC payload has lots but no R."""
    p = exec_row["payload"]
    ticket = p.get("Ticket", "?")
    lots = p.get("Lots", "?")
    if p.get("Executed") is False:
        title = f"\u26a0\ufe0f TP1 rung refused — ticket {ticket}"
        text = (f"TP1-EXEC refused ({p.get('Retcode', '?')}) for "
                f"{lots} lots — grading needs to see this.\n")
    else:
        rung = arm["payload"].get("Target") if arm else "?"
        rung_r = arm["payload"].get("TargetR", "?") if arm else "?"
        plan = arm["payload"].get("PlanPct", "?") if arm else "?"
        title = f"\U0001f3af TP1 banked — ticket {ticket}"
        text = (f"Banked {lots} lots ({plan}% plan) at the armed rung "
                f"{rung} — {rung_r}R of the move locked. Grading: "
                f"docs/soak/WEEK-TWO-TP1-LOG.md.\n")
    text += (f"Evidence: {evidence_path()}"
             + (f" (+ {len(ev_files)} journal file(s))" if ev_files else "")
             + f"\n{exec_row['ts'][:19]} UTC")
    return "discord", {
        "embeds": [{"title": title, "description": text, "color": 0x2E7D32}]}


def one_pass() -> int:
    state = load_state()
    rows = parse_rows()
    if not rows:
        return 0

    dumped = evidence_for(rows, list(state.get("dumped", [])))
    if dumped:
        state["dumped"] = sorted(set(state.get("dumped", [])) | set(dumped))
        save_state(state)

    arms = arm_by_key(rows)
    paged = 0
    url = load_settings().get("WebhookUrl") or ""
    for r in rows:
        is_arm = "TP1-ARM" in r["details"]
        is_exec = "TP1-EXEC" in r["details"]
        if not (is_arm or is_exec):
            continue
        key = f"{r['ts']}|{r['payload'].get('Ticket', '?')}"
        if key in state.get("paged", []):
            continue
        if not url:
            print(f"TP1 row {key} found but no WebhookUrl — retrying next pass")
            continue
        is_discord = "discord" in url
        _, discord = (build_payload(r, dumped) if is_arm
                      else build_exec_payload(r, arm_for(r, arms), dumped))
        body = discord if is_discord else {
            "attachments": [{
                "color": f"#{discord['embeds'][0]['color']:06x}",
                "title": discord["embeds"][0]["title"],
                "text": discord["embeds"][0]["description"],
            }]}
        status = post_webhook(url, body)
        print(f"alert: HTTP {status} for {key}")
        state["paged"] = sorted(set(state.get("paged", [])) | {key})
        save_state(state)
        paged += 1
    return 2 if paged else 0


def main() -> None:
    # Same scheduled-task trap the profit-floor watcher hit: piped stdout
    # under cp1252 crashes on non-ASCII journal text (█, —, runes in
    # symbol names). Force UTF-8 on both streams.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, OSError):
            pass
    try:
        sys.exit(one_pass())
    except RuntimeError as exc:
        print(f"pager error (will retry next pass): {exc}")
        sys.exit(1)


if __name__ == "__main__":
    main()
