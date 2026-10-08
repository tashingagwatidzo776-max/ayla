#!/usr/bin/env python3
"""Tests for scripts/watch_tp1_first_arm.py.

Real subprocess runs against temp TF_DATA_DIRs and local fake webhooks —
no real journal, no real webhook, no trading. Run:
python scripts/test_watch_tp1_first_arm.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "watch_tp1_first_arm.py"


class TmpData:
    def __init__(self, webhook_url=""):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        (self.root / "journal").mkdir()
        (self.root / "settings.json").write_text(json.dumps({
            "WebhookUrl": webhook_url, "IsDiscordWebhook": True,
        }), encoding="utf-8")

    def journal(self, rows):
        path = self.root / "journal" / "journal_20261001.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            for r in rows:
                fh.write(json.dumps({
                    "Timestamp": r["ts"], "Category": r["cat"],
                    "Details": r["details"],
                }) + "\n")

    def state(self) -> dict:
        try:
            with open(self.root / "watcher" / "tp1-first-arm-state.json",
                      encoding="utf-8") as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return {}

    def evidence_lines(self) -> list[str]:
        p = self.root / "watcher" / "tp1-first-arm-evidence.jsonl"
        if not p.exists():
            return []
        return p.read_text(encoding="utf-8").splitlines()

    def run(self, *args, extra_env=None):
        # Always pin the digest log to the temp root: otherwise the
        # default path is the repo's docs/soak/WEEK-TWO-TP1-LOG.md and a
        # banked-EXEC test rewrites the real doc with fake journal data.
        env = dict(os.environ, TF_DATA_DIR=str(self.root),
                   PYTHONIOENCODING="utf-8",
                   TF_TP1_DIGEST_LOG=str(self.root / "digest-log.md"))
        if extra_env:
            env.update(extra_env)
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args], capture_output=True,
            text=True, encoding="utf-8", env=env, timeout=60)

    def digest_log(self, body: str = "## Graded tickets\n\n| — |\n") -> Path:
        """Write (or reset) the digest target log; returns its path."""
        p = self.root / "digest-log.md"
        p.write_text("# Week two — TP1 partials live\n\n" + body,
                     encoding="utf-8")
        return p

    def digest_log_text(self) -> str:
        return (self.root / "digest-log.md").read_text(encoding="utf-8")

    def verdicts(self, path=None) -> list[dict]:
        p = path or (self.root / "watcher" / "tp1-graded-verdicts.jsonl")
        if not p.exists():
            return []
        return [json.loads(ln)
                for ln in p.read_text(encoding="utf-8").splitlines() if ln]

    def plan_reviews(self, path=None) -> list[dict]:
        p = path or (self.root / "watcher" / "tp1-plan-reviews.jsonl")
        if not p.exists():
            return []
        return [json.loads(ln)
                for ln in p.read_text(encoding="utf-8").splitlines() if ln]

    def override_check(self, path=None) -> dict:
        """The override verdict the dashboard reads back."""
        p = path or (self.root / "watcher"
                     / "tp1-override-verification.json")
        if not p.exists():
            return {}
        return json.loads(p.read_text(encoding="utf-8"))


def profit_row(ts, ticket, peak, cur, gb, mode="HYBRID_STRUCTURE_ATR"):
    payload = {"Ticket": ticket, "PeakR": peak, "CurrentR": cur,
               "GivebackPct": gb, "TrailingMode": mode}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: report — " + json.dumps(payload),
            "ts": ts}


def arm_row(ts, ticket, target="prev-day-high", price=1.1025, r=2.1,
            plan=25):
    payload = {"Ticket": ticket, "Target": target, "TargetPrice": price,
               "TargetR": r, "PlanPct": plan}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: TP1-ARM: rung {target} {price} "
                       f"(+{r}R) armed for #{ticket} ({plan}% plan) — "
                       f"executes when price crosses: " + json.dumps(payload),
            "ts": ts}


def arm_mode_row(ts, pct, review="2026-10-01T10:00:00+00:00|down"):
    """The FX_MODE audit row App.xaml.cs journals when the dashboard's
    one-click Arm persists the plan-% override (settings.json carries the
    value, this carries the WHEN)."""
    payload = {"PlanPct": pct, "ReviewId": review}
    return {"cat": "FX_MODE",
            "details": f"TP1 plan-% override ARMED at {pct:g}% by operator "
                       f"(dashboard, review {review}): " + json.dumps(payload),
            "ts": ts}


def revert_mode_row(ts):
    """The matching audit row for the dashboard's Revert button."""
    return {"cat": "FX_MODE",
            "details": "TP1 plan-% override REVERTED by operator "
                       "(dashboard) — back to the allocation plan: {}",
            "ts": ts}


def set_settings(d, **values):
    """Patch the temp settings.json the way SettingsService.Save writes it
    (the watcher reads Tp1PlanPctOverride / FxExecuteTp1Partials from it)."""
    path = d.root / "settings.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data.update(values)
    path.write_text(json.dumps(data), encoding="utf-8")
    return data


def skip_row(ts, ticket):
    payload = {"Ticket": ticket, "TrailingMode": "STRUCTURE_TRAIL",
               "PlanPct": 25}
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: TP1-SKIP: STRUCTURE_TRAIL floor "
                       f"owns the partial on #{ticket} — rung not armed: "
                       + json.dumps(payload), "ts": ts}


def close_row(ts, ticket):
    payload = {"Ticket": ticket}
    return {"cat": "FX_EXIT",
            "details": f"EURUSD: closed #{ticket} — deal 901: " + json.dumps(payload),
            "ts": ts}


class FakeWebhook(BaseHTTPRequestHandler):
    status = 204
    hits = []

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        FakeWebhook.hits.append(json.loads(self.rfile.read(length) or b"{}"))
        self.send_response(FakeWebhook.status)
        self.end_headers()

    def log_message(self, *args):
        pass


def serve_once(path="/discord/hook"):
    FakeWebhook.hits = []
    server = HTTPServer(("127.0.0.1", 0), FakeWebhook)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_address[1]}{path}"


def test_quiet_journal_exits_zero():
    d = TmpData()
    d.journal([profit_row("2026-10-01T10:00:00.0+00:00", 42, 4.0, 3.0, 20)])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert r.stdout.strip() == "", r.stdout


def test_skip_rows_dump_but_never_page():
    """A TP1-SKIP is the gate answering — evidence-dumped (the finding is
    real) but NOT the first-sighting event: no page, exit 0."""
    d = TmpData()
    d.journal([skip_row("2026-10-01T10:00:00.0+00:00", 777)])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    lines = d.evidence_lines()
    assert len(lines) == 1 and "TP1-SKIP" in lines[0], lines
    assert not d.state().get("paged")


