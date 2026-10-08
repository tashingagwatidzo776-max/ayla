#!/usr/bin/env python3
"""FX outcome-source coverage dashboard from the app's own journal.

Every FX_EXIT "closed #" row carries the writer's OutcomeSource payload
(spec: docs/superpowers/specs/2026-10-08-fx-win-rate-design.md §1):

  close-price      ensemble full close OR floor-exit confirm, R
                   computed from the close/executable price (tier 1 —
                   the only source fx_win_rate trusts from the close row
                   itself without a hold-row fallback)
  profit-snapshot  stale-retire / ops-backfilled snapshots
  unknown          writer had no trustworthy R (counted, never guessed)
  (no-payload)     legacy row from before the outcome payload existed

This dashboard tracks those sources PER DAY so coverage regressions are
visible on the day they start, and per WRITER so a source that should
never happen for a given close shape (e.g. floor confirms landing
unknown instead of profit-snapshot) is impossible to miss.

Usage:
  python scripts/fx_outcome_coverage.py                 # last 30 days
  python scripts/fx_outcome_coverage.py --days 7
  python scripts/fx_outcome_coverage.py --since v1.2.3  # labels the window
  python scripts/fx_outcome_coverage.py --data-dir DIR

Exit codes: 0 = report produced, 1 = usage error.
"""
import argparse
import glob
import json
import os
import sys
from datetime import datetime, timedelta, timezone

DATA_DIR = os.path.expandvars(r"%APPDATA%\tf\data\journal")

SOURCES = ("close-price", "profit-snapshot", "unknown", "(no-payload)")

# Writer classification order matters: floor confirms mention "deal" and
# stale retires mention "broker no longer holds", so the most specific
# marker comes first.
_WRITERS = (
    ("floor-confirm", "profit floor exit confirmed"),
    ("stale-retire", "stale tracking retired"),
    ("operator-reconcile", "operator reconcile"),
    ("ops-reconcile", "broker no longer holds"),
    ("ensemble-close", "deal"),
)


def classify_writer(text):
    for name, marker in _WRITERS:
        if marker in text:
            return name
    return "other"


def load_closes(data_dir):
    """One pass over every journal: all FX_EXIT close rows."""
    closes = []
    for path in sorted(glob.glob(os.path.join(data_dir, "journal_*.jsonl"))):
        try:
            fh = open(path, encoding="utf-8")
        except OSError:
            continue
        with fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except Exception:
                    continue
                if row.get("Category") != "FX_EXIT":
                    continue
                details = row.get("Details")
                if not isinstance(details, str) or "closed #" not in details:
                    continue
                try:
                    ts = datetime.fromisoformat(row["Timestamp"])
                except Exception:
                    continue
                i = details.find("{")
                payload = None
                if i >= 0:
                    try:
                        parsed = json.loads(details[i:])
                        if isinstance(parsed, dict):
                            payload = parsed
                    except Exception:
                        payload = None
                # A dict payload from BEFORE the outcome feature (no
                # OutcomeSource key) is legacy too — counting it as
                # "unknown" would bury the post-feature unknowns this
                # dashboard exists to surface.
                if payload is None or "OutcomeSource" not in payload:
                    source = "(no-payload)"
                else:
                    source = str(payload.get("OutcomeSource") or "unknown")
                if source not in SOURCES:
                    source = "unknown"
                text = details if i < 0 else details[:i]
                closes.append({
                    "ts": ts,
                    "ticket": _ticket_after(text),
                    "source": source,
                    "writer": classify_writer(text),
                })
    return closes


def _ticket_after(text):
    marker = "closed #"
    i = text.find(marker)
    if i < 0:
        return 0
    j = i + len(marker)
    k = j
    while k < len(text) and text[k].isdigit():
        k += 1
    return int(text[j:k]) if k > j else 0


def windowed(closes, days):
    if days <= 0:
        return closes
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    return [c for c in closes if c["ts"] >= cutoff]


def _counts(rows):
    out = {s: 0 for s in SOURCES}
    for c in rows:
        out[c["source"]] += 1
    out["closes"] = len(rows)
    out["measured"] = out["close-price"] + out["profit-snapshot"]
    return out


def _pct(part, whole):
    return f"{part / whole * 100:4.0f}%" if whole else "   -"


def render(closes, label):
    lines = [f"=== FX outcome-source coverage — {label} ==="]
    if not closes:
        lines.append("  no close rows in window")
        return "\n".join(lines)

    # Per-day table: a source regression must be visible on the day it
    # starts, not just in the lifetime total.
    by_day = {}
    for c in closes:
        by_day.setdefault(c["ts"].astimezone(timezone.utc).date(), []).append(c)
    lines.append(f"  {'day':<12} {'closes':>6} {'close-price':>11} "
                 f"{'profit-snap':>11} {'unknown':>7} {'no-payload':>10}  measured")
    for day in sorted(by_day):
        n = _counts(by_day[day])
        lines.append(f"  {str(day):<12} {n['closes']:>6} {n['close-price']:>11} "
                     f"{n['profit-snapshot']:>11} {n['unknown']:>7} "
                     f"{n['(no-payload)']:>10}  {_pct(n['measured'], n['closes'])}")

    total = _counts(closes)
    lines.append(f"  {'TOTAL':<12} {total['closes']:>6} {total['close-price']:>11} "
                 f"{total['profit-snapshot']:>11} {total['unknown']:>7} "
                 f"{total['(no-payload)']:>10}  {_pct(total['measured'], total['closes'])}")
    lines.append(f"  tier-1 (close-price) {total['close-price']}/{total['closes']} "
                 f"({_pct(total['close-price'], total['closes']).strip()})   "
                 f"measured coverage {_pct(total['measured'], total['closes']).strip()}")

    # Per-writer matrix: each close shape has a designed source; a column
    # that should be empty for a row is the regression signal.
    lines.append("")
    lines.append("-- By writer " + "-" * 54)
    by_writer = {}
    for c in closes:
        by_writer.setdefault(c["writer"], []).append(c)
    lines.append(f"  {'writer':<20} {'closes':>6} {'close-price':>11} "
                 f"{'profit-snap':>11} {'unknown':>7} {'no-payload':>10}")
    for writer in sorted(by_writer, key=lambda w: -len(by_writer[w])):
        n = _counts(by_writer[writer])
        lines.append(f"  {writer:<20} {n['closes']:>6} {n['close-price']:>11} "
                     f"{n['profit-snapshot']:>11} {n['unknown']:>7} "
                     f"{n['(no-payload)']:>10}")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--days", type=int, default=30,
                    help="look-back window in days, measured on close time")
    ap.add_argument("--since", type=str, default=None, help="label for the window")
    ap.add_argument("--data-dir", type=str, default=DATA_DIR)
    args = ap.parse_args()
    if args.days < 1:
        print("--days must be >= 1", file=sys.stderr)
        return 1
    closes = windowed(load_closes(args.data_dir), args.days)
    label = args.since or f"last {args.days} days"
    print(render(closes, label))
    return 0


if __name__ == "__main__":
    sys.exit(main())
