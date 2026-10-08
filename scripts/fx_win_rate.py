#!/usr/bin/env python3
"""FX win-rate report from the app's own journal.

Reads FX_ORDER fills, FX_EXIT/FX_PROFIT rows and FX_EXIT close
confirmations from %APPDATA%/tf/data/journal/*.jsonl and reports win
rate / expectancy per family, symbol, side and entry confidence — the
measurement half of docs/superpowers/specs/2026-10-08-fx-win-rate-design.md
(the app's Performance tab only covers TRADE_SETTLEMENT, i.e. the
binary-options book).

Outcome resolution per closed trade (never fabricated):
  tier 1  the close row's RealizedR payload (new closes),
  tier 2  the last FX_EXIT ProfitR / FX_PROFIT CurrentR row for the
          ticket before its close (legacy closes),
  else    NO OUTCOME — counted in the coverage line, excluded from
          every win-rate denominator.

Also prints the recent-tape bar preview (ROSTER-POLICY items 2-4:
n >= 30 and mean <= -0.10R excludes) so you can see which cells the
in-app filter will cut before it cuts them.

Usage:
  python scripts/fx_win_rate.py                     # last 30 days of closes
  python scripts/fx_win_rate.py --days 7
  python scripts/fx_win_rate.py --since v1.2.3      # labels the window
  python scripts/fx_win_rate.py --data-dir DIR

Exit codes: 0 = report produced, 1 = usage error.
"""
import argparse
import glob
import json
import os
import re
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

DATA_DIR = os.path.expandvars(r"%APPDATA%\tf\data\journal")

# ROSTER-POLICY constants — keep in sync with DongGfx.Core.Fx.FxRecentTape.
TAPE_WINDOW_N = 30
TAPE_EXCLUDE_MEAN_R = -0.10
# Near-threshold cells listed for watchfulness (below the bar, worth seeing).
APPROACHING_N = 10

_FAMILY_PARAMS = re.compile(r"\(.*?\)")


def _norm_family(name):
    """'ema-slope(21)' -> 'ema-slope' so signal rows and fills match
    whether or not the alpha's parameters ride along."""
    return _FAMILY_PARAMS.sub("", str(name or "")).strip()


def _text_and_payload(details):
    i = details.find("{")
    text = details if i < 0 else details[:i]
    if i < 0:
        return text, None
    try:
        return text, json.loads(details[i:])
    except Exception:
        return text, None


def _ticket_after(text, marker):
    i = text.find(marker)
    if i < 0:
        return 0
    j = i + len(marker)
    k = j
    while k < len(text) and text[k].isdigit():
        k += 1
    return int(text[j:k]) if k > j else 0


def load_journal(data_dir):
    """One ordered pass: fills, signal confidences, hold-row R snapshots,
    closes (with their resolved outcome as of close time)."""
    fills = {}        # ticket -> dict(ts, symbol, side, family, entry)
    closes = []       # dicts(ticket, ts, symbol, family, side, outcome, source)
    signals = []      # (ts, symbol, family_norm, confidence)
    last_r = {}       # ticket -> last journaled R (tier-2 snapshot)
    n_close_rows = 0

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
                details = row.get("Details")
                if not isinstance(details, str):
                    continue
                try:
                    ts = datetime.fromisoformat(row["Timestamp"])
                except Exception:
                    continue

                text, payload = _text_and_payload(details)

                if cat == "FX_ORDER" and ("fill" in details or "ticket " in details):
                    ticket = _ticket_after(text, "ticket ")
                    if ticket:
                        # "buy 0.1 lots XAUUSDmicro @ 4104.51 — ticket N"
                        m = re.search(r" lots (\S+) @", text)
                        payload = payload or {}
                        side = str(payload.get("Side") or "")
                        if side not in ("buy", "sell"):
                            side = ("buy" if " buy " in f" {text}"
                                    else "sell" if " sell " in f" {text}" else "?")
                        fills[ticket] = {
                            "ts": ts,
                            "symbol": m.group(1) if m else str(payload.get("Symbol", "?")),
                            "side": side,
                            "family": str(payload.get("Signal", "")),
                            "entry": float(payload.get("Price") or 0.0),
                        }
                elif cat == "FX_SIGNAL" and isinstance(payload, dict):
                    if "Alpha" in payload and "Confidence" in payload:
                        try:
                            signals.append((ts, str(payload.get("Symbol", "")),
                                            _norm_family(payload.get("Alpha")),
                                            float(payload["Confidence"])))
                        except (TypeError, ValueError):
                            pass
                elif cat == "FX_EXIT" and "closed #" in text:
                    n_close_rows += 1
                    ticket = _ticket_after(text, "closed #")
                    outcome, source = None, "unknown"
                    if isinstance(payload, dict):
                        rr = payload.get("RealizedR")
                        if isinstance(rr, (int, float)) and not isinstance(rr, bool):
                            outcome, source = float(rr), str(payload.get("OutcomeSource") or "close-price")
                    if outcome is None and ticket in last_r:
                        outcome, source = last_r.pop(ticket), "hold-snapshot"
                    fill = fills.pop(ticket, None)
                    closes.append({
                        "ticket": ticket,
                        "ts": ts,
                        "fill": fill,
                        "outcome": outcome,
                        "source": source,
                        "text": text.strip(),
                    })
                    last_r.pop(ticket, None)
                elif isinstance(payload, dict):
                    # Hold/progress rows keep the last known R per ticket.
                    t = payload.get("Ticket")
                    if isinstance(t, (int, float)) and not isinstance(t, bool):
                        for key in ("ProfitR", "CurrentR"):
                            v = payload.get(key)
                            if isinstance(v, (int, float)) and not isinstance(v, bool):
                                last_r[int(t)] = float(v)
                                break

    return fills, closes, signals, n_close_rows