def test_arm_pages_once_dumps_evidence_dedups():
    """The full contract: first pass pages (exit 2) and dumps every FX_*
    row for the ticket; second pass is silent (exit 0) and appends
    nothing."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        profit_row("2026-10-01T10:06:00.0+00:00", 555, 4.0, 3.0, 20),
    ])
    try:
        r1 = d.run()
        assert r1.returncode == 2, r1.stdout + r1.stderr
        assert len(FakeWebhook.hits) == 1, r1.stdout
        embed = FakeWebhook.hits[0]["embeds"][0]
        assert embed["title"].startswith("\U0001f3af")
        assert "555" in embed["title"]
        assert "prev-day-high" in embed["description"]
        assert "WEEK-TWO-TP1-LOG" in embed["description"]
        lines = d.evidence_lines()
        assert lines, "evidence file must exist"
        assert sum(1 for ln in lines if "TP1-ARM" in ln) == 1
        assert len(lines) == 3, lines   # report + ARM + report

        r2 = d.run()
        assert r2.returncode == 0, r2.stdout + r2.stderr
        assert len(FakeWebhook.hits) == 1
        assert d.evidence_lines() == lines
    finally:
        server.shutdown()


def test_no_webhook_configured_retries_without_state():
    """No WebhookUrl: the page is NOT remembered (retries next pass) and
    exit 0 keeps the scheduler calm — but evidence is still dumped."""
    d = TmpData()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert "no WebhookUrl" in r.stdout
    assert d.evidence_lines(), "evidence is dumped regardless"
    assert not d.state().get("paged")


def test_failed_post_leaves_state_untouched():
    """HTTP 500 -> 'pager error', exit 1, no paged state, so the next
    pass retries the page; the evidence dump stands either way."""
    server, url = serve_once()
    FakeWebhook.status = 500
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        close_row("2026-10-01T10:06:00.0+00:00", 555),
    ])
    try:
        r = d.run()
        assert r.returncode == 1, r.stdout + r.stderr
        assert "pager error" in r.stdout
        assert not d.state().get("paged")
        assert d.evidence_lines()
    finally:
        FakeWebhook.status = 204
        server.shutdown()


def test_webhook_recovers_on_second_pass():
    """500 then 204: the first pass fails loudly (exit 1, state clean),
    the second delivers the page (exit 2) — a page is not 'sent' until
    the endpoint accepts it."""
    server, url = serve_once()
    FakeWebhook.status = 500
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
    ])
    try:
        r1 = d.run()
        assert r1.returncode == 1, r1.stdout + r1.stderr
        FakeWebhook.status = 204
        r2 = d.run()
        assert r2.returncode == 2, r2.stdout + r2.stderr
        # Two POSTs total: the failed attempt, then the retried success.
        assert len(FakeWebhook.hits) == 2
        assert d.state().get("paged")
    finally:
        FakeWebhook.status = 204
        server.shutdown()


def test_two_arms_on_different_tickets_page_twice():
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        profit_row("2026-10-01T11:00:00.0+00:00", 556, 4.0, 3.0, 20),
        arm_row("2026-10-01T11:05:00.0+00:00", 556),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 2
        assert len(d.state().get("paged", [])) == 2
    finally:
        server.shutdown()


def exec_row(ts, ticket, lots=0.25, executed=True, retcode=None, plan=25):
    """The exact shape FxEngineHost journals on the TP1 close attempt:
    lots and Executed, but NO rung R — that lives only in the ARM row."""
    payload = {"Ticket": ticket, "Executed": executed, "Lots": lots,
               "PlanPct": plan, "ArmedPrice": 1.1025}
    if retcode is not None:
        payload["Retcode"] = retcode
    action = "TP1-EXEC: banked" if executed else "TP1-EXEC refused for"
    return {"cat": "FX_PROFIT",
            "details": f"EURUSD #{ticket}: {action} "
                       f"{lots} lots ({plan}% plan) of #{ticket}: "
                       + json.dumps(payload), "ts": ts}


def exit_full_row(ts, ticket, profit_r=2.75):
    """A decisive full exit carrying the closed R (the digest's
    'Closed R' column)."""
    payload = {"Ticket": ticket, "Action": "full", "ProfitR": profit_r,
               "Override": None}
    return {"cat": "FX_EXIT",
            "details": f"EURUSD #{ticket}: full — " + json.dumps(payload),
            "ts": ts}


DIGEST_ENV = lambda root: {"TF_TP1_DIGEST_LOG": str(root / "digest-log.md")}


def test_digest_writes_banked_row_from_journal():
    """One pass with an ARM + banked EXEC lands a digest row in the log:
    rung/plan from the payloads, Banked R = plan x rung R (+1.00R for a
    40% plan on a 2.5R rung), live peak from the newest FX_PROFIT report
    at-or-before the exec, TrailingMode reconstructed from telemetry."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555, r=2.5),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, lots=0.4, plan=40),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr   # no webhook: quiet pass
    text = d.digest_log_text()
    assert "### Live TP1-EXEC digest (auto)" in text, text
    assert "#555" in text and "EURUSD" in text
    assert "HYBRID_STRUCTURE_ATR" in text          # mode reconstructed
    assert "+1.00R" in text                        # 40% of a 2.5R rung
    assert "2.5R" in text                          # the rung column
    assert "4.0R" in text                          # live peak column
    assert "| yes |" in text
    # The block sits ABOVE the manual grading table it feeds.
    assert text.index("Live TP1-EXEC digest") < text.index("## Graded tickets")


def test_digest_block_is_replaced_never_appended():
    """Re-runs rewrite the block in place — exactly one table, even after
    a second pass and a second banked rung on another ticket."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
    ])
    r1 = d.run(extra_env=DIGEST_ENV(d.root))
    assert r1.returncode == 0, r1.stdout + r1.stderr

    d.journal([
        profit_row("2026-10-01T11:00:00.0+00:00", 556, 3.0, 2.5, 15),
        arm_row("2026-10-01T11:05:00.0+00:00", 556),
        exec_row("2026-10-01T11:20:00.0+00:00", 556, lots=0.3),
    ])
    r2 = d.run(extra_env=DIGEST_ENV(d.root))
    assert r2.returncode == 0, r2.stdout + r2.stderr

    text = d.digest_log_text()
    assert text.count("<!-- TP1-EXEC-DIGEST:START -->") == 1, text
    assert text.count("<!-- TP1-EXEC-DIGEST:END -->") == 1, text
    assert "#555" in text and "#556" in text   # every rung, not just the first


def test_digest_refused_row_shows_retcode():
    """Executed=false keeps the refusal visible in the digest (a refused
    rung is grading evidence too)."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, executed=False,
                 retcode=10018),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    text = d.digest_log_text()
    assert "no (rc 10018)" in text, text


def test_digest_updates_closed_r_when_exit_lands():
    """A rung banked while the position is still open shows Closed R = em-dash;
    once the decisive FX_EXIT lands, the next pass fills it in."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
    ])
    d.run(extra_env=DIGEST_ENV(d.root))
    assert "| — |" in d.digest_log_text()   # open position, nothing closed

    d.journal([exit_full_row("2026-10-01T12:00:00.0+00:00", 555,
                             profit_r=2.75)])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    text = d.digest_log_text()
    assert "+2.75R" in text, text


def test_digest_silent_without_exec_rows():
    """ARM-only traffic (or an empty journal) never touches the log — the
    digest is an EXEC digest, the ARM leg has its own pager page."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    assert "Live TP1-EXEC digest" not in d.digest_log_text()


def test_digest_survives_log_without_anchor():
    """No '## Graded tickets' anchor (a renamed log, say): the pass stays
    green and leaves the log untouched — never corrupt a hand-edited doc."""
    d = TmpData()
    (d.root / "digest-log.md").write_text(
        "# Week two — no anchor here", encoding="utf-8")
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    assert "Live TP1-EXEC digest" not in d.digest_log_text()


