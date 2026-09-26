#!/usr/bin/env python3
"""Convert a TickArchive jsonl file into the bar CSV the backtester eats.

TickArchive format (per line): {"b": <bid>, "a": <ask>, "t": <epoch ms>}
Output: ts,open,high,low,close — OHLC of the bid/ask mid-price, one row per
bucket (M1 by default). This is the read-only bridge between the app's
recorded market data and the offline alpha harness; it writes nothing back
into the app's data directory.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import sys
from datetime import datetime, timezone

DEFAULT_TICK_DIR = os.path.expandvars(r"%APPDATA%\tf\data\ticks\mt5")


def bars_from_ticks(path: pathlib.Path, bucket_ms: int) -> list[dict]:
    buckets: dict[int, list[float]] = {}
    skipped = 0
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                tick = json.loads(line)
                mid = (float(tick["b"]) + float(tick["a"])) / 2.0
                ts = int(tick["t"])
            except (json.JSONDecodeError, KeyError, TypeError, ValueError):
                skipped += 1
                continue
            buckets.setdefault(ts // bucket_ms, []).append(mid)

    bars = []
    for bucket in sorted(buckets):
        mids = buckets[bucket]
        minute = datetime.fromtimestamp(bucket * bucket_ms / 1000, tz=timezone.utc)
        bars.append({
            "ts": minute.strftime("%Y-%m-%dT%H:%M"),
            "open": mids[0],
            "high": max(mids),
            "low": min(mids),
            "close": mids[-1],
        })
    if skipped:
        print(f"note: skipped {skipped} malformed tick line(s)")
    return bars


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("symbol", help="e.g. XAUUSDmicro (file <symbol>_YYYYMMDD.jsonl)")
    parser.add_argument("--tick-dir", default=DEFAULT_TICK_DIR)
    parser.add_argument("--date", default=None, help="YYYYMMDD; default: newest matching file")
    parser.add_argument("--bucket", default="1min", choices=["1min", "5min"])
    parser.add_argument("--out", type=pathlib.Path, default=None)
    args = parser.parse_args()

    tick_dir = pathlib.Path(args.tick_dir)
    matches = sorted(tick_dir.glob(f"{args.symbol}_*.jsonl"))
    if not matches:
        print(f"no tick files for {args.symbol} in {tick_dir}", file=sys.stderr)
        return 1
    path = matches[-1] if args.date is None else tick_dir / f"{args.symbol}_{args.date}.jsonl"
    if not path.exists():
        print(f"missing tick file: {path}", file=sys.stderr)
        return 1

    bucket_ms = 60_000 if args.bucket == "1min" else 300_000
    bars = bars_from_ticks(path, bucket_ms)
    if len(bars) < 50:
        print(f"only {len(bars)} bars in {path.name} — the backtester needs >= 50; "
              "record more soak time or widen the bucket", file=sys.stderr)
        return 1

    out = args.out or pathlib.Path(__file__).parent / "bars" / f"{path.stem}.csv"
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="") as handle:
        handle.write("ts,open,high,low,close\n")
        for bar in bars:
            handle.write(f"{bar['ts']},{bar['open']:.6f},{bar['high']:.6f},{bar['low']:.6f},{bar['close']:.6f}\n")

    print(f"{len(bars)} {args.bucket} bars -> {out}  (source: {path.name})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