def confidence_for(fill, signals):
    """The signal that dispatched this fill: most recent FX_SIGNAL with the
    same symbol + family at or before the fill. None = not derivable."""
    if not fill:
        return None
    fam = _norm_family(fill["family"])
    best = None
    for ts, sym, sfam, conf in signals:
        if sym == fill["symbol"] and sfam == fam and ts <= fill["ts"]:
            if best is None or ts > best[0]:
                best = (ts, conf)
    return best[1] if best else None


def windowed_trades(closes, days):
    if days <= 0:
        return closes
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    return [c for c in closes if c["ts"] >= cutoff]


def stats(trades):
    measured = [t["outcome"] for t in trades if t["outcome"] is not None]
    n = len(measured)
    wins = sum(1 for r in measured if r > 0)
    return {
        "n": n,
        "wins": wins,
        "win_rate": (wins / n) if n else None,
        "avg_r": (sum(measured) / n) if n else None,
        "total_r": sum(measured),
        "closed": len(trades),
        "no_outcome": len(trades) - n,
    }


def bucket(trades, key):
    out = defaultdict(list)
    for t in trades:
        out[key(t)].append(t)
    return out


def cells(trades):
    """(symbol, family) -> list of outcomes — the recent-tape bar preview."""
    out = defaultdict(list)
    for t in trades:
        f = t["fill"]
        if f and t["outcome"] is not None:
            out[(f["symbol"], f["family"])].append(t["outcome"])
    return out


def _row(label, st, width=22):
    if st["n"] == 0:
        # Closes exist but none has a proven outcome — say so instead of
        # printing a bare "n=0", which reads like "never traded here".
        return (f"  {label:<{width}}  n=0 measured "
                f"({st['closed']} closed, outcome unknown)")
    wr = f"{st['win_rate'] * 100:5.1f}%"
    return (f"  {label:<{width}}  n={st['n']:3}  win {wr}  "
            f"avg {st['avg_r']:+.2f}R  total {st['total_r']:+.1f}R")


def render(trades, label, signals):
    lines = [f"=== FX win rate — {label} ==="]
    total = stats(trades)
    coverage = (total["n"] / total["closed"] * 100) if total["closed"] else 0.0
    lines.append(_row("ALL", total))
    lines.append(f"  closes {total['closed']}  measured {total['n']}  "
                 f"no-outcome {total['no_outcome']}  coverage {coverage:.0f}%")

    for title, key in (
        ("By family", lambda t: (t["fill"] or {}).get("family") or "?"),
        ("By symbol", lambda t: (t["fill"] or {}).get("symbol") or "?"),
        ("By side", lambda t: (t["fill"] or {}).get("side") or "?"),
    ):
        lines.append("")
        lines.append(f"-- {title} " + "-" * (48 - len(title)))
        rows = []
        for name, group in bucket(trades, key).items():
            rows.append((name, stats(group)))
        for name, st in sorted(rows, key=lambda kv: (kv[1]["avg_r"] is None, kv[1]["avg_r"] or 0.0)):
            lines.append(_row(str(name), st))

    lines.append("")
    lines.append("-- By entry confidence " + "-" * 27)
    conf_buckets = defaultdict(list)
    unknown = 0
    for t in trades:
        c = confidence_for(t["fill"], signals)
        if c is None:
            unknown += 1
        else:
            conf_buckets[f"{c:.2f}"].append(t)
    for name in sorted(conf_buckets):
        lines.append(_row(f"conf {name}", stats(conf_buckets[name])))
    if unknown:
        lines.append(_row("conf ?", stats([t for t in trades
                                           if confidence_for(t["fill"], signals) is None])))

    lines.append("")
    lines.append("-- Recent-tape bar preview (n>=30 and mean<="
                 f"{TAPE_EXCLUDE_MEAN_R}R excludes) " + "-" * 6)
    any_cell = False
    for (sym, fam), outcomes in sorted(cells(trades).items(), key=lambda kv: sum(kv[1]) / len(kv[1])):
        n = len(outcomes)
        mean = sum(outcomes) / n
        if n >= TAPE_WINDOW_N:
            any_cell = True
            verdict = "FAIL — would exclude" if mean <= TAPE_EXCLUDE_MEAN_R else "pass"
            lines.append(f"  {sym} {fam:<20} n={n:3}  mean {mean:+.2f}R  {verdict}")
    approaching = [(k, v) for k, v in cells(trades).items()
                   if APPROACHING_N <= len(v) < TAPE_WINDOW_N]
    if approaching:
        lines.append(f"  approaching the bar (n {APPROACHING_N}-{TAPE_WINDOW_N - 1}):")
        for (sym, fam), outcomes in sorted(approaching,
                                           key=lambda kv: sum(kv[1]) / len(kv[1])):
            n = len(outcomes)
            mean = sum(outcomes) / n
            lines.append(f"    {sym} {fam:<20} n={n:3}  mean {mean:+.2f}R")
    if not any_cell and not approaching:
        lines.append("  (no cell has reached the sample bar yet — default-keep holds)")

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
    _, closes, signals, _ = load_journal(args.data_dir)
    trades = windowed_trades(closes, args.days)
    label = args.since or f"last {args.days} days"
    print(render(trades, label, signals))
    return 0


if __name__ == "__main__":
    sys.exit(main())