def test_digest_mode_unknown_before_arm_shows_dash():
    """No TrailingMode telemetry at-or-before the arm → the mode column
    stays em-dash (the gate drill's conservative rule, not a blank)."""
    d = TmpData()
    d.digest_log()
    d.journal([
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
        # Mode only ever stamped AFTER the arm — cannot judge it.
        profit_row("2026-10-01T10:30:00.0+00:00", 555, 4.0, 3.0, 20,
                   mode="STRUCTURE_TRAIL"),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    text = d.digest_log_text()
    row = next(ln for ln in text.splitlines() if ln.startswith("| #555"))
    assert "| — |" in row, row


def test_exec_pages_with_banked_r_from_arm():
    """The EXEC page carries the graded number: banked lots and the rung
    R resolved from the ARM row (the EXEC payload has no R)."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, lots=0.25),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 2   # ARM page + EXEC page
        titles = [h["embeds"][0]["title"] for h in FakeWebhook.hits]
        assert any("banked" in t for t in titles)
        desc = next(h["embeds"][0]["description"] for h in FakeWebhook.hits
                    if "banked" in h["embeds"][0]["title"])
        # TargetR round-trips through JSON as a float: the C# "+2.1"
        # custom format arrives as 2.1.
        assert "0.25" in desc and "2.1R" in desc and "25% plan" in desc, desc
    finally:
        server.shutdown()


def test_exec_refusal_pages_with_retcode():
    """Executed=false pages too — a refused rung is exactly what grading
    needs to see — with the retcode in the description."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, executed=False,
                 retcode=10018),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 2
        titles = [h["embeds"][0]["title"] for h in FakeWebhook.hits]
        assert any("refused" in t for t in titles)
        desc = next(h["embeds"][0]["description"] for h in FakeWebhook.hits
                    if "refused" in h["embeds"][0]["title"])
        assert "10018" in desc
    finally:
        server.shutdown()


def test_exec_without_matching_arm_still_pages():
    """Defensive: an EXEC with no ARM row in the journal (hand-trimmed
    or rotated away) pages with '?' placeholders instead of crashing."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, lots=0.5),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        assert len(FakeWebhook.hits) == 1
        desc = FakeWebhook.hits[0]["embeds"][0]["description"]
        assert "?" in desc and "0.5" in desc
    finally:
        server.shutdown()


# --- graded verdicts (machine-readable JSONL + generated table) --------

def test_graded_verdict_jsonl_and_table():
    """A banked rung emits one machine-readable verdict with the full
    mechanical arithmetic (banked/live peak/settled/capture/vs-giveback)
    AND the hand-filled graded table is generated from it, bead-locked to
    the backtest formula: vs giveback = plan% x (rung R - settled R)."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555, r=2.5),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, lots=0.4, plan=40),
        exit_full_row("2026-10-01T12:00:00.0+00:00", 555, profit_r=2.0),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr

    verdicts = d.verdicts()
    assert len(verdicts) == 1, verdicts
    v = verdicts[0]
    assert v["ticket"] == 555 and v["symbol"] == "EURUSD"
    assert v["trailing_mode"] == "HYBRID_STRUCTURE_ATR"
    assert v["rung_r"] == 2.5 and v["plan_pct"] == 40
    assert v["banked_r"] == 1.0            # 40% of a 2.5R rung
    assert v["live_peak_r"] == 4.0
    assert v["settled_r"] == 2.0
    assert v["capture_pct"] == 50.0        # settled / peak
    assert v["vs_giveback_r"] == 0.2       # 40% x (2.5 - 2.0)
    assert v["verdict"] == "beat"

    text = d.digest_log_text()
    assert "Generated every pass by" in text, text
    assert text.count("<!-- TP1-GRADED:START -->") == 1
    assert text.count("<!-- TP1-GRADED:END -->") == 1
    assert "+1.00R (40% plan)" in text, text
    assert "| 4.0R | +2.00R | 50% | +0.20R (beat) |" in text, text
    # The hand-filled placeholder table is replaced, not left behind.
    assert "| — |" not in text, text
    # The generated table sits UNDER the heading it replaces.
    assert text.index("## Graded tickets") < text.index("Generated every pass")


def test_graded_verdict_refused_ticket_is_not_graded():
    """A refused EXEC never banked, so it is not a graded verdict — no
    JSONL row and no graded table (the digest still shows the refusal)."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555, executed=False,
                 retcode=10018),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    assert d.verdicts() == []
    assert "<!-- TP1-GRADED:START -->" not in d.digest_log_text()
    assert "Live TP1-EXEC digest" in d.digest_log_text()


def test_graded_verdict_pending_until_exit():
    """A rung banked while the position is open yields a verdict object
    with null settlement fields and verdict 'pending' — no invented
    numbers, and the table reads 'pending' until the exit lands."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    v = d.verdicts()[0]
    assert v["verdict"] == "pending"
    assert v["settled_r"] is None and v["capture_pct"] is None
    assert v["vs_giveback_r"] is None
    # The graded row (not the digest row) ends in the pending verdict.
    row = next(ln for ln in d.digest_log_text().splitlines()
               if ln.startswith("| #555") and "plan)" in ln)
    assert row.endswith("| pending |"), row


def test_graded_verdict_settles_and_rewrites_in_place():
    """Second pass after the exit lands updates the same verdict line and
    the same table row — one JSONL line per ticket, one marker block, no
    append-only drift."""
    d = TmpData()
    d.digest_log()
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
    ])
    d.run(extra_env=DIGEST_ENV(d.root))
    assert d.verdicts()[0]["verdict"] == "pending"

    d.journal([exit_full_row("2026-10-01T12:00:00.0+00:00", 555,
                             profit_r=2.75)])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    verdicts = d.verdicts()
    assert len(verdicts) == 1, verdicts
    assert verdicts[0]["verdict"] == "trailed"   # 25% x (2.1 - 2.75) < 0
    text = d.digest_log_text()
    assert text.count("<!-- TP1-GRADED:START -->") == 1
    assert "pending" not in next(ln for ln in text.splitlines()
                                 if ln.startswith("| #555") and "plan)" in ln)


def test_graded_verdict_written_even_without_log_anchor():
    """The machine-readable ledger is the durable artifact: a renamed log
    with no '## Graded tickets' anchor leaves the doc untouched but the
    JSONL is still written, so consumers never depend on the markdown."""
    d = TmpData()
    (d.root / "digest-log.md").write_text(
        "# Week two — no anchor here", encoding="utf-8")
    d.journal([
        profit_row("2026-10-01T10:00:00.0+00:00", 555, 4.0, 3.0, 20),
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
        exit_full_row("2026-10-01T12:00:00.0+00:00", 555, profit_r=3.0),
    ])
    r = d.run(extra_env=DIGEST_ENV(d.root))
    assert r.returncode == 0, r.stdout + r.stderr
    assert len(d.verdicts()) == 1
    assert d.digest_log_text() == "# Week two — no anchor here"


def test_graded_verdict_path_override():
    """TF_TP1_VERDICT_PATH redirects the ledger (tests and non-default
    deployments); the default stays data/watcher/."""
    d = TmpData()
    d.digest_log()
    target = d.root / "custom" / "verdicts.jsonl"
    d.journal([
        arm_row("2026-10-01T10:05:00.0+00:00", 555),
        exec_row("2026-10-01T10:20:00.0+00:00", 555),
    ])
    env = dict(DIGEST_ENV(d.root), TF_TP1_VERDICT_PATH=str(target))
    r = d.run(extra_env=env)
    assert r.returncode == 0, r.stdout + r.stderr
    assert target.exists(), "verdict override path not honoured"
    assert not (d.root / "watcher" / "tp1-graded-verdicts.jsonl").exists()
    assert d.verdicts(target)[0]["ticket"] == 555


