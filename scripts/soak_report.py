#!/usr/bin/env python3
"""Demo soak report: turn a live demo session into evidence.

The gate/runner test suites run against fake brokers; a demo soak with the
released exe exercises the last untested surface (real tick streams,
reconnects, actual settlement callbacks). This script reads the session's
journal + heartbeat logs and produces a markdown report with verdicts, so
"the soak looked fine" becomes a checkable artifact.

Usage:
    python scripts/soak_report.py [--data %APPDATA%/tf/data] [--since YYYY-MM-DD] [--out report.md]
                                  [--record docs/soak] [--min-entries 10]

Exit codes: 0 = soak clean, 1 = findings that need a look, 3 = NO DATA
(an empty soak is not evidence), 2 = usage/IO error.

Verdicts (demo-specific):
  - gate refusals: the gate must NEVER refuse on a demo account -- any
    real-money-gate journal entry means misconfiguration (or a real account
    slipped in) and fails the soak.
  - settlements: every TRADE_SETTLEMENT must parse and carry a bankroll;
    unparseable entries fail (silent telemetry loss would hide drift).
  - reconnects: reported per account; a reconnect storm (>20 in the window)
    fails. Reconnects themselves are normal Deriv behavior.
  - governor trips / stale unlocks: safe behavior, reported not failed.

Everything is read-only; the report goes to stdout or --out.
"""
import argparse
import glob
import json
import os
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone

MAX_RECONNECTS_PER_ACCOUNT = 20

SOAK_FILE_RE = re.compile(r"^SOAK-\d{4}-\d{2}-\d{2}\.md$")
TREND_START = "<!-- soak-trend:start -->"
TREND_END = "<!-- soak-trend:end -->"

# An empty soak is not evidence: without this floor, a never-run session
# reports SOAK CLEAN and the drill would happily "pass" on it forever.
MIN_JOURNAL_ENTRIES = 10


def parse_args(argv):
    p = argparse.ArgumentParser(description="Demo soak evidence report")
    default_data = os.path.join(os.environ.get("APPDATA", ""), "tf", "data")
    p.add_argument("--data", default=default_data, help="app data directory")
    p.add_argument("--since", default=None, help="ISO date (YYYY-MM-DD); default: all entries")
    p.add_argument("--out", default=None, help="write the markdown report here")
    p.add_argument("--record", default=None, metavar="DIR",
                   help="also save the report as evidence/SOAK-<date>.md (accumulates across runs)")
    p.add_argument("--min-entries", type=int, default=MIN_JOURNAL_ENTRIES,
                   help=f"journal entries below this report NO DATA (default {MIN_JOURNAL_ENTRIES})")
    args = p.parse_args(argv)
    if not os.path.isdir(args.data):
        print(f"error: data directory not found: {args.data}", file=sys.stderr)
        return None
    return args


def read_jsonl(pattern, since_epoch):
    for path in sorted(glob.glob(pattern)):
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    entry = json.loads(line)
                except json.JSONDecodeError:
                    entry = {"_unparseable": True, "_raw": line[:200]}
                ts = entry.get("Timestamp") or entry.get("timestamp") or ""
                try:
                    epoch = datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp()
                except (ValueError, AttributeError):
                    epoch = 0
                if since_epoch and epoch < since_epoch:
                    continue
                yield entry


SUMMARY_MARK = "SOAK-SUMMARY:"


def summary_line(verdict, journal_count, settlements, gate_refusals):
    """One machine-readable line the trend table (and future tooling) can
    grep, so accumulation never depends on prose parsing."""
    return (f"{SUMMARY_MARK} entries={journal_count} settlements={settlements} "
            f"refusals={gate_refusals} verdict={verdict}")


