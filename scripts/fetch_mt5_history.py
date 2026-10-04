#!/usr/bin/env python3
"""Fetch deep MT5 bar history for the offline FX training tape.

The tick archive only ever gave the trainer ~2,468 M1 bars per symbol —
far too little for quiet pairs to reach 3000 simulated trades. The local
MT5 terminal stores up to MaxBars (100000) bars per timeframe, back to
2010 on H1. This script pages GET /history/{symbol}?tf=&n=&start= on the
local sidecar (the same loopback bridge the app uses — no second MT5
attach) and writes one file per (symbol, timeframe):

    %APPDATA%\\tf\\data\\train-history\\<SYMBOL>_<TF>.jsonl   (ascending bars)
    %APPDATA%\\tf\\data\\train-history\\spreads.json          (venue spread snapshot)

Resume semantics: a finished file is moved into place atomically
(os.replace from .part), so an existing final file always means "this TF
is complete" and is skipped. An interrupted TF leaves only a .part and is
refetched from scratch on the next run (pages are fast — the old 600 s
timeouts came from thousands of tiny pages, not from large ones).

Bounded by construction: each request asks for at most one 50k page, so
the sidecar's MT5 request lock is held for well under the external
watchdog's 45 s probe timeout, and a stall costs one page, never the run.

Run (batched to stay inside command timeouts):

    python scripts/fetch_mt5_history.py --tfs M1,M5
    python scripts/fetch_mt5_history.py --tfs M15,M30
    python scripts/fetch_mt5_history.py --tfs H1,H4,D1
    python scripts/fetch_mt5_history.py --symbols EURUSD,GBPUSD --force
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

SIDECAR = "http://127.0.0.1:53190"
PAGE = 20000            # <= HISTORY_PAGE_MAX; small enough that a cold
                        # deep-history page finishes inside the watchdog's
                        # 45 s health-probe timeout (50k pages did not)
MAX_BARS = 300000       # hard stop per TF; local MaxBars is 100000
REQUEST_TIMEOUT = 60.0
RETRIES = 8

# The external watchdog kills an unresponsive sidecar and respawns it
# (probe every 60 s + attach) — a connection-refused mid-fetch is usually
# THAT cycle, not a real failure. Back off across it: 5+15+30+45+4x60 s of
# sleeping plus attempt timeouts spans a full kill/respawn/10048-retry loop.
RETRY_BACKOFF = (5.0, 15.0, 30.0, 45.0, 60.0, 60.0, 60.0, 60.0)

# Timeframe seconds, used by the trainer to splice eras (finest wins the
# recent window, coarser TFs extend the tape backwards without overlap).
DEFAULT_TFS = ["M1", "M5", "M15", "M30", "H1", "H4", "D1"]


def data_dir() -> str:
    override = os.environ.get("TF_DATA_DIR", "").strip()
    if override:
        return override
    return os.path.join(os.environ.get("APPDATA", ""), "tf", "data")


def out_dir() -> str:
    return os.path.join(data_dir(), "train-history")


def get_json(url: str, timeout: float = REQUEST_TIMEOUT) -> dict:
    last: Exception | None = None
    for attempt in range(RETRIES + 1):
        try:
            with urllib.request.urlopen(url, timeout=timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, OSError) as exc:
            last = exc
            if attempt < RETRIES:
                time.sleep(RETRY_BACKOFF[min(attempt, len(RETRY_BACKOFF) - 1)])
    raise RuntimeError(f"GET {url} failed after {RETRIES + 1} attempts: {last}")


def venue_symbols() -> list[dict]:
    payload = get_json(f"{SIDECAR}/symbols", timeout=30.0)
    rows = payload.get("symbols") or []
    if not rows:
        raise RuntimeError("sidecar /symbols returned nothing — is the bridge up?")
    return rows


def write_spreads(rows: list[dict], force: bool) -> None:
    path = os.path.join(out_dir(), "spreads.json")
    if os.path.exists(path) and not force:
        return
    snapshot = {
        r["symbol"]: {
            "spread_points": r.get("spread_points"),
            "point": r.get("point"),
            "digits": r.get("digits"),
        }
        for r in rows
    }
    tmp = path + ".part"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(snapshot, fh, indent=1, sort_keys=True)
    os.replace(tmp, path)


def fetch_tf(symbol: str, tf: str, force: bool) -> int:
    """Fetch one (symbol, tf) to completion. Returns bars written."""
    final = os.path.join(out_dir(), f"{symbol}_{tf}.jsonl")
    if os.path.exists(final) and not force:
        return -1   # already complete

    part = final + ".part"
    start = 0
    written = 0
    empty = 0
    with open(part, "w", encoding="utf-8") as fh:
        while written < MAX_BARS:
            n = min(PAGE, MAX_BARS - written)
            qs = urllib.parse.urlencode({"tf": tf, "n": n, "start": start})
            payload = get_json(f"{SIDECAR}/history/{urllib.parse.quote(symbol)}?{qs}")
            if "error" in payload:
                raise RuntimeError(f"{symbol} {tf}: {payload['error']}")
            bars = payload.get("bars") or []
            if not bars and written == 0:
                # First page empty: MT5 still loading this history window.
                empty += 1
                if empty >= 3:
                    raise RuntimeError(
                        f"{symbol} {tf}: 0 bars after {empty} attempts "
                        "(MT5 history still loading?)")
                time.sleep(10.0)
                continue
            for bar in bars:
                fh.write(json.dumps(
                    {"t": bar["time"], "o": bar["open"], "h": bar["high"],
                     "l": bar["low"], "c": bar["close"], "v": bar["volume"]},
                    separators=(",", ":")) + "\n")
            written += len(bars)
            if len(bars) < n:
                break   # reached the oldest bar the terminal has
            start += len(bars)
            time.sleep(1.0)   # let the watchdog /account probe slip between pages
        if written == 0:
            raise RuntimeError(f"{symbol} {tf}: 0 bars returned")
    os.replace(part, final)
    return written


def main(argv: list[str]) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--symbols", default="",
                    help="comma list (default: every symbol the venue reports)")
    ap.add_argument("--tfs", default=",".join(DEFAULT_TFS),
                    help=f"comma list (default: {','.join(DEFAULT_TFS)})")
    ap.add_argument("--force", action="store_true",
                    help="refetch even when a complete file exists")
    args = ap.parse_args(argv)

    rows = venue_symbols()
    os.makedirs(out_dir(), exist_ok=True)
    write_spreads(rows, args.force)
    known = {r["symbol"] for r in rows}
    symbols = ([s.strip() for s in args.symbols.split(",") if s.strip()]
               if args.symbols else sorted(known))
    missing = [s for s in symbols if s not in known]
    if missing:
        print(f"WARNING: not on this venue, skipped: {', '.join(missing)}")
        symbols = [s for s in symbols if s in known]
    tfs = [t.strip().upper() for t in args.tfs.split(",") if t.strip()]

    os.makedirs(out_dir(), exist_ok=True)
    print(f"fetching {len(symbols)} symbol(s) x {len(tfs)} TF(s) -> {out_dir()}")

    failures: list[str] = []
    for i, symbol in enumerate(symbols, 1):
        parts = []
        for tf in tfs:
            try:
                n = fetch_tf(symbol, tf, args.force)
                parts.append(f"{tf}={'skip' if n < 0 else n}")
            except RuntimeError as exc:
                failures.append(f"{symbol} {tf}: {exc}")
                parts.append(f"{tf}=FAIL")
        print(f"[{i}/{len(symbols)}] {symbol}: " + " ".join(parts), flush=True)

    if failures:
        print(f"\n{len(failures)} failure(s):")
        for f in failures:
            print(f"  {f}")
        return 1
    print("\nall timeframes fetched")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