def _graded_series(ticket, hour, profit_r=10.0):
    """One ticket that arms, banks the rung, then settles decisively —
    with default numbers a heavily TRAILING verdict (banked 0.525R versus
    0.25 × settled)."""
    return [
        profit_row(f"2026-10-01T{hour}:00:00.0+00:00", ticket, 12.0, 11.0, 10),
        arm_row(f"2026-10-01T{hour}:05:00.0+00:00", ticket),
        exec_row(f"2026-10-01T{hour}:20:00.0+00:00", ticket),
        exit_full_row(f"2026-10-01T{hour}:59:00.0+00:00", ticket, profit_r=profit_r),
    ]


def test_plan_gate_pages_once_when_rungs_collectively_trail():
    """Three settled rungs with a net R well below the giveback baseline
    trip the plan-% gate: one page, the flag remembered, no re-page while
    the breach holds."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += _graded_series(ticket, f"1{i}")
    d.journal(rows)
    try:
        r1 = d.run(extra_env=DIGEST_ENV(d.root))
        assert r1.returncode == 2, r1.stdout + r1.stderr
        assert d.state().get("plan_gate") == "down"
        gate_hits = [h for h in FakeWebhook.hits
                     if "plan-% review" in json.dumps(h)]
        assert gate_hits, [json.dumps(h)[:120] for h in FakeWebhook.hits]
        net = sum(v["vs_giveback_r"] for v in d.verdicts()
                  if v["vs_giveback_r"] is not None)
        text = json.dumps(gate_hits[0], ensure_ascii=False)
        assert f"3 settled rung(s), net {net:+.2f}R vs giveback" in text
        # The page carries a concrete candidate derived from the rungs.
        assert "Candidate plan %: 25% \u2192 15%" in text
        assert f"mean {net / 3:+.2f}R/rung" in text

        n = len(FakeWebhook.hits)
        r2 = d.run(extra_env=DIGEST_ENV(d.root))
        assert r2.returncode == 0, r2.stdout + r2.stderr
        assert len(FakeWebhook.hits) == n, "gate must not re-page while tripped"
    finally:
        server.shutdown()


def test_plan_gate_silent_below_the_evidence_bar():
    """Two graded trailers is below PLAN_GATE_MIN_GRADED: the ARM/EXEC
    pages fire but the plan-% gate stays silent."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556)):
        rows += _graded_series(ticket, f"1{i}")
    d.journal(rows)
    try:
        r = d.run(extra_env=DIGEST_ENV(d.root))
        assert r.returncode == 2, r.stdout + r.stderr
        assert not d.state().get("plan_gate")
        assert not any("plan-% review" in json.dumps(h)
                       for h in FakeWebhook.hits)
    finally:
        server.shutdown()


def test_plan_gate_rearms_so_a_later_breach_pages_again():
    """When a later winner lifts the net above the threshold the flag
    clears, so a subsequent breach is free to page again."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += _graded_series(ticket, f"1{i}")
    d.journal(rows)
    try:
        d.run(extra_env=DIGEST_ENV(d.root))
        assert d.state().get("plan_gate") == "down"
        # A winner big enough to pull the net back above the threshold.
        d.journal(_graded_series(558, "14", profit_r=-18.0))
        d.run(extra_env=DIGEST_ENV(d.root))
        assert "plan_gate" not in d.state(), d.state()
    finally:
        server.shutdown()


def test_plan_gate_pages_on_the_upside_when_rungs_collectively_beat():
    """The mirror direction: enough settled rungs beating the giveback by a
    healthy margin pages with the raise/widen recommendation."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += _graded_series(ticket, f"1{i}", profit_r=-1.0)
    d.journal(rows)
    try:
        r = d.run(extra_env=DIGEST_ENV(d.root))
        assert r.returncode == 2, r.stdout + r.stderr
        assert d.state().get("plan_gate") == "up"
        gate_hits = [h for h in FakeWebhook.hits
                     if "plan-% review" in json.dumps(h)]
        assert gate_hits
        text = json.dumps(gate_hits[0], ensure_ascii=False)
        net = sum(v["vs_giveback_r"] for v in d.verdicts()
                  if v["vs_giveback_r"] is not None)
        assert f"3 settled rung(s), net {net:+.2f}R vs giveback" in text
        assert "Candidate plan %: 25% \u2192 30%" in text
        assert "raising the plan %" in text
    finally:
        server.shutdown()


