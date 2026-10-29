#!/usr/bin/env python3
"""GWEN confluence-gate soak watcher.

Scans the app journal for FX_GWEN rows and measures how the desk's local
model has actually been judging dispatches over a window: verdict
coverage vs decision activity (FX_DECISION), approve/veto mix, bias mix,
fail-open (GWEN unavailable) share, and latency. It then writes/refreshes
the dated report docs/soak/GWEN-SOAK-<run-date>.md — one file per run
date, so a scheduled task keeps the weekend's latest state in one place.

Verdict row schemas (as FxEngineHost journals them):
  unavailable  {"Symbol": ..., "Unavailable": true}   (announced once
               per outage — the fail-open is visible in the book)
  verdict      {"Symbol", "Side", "Alpha", "Bias", "Confidence",
               "RrOk", "Reasons", "LatencyMs", "Pass", "Prompt"}

The report is measurement only: nothing here arms, disarms or tunes the
gate (docs/gwen-brain.md — advisory, fail-neutral; an unavailable model
trades exactly as if the gate were off).

Exit codes: 0 = report produced, 1 = usage/IO error.
"""
import argparse
import glob
import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

DEFAULT_DATA_DIR = os.path.expandvars(r"%APPDATA%\tf\data\journal")
DEFAULT_OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))), "docs", "soak")
LOG_PATH = os.path.expandvars(
    r"%APPDATA%\tf\data\logs\gwen-soak-watch.log")


def load_window(data_dir, days):
    """One ordered pass over the window: FX_GWEN verdict payloads plus
    FX_DECISION count (coverage denominator) and per-day totals."""
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    verdicts = []       # dicts from verdict rows (Unavailable absent/False)
    unavailable = []    # dicts from fail-open rows
    decisions = 0
    by_day = defaultdict(lambda: {"verdicts": 0, "unavailable": 0})

    paths = sorted(glob.glob(os.path.join(data_dir, "journal_*.jsonl")))
    for path in paths:
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
                cat = row.get("Category")
                if cat not in ("FX_GWEN", "FX_DECISION"):
                    continue
                try:
                    ts = datetime.fromisoformat(row["Timestamp"])
                except Exception:
                    continue
                if ts < cutoff:
                    continue
                day = ts.astimezone(timezone.utc).strftime("%Y-%m-%d")
                if cat == "FX_DECISION":
                    decisions += 1
                    continue
                details = row.get("Details") or ""
                i = details.find("{")
                payload = None
                if i >= 0:
                    try:
                        payload = json.loads(details[i:])
                    except Exception:
                        payload = None
                if not isinstance(payload, dict):
                    payload = {}
                payload["_ts"] = ts
                if payload.get("Unavailable"):
                    unavailable.append(payload)
                    by_day[day]["unavailable"] += 1
                else:
                    verdicts.append(payload)
                    by_day[day]["verdicts"] += 1

    return verdicts, unavailable, decisions, by_day


def _pcts(values, q):
    if not values:
        return None
    ordered = sorted(values)
    idx = min(len(ordered) - 1, int(round(q * (len(ordered) - 1))))
    return ordered[idx]


def summarize(verdicts, unavailable, decisions, by_day):
    """Pure aggregation — the report and the tests share exactly this."""
    latencies = [v["LatencyMs"] for v in verdicts
                 if isinstance(v.get("LatencyMs"), (int, float))
                 and not isinstance(v.get("LatencyMs"), bool)]
    passed = [v for v in verdicts if v.get("Pass")]
    vetoed = [v for v in verdicts if not v.get("Pass")]
    bias = defaultdict(int)
    for v in verdicts:
        bias[str(v.get("Bias", "?"))] += 1
    symbols = defaultdict(lambda: {"verdicts": 0, "pass": 0, "veto": 0,
                                   "unavailable": 0, "latency": []})
    for v in verdicts:
        s = symbols[str(v.get("Symbol", "?"))]
        s["verdicts"] += 1
        s["pass" if v.get("Pass") else "veto"] += 1
        if isinstance(v.get("LatencyMs"), (int, float)) \
                and not isinstance(v.get("LatencyMs"), bool):
            s["latency"].append(float(v["LatencyMs"]))
    for u in unavailable:
        symbols[str(u.get("Symbol", "?"))]["unavailable"] += 1

    total_observations = len(verdicts) + len(unavailable)
    return {
        "verdicts": len(verdicts),
        "unavailable": len(unavailable),
        "total_observations": total_observations,
        "passed": len(passed),
        "vetoed": len(vetoed),
        "veto_rate": (len(vetoed) / len(verdicts)) if verdicts else None,
        "fail_open_share": (len(unavailable) / total_observations)
        if total_observations else None,
        "decisions": decisions,
        "coverage": (len(verdicts) / decisions) if decisions else None,
        "bias": dict(bias),
        "latency_mean": (sum(latencies) / len(latencies)) if latencies else None,
        "latency_p50": _pcts(latencies, 0.5),
        "latency_max": max(latencies) if latencies else None,
        "symbols": dict(symbols),
        "by_day": {d: dict(c) for d, c in sorted(by_day.items())},
    }


