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
     mechanical columns only).
  5. emits a machine-readable graded verdict per BANKED ticket to
     data/watcher/tp1-graded-verdicts.jsonl and regenerates the
     '## Graded tickets' table from it — so the graded table is
     generated rather than hand-filled. Capture and the vs-giveback
     verdict come from the same arithmetic the backtest uses; nothing
     is judged by hand.   6. pages a plan-% review once the settled verdicts collectively
      trail (down) or beat (up) the giveback baseline, with a candidate
      plan %, and records every recommendation, clearance and acted-on
      mark in data/watcher/tp1-plan-reviews.jsonl — the audit trail
      read back with read_plan_reviews().
   7. verifies an armed plan-% override actually REACHED a rung. The
      dashboard's one-click Arm persists the candidate and the engine
      honors it — but only a TP1-ARM/TP1-EXEC row carrying that PlanPct
      proves it. An override that cannot fire (TP1 partials off) or
      fired at a different plan % (the engine sizing from the allocation
      plan instead) pages immediately; one that never fired at all pages
      past a grace window. --plan-reviews prints the verdict either way.

TP1-SKIP rows are evidence-dumped too (the gate answering IS the
finding) but never page — ARM and EXEC are the paging events.

Exit codes: 0 nothing new · 2 paged (scheduler-visible). Journal-only
by construction: reads, never trades.

Usage:
  python scripts/watch_tp1_first_arm.py            # one pass (scheduler)
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

TICKET_RE = re.compile(r"#(\d+)")

# The digest block is replaced in place on every pass — never appended —
# so the log stays hand-editable and the table never accumulates stale rows.
DIGEST_START = "<!-- TP1-EXEC-DIGEST:START -->"
DIGEST_END = "<!-- TP1-EXEC-DIGEST:END -->"

# The graded table is regenerated in place under its heading: the
# hand-filled placeholder is replaced on first pass, never appended to.
GRADED_START = "<!-- TP1-GRADED:START -->"
GRADED_END = "<!-- TP1-GRADED:END -->"
GRADED_ANCHOR = "## Graded tickets"

# The plan-% recommendation gate: with this many SETTLED graded rungs, a
# net R versus the giveback baseline at or below PLAN_GATE_NET_R means the
# rungs are collectively LOSING value (recommend a lower plan %), and at
# or above PLAN_GATE_UP_NET_R means they are collectively WINNING
# (recommend a higher plan % or wider rungs). vs giveback is positive when
# the rung out-earned holding the whole position to the same exit,
# negative when the floor/exit did.
PLAN_GATE_MIN_GRADED = 3
PLAN_GATE_NET_R = -1.0
PLAN_GATE_UP_NET_R = 1.0

# The candidate plan-% derivation (a graduated heuristic, not an optimizer:
# the graded sample is conditioned on the rung having filled, so the linear
# delta cannot be solved to an interior optimum). From the mean current
# plan % across the graded rungs, move one step per full R of mean
# vs-giveback in the gate's direction, inside a sane banking band.
PLAN_GATE_STEP_R = 1.0      # R of mean per-rung net that moves one step
PLAN_GATE_STEP_PCT = 5.0    # plan points per step
PLAN_GATE_MIN_PCT = 0.0
PLAN_GATE_MAX_PCT = 60.0

# Outcome feedback: once a review closes, the rungs settled AFTER the close
# score whether acting improved the graded net (the mean vs-giveback rose).
# With enough scored reviews, that track record nudges the candidate step —
# the loop learns whether acting has been helping.
PLAN_SCORE_MIN_GRADED = 1     # post-close graded rungs needed to score
PLAN_STEP_FEEDBACK_MIN = 3    # scored reviews before the step adapts
PLAN_STEP_FEEDBACK_PCT = 2.0  # step change when acting usually helps/fails
PLAN_GATE_STEP_MIN = 1.0
PLAN_GATE_STEP_MAX = 15.0
PLAN_STEP_MAX_DRIFT = 3.0     # the step may not drift further than this from base

# A recommendation left OPEN longer than this many days is flagged in the
# status view — the review it asks for never happened.
PLAN_REVIEW_STALE_DAYS = 7

# The armed plan-% override's fire check (see verify_plan_pct_override):
# a rung only arms when price crosses an eligible setup, so "nothing yet"
# is normal for a while — past this many days an armed override no rung
# has ever carried is a DEAD one (partials off, engine stopped, or the
# value simply not honored) and pages once. Watcher-only on purpose: this
# is a paging threshold for a watchdog, not a tunable the app enforces,
# and config/tp1-plan-gate.json carries only what BOTH sides read.
PLAN_OVERRIDE_GRACE_DAYS = 3.0

# How long without a journal row reads as "the app itself is not running".
# Measured against the LIVE journals: while the brain runs the longest
# silent stretch is ~58 min (2026-09-30), and every multi-hour gap ends at
# a `startup` row — so 6h is ~6x the noisiest live gap and cannot fire on
# a quiet-but-running session, while an app closed for half a day always
# clears it. Watcher-only, like PLAN_OVERRIDE_GRACE_DAYS above.
PLAN_APP_IDLE_HOURS = 6.0

# Statuses that mean the armed override will not do what the banner says.
OVERRIDE_FLAG_STATUSES = ("mismatch", "partials-off", "never-fired")

# ...and the reasons that page WITHOUT waiting out PLAN_OVERRIDE_GRACE_DAYS:
# a precondition that cannot heal itself will still be unmet in three days,
# so paging only then would be three days of an override quietly doing
# nothing. The market-healing reasons (no-eligible-rung) and the policy
# pauses (gate-blocked, hold-blocked) keep the grace window — they may
# resolve on their own, and an overdue hold already pages on its own clock.
OVERRIDE_DEAD_REASONS = ("partials-off", "engine-idle", "app-idle")

# The shared tunables file: the SAME values the app reads
# (config/tp1-plan-gate.json), so the gate the watcher recommends and the
# breaker the app enforces can never drift apart. Keys are mirrored 1:1 by
# Tp1PlanGateConfig.Keys in the C# app; a missing/malformed file leaves the
# compiled defaults above standing.
PLAN_GATE_CONFIG_ENV = "TF_TP1_PLAN_GATE_CONFIG"
PLAN_GATE_CONFIG_KEYS = {
    "min_graded": "PLAN_GATE_MIN_GRADED",
    "net_r": "PLAN_GATE_NET_R",
    "up_net_r": "PLAN_GATE_UP_NET_R",
    "step_r": "PLAN_GATE_STEP_R",
    "step_pct": "PLAN_GATE_STEP_PCT",
    "min_pct": "PLAN_GATE_MIN_PCT",
    "max_pct": "PLAN_GATE_MAX_PCT",
    "score_min_graded": "PLAN_SCORE_MIN_GRADED",
    "step_feedback_min": "PLAN_STEP_FEEDBACK_MIN",
    "step_feedback_pct": "PLAN_STEP_FEEDBACK_PCT",
    "step_min": "PLAN_GATE_STEP_MIN",
    "step_max": "PLAN_GATE_STEP_MAX",
    "step_max_drift": "PLAN_STEP_MAX_DRIFT",
    "stale_days": "PLAN_REVIEW_STALE_DAYS",
}


