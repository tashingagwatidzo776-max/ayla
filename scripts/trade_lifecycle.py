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
spoke at close, who was credited). When the ticket banked a TP1 rung, the
graded verdict (rung/plan/banked R, capture, vs-giveback) is attached under
the `tp1` key from the watcher's verdict ledger. Journal-only by
construction: reads, never trades.

Journal lives under %APPDATA%\\tf\\data\\journal (or $TF_DATA_DIR/journal),
shadow ledgers under .../fx-shadow/, TP1 graded verdicts under
.../watcher/tp1-graded-verdicts.jsonl.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys

EXIT_EVAL_ACTIONS = ("full", "partial", "tighten", "monitor", "hold")

# A single banked rung that trailed the giveback by at least this many R is
# a material loss on its own — flagged before it drags the portfolio net.
TP1_ALERT_TRAIL_R = -1.0


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


def verdicts_path() -> str:
    """The watcher's machine-readable TP1 verdict ledger (one JSON object
    per banked ticket, rewritten each pass); TF_TP1_VERDICT_PATH overrides
    for tests, matching scripts/watch_tp1_first_arm.py."""
    override = os.environ.get("TF_TP1_VERDICT_PATH")
    if override:
        return override
    return os.path.join(data_dir(), "watcher", "tp1-graded-verdicts.jsonl")


