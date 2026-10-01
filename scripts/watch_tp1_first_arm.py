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
  4. refreshes the TP1-EXEC digest block in
     docs/soak/WEEK-TWO-TP1-LOG.md so EVERY banked rung — not just the
     first — lands in the log automatically (one row per EXEC,
     mechanical columns only; the capture/vs-giveback verdict stays the
     manual runbook step).

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

# The digest block is replaced in place on every pass — never appended —
# so the log stays hand-editable and the table never accumulates stale rows.
DIGEST_START = "<!-- TP1-EXEC-DIGEST:START -->"
DIGEST_END = "<!-- TP1-EXEC-DIGEST:END -->"


def data_dir() -> str:
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def journal_files() -> list[str]:
    return sorted(glob.glob(os.path.join(data_dir(), "journal", "journal_*.jsonl")))


def state_path() -> str:
    return os.path.join(data_dir(), "watcher", "tp1-first-arm-state.json")


def evidence_path() -> str:
    return os.path.join(data_dir(), "watcher", "tp1-first-arm-evidence.jsonl")


def digest_log_path() -> str:
    """Where the digest block lands: TF_TP1_DIGEST_LOG override (tests),
    else the repo's docs/soak/WEEK-TWO-TP1-LOG.md (the script lives in
    <repo>/scripts, the log in <repo>/docs/soak)."""
    override = os.environ.get("TF_TP1_DIGEST_LOG")
    if override:
        return override
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(repo, "docs", "soak", "WEEK-TWO-TP1-LOG.md")


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


def parse_envelope(line: str) -> dict | None:
    """One raw journal line -> the row shape every consumer here uses
    ({ts, cat, details, payload}), or None for unparseable lines. The
    payload is the JSON embedded in Details after its first '{'."""
    try:
        env = json.loads(line)
    except json.JSONDecodeError:
        return None
    details = str(env.get("Details", ""))
    brace = details.find("{")
    payload = {}
    if brace >= 0:
        try:
            payload = json.loads(details[brace:])
        except json.JSONDecodeError:
            payload = {}
    return {"ts": str(env.get("Timestamp", "")),
            "cat": str(env.get("Category", "")),
            "details": details, "payload": payload}


def parse_rows() -> list[dict]:
    """Every journal row whose details mention TP1-, with the payload
    parsed out of the first '{' (the watcher's rows() convention)."""
    out = []
    for path in journal_files():
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "TP1-" not in line or '"FX_' not in line:
                    continue
                row = parse_envelope(line)
                if row is not None:
                    row["file"] = os.path.basename(path)
                    out.append(row)
    out.sort(key=lambda r: r["ts"])
    return out


def digest_rows() -> list[dict]:
    """The digest's substrate: every FX_PROFIT / FX_EXIT row plus every
    TP1-marked row, ts-ordered. Read straight from the journals — NOT
    from the evidence file, whose dump is once-per-journal-file and so
    misses rows landing after the first dump (a second banked rung on
    the same day must still reach the log)."""
    out = []
    for path in journal_files():
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if '"FX_' not in line:
                    continue
                if "TP1-" not in line and '"FX_PROFIT"' not in line \
                        and '"FX_EXIT"' not in line:
                    continue
                row = parse_envelope(line)
                if row is not None:
                    out.append(row)
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

    # Every banked rung, not just the first, lands in the log's digest
    # block. Best-effort by construction: a failure here must never
    # break the watch — the journals keep the raw rows either way.
    try:
        build_exec_digest(digest_log_path())
    except Exception:
        pass

    return 2 if paged else 0


def _num(value) -> float | None:
    return value if isinstance(value, (int, float)) else None


def _trailing_mode(rows: list[dict], ticket, at_ts: str) -> str:
    """The gate-drill reconstruction rule: the newest TrailingMode stamp
    at-or-before at_ts; '—' when the mode was never journaled there."""
    stamps = []
    for r in rows:
        if r["cat"] != "FX_PROFIT":
            continue
        p = r["payload"]
        try:
            if int(p.get("Ticket", -1)) != int(ticket):
                continue
        except (TypeError, ValueError):
            continue
        mode = p.get("TrailingMode")
        if isinstance(mode, str) and mode and r["ts"] <= at_ts:
            stamps.append((r["ts"], mode))
    return sorted(stamps)[-1][1] if stamps else "—"


def _symbol_of(details: str) -> str:
    head = details.split(" #", 1)[0].strip()
    return head if head and len(head) <= 12 and " " not in head else "—"