def plan_gate_config_path() -> str:
    """The shared tunables file both the watcher and the app read: one
    source of truth for the gate/breaker constants
    (TF_TP1_PLAN_GATE_CONFIG overrides for tests). Repo-root config/,
    resolved relative to this script so the scheduled task works anywhere."""
    override = os.environ.get(PLAN_GATE_CONFIG_ENV)
    if override:
        return override
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(repo, "config", "tp1-plan-gate.json")


def load_plan_gate_config(path: str | None = None) -> dict:
    """Reads the shared tunables and returns only the recognized numeric
    keys. A missing or malformed file returns {} — the compiled defaults
    stand, so a bad edit can never break the watch."""
    target = path or plan_gate_config_path()
    try:
        with open(target, encoding="utf-8") as fh:
            raw = json.load(fh)
    except (OSError, ValueError):
        return {}
    if not isinstance(raw, dict):
        return {}
    out = {}
    for key in PLAN_GATE_CONFIG_KEYS:
        val = raw.get(key)
        if isinstance(val, (int, float)) and not isinstance(val, bool):
            out[key] = val
    return out


def apply_plan_gate_config(config: dict | None = None,
                           path: str | None = None) -> dict:
    """Override the module constants from the shared tunables file (or a
    caller-supplied config). Returns the constants it actually applied, so
    a caller can log or assert what changed. Called once at import, before
    the functions that capture these as default arguments are defined."""
    applied = {}
    cfg = load_plan_gate_config(path) if config is None else config
    for key, const in PLAN_GATE_CONFIG_KEYS.items():
        if key in cfg:
            globals()[const] = cfg[key]
            applied[const] = cfg[key]
    return applied


apply_plan_gate_config()


def data_dir() -> str:
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def journal_files() -> list[str]:
    return sorted(glob.glob(os.path.join(data_dir(), "journal", "journal_*.jsonl")))


def state_path() -> str:
    return os.path.join(data_dir(), "watcher", "tp1-first-arm-state.json")


def evidence_path() -> str:
    return os.path.join(data_dir(), "watcher", "tp1-first-arm-evidence.jsonl")


def plan_review_path() -> str:
    """The append-only plan-% review ledger: one JSON object per gate
    event (recommended / cleared / acted), never rewritten — the audit
    trail of every recommendation and what became of it
    (TF_TP1_PLAN_REVIEW_PATH overrides for tests)."""
    override = os.environ.get("TF_TP1_PLAN_REVIEW_PATH")
    if override:
        return override
    return os.path.join(data_dir(), "watcher", "tp1-plan-reviews.jsonl")


def verdicts_path() -> str:
    """The machine-readable graded-verdict ledger: one JSON object per
    banked ticket, rewritten in full each pass (TF_TP1_VERDICT_PATH
    overrides for tests)."""
    override = os.environ.get("TF_TP1_VERDICT_PATH")
    if override:
        return override
    return os.path.join(data_dir(), "watcher", "tp1-graded-verdicts.jsonl")


def override_verification_path() -> str:
    """The override verdict the dashboard reads back beside the Arm button
    (TF_TP1_OVERRIDE_CHECK_PATH overrides for tests) — one small JSON
    object beside the other watcher artefacts."""
    override = os.environ.get("TF_TP1_OVERRIDE_CHECK_PATH")
    if override:
        return override
    return os.path.join(data_dir(), "watcher",
                        "tp1-override-verification.json")


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
    settings = load_settings()
    url = settings.get("WebhookUrl") or ""
    # The armed plan-% override's verdict: computed ONCE per pass, written
    # for the dashboard to read back, then acted on by the pager below —
    # so the banner and the page are the same answer.
    override = verify_plan_pct_override(rows, settings)
    write_override_verification(override)
    if not rows:
        # An armed plan-% override with an EMPTY TP1 journal is exactly the
        # dead one this pass exists to catch — the quiet-journal return
        # must not skip it. (No override reads as a silent exit 0.)
        return 2 if page_override_verification(override, url, state) else 0

    dumped = evidence_for(rows, list(state.get("dumped", [])))
    if dumped:
        state["dumped"] = sorted(set(state.get("dumped", [])) | set(dumped))
        save_state(state)

    arms = arm_by_key(rows)
    paged = 0
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

    # Every banked rung, not just the first, lands in the log — the raw
    # EXEC digest plus the generated graded verdict (JSONL + the graded
    # table). Best-effort by construction: a failure here must never
    # break the watch — the journals keep the raw rows either way.
    verdicts: list[dict] = []
    try:
        drows = digest_rows()
        verdicts = compute_verdicts(drows)
        if verdicts:
            write_verdicts(verdicts)
            build_graded_table(digest_log_path(), verdicts)
        build_exec_digest(digest_log_path(), drows)
    except Exception:
        pass

    # Auto-close the reviews the live plan % has followed, before the gate
    # re-evaluates: a recommendation that was acted on must not read open.
    reconcile_plan_reviews(rows)

    # The plan-% recommendation gate rides the same verdicts: enough
    # settled rungs collectively trailing the giveback pages the operator
    # to reconsider the plan %. A failed POST propagates to main (exit 1)
    # so the page retries next pass, exactly like the ARM/EXEC pages.
    if page_plan_gate(verdicts, url, state):
        paged += 1

    # An open review aged past the threshold pages once, so it cannot sit
    # unactioned.
    if page_stale_reviews(read_plan_reviews(), url, state):
        paged += 1

    # The armed plan-% override must actually reach a rung: verify the
    # journal carries it, and page once when it cannot (partials off, a
    # mismatched plan %) or has not within the grace window — advice the
    # operator armed in one click must not silently do nothing.
    if page_override_verification(override, url, state):
        paged += 1

    # Score closed reviews (does acting help?) and log any resulting step
    # change, so the learning is auditable.
    try:
        score_closed_plan_reviews(verdicts)
        log_step_adaptation()
    except Exception:
        pass

    return 2 if paged else 0


def _num(value) -> float | None:
    return value if isinstance(value, (int, float)) else None


def _same_ticket(payload: dict, ticket) -> bool:
    try:
        return int(payload.get("Ticket", -1)) == int(ticket)
    except (TypeError, ValueError):
        return False


def _profit_peak(rows: list[dict], ticket, at_ts: str) -> float | None:
    """Newest FX_PROFIT PeakR for the ticket at-or-before at_ts (the
    digest's 'live peak' and the verdict's MFE)."""
    val = None
    for r in rows:
        if r["cat"] != "FX_PROFIT" or r["ts"] > at_ts:
            continue
        if not _same_ticket(r["payload"], ticket):
            continue
        v = _num(r["payload"].get("PeakR"))
        if v is not None:
            val = v
    return val


def _decisive_exit(rows: list[dict], ticket, after_ts: str) -> float | None:
    """Newest decisive FX_EXIT ProfitR for the ticket strictly after
    after_ts (override or an explicit full/partial close), else None."""
    val = None
    for r in rows:
        if r["cat"] != "FX_EXIT" or r["ts"] <= after_ts:
            continue
        if not _same_ticket(r["payload"], ticket):
            continue
        p = r["payload"]
        if p.get("Action") not in ("full", "partial") and p.get("Override") is None:
            continue
        v = _num(p.get("ProfitR"))
        if v is not None:
            val = v
    return val


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