def render(summary, label):
    """Markdown report — the file under docs/soak and the stdout text."""
    lines = [f"# GWEN soak watch — {label}", ""]
    s = summary
    if s["total_observations"] == 0:
        lines.append("No FX_GWEN rows in the window — the gate journalled"
                     " nothing (desk off, or no proposed dispatches).")
        return "\n".join(lines) + "\n"

    cov = "n/a (no FX_DECISION rows in window)" if s["coverage"] is None \
        else f"{s['coverage'] * 100:.0f}% of {s['decisions']} decisions"
    lines += [
        f"- verdicts: **{s['verdicts']}** ({s['passed']} pass, "
        f"{s['vetoed']} veto"
        + (f", veto rate {s['veto_rate'] * 100:.0f}%" if s['veto_rate'] is not None else "")
        + ")",
        f"- fail-open (GWEN unavailable): **{s['unavailable']}** "
        + (f"({s['fail_open_share'] * 100:.0f}% of observations)"
           if s["fail_open_share"] is not None else ""),
        f"- verdict coverage: {cov}",
    ]
    if s["latency_mean"] is not None:
        lines.append(f"- latency: mean {s['latency_mean']:.0f} ms, "
                     f"p50 {s['latency_p50']:.0f} ms, max {s['latency_max']:.0f} ms")
    if s["bias"]:
        mix = ", ".join(f"{b} {n}" for b, n in sorted(s["bias"].items()))
        lines.append(f"- bias mix: {mix}")

    lines += ["", "| symbol | verdicts | pass | veto | unavailable | mean latency |",
              "|---|---|---|---|---|---|"]
    for sym, d in sorted(s["symbols"].items()):
        mean_lat = (f"{sum(d['latency']) / len(d['latency']):.0f} ms"
                    if d["latency"] else "—")
        lines.append(f"| {sym} | {d['verdicts']} | {d['pass']} | {d['veto']} "
                     f"| {d['unavailable']} | {mean_lat} |")

    lines += ["", "## Per day", "", "| day | verdicts | unavailable |",
              "|---|---|---|"]
    for day, c in s["by_day"].items():
        lines.append(f"| {day} | {c['verdicts']} | {c['unavailable']} |")

    lines += ["", "Fail-neutral reminder: an unavailable model trades"
              " exactly as if the gate were off — the fail-open share is"
              " the honest measure of how much of this soak the gate was"
              " actually judging."]
    return "\n".join(lines) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--days", type=int, default=3,
                    help="look-back window in days (default 3 covers a weekend)")
    ap.add_argument("--data-dir", type=str, default=DEFAULT_DATA_DIR)
    ap.add_argument("--out-dir", type=str, default=DEFAULT_OUT_DIR)
    args = ap.parse_args()
    if args.days < 1:
        print("--days must be >= 1", file=sys.stderr)
        return 1

    now = datetime.now(timezone.utc)
    label = f"last {args.days} days, generated {now:%Y-%m-%d %H:%M UTC}"
    verdicts, unavailable, decisions, by_day = load_window(args.data_dir, args.days)
    text = render(summarize(verdicts, unavailable, decisions, by_day), label)
    print(text, end="")

    try:
        if not os.path.isdir(args.out_dir):
            os.makedirs(args.out_dir, exist_ok=True)
        out_path = os.path.join(
            args.out_dir, f"GWEN-SOAK-{now:%Y-%m-%d}.md")
        with open(out_path, "w", encoding="utf-8") as fh:
            fh.write(text)
        # Self-log (the scheduled task captures no stdout): append one
        # summary line so consecutive runs are traceable.
        with open(LOG_PATH, "a", encoding="utf-8") as fh:
            fh.write(f"{now:%Y-%m-%dT%H:%M:%SZ} verdicts={len(verdicts)} "
                     f"unavailable={len(unavailable)} decisions={decisions} "
                     f"report={out_path}\n")
        print(f"\nwrote {out_path}")
    except OSError as exc:
        print(f"cannot write report: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
