#!/usr/bin/env python3
"""Alpha Researcher step 2: deterministically backtest one proposal.

Reads the proposal JSON, replays it over OHLC bars (a hand-exported CSV, or
a seeded synthetic random walk for pipeline smoke tests) and writes a report
JSON next to the proposal. Pure functions, fixed seed, no network, no app
access — the same bars always produce the same report, which is the whole
point: the LLM proposes, this script measures, the human decides.
"""
from __future__ import annotations

import argparse
import json
import math
import pathlib
import random
import statistics
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
RESULTS = HERE / "results"
PIP = 0.0001  # report-only normalization; P&L here is in R multiples anyway


def load_bars(path: pathlib.Path) -> list[dict]:
    rows: list[dict] = []
    with path.open(newline="", encoding="utf-8") as handle:
        header = handle.readline().strip().lower().split(",")
        need = {"ts", "open", "high", "low", "close"}
        if not need.issubset(header):
            raise ValueError(f"bar CSV needs columns ts,open,high,low,close (got {header})")
        idx = {name: header.index(name) for name in need}
        for line in handle:
            parts = line.strip().split(",")
            if len(parts) < len(header):
                continue
            rows.append({
                "ts": parts[idx["ts"]],
                "open": float(parts[idx["open"]]),
                "high": float(parts[idx["high"]]),
                "low": float(parts[idx["low"]]),
                "close": float(parts[idx["close"]]),
            })
    if len(rows) < 50:
        raise ValueError(f"need >= 50 bars, got {len(rows)}")
    return rows


def synthetic_bars(count: int, seed: int) -> list[dict]:
    """Geometric random walk, seeded — identical output for identical seed."""
    rng = random.Random(seed)
    price, bars = 1.5000, []
    for i in range(count):
        step = rng.gauss(0, 0.0006)
        op = price
        cl = max(1e-4, op * (1 + step))
        hi = max(op, cl) * (1 + abs(rng.gauss(0, 0.0003)))
        lo = min(op, cl) * (1 - abs(rng.gauss(0, 0.0003)))
        bars.append({"ts": f"synthetic-{i:04d}", "open": op, "high": hi, "low": lo, "close": cl})
        price = cl
    return bars


# ── indicators (deterministic, same vocabulary as the proposal schema) ──
def donchian_high(bars: list[dict], end: int, window: int) -> float:
    return max(b["high"] for b in bars[max(0, end - window):end])


def donchian_low(bars: list[dict], end: int, window: int) -> float:
    return min(b["low"] for b in bars[max(0, end - window):end])


def sma(bars: list[dict], end: int, window: int) -> float:
    return statistics.fmean(b["close"] for b in bars[max(0, end - window):end])


def rsi(bars: list[dict], end: int, window: int = 14) -> float:
    if end < window:
        return 50.0
    gains = losses = 0.0
    for i in range(end - window, end):
        change = bars[i]["close"] - bars[i - 1]["close"]
        gains += max(change, 0.0)
        losses += max(-change, 0.0)
    if losses == 0:
        return 100.0
    rs = gains / losses
    return 100.0 - 100.0 / (1.0 + rs)


def entry_signal(bars: list[dict], i: int, spec: dict, direction: str) -> bool:
    kind, params = spec["indicator"], spec.get("params", {})
    close = bars[i]["close"]
    if kind == "donchian":
        window = int(params.get("window", 20))
        if direction == "long":
            return close > donchian_high(bars, i, window)
        return close < donchian_low(bars, i, window)
    if kind == "sma_cross":
        fast, slow = int(params.get("fast_window", 5)), int(params.get("slow_window", 20))
        if i <= max(fast, slow):
            return False  # warm-up: not enough history for either average
        crossed_up = sma(bars, i - 1, fast) <= sma(bars, i - 1, slow) and sma(bars, i, fast) > sma(bars, i, slow)
        return crossed_up if direction == "long" else not crossed_up and sma(bars, i, fast) < sma(bars, i, slow)
    if kind == "rsi":
        window, level = int(params.get("window", 14)), float(params.get("level", 30))
        value = rsi(bars, i, window)
        return value < level if direction == "long" else value > (100 - level)
    return False


