#!/usr/bin/env python3
"""Trade-lifecycle query over the DON G FX journal.

One command that answers "what actually happened to ticket N" — and its
portfolio-level twin — from the journal alone:

  python scripts/trade_lifecycle.py                 # all tickets, one table
  python scripts/trade_lifecycle.py --ticket 9820781127
  python scripts/trade_lifecycle.py --json          # machine-readable
  python scripts/trade_lifecycle.py --open          # only unresolved tickets

Stitches, per ticket: the FX_ORDER fill (side/lots/SL/signal), the FX_EXIT
evaluation trail (action/score/MFE/MAE/P&L in R/overrides), the FX_PROFIT
reports (state/peak/floor/giveback), FX_RISK floor breaches, the
TRADE_SETTLEMENT outcome, and the shadow-ledger rows (which shadow engines
spoke at close, who was credited). Journal-only by construction: reads,
never trades.

Journal lives under %APPDATA%\\tf\\data\\journal (or $TF_DATA_DIR/journal),
shadow ledgers under .../fx-shadow/.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys

EXIT_EVAL_ACTIONS = ("full", "partial", "tighten", "monitor", "hold")


def data_dir() -> str:
    override = os.environ.get("TF_DATA_DIR")
    return override if override else os.path.expandvars(r"%APPDATA%\tf\data")


def payload(details: str) -> dict:
    brace = details.find("{")
    if brace < 0:
        return {}
    try:
        return json.loads(details[brace:])
    except json.JSONDecodeError:
        return {}


def read_journal() -> list[dict]:
    rows = []
    pattern = os.path.join(data_dir(), "journal", "journal_*.jsonl")
    for path in sorted(glob.glob(pattern)):
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if '"Category"' not in line and "Category" not in line:
                    continue
                try:
                    env = json.loads(line)
                except json.JSONDecodeError:
                    continue
                rows.append({
                    "ts": str(env.get("Timestamp", "")),
                    "cat": env.get("Category", ""),
                    "details": env.get("Details", ""),
                    "p": payload(env.get("Details", "")),
                })
    rows.sort(key=lambda r: r["ts"])
    return rows


def read_shadow_rows() -> list[dict]:
    rows = []
    pattern = os.path.join(data_dir(), "fx-shadow", "fx-shadow-*.jsonl")
    for path in sorted(glob.glob(pattern)):
        symbol = os.path.basename(path).replace("fx-shadow-", "").replace(".jsonl", "")
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                try:
                    r = json.loads(line)
                except json.JSONDecodeError:
                    continue
                r["_symbol"] = symbol
                rows.append(r)
    return rows


def build_lifecycles(rows: list[dict], shadow_rows: list[dict]) -> dict[int, dict]:
    """Stitch every journal touch into one lifecycle per ticket."""
    lc: dict[int, dict] = {}

    def entry(ticket) -> dict:
        try:
            t = int(ticket)
        except (TypeError, ValueError):
            t = 0
        if t <= 0:
            t = -1  # unusable ticket: park in a bucket that never renders
        return lc.setdefault(t, {
            "ticket": t, "symbol": "", "side": "", "lots": 0.0, "sl": 0.0,
            "signal": "", "opened": "", "fills": [], "exits": [], "profit": [],
            "breaches": [], "settlement": None, "shadow": [],
        })

    for r in rows:
        cat, p = r["cat"], r["p"]
        if cat == "FX_ORDER":
            ticket = p.get("Order") or p.get("Deal") or 0
            e = entry(ticket)
            if p.get("Side") and not e["opened"]:
                e.update({
                    "side": p.get("Side", ""),
                    "lots": p.get("Lots", 0.0) or 0.0,
                    "sl": p.get("Sl") or 0.0,
                    "signal": p.get("Signal", ""),
                    "opened": r["ts"],
                    "sized_stop": p.get("SizedStopDistance") or 0.0,
                })
                e["fills"].append(r["ts"])
        elif cat == "FX_EXIT":
            ticket = p.get("Ticket") or 0
            e = entry(ticket)
            if p.get("Symbol") and not e["symbol"]:
                e["symbol"] = p["Symbol"]
            action = p.get("Action", "")
            if action in EXIT_EVAL_ACTIONS:
                e["exits"].append({
                    "ts": r["ts"], "action": action,
                    "score": p.get("Score"), "mfe": p.get("MfeR"),
                    "mae": p.get("MaeR"), "pl": p.get("ProfitR"),
                    "override": p.get("Override"),
                })
            elif f"closed #{ticket}" in r["details"]:
                e["exits"].append({
                    "ts": r["ts"], "action": "CLOSED",
                    "detail": r["details"].split(": ", 1)[-1].strip(),
                })
        elif cat == "FX_PROFIT":
            ticket = p.get("Ticket") or 0
            e = entry(ticket)
            if p.get("Symbol") and not e["symbol"]:
                e["symbol"] = p["Symbol"]
            e["profit"].append({
                "ts": r["ts"], "state": p.get("State", ""),
                "cur": p.get("CurrentR"), "peak": p.get("PeakR"),
                "floor": p.get("FloorR"), "gb": p.get("GivebackPct"),
                "class": p.get("GivebackClass", ""),
            })
        elif cat == "FX_RISK":
            e = entry(p.get("Ticket") or 0)
            if "breached" in r["details"]:
                e["breaches"].append(r["ts"])
        elif cat == "TRADE_SETTLEMENT":
            ticket = p.get("ContractId") or 0
            e = entry(ticket)
            e["settlement"] = {
                "ts": r["ts"], "won": p.get("Won"),
                "profit": p.get("Profit"), "payout": p.get("Payout"),
            }
            if not e["symbol"]:
                e["symbol"] = "(settled)"

    for s in shadow_rows:
        e = entry(s.get("Ticket") or 0)
        e["shadow"].append({
            "engine": s.get("Engine", ""), "exit": s.get("ExitAtClose"),
            "action": s.get("ResolvedAction", ""), "won": s.get("Won"),
            "helped": s.get("Helped"), "symbol": s.get("_symbol", ""),
        })

    return {t: e for t, e in lc.items() if t > 0}


def final_state(e: dict) -> str:
    if e["settlement"] is not None:
        s = e["settlement"]
        return ("settled WIN" if s.get("won") else "settled LOSS")
    if any(x["action"] == "CLOSED" for x in e["exits"]):
        return "closed (no settlement row)"
    return "OPEN"


def peak_r(e: dict) -> float:
    peaks = [x["peak"] for x in e["profit"] if isinstance(x.get("peak"), (int, float))]
    return max(peaks) if peaks else 0.0


def render_ticket(e: dict) -> str:
    out = [f"== #{e['ticket']} — {final_state(e).upper()} =="]
    if e["opened"]:
        sl = f" SL {e['sl']:g}" if e["sl"] else ""
        sized = (f" (sized stop {e['sized_stop']:g})"
                 if e.get("sized_stop") else "")
        out.append(f"  opened {e['opened'][:19]} {e['side']} {e['lots']:g} "
                   f"{e['symbol']}{sl}{sized} via {e['signal'] or '?'}")
    for x in e["exits"]:
        if x["action"] == "CLOSED":
            out.append(f"  {x['ts'][:19]} CLOSE: {x['detail']}")
        else:
            ov = f" OVERRIDE={x['override']}" if x["override"] else ""
            out.append(f"  {x['ts'][:19]} {x['action']:>7} score {x['score'] or 0:5.1f} "
                       f"mfe {x['mfe'] or 0:+6.2f}R mae {x['mae'] or 0:5.2f}R "
                       f"pl {x['pl'] or 0:+6.2f}R{ov}")
    for pr in e["profit"][-3:]:
        out.append(f"  {pr['ts'][:19]} PROFIT {pr['state']} peak {pr['peak'] or 0:.1f}R "
                   f"floor {pr['floor'] or 0:.1f}R giveback {pr['gb'] or 0:.0f}% {pr['class']}")
    for b in e["breaches"]:
        out.append(f"  {b[:19]} FLOOR BREACH — exit evaluation requested")
    if e["settlement"]:
        s = e["settlement"]
        out.append(f"  {s['ts'][:19]} SETTLEMENT {'WIN' if s['won'] else 'LOSS'} "
                   f"profit {s['profit']}")
    for s in e["shadow"]:
        mark = " HELPED" if s["helped"] else ""
        out.append(f"  shadow {s['engine']:<14} exit {s['exit']} {s['action']}{mark}")
    return "\n".join(out)


def render_table(lcs: dict[int, dict], only_open: bool) -> str:
    rows = []
    for t, e in sorted(lcs.items()):
        state = final_state(e)
        if only_open and state != "OPEN":
            continue
        saves = sum(1 for s in e["shadow"] if s["helped"])
        rows.append((
            e["opened"][:19] or "?", t, e["symbol"] or "?", e["side"] or "?",
            f"{peak_r(e):.1f}R",
            str(len(e["exits"])),
            str(len(e["breaches"])),
            state, f"{saves} saves" if saves else "",
        ))
    out = [f"{'opened':<19} {'ticket':<12} {'symbol':<13} {'side':<5} "
           f"{'peak':>6} {'evals':>5} {'brch':>4}  state"]
    for r in rows:
        out.append(f"{r[0]:<19} #{r[1]:<11} {r[2]:<13} {r[3]:<5} "
                   f"{r[4]:>6} {r[5]:>5} {r[6]:>4}  {r[7]}"
                   + (f"  [{r[8]}]" if r[8] else ""))
    out.append(f"\n{len(rows)} ticket(s)")
    return "\n".join(out)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--ticket", type=int, default=None,
                    help="full timeline for one ticket")
    ap.add_argument("--open", action="store_true",
                    help="only tickets not yet settled/closed")
    ap.add_argument("--json", action="store_true",
                    help="machine-readable output")
    args = ap.parse_args()

    lcs = build_lifecycles(read_journal(), read_shadow_rows())

    if args.ticket is not None:
        e = lcs.get(args.ticket)
        if e is None:
            print(f"no journal evidence for ticket {args.ticket}")
            sys.exit(1)
        print(render_ticket(e) if not args.json
              else json.dumps(e, indent=1, default=str))
        sys.exit(0)

    if args.json:
        print(json.dumps({str(t): e for t, e in sorted(lcs.items())},
                         indent=1, default=str))
        sys.exit(0)

    print(render_table(lcs, only_open=args.open))


if __name__ == "__main__":
    main()