def _load_script():
    import importlib.util
    spec = importlib.util.spec_from_file_location("watch_tp1_first_arm", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def test_plan_review_ledger_records_and_clears():
    """Every page lands in the audit ledger with its evidence, and a
    recovery appends a clearance for the same review id (history kept)."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += _graded_series(ticket, f"1{i}")
    d.journal(rows)
    try:
        d.run(extra_env=DIGEST_ENV(d.root))
        reviews = d.plan_reviews()
        assert len(reviews) == 1, reviews
        rec = reviews[0]
        assert rec["event"] == "recommended"
        assert rec["direction"] == "down"
        assert rec["baseline_pct"] == 25 and rec["candidate_pct"] == 15
        assert rec["ts"]
        assert rec["graded"] == 3 and rec["trailed"] == 3
        assert {t["ticket"] for t in rec["tickets"]} == {555, 556, 557}
        assert all(t["vs_giveback_r"] is not None for t in rec["tickets"])

        # Recovery clears the open review; both events stay on record.
        d.journal(_graded_series(558, "14", profit_r=-18.0))
        d.run(extra_env=DIGEST_ENV(d.root))
        reviews = d.plan_reviews()
        assert [r["event"] for r in reviews] == ["recommended", "cleared"], \
            reviews
        assert reviews[1]["id"] == reviews[0]["id"]
    finally:
        server.shutdown()


def test_plan_review_ledger_records_the_upside():
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += _graded_series(ticket, f"1{i}", profit_r=-1.0)
    d.journal(rows)
    try:
        d.run(extra_env=DIGEST_ENV(d.root))
        rec = d.plan_reviews()[0]
        assert rec["direction"] == "up"
        assert rec["baseline_pct"] == 25 and rec["candidate_pct"] == 30
        assert rec["beaten"] == 3
    finally:
        server.shutdown()


def test_plan_reviews_reader_folds_cleared_and_acted():
    """read_plan_reviews() folds the append-only events into one status per
    recommendation — acted, cleared, or still open."""
    mod = _load_script()
    d = TmpData()
    os.environ["TF_TP1_PLAN_REVIEW_PATH"] = str(d.root / "reviews.jsonl")
    try:
        mod.append_plan_review({"event": "recommended", "id": "r1",
                                "ts": "t1", "direction": "down"})
        mod.append_plan_review({"event": "recommended", "id": "r2",
                                "ts": "t2", "direction": "up"})
        mod.mark_plan_review_acted("r1", note="lowered to 15")
        mod.append_plan_review({"event": "cleared", "id": "r2", "ts": "t3"})

        reviews = mod.read_plan_reviews()
        by_id = {r["id"]: r for r in reviews}
        assert [r["id"] for r in reviews] == ["r1", "r2"]
        assert by_id["r1"]["status"] == "acted"
        assert by_id["r1"]["acted_note"] == "lowered to 15"
        assert by_id["r2"]["status"] == "cleared"
    finally:
        os.environ.pop("TF_TP1_PLAN_REVIEW_PATH", None)


def test_plan_reviews_status_view_flags_stale_open():
    """The status view lists every review with its lifecycle status and
    flags an OPEN review older than the threshold."""
    mod = _load_script()
    now = datetime.now(timezone.utc)
    old = (now - timedelta(days=10)).isoformat(timespec="seconds")
    fresh = (now - timedelta(days=1)).isoformat(timespec="seconds")
    reviews = [
        {"id": "a", "event": "recommended", "ts": old, "direction": "down",
         "baseline_pct": 25, "candidate_pct": 15, "net_r": -5.93,
         "mean_vs_giveback_r": -1.98, "graded": 3, "status": "open"},
        {"id": "b", "event": "recommended", "ts": fresh, "direction": "up",
         "baseline_pct": 25, "candidate_pct": 30, "net_r": 2.33,
         "mean_vs_giveback_r": 0.78, "graded": 3, "status": "acted"},
    ]
    view = mod.render_plan_reviews(reviews, now=now)
    assert "2 review(s): 1 open, 0 cleared, 1 acted" in view
    assert "\u26a0 STALE 10d" in view
    assert "25%\u219215%" in view
    assert mod.render_plan_reviews([]) == "no plan-% reviews recorded"


def test_plan_reviews_cli_lists_the_ledger():
    d = TmpData()
    path = d.root / "watcher" / "tp1-plan-reviews.jsonl"
    path.parent.mkdir()
    path.write_text(json.dumps({
        "event": "recommended", "id": "r1", "ts": "2026-10-01T10:00:00+00:00",
        "direction": "down", "baseline_pct": 25, "candidate_pct": 15,
        "net_r": -5.93, "mean_vs_giveback_r": -1.98, "graded": 3,
    }) + "\n", encoding="utf-8")
    r = d.run("--plan-reviews")
    assert r.returncode == 0, r.stdout + r.stderr
    assert "review(s): 1 open, 0 cleared, 0 acted" in r.stdout
    assert "25%\u219215%" in r.stdout


def test_plan_review_auto_closes_when_live_plan_reaches_candidate():
    """The live plan % moving to the candidate closes the review without
    a manual mark — an auto note records the observation."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    now = datetime.now(timezone.utc)

    def at(mins):
        return (now + timedelta(minutes=mins)).isoformat(timespec="seconds")

    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += [
            profit_row(at(-40 + i), ticket, 12.0, 11.0, 10),
            arm_row(at(-35 + i), ticket),
            exec_row(at(-20 + i), ticket),
            exit_full_row(at(-5 + i), ticket, profit_r=10.0),
        ]
    d.journal(rows)
    try:
        d.run(extra_env=DIGEST_ENV(d.root))
        assert [r["event"] for r in d.plan_reviews()] == ["recommended"]

        # Someone lowers the plan % to the candidate; the next pass sees it.
        d.journal([exec_row(at(5), 559, plan=15)])
        d.run(extra_env=DIGEST_ENV(d.root))
        events = d.plan_reviews()
        assert [r["event"] for r in events] == ["recommended", "acted"], events
        assert "reached candidate" in events[-1]["note"]
    finally:
        server.shutdown()


def test_stale_open_review_pages_once():
    """An OPEN recommendation older than the threshold pages the webhook
    once and is remembered, so it cannot sit unactioned silently."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    old = (datetime.now(timezone.utc)
           - timedelta(days=10)).isoformat(timespec="seconds")
    path = d.root / "watcher" / "tp1-plan-reviews.jsonl"
    path.parent.mkdir()
    path.write_text(json.dumps({
        "event": "recommended", "id": old + "|down", "ts": old,
        "direction": "down", "baseline_pct": 25, "candidate_pct": 15,
        "net_r": -5.93, "mean_vs_giveback_r": -1.98, "graded": 3,
    }) + "\n", encoding="utf-8")
    # One ARM row keeps the pass alive without tripping the plan gate.
    d.journal([arm_row("2026-10-01T10:00:00.0+00:00", 555)])
    try:
        r1 = d.run(extra_env=DIGEST_ENV(d.root))
        assert r1.returncode == 2, r1.stdout + r1.stderr
        stale_hits = [h for h in FakeWebhook.hits
                      if "overdue" in json.dumps(h)]
        assert stale_hits, [json.dumps(h)[:120] for h in FakeWebhook.hits]
        assert d.state().get("stale_alerted")

        n = len(FakeWebhook.hits)
        r2 = d.run(extra_env=DIGEST_ENV(d.root))
        assert r2.returncode == 0, r2.stdout + r2.stderr
        assert len(FakeWebhook.hits) == n, "stale alert must not re-page"
    finally:
        server.shutdown()


def test_plan_step_pct_adapts_from_scored_history():
    """The candidate step widens when acting usually helped and narrows when
    it usually did not; below the evidence bar the base step stands."""
    mod = _load_script()
    base = mod.PLAN_GATE_STEP_PCT
    assert mod.plan_step_pct([]) == base
    assert mod.plan_step_pct([{"helped": True}] * 4) \
        == base + mod.PLAN_STEP_FEEDBACK_PCT
    assert mod.plan_step_pct([{"helped": False}] * 4) \
        == base - mod.PLAN_STEP_FEEDBACK_PCT
    assert mod.plan_step_pct([{"helped": True}, {"helped": False}] * 2) == base
    # The step sizes the candidate: 25% baseline, 2 steps down, step 9 -> 7%.
    graded = [{"plan_pct": 25, "vs_giveback_r": -1.975}] * 3
    baseline, candidate = mod.plan_candidate(graded, "down", step_pct=9.0)
    assert baseline == 25 and candidate == 7


def test_plan_review_scored_after_close():
    """A closed review is scored from the rungs settled after it — acting
    that lifted the graded net is recorded as helped."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    now = datetime.now(timezone.utc)

    def at(mins):
        return (now + timedelta(minutes=mins)).isoformat(timespec="seconds")

    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += [
            profit_row(at(-40 + i), ticket, 12.0, 11.0, 10),
            arm_row(at(-35 + i), ticket),
            exec_row(at(-20 + i), ticket),
            exit_full_row(at(-5 + i), ticket, profit_r=10.0),
        ]
    d.journal(rows)
    try:
        d.run(extra_env=DIGEST_ENV(d.root))          # gate fires (down)
        # The plan moves to the candidate: arm + exec at 15%.
        d.journal([arm_row(at(2), 559), exec_row(at(5), 559, plan=15)])
        d.run(extra_env=DIGEST_ENV(d.root))          # auto-closes (acted)
        # The new rung settles after the close: scoreable.
        d.journal([exit_full_row(at(10), 559, profit_r=1.0)])
        d.run(extra_env=DIGEST_ENV(d.root))

        events = [r["event"] for r in d.plan_reviews()]
        assert "scored" in events, events
        scored = next(r for r in d.plan_reviews() if r["event"] == "scored")
        assert scored["helped"] is True
        assert scored["delta_r"] > 0
    finally:
        server.shutdown()


def test_step_feedback_is_bounded_and_logged():
    """The step feedback cannot drift past its cap, needs the minimum
    sample, and every actual change is logged to the audit ledger."""
    mod = _load_script()
    d = TmpData()
    os.environ["TF_TP1_PLAN_REVIEW_PATH"] = str(d.root / "reviews.jsonl")
    try:
        base = mod.PLAN_GATE_STEP_PCT
        helped = [{"scored": True, "helped": True} for _ in range(50)]
        widened = mod.plan_step_pct(helped)
        assert widened <= base + mod.PLAN_STEP_MAX_DRIFT
        # Below the minimum sample the base step stands.
        assert mod.plan_step_pct([{"helped": True}] * 2) == base

        # No event while the step is the base; one when it changes, and
        # idempotent thereafter; the history records the sample behind it.
        assert mod.log_step_adaptation(helped, step=base) is False
        assert mod.log_step_adaptation(helped, step=widened) is True
        assert mod.log_step_adaptation(helped, step=widened) is False
        history = mod.read_step_adaptations()
        assert len(history) == 1
        assert history[0]["step_pct"] == widened
        assert history[0]["scored"] == 50 and history[0]["helped"] == 50
    finally:
        os.environ.pop("TF_TP1_PLAN_REVIEW_PATH", None)


def test_plan_gate_config_file_matches_the_watcher_constants():
    """The shared tunables file is the single source of truth: every value
    it carries equals the watcher constant it names, so the gate the watcher
    recommends and the breaker the app enforces cannot drift apart."""
    mod = _load_script()
    with open(mod.plan_gate_config_path(), encoding="utf-8") as fh:
        raw = json.load(fh)
    assert set(mod.PLAN_GATE_CONFIG_KEYS) <= set(raw)
    for key, const in mod.PLAN_GATE_CONFIG_KEYS.items():
        assert raw[key] == getattr(mod, const), (key, raw[key], const)
    # The file carries no tunable the loader ignores (only _comment is free).
    assert set(raw) - set(mod.PLAN_GATE_CONFIG_KEYS) == {"_comment"}


def test_plan_gate_config_keys_are_shared_with_the_app():
    """Cross-language parity: the key set the C# loader (Tp1PlanGateConfig)
    reads is exactly the set the watcher names — editing the shared file
    moves both sides together."""
    mod = _load_script()
    cs = (HERE.parent / "src" / "DongGfx.App" / "Infrastructure"
          / "Tp1PlanGateConfig.cs")
    text = cs.read_text(encoding="utf-8")
    block = text.split("IReadOnlyList<string> Keys", 1)[1].split("};", 1)[0]
    keys = set(re.findall(r'"([a-z_]+)"', block))
    assert keys == set(mod.PLAN_GATE_CONFIG_KEYS), keys


def test_plan_gate_config_overrides_the_constants():
    """Pointing TF_TP1_PLAN_GATE_CONFIG at a scratch file changes the gate's
    constants at import (what the app does too), and a malformed file leaves
    the compiled defaults standing — a bad edit can never break the watch."""
    tmp = tempfile.TemporaryDirectory()
    try:
        cfg = Path(tmp.name) / "gate.json"
        cfg.write_text(json.dumps({"stale_days": 3, "step_max_drift": 1.5,
                                   "min_graded": 2}), encoding="utf-8")
        os.environ["TF_TP1_PLAN_GATE_CONFIG"] = str(cfg)
        mod = _load_script()
        assert mod.PLAN_REVIEW_STALE_DAYS == 3
        assert mod.PLAN_STEP_MAX_DRIFT == 1.5
        assert mod.PLAN_GATE_MIN_GRADED == 2
        assert mod.PLAN_GATE_STEP_PCT == 5.0   # unlisted key keeps its default

        cfg.write_text("{ not json", encoding="utf-8")
        mod2 = _load_script()
        assert mod2.PLAN_REVIEW_STALE_DAYS == 7
        assert mod2.PLAN_GATE_MIN_GRADED == 3
    finally:
        os.environ.pop("TF_TP1_PLAN_GATE_CONFIG", None)
        tmp.cleanup()


def test_plan_review_stale_threshold_follows_the_config():
    """The stale flag in the status view tracks the shared threshold rather
    than a hard-coded 7 days."""
    tmp = tempfile.TemporaryDirectory()
    try:
        cfg = Path(tmp.name) / "gate.json"
        cfg.write_text(json.dumps({"stale_days": 2}), encoding="utf-8")
        os.environ["TF_TP1_PLAN_GATE_CONFIG"] = str(cfg)
        mod = _load_script()
        now = datetime.now(timezone.utc)
        review = {"id": "r1", "direction": "down", "status": "open"}
        old = dict(review, ts=(now - timedelta(days=3)).isoformat())
        assert "\u26a0 STALE" in mod.render_plan_reviews([old], now)
        recent = dict(review, ts=(now - timedelta(days=1)).isoformat())
        assert "\u26a0 STALE" not in mod.render_plan_reviews([recent], now)
    finally:
        os.environ.pop("TF_TP1_PLAN_GATE_CONFIG", None)
        tmp.cleanup()


def test_plan_review_ledger_carries_the_evidence_the_app_shows():
    """The app renders its recommendation line straight from the ledger, so
    the `recommended` event must carry the candidate plan %, the net R, the
    mean per-rung R and the graded count — or the in-app line goes blank."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    rows = []
    for i, ticket in enumerate((555, 556, 557)):
        rows += _graded_series(ticket, f"1{i}")
    d.journal(rows)
    try:
        r = d.run(extra_env=DIGEST_ENV(d.root))
        assert r.returncode == 2, r.stdout + r.stderr
        path = d.root / "watcher" / "tp1-plan-reviews.jsonl"
        events = [json.loads(l) for l in
                  path.read_text(encoding="utf-8").splitlines() if l.strip()]
        rec = next(e for e in events if e.get("event") == "recommended")
        assert rec["direction"] == "down"
        assert rec["baseline_pct"] == 25 and rec["candidate_pct"] == 15
        assert rec["net_r"] <= -1.0
        assert isinstance(rec["mean_vs_giveback_r"], float)
        assert rec["graded"] == 3 and rec["trailed"] == 3
        assert len(rec["tickets"]) == 3   # the per-rung evidence
    finally:
        server.shutdown()


# ── the armed plan-% override: did it reach a rung? ─────────────────


def test_override_that_never_fired_pages_once_and_is_deduped():
    """An armed plan-% override no rung has ever carried pages once past
    the grace window (exit 2), then stays quiet; --plan-reviews states the
    verdict either way."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True)
    armed = (datetime.now(timezone.utc) - timedelta(days=5)
             ).isoformat(timespec="seconds")
    # Only the arm audit row: partials on, but no rung ever armed.
    d.journal([arm_mode_row(armed, 15)])
    try:
        r1 = d.run()
        assert r1.returncode == 2, r1.stdout + r1.stderr
        hits = [h for h in FakeWebhook.hits if "never fired" in json.dumps(h)]
        assert hits, [json.dumps(h)[:200] for h in FakeWebhook.hits]
        assert "15%" in json.dumps(hits[0])
        assert d.state().get("override_flagged")

        r2 = d.run()
        assert r2.returncode == 0, r2.stdout + r2.stderr
        assert len(FakeWebhook.hits) == 1, "must not re-page"

        view = d.run("--plan-reviews")
        assert view.returncode == 0, view.stdout + view.stderr
        assert "NEVER FIRED" in view.stdout, view.stdout
        assert "armed 15%" in view.stdout, view.stdout
    finally:
        server.shutdown()


def test_override_verifies_when_the_next_rung_carries_it():
    """The rung arming and banking at the armed plan % is the proof the
    override took effect — no page, no flag, and the status view says so."""
    d = TmpData()
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True)
    now = datetime.now(timezone.utc)
    d.journal([
        arm_mode_row((now - timedelta(days=5)).isoformat(timespec="seconds"), 15),
        arm_row((now - timedelta(days=4)).isoformat(timespec="seconds"),
                555, plan=15),
        exec_row((now - timedelta(days=4, minutes=-1)
                  ).isoformat(timespec="seconds"), 555, plan=15),
    ])
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr   # no override page
    assert not d.state().get("override_flagged")
    view = d.run("--plan-reviews")
    assert view.returncode == 0, view.stdout + view.stderr
    assert "used by the next rung" in view.stdout, view.stdout