def build_exec_digest(log_path: str) -> int:
    """Refresh the TP1-EXEC digest block in the week-two log: one row
    per banked (or refused) rung, mechanically derived from the
    journals — rung/plan/banked R from the ARM+EXEC payloads, live peak
    from the newest FX_PROFIT report at-or-before the exec, closed R
    from the newest decisive FX_EXIT after it, TrailingMode
    reconstructed with the gate-drill rule. Capture and the
    vs-giveback verdict stay the manual runbook step.

    Returns 0 written · 2 nothing to do (no EXEC rows, unreadable
    journal, or no anchor heading in the log). Idempotent: the block
    between the markers is REPLACED, never appended, so re-runs and
    re-pages leave exactly one current table.
    """
    rows = digest_rows()
    execs = [r for r in rows if "TP1-EXEC" in r["details"]]
    if not execs:
        return 2

    block = [
        "### Live TP1-EXEC digest (auto)",
        "",
        "Regenerated every pass by `scripts/watch_tp1_first_arm.py` —",
        "mechanical columns only; Capture and the vs-giveback verdict",
        "remain the manual runbook step below. Banked R = plan % × rung R",
        "(position-weighted R locked at the cross). Closed R = newest",
        "decisive FX_EXIT ProfitR after the exec ('—' while open).",
        "TrailingMode is reconstructed from the ticket's FX_PROFIT",
        "telemetry at-or-before the arm (unknown → '—'; the same rule",
        "the gate-regression drill uses).",
        "",
        "| Ticket | Symbol | Mode | Armed (UTC) | Exec (UTC) | Rung | Plan | Banked R | Live peak | Closed R | Executed |",
        "|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    arms = arm_by_key(rows)
    for r in execs:
        p = r["payload"]
        ticket = p.get("Ticket", "?")
        arm = arm_for(r, arms)
        rung_r = _num((arm or {}).get("payload", {}).get("TargetR"))
        plan = _num(p.get("PlanPct")) or _num((arm or {}).get("payload", {}).get("PlanPct"))
        banked = f"{plan / 100 * rung_r:+.2f}R" \
            if plan is not None and rung_r is not None else "—"
        rung = f"{rung_r:g}R" if rung_r is not None else "—"

        peak = "—"
        for pr in rows:
            if pr["cat"] != "FX_PROFIT" or pr["ts"] > r["ts"]:
                continue
            pp = pr["payload"]
            try:
                if int(pp.get("Ticket", -1)) != int(ticket):
                    continue
            except (TypeError, ValueError):
                continue
            v = _num(pp.get("PeakR"))
            if v is not None:
                peak = f"{v:.1f}R"

        closed = "—"
        for xr in rows:
            if xr["cat"] != "FX_EXIT" or xr["ts"] <= r["ts"]:
                continue
            xp = xr["payload"]
            try:
                if int(xp.get("Ticket", -1)) != int(ticket):
                    continue
            except (TypeError, ValueError):
                continue
            if xp.get("Action") not in ("full", "partial") \
                    and xp.get("Override") is None:
                continue
            v = _num(xp.get("ProfitR"))
            if v is not None:
                closed = f"{v:+.2f}R"

        executed = "yes" if p.get("Executed") is not False \
            else f"no (rc {p.get('Retcode', '?')})"
        block.append(
            f"| #{ticket} | {_symbol_of(r['details'])} "
            f"| {_trailing_mode(rows, ticket, (arm or r)['ts'])} "
            f"| {(arm or r)['ts'][:19]} | {r['ts'][:19]} "
            f"| {rung} | {plan if plan is not None else '—'} "
            f"| {banked} | {peak} | {closed} | {executed} |"
        )
    block.append("")

    try:
        with open(log_path, encoding="utf-8") as fh:
            log = fh.read()
    except OSError:
        return 2

    new_block = f"{DIGEST_START}\n" + "\n".join(block) + f"\n{DIGEST_END}"
    if DIGEST_START in log and DIGEST_END in log:
        pre, rest = log.split(DIGEST_START, 1)
        _, post = rest.split(DIGEST_END, 1)
        new_log = pre + new_block + post
    else:
        # First insertion: directly above the manual grading table the
        # digest feeds — the anchor the runbook grades from.
        anchor = "## Graded tickets"
        idx = log.find(anchor)
        if idx < 0:
            return 2
        new_log = log[:idx] + new_block + "\n\n" + log[idx:]

    with open(log_path, "w", encoding="utf-8") as fh:
        fh.write(new_log)
    return 0


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