def build_exec_digest(log_path: str, rows: list[dict] | None = None) -> int:
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
    if rows is None:
        rows = digest_rows()
    execs = [r for r in rows if "TP1-EXEC" in r["details"]]
    if not execs:
        return 2

    block = [
        "### Live TP1-EXEC digest (auto)",
        "",
        "Regenerated every pass by `scripts/watch_tp1_first_arm.py` —",
        "the raw EXEC ledger; the graded verdict (capture, vs giveback)",
        "is generated below. Banked R = plan % × rung R",
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

        peak_v = _profit_peak(rows, ticket, r["ts"])
        peak = f"{peak_v:.1f}R" if peak_v is not None else "—"
        closed_v = _decisive_exit(rows, ticket, r["ts"])
        closed = f"{closed_v:+.2f}R" if closed_v is not None else "—"

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


def compute_verdicts(rows: list[dict]) -> list[dict]:
    """The machine-readable graded verdict for every BANKED rung — one
    dict per TP1-EXEC row with Executed != False, oldest first. Refused
    rungs are not graded verdicts (they never banked); they stay visible
    in the digest block and the pager's refusal page.

    The arithmetic mirrors scripts/tp1_save_backtest.py, so the live
    verdict and the replay agree by construction:

      banked R    = plan % × rung R          (R locked at the cross)
      live peak   = newest PeakR ≤ exec
      settled R   = newest decisive FX_EXIT ProfitR > exec (None if open)
      capture     = settled R ÷ live peak    (the weekly digest's rule)
      vs giveback = plan % × (rung R − settled R) — the R the early rung
                    locked minus holding the whole position to the same
                    exit; positive = the rung beat the giveback, negative
                    = the floor/exit out-earned it.
      verdict     = pending | beat | flat | trailed (±0.005R tolerance)
    """
    arms = arm_by_key(rows)
    out = []
    for r in rows:
        if "TP1-EXEC" not in r["details"]:
            continue
        p = r["payload"]
        if p.get("Executed") is False:
            continue   # refused: never banked, so not a graded verdict
        ticket = p.get("Ticket", "?")
        arm = arm_for(r, arms)
        ap = (arm or {}).get("payload", {})
        rung_r = _num(ap.get("TargetR"))
        plan = _num(p.get("PlanPct"))
        if plan is None:
            plan = _num(ap.get("PlanPct"))
        armed_ts = (arm or r)["ts"]
        banked = (plan / 100 * rung_r
                  if plan is not None and rung_r is not None else None)
        peak = _profit_peak(rows, ticket, r["ts"])
        settled = _decisive_exit(rows, ticket, r["ts"])
        capture = (settled / peak
                   if settled is not None and peak else None)
        with_rung = (banked + (1 - plan / 100) * settled
                     if banked is not None and settled is not None else None)
        delta = (with_rung - settled
                 if with_rung is not None and settled is not None else None)
        if delta is None:
            verdict = "pending"
        elif delta > 0.005:
            verdict = "beat"
        elif delta < -0.005:
            verdict = "trailed"
        else:
            verdict = "flat"
        out.append({
            "ticket": ticket,
            "symbol": _symbol_of(r["details"]),
            "trailing_mode": _trailing_mode(rows, ticket, armed_ts),
            "armed_utc": armed_ts[:19],
            "exec_utc": r["ts"][:19],
            "rung_r": rung_r,
            "plan_pct": plan,
            "banked_r": round(banked, 4) if banked is not None else None,
            "live_peak_r": peak,
            "settled_r": settled,
            "capture_pct": round(capture * 100, 1) if capture is not None
                          else None,
            "giveback_baseline_r": settled,
            "with_rung_r": round(with_rung, 4) if with_rung is not None else None,
            "vs_giveback_r": round(delta, 4) if delta is not None else None,
            "verdict": verdict,
        })
    out.sort(key=lambda v: v["exec_utc"])
    return out


def plan_step_pct(scored: list[dict]) -> float:
    """The candidate step size, fed by the scored review history: with
    enough scored reviews, acting that usually helped widens the step and
    acting that usually did not narrows it; otherwise the base step stands.
    Clamped to [PLAN_GATE_STEP_MIN, PLAN_GATE_STEP_MAX]."""
    if len(scored) < PLAN_STEP_FEEDBACK_MIN:
        return PLAN_GATE_STEP_PCT
    helped = sum(1 for s in scored if s.get("helped"))
    share = helped / len(scored)
    if share >= 0.6:
        step = PLAN_GATE_STEP_PCT + PLAN_STEP_FEEDBACK_PCT
    elif share <= 0.4:
        step = PLAN_GATE_STEP_PCT - PLAN_STEP_FEEDBACK_PCT
    else:
        step = PLAN_GATE_STEP_PCT
    # Bounded twice: never past the absolute band, and never further than
    # PLAN_STEP_MAX_DRIFT from the base step — feedback can tune, not run.
    low = max(PLAN_GATE_STEP_MIN, PLAN_GATE_STEP_PCT - PLAN_STEP_MAX_DRIFT)
    high = min(PLAN_GATE_STEP_MAX, PLAN_GATE_STEP_PCT + PLAN_STEP_MAX_DRIFT)
    return min(high, max(low, step))


def plan_candidate(graded: list[dict], direction: str,
                   step_pct: float = PLAN_GATE_STEP_PCT) -> tuple[float, float]:
    """(baseline plan %, candidate plan %) derived from the graded rungs.
    Baseline = the mean of the rungs' current plan %s; candidate moves one
    step_pct-sized step per full PLAN_GATE_STEP_R of mean vs-giveback in the
    gate's direction, clamped to [PLAN_GATE_MIN_PCT, PLAN_GATE_MAX_PCT].
    Returns equal values when no plan % was journaled (nothing to move)."""
    plans = [v.get("plan_pct") for v in graded
             if _num(v.get("plan_pct")) is not None]
    if not plans:
        return 25.0, 25.0
    baseline = sum(plans) / len(plans)
    avg = sum(v["vs_giveback_r"] for v in graded) / len(graded)
    steps = max(1, math.ceil(abs(avg) / PLAN_GATE_STEP_R))
    delta = steps * step_pct * (1 if direction == "up" else -1)
    candidate = min(PLAN_GATE_MAX_PCT, max(PLAN_GATE_MIN_PCT, baseline + delta))
    return baseline, candidate


def _now_utc() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def plan_gate(verdicts: list[dict],
              step_pct: float = PLAN_GATE_STEP_PCT) -> dict | None:
    """The plan-% recommendation gate: a dict carrying the direction, the
    message, and the evidence (net/mean R, baseline + candidate plan %,
    graded/trailed/beaten counts, and the graded verdicts), or None below
    the evidence bar / inside the tolerance band. Both directions require
    at least PLAN_GATE_MIN_GRADED SETTLED rungs (pending rows carry no
    vs-giveback number): 'down' when the net R is at or below
    PLAN_GATE_NET_R (recommend a lower plan %), 'up' when it is at or above
    PLAN_GATE_UP_NET_R (recommend a higher plan % or wider rungs)."""
    graded = [v for v in verdicts if _num(v.get("vs_giveback_r")) is not None]
    if len(graded) < PLAN_GATE_MIN_GRADED:
        return None
    net = sum(v["vs_giveback_r"] for v in graded)
    trailed = sum(1 for v in graded if v.get("verdict") == "trailed")
    beaten = sum(1 for v in graded if v.get("verdict") == "beat")
    avg = net / len(graded)
    if net <= PLAN_GATE_NET_R:
        base, cand = plan_candidate(graded, "down", step_pct)
        message = (
            f"TP1 plan-% review: {len(graded)} settled rung(s), net "
            f"{net:+.2f}R vs giveback ({trailed} trailed) — the rungs are "
            f"collectively losing R. Candidate plan %: {base:g}% \u2192 "
            f"{cand:g}% (mean {avg:+.2f}R/rung). Consider reducing the plan % "
            f"(docs/soak/WEEK-TWO-TP1-LOG.md)."
        )
    elif net >= PLAN_GATE_UP_NET_R:
        base, cand = plan_candidate(graded, "up", step_pct)
        message = (
            f"TP1 plan-% review: {len(graded)} settled rung(s), net "
            f"{net:+.2f}R vs giveback ({beaten} beat) — the rungs are "
            f"collectively beating the giveback. Candidate plan %: "
            f"{base:g}% \u2192 {cand:g}% (mean {avg:+.2f}R/rung). Consider "
            f"raising the plan % or widening the rungs "
            f"(docs/soak/WEEK-TWO-TP1-LOG.md)."
        )
    else:
        return None
    return {
        "direction": "down" if net <= PLAN_GATE_NET_R else "up",
        "message": message,
        "net_r": net,
        "mean_vs_giveback_r": avg,
        "baseline_pct": base,
        "candidate_pct": cand,
        "graded": graded,
        "trailed": trailed,
        "beaten": beaten,
    }


def append_plan_review(record: dict) -> None:
    """Append one event to the plan-review ledger. Best-effort: a lost
    line never breaks the watch (the journals keep the raw rows)."""
    path = plan_review_path()
    try:
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record, sort_keys=True) + "\n")
    except OSError:
        pass