def test_override_flagged_when_the_rung_is_sized_from_the_allocation_plan():
    """The override says 15% but the next rung arms at 25%: the engine is
    sizing from the allocation plan. Certain, so it pages immediately —
    well inside the grace window that only governs 'never fired'."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True)
    now = datetime.now(timezone.utc)
    d.journal([
        arm_mode_row((now - timedelta(hours=1)).isoformat(timespec="seconds"),
                     15),
        arm_row((now - timedelta(minutes=30)).isoformat(timespec="seconds"),
                555, plan=25),
    ])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        ignored = [h for h in FakeWebhook.hits
                   if "override ignored" in json.dumps(h)]
        assert ignored, [json.dumps(h)[:200] for h in FakeWebhook.hits]
        body = json.dumps(ignored[0])
        assert "15%" in body and "25%" in body, body
        assert d.state().get("override_flagged")

        view = d.run("--plan-reviews")
        assert "IGNORED" in view.stdout, view.stdout
        assert "25%" in view.stdout, view.stdout
    finally:
        server.shutdown()


def test_override_flagged_when_tp1_partials_are_off():
    """TP1 partial execution OFF means no rung can ever take the armed
    override — certain, so it pages regardless of how fresh the arm is."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=False)
    d.journal([arm_mode_row(
        (datetime.now(timezone.utc) - timedelta(days=1)
         ).isoformat(timespec="seconds"), 15)])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        hits = [h for h in FakeWebhook.hits if "cannot fire" in json.dumps(h)]
        assert hits, [json.dumps(h)[:200] for h in FakeWebhook.hits]
        assert "partial execution is OFF" in json.dumps(hits[0])

        view = d.run("--plan-reviews")
        assert "CANNOT FIRE" in view.stdout, view.stdout
    finally:
        server.shutdown()