def exit_signal(bars: list[dict], i: int, entry_i: int, spec: dict, direction: str) -> bool:
    kind, params = spec["indicator"], spec.get("params", {})
    close = bars[i]["close"]
    if kind == "fixed_bars":
        return i - entry_i >= int(params.get("bars", 24))
    if kind == "donchian":
        window = int(params.get("window", 10))
        if direction == "long":
            return close < donchian_low(bars, i, window)
        return close > donchian_high(bars, i, window)
    if kind == "sma_cross":
        fast, slow = int(params.get("fast_window", 5)), int(params.get("slow_window", 20))
        if i <= max(fast, slow):
            return False
        crossed_down = sma(bars, i - 1, fast) >= sma(bars, i - 1, slow) and sma(bars, i, fast) < sma(bars, i, slow)
        return crossed_down if direction == "long" else not crossed_down and sma(bars, i, fast) > sma(bars, i, slow)
    return False


def run_backtest(bars: list[dict], proposal: dict) -> dict:
    direction = proposal["direction"]
    directions = ["long", "short"] if direction == "long_or_short" else [direction]
    trades: list[dict] = []
    stop_bars = float(proposal.get("risk", {}).get("stop_bars", 2.0))

    for d in directions:
        in_trade = False
        entry_i = 0
        entry_price = 0.0
        stop = 0.0
        for i in range(1, len(bars)):
            if not in_trade:
                if entry_signal(bars, i, proposal["entry"], d):
                    in_trade, entry_i, entry_price = True, i, bars[i]["close"]
                    atr = max(
                        (bars[j]["high"] - bars[j]["low"]) for j in range(max(1, i - 5), i)
                    ) or 1e-6
                    stop = entry_price - stop_bars * atr if d == "long" else entry_price + stop_bars * atr
            else:
                price = bars[i]["close"]
                stopped = price <= stop if d == "long" else price >= stop
                if stopped or exit_signal(bars, i, entry_i, proposal["exit"], d):
                    move = (price - entry_price) if d == "long" else (entry_price - price)
                    trades.append({
                        "direction": d,
                        "entry_ts": bars[entry_i]["ts"],
                        "exit_ts": bars[i]["ts"],
                        "r_multiple": round(move / atr, 4),
                        "exit_reason": "stop" if stopped else "signal",
                    })
                    in_trade = False
    return summarize(trades, proposal)


def summarize(trades: list[dict], proposal: dict) -> dict:
    r_values = [t["r_multiple"] for t in trades]
    wins = [r for r in r_values if r > 0]
    losses = [r for r in r_values if r <= 0]
    equity, curve, peak, max_dd = 0.0, [], 0.0, 0.0
    for r in r_values:
        equity += r
        curve.append(round(equity, 4))
        peak = max(peak, equity)
        max_dd = min(max_dd, equity - peak)
    return {
        "name": proposal["name"],
        "hypothesis": proposal["hypothesis"],
        "model": proposal.get("model", "unknown"),
        "trades": len(trades),
        "win_rate": round(len(wins) / len(trades), 4) if trades else 0.0,
        "total_r": round(sum(r_values), 4),
        "avg_r": round(statistics.fmean(r_values), 4) if r_values else 0.0,
        "worst_r": round(min(r_values), 4) if r_values else 0.0,
        "max_drawdown_r": round(max_dd, 4),
        "expectancy_verdict": (
            "insufficient trades (< 5) — do not promote"
            if len(r_values) < 5
            else ("positive expectancy — review for C# port" if sum(r_values) > 0 and max_dd > -5
                  else "negative expectancy or deep drawdown — reject")
        ),
        "equity_curve_r": curve,
        "detail": trades[:50],
        "backtested_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("proposal", type=pathlib.Path)
    parser.add_argument("--bars", type=pathlib.Path, help="CSV with ts,open,high,low,close")
    parser.add_argument("--synthetic", type=int, help="generate N seeded synthetic bars instead")
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    proposal = json.loads(args.proposal.read_text(encoding="utf-8"))
    if args.bars:
        bars = load_bars(args.bars)
        source = str(args.bars)
    elif args.synthetic:
        bars = synthetic_bars(args.synthetic, args.seed)
        source = f"synthetic:{args.synthetic}:seed{args.seed}"
    else:
        print("nothing to test: pass --bars FILE or --synthetic N", file=sys.stderr)
        return 2

    report = {**run_backtest(bars, proposal), "bars_source": source}
    RESULTS.mkdir(parents=True, exist_ok=True)
    out = RESULTS / args.proposal.name.replace(".json", ".report.json")
    out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    print(f"report: {out}")
    print(f"  trades {report['trades']} | win rate {report['win_rate']:.0%} | "
          f"total {report['total_r']:+.1f}R | max DD {report['max_drawdown_r']:.1f}R")
    print(f"  verdict: {report['expectancy_verdict']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