def mark_plan_review_acted(review_id: str, note: str = "") -> None:
    """Record that a recommended plan-% change was acted on (or explicitly
    declined) — appended, so the original recommendation stays in the
    history. The human step the runbook leaves open, made auditable."""
    append_plan_review({
        "event": "acted", "id": review_id, "ts": _now_utc(), "note": note,
    })


def read_plan_reviews(path: str | None = None) -> list[dict]:
    """Fold the append-only ledger into one dict per recommendation, in
    order, each carrying its lifecycle status: 'open' (fired, unreviewed),
    'cleared' (the condition resolved), or 'acted' (a change was recorded
    via mark_plan_review_acted)."""
    target = path or plan_review_path()
    reviews: dict[str, dict] = {}
    order: list[str] = []
    try:
        with open(target, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                rid = str(rec.get("id", ""))
                event = rec.get("event", "")
                if event == "recommended":
                    reviews[rid] = dict(rec, status="open")
                    order.append(rid)
                elif rid in reviews:
                    if event == "cleared" and reviews[rid]["status"] == "open":
                        reviews[rid]["status"] = "cleared"
                        reviews[rid]["cleared_ts"] = rec.get("ts")
                    elif event == "acted":
                        reviews[rid]["status"] = "acted"
                        reviews[rid]["acted_ts"] = rec.get("ts")
                        reviews[rid]["acted_note"] = rec.get("note", "")
                    elif event == "scored":
                        reviews[rid]["scored"] = True
                        reviews[rid]["scored_ts"] = rec.get("ts")
                        reviews[rid]["post_mean_r"] = rec.get("post_mean_r")
                        reviews[rid]["score_delta_r"] = rec.get("delta_r")
                        reviews[rid]["helped"] = rec.get("helped")
    except OSError:
        return []
    return [reviews[rid] for rid in order]


def _iso(ts) -> datetime | None:
    """A journal/ledger timestamp as an aware datetime (None when absent
    or unparseable) — every age and 'after' comparison goes through this
    rather than raw string ordering, whose fractional-second edge cases
    differ between the C# and Python serializers."""
    if not isinstance(ts, str) or not ts:
        return None
    try:
        t = datetime.fromisoformat(ts)
    except ValueError:
        return None
    return t if t.tzinfo is not None else t.replace(tzinfo=timezone.utc)


def _review_age_days(ts, now: datetime) -> float | None:
    t = _iso(ts)
    return None if t is None else (now - t).total_seconds() / 86400.0


def render_plan_reviews(reviews: list[dict], now: datetime | None = None,
                        stale_days: int = PLAN_REVIEW_STALE_DAYS) -> str:
    """Human view of the plan-review ledger: one line per recommendation
    with its lifecycle status, flagging OPEN reviews older than stale_days
    so an unactioned suggestion is impossible to miss. Empty ledger reads
    as such."""
    if not reviews:
        return "no plan-% reviews recorded"
    now = now or datetime.now(timezone.utc)
    out = [f"{'recommended (UTC)':<20} {'dir':<5} {'plan %':<12}"
           f" {'net R':>7} {'mean R':>7} {'gr':>3}  status"]
    counts = {"open": 0, "cleared": 0, "acted": 0}
    stale = 0
    for r in reviews:
        status = r.get("status", "open")
        counts[status] = counts.get(status, 0) + 1
        base, cand = r.get("baseline_pct"), r.get("candidate_pct")
        plan = (f"{base:g}%\u2192{cand:g}%"
                if isinstance(base, (int, float))
                and isinstance(cand, (int, float)) else "—")
        net, mean = r.get("net_r"), r.get("mean_vs_giveback_r")
        net_s = f"{net:+.2f}R" if isinstance(net, (int, float)) else "—"
        mean_s = f"{mean:+.2f}" if isinstance(mean, (int, float)) else "—"
        age = _review_age_days(r.get("ts"), now)
        flag = ""
        if status == "open" and age is not None and age >= stale_days:
            flag = f"  \u26a0 STALE {age:.0f}d"
            stale += 1
        scored = ""
        if r.get("scored"):
            scored = " helped" if r.get("helped") else " no-effect"
        out.append(f"{str(r.get('ts', ''))[:19]:<20} {r.get('direction', ''):<5}"
                   f" {plan:<12} {net_s:>7} {mean_s:>7}"
                   f" {str(r.get('graded', '')):>3}  {status}{scored}{flag}")
    out.append(f"\n{len(reviews)} review(s): {counts['open']} open, "
               f"{counts['cleared']} cleared, {counts['acted']} acted"
               + (f" — \u26a0 {stale} stale (open >{stale_days}d)"
                  if stale else ""))
    return "\n".join(out)


def _newest_plan_pct_after(rows: list[dict], after_ts: str) -> float | None:
    """The newest PlanPct journaled strictly after after_ts (rows are
    ts-sorted) — the live plan % a review can be judged against."""
    val = None
    for r in rows:
        if str(r.get("ts", "")) <= after_ts:
            continue
        p = r.get("payload", {}).get("PlanPct")
        if isinstance(p, (int, float)):
            val = p
    return val


def _acted_by_plan(rows: list[dict], review: dict) -> str | None:
    """An auto note when the live plan % has followed the recommendation:
    a plan-% observation after the review reaches the candidate in the
    recommended direction (down: ≤ candidate, up: ≥ candidate). None when
    there is no such observation."""
    cand = review.get("candidate_pct")
    direction = review.get("direction")
    ts = review.get("ts")
    if not isinstance(cand, (int, float)) or not ts \
            or direction not in ("down", "up"):
        return None
    seen = _newest_plan_pct_after(rows, ts)
    if seen is None:
        return None
    reached = seen <= cand if direction == "down" else seen >= cand
    if reached:
        return f"auto: live plan % {seen:g}% reached candidate {cand:g}%"
    return None


def reconcile_plan_reviews(rows: list[dict],
                           reviews: list[dict] | None = None) -> int:
    """Auto-close OPEN reviews the live plan % has followed, so acting on
    a recommendation needs no manual mark. Returns how many it closed."""
    if reviews is None:
        reviews = read_plan_reviews()
    closed = 0
    for r in reviews:
        if r.get("status") != "open":
            continue
        note = _acted_by_plan(rows, r)
        if note:
            mark_plan_review_acted(str(r.get("id", "")), note=note)
            closed += 1
    return closed


# ── the armed plan-% override: did it actually reach a rung? ────────


def _pct(value) -> float | None:
    """A plan-% candidate: a real number, never a JSON boolean."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return float(value)


def armed_plan_pct(settings: dict | None) -> float | None:
    """The plan-% override the app persisted (settings.json), or None when
    the rung is back on the brain's own allocation plan."""
    return _pct((settings or {}).get("Tp1PlanPctOverride"))


def override_mode_rows() -> list[dict]:
    """The FX_MODE audit rows recording the plan-% override being armed or
    reverted — WHEN the operator changed it and to what, which
    settings.json (a bare value with no history) cannot answer. Scanned
    separately from parse_rows(): those filter on a 'TP1-' token these
    rows do not carry, and folding them in would let an arm intent read
    as a live plan-% observation."""
    out = []
    for path in journal_files():
        try:
            with open(path, encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    if "TP1 plan-% override" not in line:
                        continue
                    row = parse_envelope(line)
                    if row is not None:
                        out.append(row)
        except OSError:
            continue
    out.sort(key=lambda r: r["ts"])
    return out


def override_armed_ts(mode_rows: list[dict], pct: float) -> str | None:
    """The timestamp THIS override value was armed at: the newest 'ARMED'
    audit row carrying pct, cancelled by any later 'REVERTED' row (or by a
    newer arm of a different value). None when it was never journaled — a
    hand-edited settings file has no arm to date."""
    armed = None
    for r in mode_rows:
        if "override ARMED" in r["details"]:
            seen = _pct(r.get("payload", {}).get("PlanPct"))
            armed = r["ts"] if seen is not None and abs(seen - pct) < 1e-6 \
                else None
        elif "override REVERTED" in r["details"]:
            armed = None
    return armed


def last_journal_activity() -> datetime | None:
    """The newest timestamp anywhere in the journals — the app's own
    liveness signal (its brain writes FX_* rows continuously while alive,
    and every measured multi-hour gap ends at a `startup` row). Read from
    the TAIL of the newest file that has rows (rows append in time order),
    so it stays cheap beside the full scans the digest already does."""
    for path in reversed(journal_files()):
        try:
            with open(path, "rb") as fh:
                fh.seek(0, os.SEEK_END)
                fh.seek(max(0, fh.tell() - 65536))
                tail = fh.read().decode("utf-8", errors="replace")
        except OSError:
            continue
        # Walk newest-first: a half-written last line simply fails to parse
        # and the previous one answers instead.
        for line in reversed(tail.splitlines()):
            row = parse_envelope(line)
            ts = _iso(row["ts"]) if row else None
            if ts is not None:
                return ts
    return None


# Sentinel: "resolve the journal's last activity yourself" — so a test can
# inject a timestamp without reading a journal.
_LOOKUP_ACTIVITY = object()


def _unmet_precondition(verdict: dict, rows: list[dict],
                        settings: dict | None, now: datetime,
                        activity=_LOOKUP_ACTIVITY) -> tuple[str | None, str | None]:
    """WHY no rung has carried the armed override — the one thing a status
    cannot say on its own ("never fired" and "cannot fire" are different
    problems with different fixes). First unmet precondition wins:

      partials-off     TP1 partial execution is off — no rung can arm
      engine-idle      the brain's loop is off (settings.FxBrainRunning)
      app-idle         the app itself has journaled nothing for hours
      hold-blocked     an overdue plan-% review is holding the rung
      gate-blocked     the trailing-mode gate said no (the floor owns it)
      no-eligible-rung nothing is unmet — the setup simply has not come

    Returns (reason, the operator's sentence for it). The sentence is built
    HERE so the CLI, the webhook page and the dashboard banner cannot
    phrase the same finding differently."""
    if not verdict.get("partials"):
        return "partials-off", "TP1 partial execution is off"
    if not (settings or {}).get("FxBrainRunning"):
        return "engine-idle", "the brain loop is off"

    if activity is _LOOKUP_ACTIVITY:
        activity = last_journal_activity()
    if activity is None:
        return "app-idle", "the app is not journaling at all"
    idle_h = (now - activity).total_seconds() / 3600.0
    if idle_h >= PLAN_APP_IDLE_HOURS:
        return "app-idle", f"the app has journaled nothing for {idle_h:g}h"

    # The rung question was REACHED and answered no — a different answer
    # from "never asked". Scoped to rows after the arm: a skip from before
    # it says nothing about this override.
    armed_at = _iso(verdict.get("armed_ts"))
    if armed_at is not None:
        after = [r for r in rows
                 if _iso(r["ts"]) is not None and _iso(r["ts"]) > armed_at]
        holds = sum(1 for r in after if "TP1-HOLD" in r["details"])
        if holds:
            return "hold-blocked", (f"an overdue plan-% review is holding "
                                    f"the rung ({holds}x)")
        skips = sum(1 for r in after if "TP1-SKIP" in r["details"])
        if skips:
            return "gate-blocked", (f"the trailing-mode gate has said no "
                                    f"({skips}x)")

    # Inside the grace window this is the EXPECTED state — the status
    # already reads 'waiting', so repeating it would be noise. Outside it,
    # "nothing was ever eligible" is the answer the operator is missing.
    return ("no-eligible-rung", None if verdict.get("status") == "waiting"
            else "nothing has been eligible to arm")


def verify_plan_pct_override(rows: list[dict], settings: dict | None,
                             now: datetime | None = None,
                             mode_rows: list[dict] | None = None) -> dict | None:
    """Did the engine actually use the armed plan-% override?

    Returns None when no override is armed, else a verdict carrying the
    armed value, when it was armed, its age, and one of:

      ok           the newest rung after the arm carries that PlanPct —
                   the override drove eligibility/sizing as promised
      mismatch     a rung armed after it journaled a DIFFERENT plan %, so
                   the engine is sizing from the allocation plan (certain)
      partials-off TP1 partial execution is off, so no rung can ever fire
                   (certain)
      never-fired  armed longer than the grace window with no rung on it
      waiting      armed recently, no rung yet — inside the grace window
      unsourced    armed in settings.json but never journaled, so there is
                   no honest "since when" to judge it against

    Sizing is the PlanPct the ARM/EXEC rows carry: the engine reads the
    override at arm time for eligibility and again at the cross for the
    lot size, so a rung that armed BEFORE the override legitimately froze
    at the old figure while its EXEC still re-reads the new one — hence
    only rows strictly after the arm are judged."""
    armed = armed_plan_pct(settings)
    if armed is None:
        return None
    now = now or datetime.now(timezone.utc)
    if mode_rows is None:
        mode_rows = override_mode_rows()
    armed_ts = override_armed_ts(mode_rows, armed)
    armed_at = _iso(armed_ts)
    age = None if armed_at is None else max(
        0.0, (now - armed_at).total_seconds() / 86400.0)
    verdict = {
        "status": "", "plan_pct": armed, "armed_ts": armed_ts,
        "age_days": age,
        "partials": bool((settings or {}).get("FxExecuteTp1Partials")),
        "seen_pct": None, "seen_ts": None,
        # Why no rung has reached it (set only while that is the story):
        # the machine-readable reason plus the one sentence to show.
        "reason": None, "reason_text": None,
    }

    if not verdict["partials"]:
        # Certain: no rung can arm at all while the prototype is off.
        verdict["status"] = "partials-off"
        verdict["reason"], verdict["reason_text"] = _unmet_precondition(
            verdict, rows, settings, now)
        return verdict
    if armed_at is None:
        verdict["status"] = "unsourced"
        return verdict

    after = [r for r in rows
             if ("TP1-ARM" in r["details"] or "TP1-EXEC" in r["details"])
             and _iso(r["ts"]) is not None and _iso(r["ts"]) > armed_at]
    seen = [(r, _pct(r.get("payload", {}).get("PlanPct"))) for r in after]
    seen = [(r, p) for r, p in seen if p is not None]
    if not seen:
        verdict["status"] = ("never-fired"
                             if age is not None
                             and age >= PLAN_OVERRIDE_GRACE_DAYS
                             else "waiting")
        verdict["reason"], verdict["reason_text"] = _unmet_precondition(
            verdict, rows, settings, now)
        return verdict

    newest, newest_pct = seen[-1]
    verdict["seen_pct"] = newest_pct
    verdict["seen_ts"] = newest["ts"]
    verdict["status"] = "ok" if abs(newest_pct - armed) <= 0.01 else "mismatch"
    return verdict


def render_override_verification(verdict: dict | None) -> str:
    """One line for the status view: what the override is, and whether the
    engine has actually used it. No override reads as such."""
    if verdict is None:
        return ("TP1 plan-% override: none armed — the rung uses the "
                "brain's own allocation plan")
    head = f"TP1 plan-% override: armed {verdict['plan_pct']:g}%"
    if verdict.get("armed_ts"):
        head += f" at {verdict['armed_ts'][:19]}"
        if verdict.get("age_days") is not None:
            head += f" ({verdict['age_days']:.1f}d ago)"
    else:
        head += " (settings.json only — never journaled)"
    seen = verdict.get("seen_pct")
    age = verdict.get("age_days")
    detail = {
        "ok": "✅ used by the next rung",
        "waiting": ("⏳ waiting for a rung (grace "
                    f"{PLAN_OVERRIDE_GRACE_DAYS:g}d)"),
        "never-fired": ("⚠ NEVER FIRED — no rung has carried it in "
                        + (f"{age:.1f}d" if age is not None else "the grace "
                           f"window")),
        "mismatch": ("🚨 IGNORED — the newest rung journaled "
                     + (f"{seen:g}%" if seen is not None else "another %")),
        "partials-off": "🚨 CANNOT FIRE — TP1 partial execution is OFF",
        "unsourced": ("ℹ no arm row to date it against — reported, "
                      "not paged"),
    }.get(verdict["status"], verdict["status"])
    reason = verdict.get("reason_text")
    return f"{head} — {detail}" + (f" ({reason})" if reason else "")


def _age_label(days) -> str:
    """An arm age in the unit that reads honestly: hours while it is still
    hours old, days once it is not."""
    if days is None:
        return "an unknown age"
    return f"{days * 24:.1f}h" if days < 1 else f"{days:.1f}d"


def override_page(v: dict) -> tuple[str, str]:
    """The page for an override that will not do what the dashboard says:
    the reason and the action, where the operator reads it."""
    pct = v["plan_pct"]
    armed = (v.get("armed_ts") or "")[:19]
    seen = v.get("seen_pct")
    seen_s = f"{seen:g}%" if seen is not None else "another plan %"
    status = v["status"]
    if status == "mismatch":
        return ("🚨 TP1 plan-% override ignored",
                f"The plan-% override is armed at {pct:g}% ({armed} UTC), but "
                f"the newest rung journaled {seen_s} at "
                f"{(v.get('seen_ts') or '')[:19]} UTC — the engine is sizing "
                f"TP1 rungs from the allocation plan, not the override. Check "
                f"that the app loaded it (Settings → Prototype) "
                f"(docs/soak/WEEK-TWO-TP1-LOG.md).")
    if status == "partials-off":
        return ("⚠️ TP1 plan-% override cannot fire",
                f"The plan-% override is armed at {pct:g}% ({armed} UTC) but "
                f"TP1 partial execution is OFF, so no rung can ever take it. "
                f"Arm TP1 partials (Settings → Prototype) or Revert the "
                f"override on the dashboard (docs/soak/WEEK-TWO-TP1-LOG.md).")
    if status == "waiting":
        # Paged WITHOUT the grace window: only a dead precondition reaches
        # this branch (OVERRIDE_DEAD_REASONS), so there is nothing to wait
        # for — say which one rather than crying "never fired" at an
        # override an hour old.
        reason = v.get("reason_text") or "a precondition is unmet"
        return ("⚠️ TP1 plan-% override will not fire",
                f"The plan-% override was armed at {pct:g}% ({armed} UTC, "
                f"{_age_label(v.get('age_days'))} ago) and cannot take "
                f"effect yet: {reason}. Fix that and the next rung carries "
                f"it (docs/soak/WEEK-TWO-TP1-LOG.md).")

    # The reason the rung never came is the actionable half of the page —
    # "engine idle" and "nothing was eligible" call for different fixes.
    reason = v.get("reason_text")
    reason_s = f" Reason: {reason}." if reason else ""
    return ("⚠️ TP1 plan-% override never fired",
            f"The plan-% override was armed at {pct:g}% ({armed} UTC, "
            f"{v['age_days']:.1f}d ago) and no TP1-ARM/TP1-EXEC row carries "
            f"it — the rung is still sized by the allocation plan.{reason_s} "
            f"Check the engine loop and TP1 partials, or Revert the override "
            f"(docs/soak/WEEK-TWO-TP1-LOG.md).")


def write_override_verification(verdict: dict | None) -> bool:
    """Persist the override verdict for the dashboard: one JSON object,
    rewritten every pass so the app reads a CURRENT answer instead of
    re-implementing the check. 'none' when no override is armed, so a
    reader gets a definite answer rather than a file that lags a Revert.
    Atomic (temp + move) — the dashboard reads it concurrently and a
    half-written file would read as no verdict. Best-effort: a lost write
    just makes the banner wait for the next pass."""
    record = dict(verdict) if verdict else {"status": "none", "plan_pct": None}
    record["computed_at"] = _now_utc()
    path = override_verification_path()
    try:
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(record, fh, sort_keys=True)
        os.replace(tmp, path)
        return True
    except OSError:
        return False


def page_override_verification(verdict: dict | None, url: str,
                               state: dict) -> bool:
    """Page the webhook ONCE when the armed plan-% override will not do
    what the dashboard says — deduped per arm and cleared once it
    verifies, so a fix followed by a regression pages again. Takes the
    verdict one_pass() already computed (and wrote for the dashboard), so
    the pager and the banner can never disagree. Returns True only when a
    page was posted this pass."""
    flagged = set(state.get("override_flagged", []))
    if verdict is None:
        if flagged:
            state["override_flagged"] = []   # reverted: nothing left to verify
            save_state(state)
        return False

    key = f"override|{verdict['plan_pct']:g}|{verdict.get('armed_ts') or ''}"
    if verdict["status"] == "ok":
        if flagged:
            flagged.discard(key)
            state["override_flagged"] = sorted(flagged)
            save_state(state)
        return False
    if (verdict["status"] not in OVERRIDE_FLAG_STATUSES
            and verdict.get("reason") not in OVERRIDE_DEAD_REASONS) \
            or key in flagged:
        return False      # waiting/unsourced: reported, never paged
    if not url:
        print("TP1 plan-% override did not take effect but no WebhookUrl — "
              "retrying next pass")
        return False

    title, text = override_page(verdict)
    color = 0x6A1B9A
    discord = {"embeds": [{"title": title, "description": text,
                           "color": color}]}
    body = discord if "discord" in url else {
        "attachments": [{"color": f"#{color:06x}", "title": title,
                         "text": text}]}
    status = post_webhook(url, body)
    print(f"alert: HTTP {status} for TP1 plan-% override "
          f"({verdict['status']})")
    flagged.add(key)
    state["override_flagged"] = sorted(flagged)
    save_state(state)
    return True


def page_stale_reviews(reviews: list[dict], url: str, state: dict,
                       now: datetime | None = None) -> bool:
    """Page ONCE per review when an OPEN recommendation passes the
    staleness threshold — an unactioned suggestion must not sit silently.
    Returns True only when a page was posted this pass."""
    now = now or datetime.now(timezone.utc)
    alerted = set(state.get("stale_alerted", []))
    todo = []
    for r in reviews:
        if r.get("status") != "open":
            continue
        age = _review_age_days(r.get("ts"), now)
        if age is not None and age >= PLAN_REVIEW_STALE_DAYS \
                and str(r.get("id")) not in alerted:
            todo.append((r, age))
    if not todo:
        return False
    if not url:
        print("TP1 plan-review stale alert due but no WebhookUrl — "
              "retrying next pass")
        return False
    bits = []
    for r, age in todo:
        base, cand = r.get("baseline_pct"), r.get("candidate_pct")
        plan = (f"{base:g}%\u2192{cand:g}%"
                if isinstance(base, (int, float))
                and isinstance(cand, (int, float)) else "—")
        bits.append(f"{r.get('direction', '?')} {plan} ({age:.0f}d old)")
    text = (f"Open plan-% review(s) past {PLAN_REVIEW_STALE_DAYS}d: "
            + "; ".join(bits)
            + ". Act on one or mark it: scripts/watch_tp1_first_arm.py "
              "--plan-reviews.")
    discord = {"embeds": [{
        "title": "\u23f0 TP1 plan-% review overdue",
        "description": text,
        "color": 0xEF6C00,
    }]}
    is_discord = "discord" in url
    body = discord if is_discord else {
        "attachments": [{
            "color": f"#{discord['embeds'][0]['color']:06x}",
            "title": discord["embeds"][0]["title"],
            "text": discord["embeds"][0]["description"],
        }]}
    status = post_webhook(url, body)
    print(f"alert: HTTP {status} for TP1 plan-review overdue ({len(todo)})")
    state["stale_alerted"] = sorted(
        alerted | {str(r.get("id")) for r, _ in todo})
    save_state(state)
    return True


def score_closed_plan_reviews(verdicts: list[dict],
                              reviews: list[dict] | None = None) -> int:
    """After a review closes, score whether acting improved the graded net:
    the mean vs-giveback of rungs settled AFTER the close minus the mean at
    review time. Appends one `scored` event per review (helped = improved),
    which plan_step_pct() then feeds back into the candidate step. A proxy,
    not a controlled counterfactual — it keeps the loop honest about being
    observational. Returns how many reviews it scored."""
    if reviews is None:
        reviews = read_plan_reviews()
    scored = 0
    for r in reviews:
        if r.get("status") not in ("acted", "cleared") or r.get("scored"):
            continue
        close_ts = r.get("acted_ts") or r.get("cleared_ts")
        if not close_ts:
            continue
        post = [v for v in verdicts
                if str(v.get("exec_utc", "")) > close_ts
                and _num(v.get("vs_giveback_r")) is not None]
        if len(post) < PLAN_SCORE_MIN_GRADED:
            continue
        post_mean = sum(v["vs_giveback_r"] for v in post) / len(post)
        pre_mean = r.get("mean_vs_giveback_r")
        delta = (post_mean - pre_mean
                 if isinstance(pre_mean, (int, float)) else None)
        append_plan_review({
            "event": "scored", "id": r.get("id"), "ts": _now_utc(),
            "post_mean_r": round(post_mean, 4),
            "post_graded": len(post),
            "pre_mean_r": pre_mean,
            "delta_r": round(delta, 4) if delta is not None else None,
            "helped": bool(delta is not None and delta > 0),
        })
        scored += 1
    return scored


def read_step_adaptations(path: str | None = None) -> list[dict]:
    """The step-size adaptation history from the ledger (append-only), in
    order — each entry records the effective step and the sample behind it."""
    target = path or plan_review_path()
    out = []
    try:
        with open(target, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if rec.get("event") == "adapted":
                    out.append(rec)
    except OSError:
        return []
    return out


def log_step_adaptation(reviews: list[dict] | None = None,
                        step: float | None = None) -> bool:
    """Append an `adapted` audit event whenever the effective step differs
    from the last logged one (the base step counts as the initial value, so
    a run that never adapts logs nothing). Returns True when it logged."""
    if reviews is None:
        reviews = read_plan_reviews()
    scored = [r for r in reviews if r.get("scored")]
    if step is None:
        step = plan_step_pct(scored)
    history = read_step_adaptations()
    last = history[-1].get("step_pct") if history else PLAN_GATE_STEP_PCT
    if isinstance(last, (int, float)) and abs(last - step) < 1e-9:
        return False
    append_plan_review({
        "event": "adapted", "ts": _now_utc(),
        "step_pct": step, "base_pct": PLAN_GATE_STEP_PCT,
        "scored": len(scored),
        "helped": sum(1 for s in scored if s.get("helped")),
    })
    return True


def page_plan_gate(verdicts: list[dict], url: str, state: dict) -> bool:
    """Page ONCE per direction: when the gate first trips, and again when
    it later trips the OTHER way (or after a recovery re-arms it). Every
    recommendation and every clearance lands in the audit ledger. The
    candidate step is fed by the scored review history. Returns True only
    when a page was posted this pass."""
    reviews = read_plan_reviews()
    step = plan_step_pct([r for r in reviews if r.get("scored")])
    gate = plan_gate(verdicts, step_pct=step)
    if gate is None:
        if state.get("plan_gate") is not None:
            append_plan_review({
                "event": "cleared", "id": state.get("plan_gate_id"),
                "ts": _now_utc(), "direction": state.get("plan_gate"),
            })
            state.pop("plan_gate", None)
            state.pop("plan_gate_id", None)
            save_state(state)
        return False
    direction = gate["direction"]
    if state.get("plan_gate") == direction:
        return False
    if not url:
        print("TP1 plan-% gate tripped but no WebhookUrl — retrying next pass")
        return False
    down = direction == "down"
    discord = {"embeds": [{
        "title": "\u26a0\ufe0f TP1 plan-% review" if down
                 else "\u2b06\ufe0f TP1 plan-% review",
        "description": gate["message"],
        "color": 0xC62828 if down else 0x2E7D32,
    }]}
    is_discord = "discord" in url
    body = discord if is_discord else {
        "attachments": [{
            "color": f"#{discord['embeds'][0]['color']:06x}",
            "title": discord["embeds"][0]["title"],
            "text": discord["embeds"][0]["description"],
        }]}
    status = post_webhook(url, body)
    print(f"alert: HTTP {status} for TP1 plan-% gate ({direction})")
    ts = _now_utc()
    review_id = f"{ts}|{direction}"
    state["plan_gate"] = direction
    state["plan_gate_id"] = review_id
    save_state(state)
    append_plan_review({
        "event": "recommended", "id": review_id, "ts": ts,
        "direction": direction,
        "baseline_pct": round(gate["baseline_pct"], 2),
        "candidate_pct": round(gate["candidate_pct"], 2),
        "net_r": round(gate["net_r"], 4),
        "mean_vs_giveback_r": round(gate["mean_vs_giveback_r"], 4),
        "graded": len(gate["graded"]),
        "trailed": gate["trailed"],
        "beaten": gate["beaten"],
        "tickets": [
            {"ticket": v.get("ticket"), "verdict": v.get("verdict"),
             "vs_giveback_r": v.get("vs_giveback_r")}
            for v in gate["graded"]
        ],
    })
    return True


def write_verdicts(verdicts: list[dict]) -> int:
    """Rewrite the machine-readable ledger: exactly one JSON object per
    banked ticket, ts-ordered, so consumers get a current snapshot with
    no duplicate history to reconcile. Returns the line count; a no-op
    (0) on an empty list — the file exists only once a rung banks."""
    if not verdicts:
        return 0
    path = verdicts_path()
    directory = os.path.dirname(path)
    if directory:
        os.makedirs(directory, exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        for v in verdicts:
            fh.write(json.dumps(v, sort_keys=True) + "\n")
    return len(verdicts)


def _graded_body_after_table(rest: str) -> str:
    """The log text following the '## Graded tickets' heading with any
    hand-filled markdown table (and the blank lines around it) removed —
    so the first generated block replaces it outright."""
    lines = rest.split("\n")
    i = 0
    while i < len(lines) and lines[i].strip() == "":
        i += 1
    if i < len(lines) and lines[i].lstrip().startswith("|"):
        while i < len(lines) and lines[i].lstrip().startswith("|"):
            i += 1
        while i < len(lines) and lines[i].strip() == "":
            i += 1
    return "\n".join(lines[i:])


def _r_cell(value: float | None) -> str:
    return f"{value:+.2f}R" if value is not None else "—"


def build_graded_table(log_path: str, verdicts: list[dict]) -> int:
    """Regenerate the '## Graded tickets' table from the verdicts: the
    hand-filled table is replaced by a marker-delimited block, so the
    verdict — Capture and vs giveback included — is generated, not
    typed. The mechanical columns come from compute_verdicts(); the
    human reading (is this rung worth keeping?) stays a judgment call,
    but the numbers no longer are.

    Returns 0 written · 2 nothing to do (no verdicts, unreadable log, or
    no heading). Idempotent: the markers make the block replace-in-place.
    """
    if not verdicts:
        return 2

    lines = [
        "Generated every pass by `scripts/watch_tp1_first_arm.py` from the",
        "journals — one row per banked rung; the numbers are mechanical.",
        "Rung banked = plan % × rung R. Capture = settled R ÷ live peak",
        "(the weekly digest's rule). vs giveback baseline = plan % ×",
        "(rung R − settled R): the R the early rung locked versus holding",
        "the whole position to the same exit — positive means the rung",
        "beat the giveback the ticket would otherwise have taken, negative",
        "means the floor/exit out-earned it. Verdict is 'pending' until a",
        "decisive FX_EXIT settles the ticket. Machine-readable twin:",
        "`data/watcher/tp1-graded-verdicts.jsonl`.",
        "",
        "| Ticket | Symbol | TrailingMode | Armed (UTC) | Exec (UTC) "
        "| Rung banked | Live peak | Settled | Capture | vs giveback baseline |",
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for v in verdicts:
        peak_v = v.get("live_peak_r")
        capture_v = v.get("capture_pct")
        peak_s = f"{peak_v:.1f}R" if peak_v is not None else "—"
        capture_s = f"{capture_v:.0f}%" if capture_v is not None else "—"
        plan = v.get("plan_pct")
        plan_s = f"{plan:g}%" if plan is not None else "—"
        delta = v.get("vs_giveback_r")
        vs_s = (v["verdict"] if delta is None
                else f"{delta:+.2f}R ({v['verdict']})")
        lines.append(
            f"| #{v['ticket']} | {v['symbol']} | {v['trailing_mode']} "
            f"| {v['armed_utc']} | {v['exec_utc']} "
            f"| {_r_cell(v.get('banked_r'))} ({plan_s} plan) "
            f"| {peak_s} | {_r_cell(v.get('settled_r'))} "
            f"| {capture_s} | {vs_s} |"
        )
    lines.append("")
    new_block = f"{GRADED_START}\n" + "\n".join(lines) + f"\n{GRADED_END}"

    try:
        with open(log_path, encoding="utf-8") as fh:
            log = fh.read()
    except OSError:
        return 2

    if GRADED_START in log and GRADED_END in log:
        pre, rest = log.split(GRADED_START, 1)
        _, post = rest.split(GRADED_END, 1)
        new_log = pre + new_block + post
    else:
        idx = log.find(GRADED_ANCHOR)
        if idx < 0:
            return 2
        heading_end = log.find("\n", idx)
        if heading_end < 0:
            return 2
        heading_end += 1
        rest = _graded_body_after_table(log[heading_end:])
        tail = ("\n\n" + rest) if rest else "\n"
        new_log = log[:heading_end] + "\n" + new_block + tail

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

    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--plan-reviews", action="store_true",
                    help="list the plan-%% review ledger with statuses")
    args = ap.parse_args()
    if args.plan_reviews:
        print(render_plan_reviews(read_plan_reviews()))
        print()
        # The other half of the plan-% story: whether the override the
        # dashboard armed has actually reached a rung yet.
        print(render_override_verification(verify_plan_pct_override(
            parse_rows(), load_settings(), mode_rows=override_mode_rows())))
        sys.exit(0)

    try:
        sys.exit(one_pass())
    except RuntimeError as exc:
        print(f"pager error (will retry next pass): {exc}")
        sys.exit(1)


if __name__ == "__main__":
    main()