def test_override_inside_the_grace_window_is_reported_not_paged():
    """A rung only arms when price crosses, so a freshly armed override
    with no rung yet is NORMAL: report it, do not page — the reason
    ladder has run and found nothing actually broken."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True,
                 FxBrainRunning=True)
    now = datetime.now(timezone.utc)
    d.journal([
        arm_mode_row((now - timedelta(hours=6)).isoformat(timespec="seconds"),
                     15),
        # The brain is alive — something journaled inside the idle window,
        # so this is 'waiting', not 'app-idle'.
        profit_row(now.isoformat(timespec="seconds"), 555, 4.0, 3.0, 20),
    ])
    try:
        r = d.run()
        assert r.returncode == 0, r.stdout + r.stderr
        assert not FakeWebhook.hits, \
            [json.dumps(h)[:160] for h in FakeWebhook.hits]
        assert not d.state().get("override_flagged")
        assert d.override_check()["reason"] == "no-eligible-rung"

        view = d.run("--plan-reviews")
        assert "waiting for a rung" in view.stdout, view.stdout
    finally:
        server.shutdown()


def test_dead_precondition_pages_without_the_grace_window():
    """The grace window exists to give the MARKET time to move. A
    precondition that cannot heal itself (the brain's loop is off) will be
    equally unmet in three days — so it pages now, with the reason, and
    says 'will not fire' rather than 'never fired' at an hour-old arm."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True,
                 FxBrainRunning=False)
    d.journal([arm_mode_row(
        (datetime.now(timezone.utc) - timedelta(hours=1)
         ).isoformat(timespec="seconds"), 15)])
    try:
        r = d.run()
        assert r.returncode == 2, r.stdout + r.stderr
        hits = [h for h in FakeWebhook.hits
                if "will not fire" in json.dumps(h)]
        assert hits, [json.dumps(h)[:200] for h in FakeWebhook.hits]
        body = json.dumps(hits[0])
        assert "brain loop is off" in body, body
        assert "h ago" in body, body   # hours, not days, this early
        assert d.state().get("override_flagged")

        # Deduped like every other override page.
        n = len(FakeWebhook.hits)
        r2 = d.run()
        assert r2.returncode == 0, r2.stdout + r2.stderr
        assert len(FakeWebhook.hits) == n, "must not re-page"

        # Still inside the grace window — the STATUS says waiting, the
        # REASON says why that will not resolve on its own.
        view = d.run("--plan-reviews")
        assert "waiting for a rung" in view.stdout, view.stdout
        assert "(the brain loop is off)" in view.stdout, view.stdout
    finally:
        server.shutdown()


def test_override_with_no_arm_row_is_reported_but_never_paged():
    """A value only ever written to settings.json (hand-edited) has no arm
    to date it against: say so instead of paging a guess."""
    d = TmpData()
    set_settings(d, Tp1PlanPctOverride=40, FxExecuteTp1Partials=True)
    r = d.run()
    assert r.returncode == 0, r.stdout + r.stderr
    assert not d.state().get("override_flagged")
    view = d.run("--plan-reviews")
    assert "never journaled" in view.stdout, view.stdout
    assert "armed 40%" in view.stdout, view.stdout


def test_reverting_the_override_clears_a_standing_flag():
    """Revert on the dashboard (settings null + the audit row) stops the
    verification: the standing flag is forgotten and the view reads 'none
    armed', so a stale alert cannot outlive its override."""
    server, url = serve_once()
    d = TmpData(webhook_url=url)
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True)
    d.journal([arm_mode_row(
        (datetime.now(timezone.utc) - timedelta(days=5)
         ).isoformat(timespec="seconds"), 15)])
    try:
        assert d.run().returncode == 2, "the never-fired page must fire"
        assert d.state().get("override_flagged")

        d.journal([revert_mode_row(
            datetime.now(timezone.utc).isoformat(timespec="seconds"))])
        set_settings(d, Tp1PlanPctOverride=None)
        r = d.run()
        assert r.returncode == 0, r.stdout + r.stderr
        assert d.state().get("override_flagged") == []

        view = d.run("--plan-reviews")
        assert "none armed" in view.stdout, view.stdout
    finally:
        server.shutdown()


def test_override_arm_ts_follows_arms_reverts_and_re_arms():
    """The arm timestamp is read from the audit trail: a revert cancels it,
    a later arm of the SAME value re-dates it, and a newer arm of a
    DIFFERENT value supersedes it — so a stale arm never dates a fresh one."""
    mod = _load_script()

    def parsed(row):
        # Through the real parser, so this sees the same
        # {ts, cat, details, payload} shape one_pass() hands it.
        return mod.parse_envelope(json.dumps({
            "Timestamp": row["ts"], "Category": row["cat"],
            "Details": row["details"]}))

    a15 = parsed(arm_mode_row("2026-10-01T10:00:00+00:00", 15))
    rev = parsed(revert_mode_row("2026-10-02T10:00:00+00:00"))
    b15 = parsed(arm_mode_row("2026-10-03T10:00:00+00:00", 15))
    c25 = parsed(arm_mode_row("2026-10-04T10:00:00+00:00", 25))
    assert mod.override_armed_ts([a15], 15) == "2026-10-01T10:00:00+00:00"
    assert mod.override_armed_ts([a15, rev], 15) is None      # reverted
    assert mod.override_armed_ts([a15, rev, b15], 15) \
        == "2026-10-03T10:00:00+00:00"                        # re-armed
    assert mod.override_armed_ts([a15, rev, b15, c25], 15) is None
    assert mod.override_armed_ts([a15, rev, b15, c25], 25) \
        == "2026-10-04T10:00:00+00:00"
    assert mod.override_armed_ts([], 15) is None

    # And no override in settings at all reads as no verdict.
    assert mod.verify_plan_pct_override([], {}) is None
    assert mod.verify_plan_pct_override(
        [], {"Tp1PlanPctOverride": None}) is None