def read_tp1_verdicts() -> dict[int, dict]:
    """The TP1 graded verdict per banked ticket, keyed by ticket. Absent
    or unreadable ledger (no rung has banked yet) yields no verdicts — the
    lifecycle simply carries no `tp1` block."""
    verdicts: dict[int, dict] = {}
    try:
        with open(verdicts_path(), encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    v = json.loads(line)
                except json.JSONDecodeError:
                    continue
                try:
                    t = int(v.get("ticket"))
                except (TypeError, ValueError):
                    continue
                verdicts[t] = v
    except OSError:
        return {}
    return verdicts


def build_lifecycles(rows: list[dict], shadow_rows: list[dict],
                     verdicts: dict[int, dict] | None = None) -> dict[int, dict]:
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
            "tp1": None,
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

    for t, e in lc.items():
        e["tp1"] = (verdicts or {}).get(t)

    return {t: e for t, e in lc.items() if t > 0}


def final_state(e: dict) -> str:
    if e["settlement"] is not None:
        s = e["settlement"]
        return ("settled WIN" if s.get("won") else "settled LOSS")
    if any(x["action"] == "CLOSED" for x in e["exits"]):
        return "closed (no settlement row)"
    return "OPEN"


def tp1_summary(e: dict) -> str:
    """One line for the timeline view: the graded numbers behind the
    verdict, or '' when the ticket never banked a rung."""
    v = e.get("tp1")
    if not v:
        return ""
    bits = []
    if v.get("banked_r") is not None:
        bits.append(f"rung banked {v['banked_r']:+.2f}R")
    if v.get("live_peak_r") is not None:
        bits.append(f"peak {v['live_peak_r']:.1f}R")
    if v.get("giveback_baseline_r") is not None or v.get("with_rung_r") is not None:
        norung = v.get("giveback_baseline_r")
        rung = v.get("with_rung_r")
        n = f"{norung:+.2f}R" if isinstance(norung, (int, float)) else "—"
        r = f"{rung:+.2f}R" if isinstance(rung, (int, float)) else "—"
        bits.append(f"no-rung {n} vs rung {r}")
    if v.get("capture_pct") is not None:
        bits.append(f"capture {v['capture_pct']:.0f}%")
    if v.get("vs_giveback_r") is not None:
        bits.append(f"vs giveback {v['vs_giveback_r']:+.2f}R")
    verdict = v.get("verdict", "")
    return ", ".join(bits) + (f" — {verdict}" if verdict else "")


def tp1_label(e: dict) -> str:
    """The table's compact TP1 cell: the verdict with its vs-giveback R
    when graded, or '' for tickets that never banked a rung."""
    v = e.get("tp1")
    if not v:
        return ""
    verdict = v.get("verdict", "")
    delta = v.get("vs_giveback_r")
    if isinstance(delta, (int, float)):
        return f"{verdict} ({delta:+.2f}R)"
    return verdict


def tp1_counterfactual(e: dict) -> str:
    """The side-by-side cell: the R with the rung banked (rung + the
    remainder held to the same exit) beside the no-rung counterfactual
    (the whole position held to that exit). '' when neither is known."""
    v = e.get("tp1")
    if not v:
        return ""
    rung = v.get("with_rung_r")
    norung = v.get("giveback_baseline_r")
    if rung is None and norung is None:
        return ""
    r = f"{rung:+.2f}R" if isinstance(rung, (int, float)) else "—"
    n = f"{norung:+.2f}R" if isinstance(norung, (int, float)) else "—"
    return f"{r} / {n}"


def tp1_trailing(lcs: dict[int, dict]) -> list[tuple[int, dict]]:
    """Tickets whose banked rung TRAILED the giveback (the rung lost R
    versus holding the whole position to the same exit), ts-ordered."""
    out = []
    for t, e in sorted(lcs.items()):
        v = e.get("tp1")
        if v and v.get("verdict") == "trailed":
            out.append((t, v))
    return out


def tp1_portfolio_line(lcs: dict[int, dict]) -> str:
    """One-line portfolio rollup of the graded verdicts: how many banked
    rungs beat / trailed / were flat / are pending, and the net R versus
    the giveback baseline. '' when no ticket banked a rung."""
    verdicts = [e["tp1"] for e in lcs.values() if e.get("tp1")]
    if not verdicts:
        return ""
    counts = {"beat": 0, "trailed": 0, "flat": 0, "pending": 0}
    deltas = []
    for v in verdicts:
        verdict = v.get("verdict", "")
        if verdict in counts:
            counts[verdict] += 1
        d = v.get("vs_giveback_r")
        if isinstance(d, (int, float)):
            deltas.append(d)
    tally = ", ".join(f"{counts[k]} {k}" for k in
                      ("beat", "trailed", "flat", "pending") if counts[k])
    net = (f" — net {sum(deltas):+.2f}R vs giveback" if deltas else "")
    return f"TP1 grading: {len(verdicts)} banked rung(s): {tally}{net}"


def tp1_loss_alerts(lcs: dict[int, dict]) -> list[tuple[int, float]]:
    """Tickets whose banked rung surrendered at least TP1_ALERT_TRAIL_R
    versus the giveback baseline — a single materially costly rung,
    ts-ordered."""
    out = []
    for t, e in sorted(lcs.items()):
        d = (e.get("tp1") or {}).get("vs_giveback_r")
        if isinstance(d, (int, float)) and d <= TP1_ALERT_TRAIL_R:
            out.append((t, d))
    return out


def tp1_loss_alert_line(lcs: dict[int, dict]) -> str:
    """The per-ticket loss alert: one rung trailing by at least
    TP1_ALERT_TRAIL_R is flagged on its own, before it drags the portfolio
    net. '' when no rung is that costly."""
    hits = tp1_loss_alerts(lcs)
    if not hits:
        return ""
    bits = [f"#{t} ({d:+.2f}R)" for t, d in hits]
    return ("\U0001f6a8 TP1 rung loss alert "
            f"(\u2265{abs(TP1_ALERT_TRAIL_R):.0f}R surrendered vs giveback): "
            + ", ".join(bits))


def tp1_trailing_line(lcs: dict[int, dict]) -> str:
    """The operator-attention callout: the tickets whose rung trailed the
    giveback, so the rungs that lost value are impossible to miss. ''
    when none trailed."""
    trailed = tp1_trailing(lcs)
    if not trailed:
        return ""
    bits = []
    for t, v in trailed:
        d = v.get("vs_giveback_r")
        bits.append(f"#{t}" + (f" ({d:+.2f}R)" if isinstance(d, (int, float))
                               else ""))
    return "\u26a0 TP1 rung TRAILED the giveback (early rung lost R): " + \
        ", ".join(bits)


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
    tp1 = tp1_summary(e)
    if tp1:
        out.append(f"  TP1 {tp1}")
    if (e.get("tp1") or {}).get("verdict") == "trailed":
        d = e["tp1"].get("vs_giveback_r")
        lost = f" by {d:+.2f}R" if isinstance(d, (int, float)) else ""
        out.append(f"  \u26a0 TP1 rung TRAILED the giveback{lost}")
        if isinstance(d, (int, float)) and d <= TP1_ALERT_TRAIL_R:
            out.append(f"  \U0001f6a8 TP1 rung loss alert: surrendered "
                       f"{abs(d):.2f}R vs the giveback")
    return "\n".join(out)


def render_table(lcs: dict[int, dict], only_open: bool) -> str:
    rows = []
    for t, e in sorted(lcs.items()):
        state = final_state(e)
        if only_open and state != "OPEN":
            continue
        saves = sum(1 for s in e["shadow"] if s["helped"])
        state_cell = state + (f" [{saves} saves]" if saves else "")
        rows.append((
            e["opened"][:19] or "?", t, e["symbol"] or "?", e["side"] or "?",
            f"{peak_r(e):.1f}R",
            str(len(e["exits"])),
            str(len(e["breaches"])),
            state_cell, tp1_label(e), tp1_counterfactual(e),
        ))
    out = [f"{'opened':<19} {'ticket':<12} {'symbol':<13} {'side':<5} "
           f"{'peak':>6} {'evals':>5} {'brch':>4}  {'state':<28} "
           f"{'tp1':<15} rung/no-rung"]
    for r in rows:
        out.append(f"{r[0]:<19} #{r[1]:<11} {r[2]:<13} {r[3]:<5} "
                   f"{r[4]:>6} {r[5]:>5} {r[6]:>4}  {r[7]:<28} "
                   f"{r[8]:<15}"
                   + (f" {r[9]}" if r[9] else ""))
    out.append(f"\n{len(rows)} ticket(s)")
    summary = tp1_portfolio_line(lcs)
    if summary:
        out.append(summary)
    trailing = tp1_trailing_line(lcs)
    if trailing:
        out.append(trailing)
    alert = tp1_loss_alert_line(lcs)
    if alert:
        out.append(alert)
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

    lcs = build_lifecycles(read_journal(), read_shadow_rows(),
                           read_tp1_verdicts())

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