def update_trend_table(record_dir):
    """Regenerates the verdict-trend table in the record dir's README.md
    (docs/soak/README.md in the normal layout) from every SOAK-*.md found,
    newest first. Reports predating the summary line still contribute a
    verdict parsed from their prose. Idempotent: re-runs rewrite the same
    rows between the trend markers."""
    readme = os.path.join(record_dir, "README.md")
    if not os.path.isfile(readme):
        return None
    rows = []
    for path in sorted(glob.glob(os.path.join(record_dir, "SOAK-*.md")), reverse=True):
        name = os.path.basename(path)
        if not SOAK_FILE_RE.match(name):
            continue
        date = name[len("SOAK-"):-len(".md")]
        with open(path, encoding="utf-8") as f:
            text = f.read()
        entries = settlements = refusals = "?"
        for line in text.splitlines():
            if line.startswith(SUMMARY_MARK):
                for kv in line[len(SUMMARY_MARK):].split():
                    if kv.startswith("entries="):
                        entries = kv.split("=", 1)[1]
                    elif kv.startswith("settlements="):
                        settlements = kv.split("=", 1)[1]
                    elif kv.startswith("refusals="):
                        refusals = kv.split("=", 1)[1]
                break
        if "SOAK CLEAN" in text:
            verdict = "CLEAN"
        elif "FINDINGS" in text:
            verdict = "FINDINGS"
        else:
            verdict = "NO DATA"
        rows.append(f"| {date} | {entries} | {settlements} | {refusals} | {verdict} |")
    table = ["## Verdict trend", "",
             "Newest first. Regenerated by `soak_report.py --record docs/soak` —",
             "do not edit rows by hand.", "",
             "| Date | Entries | Settlements | Refusals | Verdict |",
             "|------|--------:|------------:|---------:|---------|"]
    table += rows if rows else ["| — | — | — | — | no evidence recorded yet |"]
    block = "\n".join(table) + "\n"
    with open(readme, encoding="utf-8") as f:
        text = f.read()
    start = text.find(TREND_START)
    end = text.find(TREND_END)
    if start != -1 and end != -1 and end > start:
        text = text[:start] + TREND_START + "\n" + block + TREND_END + text[end + len(TREND_END):]
    else:
        text = text.rstrip("\n") + "\n\n" + TREND_START + "\n" + block + TREND_END + "\n"
    with open(readme, "w", encoding="utf-8") as f:
        f.write(text)
    return readme


def record_evidence(report, record_dir):
    """Saves the report under <record_dir>/SOAK-<UTC-date>.md (one file per
    day, overwritten on re-runs — the accumulation IS the trend). Returns
    the written path."""
    os.makedirs(record_dir, exist_ok=True)
    path = os.path.join(record_dir, f"SOAK-{datetime.now(timezone.utc).date()}.md")
    with open(path, "w", encoding="utf-8") as f:
        f.write(report)
    return path


