#!/usr/bin/env python3
"""Audit paper-soak provenance: was each soak credit earned on a live market?

The paper-soak bar is the evidence base for the real-money go-live gate
(FxPortfolioHost.GoLive refuses without it), so every credit must have been
earned while the venue was actually trading. Two failure modes make a credit
worthless while still looking legitimate in the journal:

  weekend    - the venue is shut. Bars freeze at the Friday close, yet the
               frozen tail still carries real range, so ATR% keeps clearing
               the regime detector's dead-tape floor and the same signal
               re-fires every cycle. Observed 2026-10-03 (Saturday):
               XAUUSDmicro logged 66 identical signals and walked its soak
               bar to 66/10 on a closed market.
  frozen     - the venue is open by the calendar but the feed stopped
               moving (broker outage, halted symbol, holiday). Same symptom,
               weekday timestamp.

This script cross-references two sources the app already writes:

  data/journal/journal_*.jsonl   FX_MODE "paper soak N/M on SYMBOL" rows,
                                 plus FX_SIGNAL for the stall fingerprint
  data/ticks/mt5/*.jsonl         the tick archive, {b, a, t} per line

and classifies every credit event:

  closed     outside market hours                            -> CONTAMINATED
  frozen     inside hours, ticks present, <=1 distinct price -> CONTAMINATED
  live       inside hours, >=2 distinct prices               -> clean
  uncovered  inside hours, no tick archive for that hour     -> cannot judge

A `uncovered` credit gets one more witness: the identical-signal fingerprint.
When the tape stops moving, the market-driven part of the FX_SIGNAL payload
(alpha, direction, confidence, reason) repeats byte-for-byte across cycles,
so a long unchanged run in an hour the archive does not cover is re-classified
as `frozen`. A short run is NOT evidence of a stall -- vol-breakout hard-codes
conf 0.60 and rounds its reason to 4 decimals, so 4-5 identical signals occur
on a perfectly live tape. Hence the threshold at STALL_RUN_LENGTH.

`uncovered` is reported but never fails the run: the tick archive only covers
periods when the app ran, and absence of ticks is not evidence of a frozen
price. It is listed so the operator can see exactly what this audit could not
verify. (On the 2026-09-28..2026-10-03 history all 11 uncovered credits had
run length 1 -- every signal differed from its neighbours.)

Market hours are the standard FX week (closed Sat, open Sun 21:00 UTC to
Fri 22:00 UTC), which is an approximation of any given broker's schedule --
holidays and broker-specific opens are NOT modelled. The frozen-price test
below is the one that catches those, which is why both run.

Exit codes:
  0 = every credit earned on a live market (or no credits at all)
  1 = at least one credit was earned outside market hours / on a frozen feed
  2 = usage/IO error
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone

SOAK_RE = re.compile(r"paper soak (\d+)/(\d+) on ([A-Za-z0-9]+)")

# Freshness guard thresholds, kept in sync with FxEngineHost.StaleBarSeconds
# by value, not by reference (a Python script cannot read C# constants).
MARKET_CLOSED_VERDICTS = ("closed", "frozen")

# An identical FX_SIGNAL run at least this long, in a hour the tick archive
# does not cover, is treated as a frozen feed: the market-driven part of the
# payload (alpha, direction, confidence, reason) did not change for N
# consecutive cycles. Below this the repetition is ordinary -- vol-breakout
# in particular hard-codes conf 0.60 and rounds its reason to 4 decimals, so
# 4-5 identical signals in a row happen on a perfectly live tape.
STALL_RUN_LENGTH = 10


def default_data_dir() -> str:
    appdata = os.environ.get("APPDATA")
    if appdata:
        return os.path.join(appdata, "tf", "data")
    return os.path.join(os.path.expanduser("~"), ".local", "share", "tf", "data")


def parse_args(argv):
    p = argparse.ArgumentParser(description="Audit paper-soak credit provenance")
    p.add_argument("--data", default=default_data_dir(),
                   help="app data dir holding journal/ and ticks/")
    p.add_argument("--journal-dir", default=None,
                   help="override the journal directory (default: DATA/journal)")
    p.add_argument("--tick-dir", default=None,
                   help="override the tick archive dir (default: DATA/ticks/mt5)")
    p.add_argument("--since", default=None, metavar="YYYY-MM-DD",
                   help="only audit credits at or after this UTC date (e.g. "
                        "the day the stale-feed guard shipped)")
    p.add_argument("--verbose", action="store_true",
                   help="print every contaminated credit, not just the summary")
    return p.parse_args(argv)


def market_open(ts: str) -> tuple[bool, str]:
    """(is_open, reason). ts is an ISO-8601 instant carrying its own offset;
    anything unparseable fails CLOSED -- an unknown instant must not be
    credited as clean evidence."""
    try:
        dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except ValueError:
        return False, "unparseable timestamp"
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    dt = dt.astimezone(timezone.utc)
    weekday = dt.weekday()          # 0=Mon .. 5=Sat .. 6=Sun
    hour = dt.hour + dt.minute / 60.0
    if weekday == 5:
        return False, "Saturday"
    if weekday == 6 and hour < 21:
        return False, "Sunday before 21:00 UTC open"
    if weekday == 4 and hour >= 22:
        return False, "Friday after 22:00 UTC close"
    return True, "open"


def load_soak_events(journal_dir: str) -> list[tuple[str, str, int, int]]:
    """[(timestamp, symbol, seen, required)] oldest first, across every
    journal_*.jsonl file. A malformed line is skipped, never fatal -- the
    journal is written concurrently and may be mid-flush."""
    events = []
    for path in sorted(glob.glob(os.path.join(journal_dir, "journal_*.jsonl"))):
        try:
            handle = open(path, encoding="utf-8", errors="replace")
        except OSError:
            continue
        with handle:
            for line in handle:
                if "paper soak" not in line:
                    continue
                try:
                    entry = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if entry.get("Category") != "FX_MODE":
                    continue
                match = SOAK_RE.search(entry.get("Details") or "")
                if not match or "Timestamp" not in entry:
                    continue
                events.append((entry["Timestamp"], match.group(3),
                               int(match.group(1)), int(match.group(2))))
    events.sort(key=lambda e: e[0])
    return events


def load_tick_hours(tick_dir: str) -> dict[tuple[str, str, str], set]:
    """{(yyyy-mm-dd, symbol, HH): {(bid, ask), ...}} -- the distinct prices
    the venue showed during that UTC hour."""
    hours: dict[tuple[str, str, str], set] = defaultdict(set)
    for path in glob.glob(os.path.join(tick_dir, "*.jsonl")):
        name = os.path.basename(path)
        stem = name[:-6] if name.endswith(".jsonl") else name
        if "_" not in stem:
            continue
        symbol, date = stem.rsplit("_", 1)
        if len(date) != 8 or not date.isdigit():
            continue
        iso = f"{date[:4]}-{date[4:6]}-{date[6:]}"
        try:
            handle = open(path, encoding="utf-8", errors="replace")
        except OSError:
            continue
        with handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    tick = json.loads(line)
                    when = datetime.fromtimestamp(tick["t"] / 1000, timezone.utc)
                    bid, ask = tick["b"], tick["a"]
                except (json.JSONDecodeError, KeyError, TypeError,
                        ValueError, OverflowError, OSError):
                    continue
                hours[(iso, symbol, when.strftime("%H"))].add((bid, ask))
    return hours


def load_signal_runs(journal_dir: str) -> dict[str, dict[str, int]]:
    """{symbol: {timestamp: run_length}} for every FX_SIGNAL row.

    A signal payload that repeats byte-for-byte across cycles is the
    fingerprint of a tape that is not moving -- it is the one thing we can
    still read when the tick archive has no coverage for that hour. Run
    length 1 means the signal differed from its neighbours (something
    changed in the market between cycles).
    """
    payloads: dict[str, list[tuple[str, str]]] = defaultdict(list)
    for path in sorted(glob.glob(os.path.join(journal_dir, "journal_*.jsonl"))):
        try:
            handle = open(path, encoding="utf-8", errors="replace")
        except OSError:
            continue
        with handle:
            for line in handle:
                if "FX_SIGNAL" not in line:
                    continue
                try:
                    entry = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if entry.get("Category") != "FX_SIGNAL" or "Timestamp" not in entry:
                    continue
                detail = entry.get("Details") or ""
                # The memory tilt rides on the payload and changes per cycle
                # by construction; strip it so the market-driven part alone
                # defines sameness.
                detail = re.sub(r',\\"MemoryWeight\\":[0-9.]+', '', detail)
                match = re.search(r'Symbol[^A-Za-z0-9]{1,8}([A-Za-z0-9]+)',
                                  detail.replace("\\", ""))
                symbol = match.group(1) if match else "?"
                payloads[symbol].append((entry["Timestamp"], detail))

    runs: dict[str, dict[str, int]] = {}
    for symbol, rows in payloads.items():
        lengths: dict[str, int] = {}
        start = 0
        for i in range(1, len(rows) + 1):
            if i == len(rows) or rows[i][1] != rows[i - 1][1]:
                for j in range(start, i):
                    lengths[rows[j][0]] = i - start
                start = i
        runs[symbol] = lengths
    return runs


def signal_run_at(runs, ts: str, symbol: str, slop_seconds: float = 2.0) -> int:
    """Length of the identical-signal run containing the signal that fired
    with this soak credit (0 when no matching signal can be found). The
    credit row is written a few tens of milliseconds after its signal, so
    they are matched on symbol + proximity, never on exact equality."""
    table = (runs or {}).get(symbol) or {}
    if not table:
        return 0
    try:
        when = datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except ValueError:
        return 0
    if when.tzinfo is None:
        when = when.replace(tzinfo=timezone.utc)
    best = 0
    for stamp, length in table.items():
        try:
            other = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
        except ValueError:
            continue
        if other.tzinfo is None:
            other = other.replace(tzinfo=timezone.utc)
        delta = abs((other - when).total_seconds())
        if delta <= slop_seconds:
            best = max(best, length)
    return best


def classify(ts: str, symbol: str, tick_hours, runs=None) -> tuple[str, str]:
    """The verdict for one credit event plus a human-readable why."""
    open_now, why = market_open(ts)
    if not open_now:
        return "closed", why
    if tick_hours is None:
        return "uncovered", "no tick archive supplied"
    prices = tick_hours.get((ts[:10], symbol, ts[11:13]))
    if prices is None:
        # No archive coverage for that hour. The identical-signal
        # fingerprint is the only remaining witness: a long unchanged run
        # says the tape was not moving between cycles.
        length = signal_run_at(runs, ts, symbol)
        if length >= STALL_RUN_LENGTH:
            return "frozen", (f"no tick archive, but the signal repeated "
                              f"{length}x unchanged")
        return "uncovered", ("tick archive has no coverage for that hour"
                             + (f" (signal varied — run {length})" if length else ""))
    if len(prices) <= 1:
        return "frozen", "venue showed one price all hour"
    return "live", f"{len(prices)} distinct prices"


def audit(events, tick_hours, runs=None) -> dict[str, list]:
    buckets: dict[str, list] = defaultdict(list)
    for ts, symbol, seen, required in events:
        verdict, why = classify(ts, symbol, tick_hours, runs)
        buckets[verdict].append((ts, symbol, seen, required, why))
    return buckets


def main(argv) -> int:
    args = parse_args(argv)
    journal_dir = args.journal_dir or os.path.join(args.data, "journal")
    tick_dir = args.tick_dir or os.path.join(args.data, "ticks", "mt5")
    if not os.path.isdir(journal_dir):
        print(f"::error::no journal directory: {journal_dir}", file=sys.stderr)
        return 2

    events = load_soak_events(journal_dir)
    if args.since:
        try:
            datetime.fromisoformat(args.since)
        except ValueError:
            print(f"::error::--since must be YYYY-MM-DD, got {args.since!r}",
                  file=sys.stderr)
            return 2
        events = [e for e in events if e[0][:10] >= args.since]
        if not events:
            print(f"OK: no paper-soak credits on or after {args.since}")
            return 0
    if not events:
        print("OK: no paper-soak credits in the journal yet — nothing to audit")
        return 0

    tick_hours = load_tick_hours(tick_dir) if os.path.isdir(tick_dir) else None
    runs = load_signal_runs(journal_dir)
    buckets = audit(events, tick_hours, runs)

    counts = Counter({k: len(v) for k, v in buckets.items()})
    contaminated = counts.get("closed", 0) + counts.get("frozen", 0)

    print(f"soak credits audited : {len(events)}")
    for verdict in ("live", "closed", "frozen", "uncovered"):
        if counts.get(verdict):
            print(f"  {verdict:10s}: {counts[verdict]}")

    bad = buckets.get("closed", []) + buckets.get("frozen", [])
    if bad:
        print("\nCONTAMINATED credits (by day, symbol):")
        by_day = Counter((ts[:10], sym, why)
                         for ts, sym, _n, _req, why in bad)
        for (day, sym, why), n in sorted(by_day.items()):
            print(f"  {day} {sym:13s} x{n:<4} {why}")
        if args.verbose:
            print("\nevery contaminated credit:")
            for ts, sym, seen, _req, why in sorted(bad):
                print(f"  {ts[:19]} {sym:13s} soak {seen:3d}  {why}")
        print(f"\nFAIL: {contaminated} of {len(events)} soak credits were earned "
              "on a closed or frozen market.")
        return 1

    uncovered = counts.get("uncovered", 0)
    note = (f" ({uncovered} could not be judged — no tick archive for that hour)"
            if uncovered else "")
    print(f"\nOK: all judgeable soak credits were earned on a live market{note}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