def test_override_verdict_is_written_for_the_dashboard():
    """Every pass writes the verdict the banner reads: 'waiting' for a
    freshly armed override, 'none' the moment it is reverted — so the app
    shows what the pager is acting on without re-reading the journals."""
    d = TmpData()
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True)
    d.journal([arm_mode_row(
        (datetime.now(timezone.utc) - timedelta(hours=6)
         ).isoformat(timespec="seconds"), 15)])
    assert d.run().returncode == 0, "no webhook: a quiet pass"

    check = d.override_check()
    assert check["status"] == "waiting", check
    assert check["plan_pct"] == 15
    assert check["partials"] is True
    assert check["armed_ts"], check
    assert check["computed_at"], check

    # Revert: the file says so rather than lagging behind the banner.
    set_settings(d, Tp1PlanPctOverride=None)
    assert d.run().returncode == 0
    assert d.override_check()["status"] == "none"


def test_override_verdict_tracks_the_rung_that_carries_it():
    """The file is a snapshot, not a latch: a rung at the armed plan %
    flips it to 'ok', a rung sized from the allocation plan reads
    'mismatch' — exactly the verdict the pager pages on."""
    d = TmpData()
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True)
    now = datetime.now(timezone.utc)
    d.journal([arm_mode_row(
        (now - timedelta(days=5)).isoformat(timespec="seconds"), 15)])
    assert d.run().returncode == 0
    assert d.override_check()["status"] == "never-fired"

    # The next rung arms at the armed plan %: verified.
    d.journal([arm_row((now - timedelta(minutes=10))
                       .isoformat(timespec="seconds"), 555, plan=15)])
    assert d.run().returncode == 0
    check = d.override_check()
    assert check["status"] == "ok", check
    assert check["seen_pct"] == 15, check

    # A later rung sized from the allocation plan instead: mismatch.
    d.journal([arm_row(now.isoformat(timespec="seconds"), 556, plan=25)])
    assert d.run().returncode == 0
    check = d.override_check()
    assert check["status"] == "mismatch", check
    assert check["seen_pct"] == 25, check
    assert check["plan_pct"] == 15, check


def test_override_verdict_path_is_redirectable():
    """TF_TP1_OVERRIDE_CHECK_PATH moves the file the dashboard reads, the
    same redirect the other watcher artefacts offer for tests."""
    d = TmpData()
    set_settings(d, Tp1PlanPctOverride=40, FxExecuteTp1Partials=True)
    target = d.root / "elsewhere" / "check.json"
    r = d.run(extra_env={"TF_TP1_OVERRIDE_CHECK_PATH": str(target)})
    assert r.returncode == 0, r.stdout + r.stderr
    assert target.exists(), "written to the redirected path"
    assert d.override_check(target)["plan_pct"] == 40
    assert not (d.root / "watcher"
                / "tp1-override-verification.json").exists()


def _override_verdict(**over):
    """A verdict shaped exactly as verify_plan_pct_override emits it."""
    v = {"status": "never-fired", "plan_pct": 15, "armed_ts": None,
         "age_days": 4.0, "partials": True, "seen_pct": None,
         "seen_ts": None, "reason": None, "reason_text": None}
    v.update(over)
    return v


def test_override_reason_names_the_first_unmet_precondition():
    """The status says the override has not fired; the REASON says why —
    and the ladder stops at the first precondition actually unmet, because
    each one has a different fix."""
    mod = _load_script()
    now = datetime(2026, 10, 8, 12, 0, tzinfo=timezone.utc)
    fresh = now - timedelta(minutes=5)
    arm = (now - timedelta(days=4)).isoformat(timespec="seconds")
    after = (now - timedelta(days=1)).isoformat(timespec="seconds")

    def reason(v, settings=None, activity=fresh, rows=None):
        return mod._unmet_precondition(
            v, rows or [],
            settings or {"FxExecuteTp1Partials": True, "FxBrainRunning": True},
            now, activity)

    # 1. Partials off beats everything — nothing else can be observed.
    assert reason(_override_verdict(status="partials-off", partials=False)) \
        == ("partials-off", "TP1 partial execution is off")

    # 2. Then the brain's loop.
    got, text = reason(_override_verdict(),
                       settings={"FxExecuteTp1Partials": True,
                                 "FxBrainRunning": False})
    assert (got, text) == ("engine-idle", "the brain loop is off"), (got, text)

    # 3. Then the app itself going quiet.
    got, text = reason(_override_verdict(), activity=now - timedelta(hours=9))
    assert got == "app-idle" and "nothing for 9h" in text, (got, text)
    got, text = reason(_override_verdict(), activity=None)
    assert (got, text) == ("app-idle", "the app is not journaling at all")

    # 4/5. The rung question being ANSWERED 'no' is a different answer from
    # 'never asked' — and a hold outranks the gate, being the rarer, more
    # actionable pause.
    hold = {"ts": after, "details": "EURUSD #555: TP1-HOLD: overdue"}
    skip = {"ts": after, "details": "EURUSD #555: TP1-SKIP: floor owns it"}
    got, text = reason(_override_verdict(armed_ts=arm), rows=[hold])
    assert got == "hold-blocked" and "holding the rung (1x)" in text, text
    got, text = reason(_override_verdict(armed_ts=arm),
                       rows=[skip, dict(skip, ts=arm)])
    assert got == "gate-blocked" and "said no (1x)" in text, text
    got, _ = reason(_override_verdict(armed_ts=arm), rows=[hold, skip])
    assert got == "hold-blocked"

    # Rows from BEFORE the arm say nothing about this override.
    got, text = reason(_override_verdict(armed_ts=arm),
                       rows=[{"ts": (now - timedelta(days=10)).isoformat(),
                              "details": "TP1-SKIP: old"}])
    assert got == "no-eligible-rung" and "eligible" in text, (got, text)

    # 6. Nothing unmet: say so only where it adds something — inside the
    # grace window the status already reads 'waiting'.
    got, text = reason(_override_verdict(status="waiting", armed_ts=arm))
    assert (got, text) == ("no-eligible-rung", None), (got, text)


def test_override_reason_lands_in_the_file_and_the_status_line():
    """The reason travels with the verdict: the file the dashboard reads
    and the --plan-reviews line both name the unmet precondition."""
    d = TmpData()
    set_settings(d, Tp1PlanPctOverride=15, FxExecuteTp1Partials=True,
                 FxBrainRunning=False)   # the loop is off, not the rungs
    d.journal([arm_mode_row(
        (datetime.now(timezone.utc) - timedelta(days=5)
         ).isoformat(timespec="seconds"), 15)])
    assert d.run().returncode == 0, "no webhook: a quiet pass"

    check = d.override_check()
    assert check["status"] == "never-fired", check
    assert check["reason"] == "engine-idle", check   # beats app-idle, below
    assert check["reason_text"] == "the brain loop is off", check

    view = d.run("--plan-reviews")
    assert view.returncode == 0, view.stdout + view.stderr
    assert "NEVER FIRED" in view.stdout, view.stdout
    assert "(the brain loop is off)" in view.stdout, view.stdout


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"  ok  {t.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"FAIL  {t.__name__}: {exc}")
        except Exception as exc:  # noqa: BLE001
            failed += 1
            print(f"ERROR {t.__name__}: {exc.__class__.__name__}: {exc}")
    print(f"{'FAIL' if failed else 'PASS'}: {len(tests) - failed}/{len(tests)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