def build_report(data_dir, since_epoch, min_entries=MIN_JOURNAL_ENTRIES):
    journal = read_jsonl(os.path.join(data_dir, "journal", "journal_*.jsonl"), since_epoch)
    heartbeats = read_jsonl(os.path.join(data_dir, "heartbeats", "heartbeat_*.jsonl"), since_epoch)

    gate_refusals, settlements, governor, arms, errors = [], [], [], [], []
    unparseable = 0
    journal_count = 0
    for e in journal:
        journal_count += 1
        if e.get("_unparseable"):
            unparseable += 1
            continue
        cat = e.get("Category", "")
        details = e.get("Details", "")
        if cat == "GROWTH_STATE":
            try:
                payload = json.loads(details)
            except (json.JSONDecodeError, TypeError):
                payload = {}
            state = payload.get("State", "")
            if state == "real-money-gate":
                gate_refusals.append((e.get("Timestamp"), payload.get("Reason", details)))
            elif state == "portfolio-governor":
                governor.append((e.get("Timestamp"), details))
        elif cat == "TRADE_SETTLEMENT":
            try:
                payload = json.loads(details)
                if payload.get("NewBankroll") is None:
                    raise ValueError("no bankroll")
                settlements.append(payload)
            except (json.JSONDecodeError, TypeError, ValueError):
                errors.append((e.get("Timestamp"), f"unparseable settlement: {details[:120]}"))
        elif cat == "REAL_MONEY_UNLOCK_ARMED":
            arms.append(e.get("Timestamp"))
        elif "ERROR" in cat.upper():
            errors.append((e.get("Timestamp"), f"{cat}: {details[:120]}"))

    reconnects = defaultdict(int)
    for e in heartbeats:
        if e.get("_unparseable"):
            continue
        d = e.get("Details", "")
        try:
            payload = json.loads(d)
            frm, to = payload.get("From", ""), payload.get("To", "")
        except (json.JSONDecodeError, TypeError):
            frm, to = "", str(d)
        # a recovery into Connected from anything else is a reconnect
        if "Connected" in str(to) and "Connected" not in str(frm):
            reconnects[str(payload.get("AccountName") or payload.get("AccountId") or "?")] += 1

    lines = []
    add = lines.append
    add("# Demo soak report")
    add("")
    add(f"- generated: {datetime.now(timezone.utc).isoformat(timespec='seconds')}")
    add(f"- data dir: `{data_dir}`")
    if since_epoch:
        add(f"- window: entries since {datetime.fromtimestamp(since_epoch, tz=timezone.utc).date()}")
    add("")

    def verdict(ok, title, body):
        add(f"## {'PASS' if ok else 'FAIL'} - {title}")
        add("")
        for b in body:
            add(b)
        add("")

    verdict(not gate_refusals, "gate refusals",
            [f"{len(gate_refusals)} real-money-gate refusals (demo must never be refused: check account flags)."]
            + [f"- `{t}` {r}" for t, r in gate_refusals[:10]])

    verdict(not errors, "telemetry integrity",
            [f"{unparseable} unparseable journal lines, {len(errors)} malformed settlements/errors."]
            + [f"- `{t}` {m}" for t, m in errors[:10]])

    won = sum(1 for s in settlements if s.get("Won"))
    net = sum(float(s.get("Profit", 0)) for s in settlements)
    add("## Settlements")
    add("")
    add(f"- {len(settlements)} settled trades, {won} won / {len(settlements) - won} lost, net {net:+.2f}")
    if settlements:
        add(f"- last bankroll: {settlements[-1].get('NewBankroll')}")
    add("")

    add("## Connection stability")
    add("")
    for name, count in sorted(reconnects.items()):
        add(f"- {name}: {count} reconnect(s)")
    if not reconnects:
        add("- no reconnects in the window")
    add("")

    add("## Safety events (informational)")
    add("")
    add(f"- governor entries: {len(governor)} (safe stop behavior)")
    add(f"- unlock arms: {len(arms)} (on a demo soak these arm manual surfaces only)")
    add("")

    storm = [n for n, c in reconnects.items() if c > MAX_RECONNECTS_PER_ACCOUNT]
    ok = not gate_refusals and not errors and not storm and unparseable == 0
    # Findings outrank emptiness: a gate refusal in a tiny journal is a
    # finding. Only a CLEAN-but-thin soak degrades to NO DATA — an empty
    # soak must never read as evidence, but it is also not a finding.
    if ok and journal_count < min_entries:
        ok = None  # NO DATA
    add("## Verdict")
    add("")
    if ok is None:
        add(f"NO DATA - only {journal_count} journal entries (minimum {min_entries}). An empty soak is not "
            "evidence: connect the app to a demo account, let it trade a session, then re-run this report.")
    elif ok:
        add(f"SOAK CLEAN - gate silent, telemetry intact, connections stable ({journal_count} journal entries, "
            f"{len(settlements)} settlements).")
    else:
        add("FINDINGS - see the FAIL sections above before trusting real mode.")
    verdict_text = "NO_DATA" if ok is None else ("CLEAN" if ok else "FINDINGS")
    add("")
    add(summary_line(verdict_text, journal_count, len(settlements), len(gate_refusals)))
    return "\n".join(lines) + "\n", ok


def main(argv):
    args = parse_args(argv)
    if args is None:
        return 2
    since_epoch = 0
    if args.since:
        since_epoch = datetime.fromisoformat(args.since).replace(tzinfo=timezone.utc).timestamp()
    report, ok = build_report(args.data, since_epoch, args.min_entries)
    if args.record:
        print(f"evidence recorded: {record_evidence(report, args.record)}")
        trend = update_trend_table(args.record)
        if trend:
            print(f"trend table updated: {trend}")
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(report)
        print(f"report written: {args.out}")
    else:
        print(report)
    # 0 = clean, 1 = findings, 3 = NO DATA (an empty soak is not evidence,
    # but it is also not a finding — the drill freshness check decides).
    if ok is None:
        return 3
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
