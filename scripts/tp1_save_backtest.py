#!/usr/bin/env python3
"""Profit-floor × TP1 backtest: would a TP1 rung have banked R on top of
the 2026-09-30 saves?

The five saves (three giveback overrides at 06:05/06:46/08:57, two hard
guard closes at 11:27) closed the WHOLE position at the save price. The
TP1 partial prototype (FxEngineHost, arm-then-cross) banks a RUNG
ALLOCATION of the position once price reaches the Profit Brain's first
rung — money that today's save layer does not bank separately. This tool
replays, per save ticket, the counterfactual "TP1 armed and executed
before the save closed the position":

  1. ARM: scan the ticket's FX_PROFIT rows chronologically; the first row
     with a Tp1 rung, Allocation.Tp1 >= 10 (the live gate), and PeakR >= 1
     arms the rung at its journaled R distance (Tp1.R).
  2. CROSS: any later row with CurrentR >= armed rung R executes the rung.
  3. SAVE: the ticket's journaled save (override close or floor exit)
     happens at its recorded price; the rung is extra only if it crossed
     BEFORE the save.

R accounting: the FX_PROFIT/FX_EXIT rows are R-unit clean, but the floor
payload is price-based (OriginalRisk in PRICE units — the guard's own
ruler, not the R engine's), so the guard saves are priced from the
journaled ExecutablePrice and the Tp1 rung PRICE:

    banked = rung_lots * (rung_price - entry)          (buy)
    save   = (1 - rung_lots) * (save_price - entry)
    entry  = rung_price - Tp1.R * risk_per_lot

with rung_lots = Allocation.Tp1% of position volume, risk_per_lot from
the floor payload's OriginalRisk (the guard's own entry→initial-SL
distance), and position volume recovered from the TP1-EXEC venue math:
lots = snap(volume * plan% / 100) with plan% ≥ 10 ⇒ volume ≈ lots * 100
/ plan% (snap error ≤ one venue step per 10% bucket).

Cross timing is journaled only per FX_PROFIT cycle (~1/min), so the rung
executes at the first row whose CurrentR reaches the rung — the same
per-minute resolution the live arm-then-cross uses. The giveback override
fires when (peak-current)/peak ≥ 0.75 at ≥2R peak, i.e. current ≤ 25% of
peak: unless the rung sat below 25% of the peak, the cross happens at a
strictly better price than the override's close. That is the whole
point: the rung banks EARLY, the save catches what's left.

Run: python scripts/tp1_save_backtest.py [--write-doc]
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
from collections import defaultdict

JOURNAL_GLOB = os.path.join(os.environ.get("APPDATA", ""), "tf", "data",
                            "journal", "journal_*.jsonl")
SAVE_TICKETS = [9820814920, 9820161383, 9820781127, 9820712902, 9820719898]
TP1_GATE_PLAN_PCT = 10   # FxEngineHost: Allocation.Tp1 >= 10
TP1_GATE_MFE_R = 1.0    # FxEngineHost: MfeR >= 1.0 (MfeR == PeakR per row)


def journal_rows(pattern: str = JOURNAL_GLOB):
    for path in sorted(glob.glob(pattern)):
        with open(path, "r", encoding="utf-8") as fh:
            for line in fh:
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    continue
                yield row


def payload(details: str):
    brace = details.find("{")
    if brace < 0:
        return None
    try:
        return json.loads(details[brace:])
    except json.JSONDecodeError:
        return None


def ticket_rows(rows, ticket: int, category: str | None = None):
    for row in rows:
        details = str(row.get("Details", ""))
        if f"#{ticket}" not in details:
            continue
        if category and row.get("Category") != category:
            continue
        yield row, payload(details)


def save_prices(rows) -> dict[int, dict]:
    """The journaled save economics per ticket: override closes from the
    FX_EXIT override rows (R units — LAST row wins: the final override
    row is the executed close, earlier rows are decision cycles that did
    not execute), guard exits from the FX_FLOOR command rows (price
    units; CurrentR/PeakR/ProtectedFloorR are already R)."""
    saves = {}
    for row in rows:
        cat = row.get("Category")
        details = str(row.get("Details", ""))
        if cat == "FX_EXIT" and "profit-floor" in details:
            dec = payload(details)
            if dec and dec.get("Override") == "profit-floor":
                t = int(dec["Ticket"])
                saves[t] = {
                    "kind": "override", "ProfitR": dec["ProfitR"],
                    "PeakR": dec["MfeR"], "price": None,
                }
        elif cat == "FX_FLOOR" and "exit commanded" in details:
            # Matches both "hard exit commanded for event ..." and the
            # combined "HARD PROFIT FLOOR BREACH ... exit commanded" row.
            pl = payload(details)
            if pl:
                t = int(pl["Ticket"])
                saves[t] = {
                    "kind": "guard", "ProfitR": None,
                    "PeakR": pl["PeakR"], "price": pl["ExecutablePrice"],
                    "OriginalRisk": pl["OriginalRisk"],
                    "breach_r": pl["CurrentR"],  # already R units
                }
    return saves


def replay_ticket(rows, ticket: int, save: dict):
    """Replay one save ticket. Returns a result dict or None when the
    journal lacks the rung evidence to price the counterfactual."""
    armed_r = armed_price = None
    plan_pct = None
    crossed_at = None  # (ts, CurrentR)
    for row, pl in ticket_rows(rows, ticket, "FX_PROFIT"):
        if pl is None:
            continue
        ts = row["Timestamp"][:19]
        tp1 = pl.get("Tp1")
        alloc = pl.get("Allocation") or {}
        plan = alloc.get("Tp1", 0)
        peak = pl.get("PeakR", 0.0)
        cur = pl.get("CurrentR", 0.0)
        # ARM: the live gate — rung exists, plan >= 10, peak >= 1R.
        if armed_r is None and tp1 and plan >= TP1_GATE_PLAN_PCT and peak >= TP1_GATE_MFE_R:
            armed_r = tp1["R"]
            armed_price = tp1["Price"]
            plan_pct = plan
            armed_ts = ts
        # CROSS: rung reached before the save.
        if armed_r is not None and crossed_at is None and cur >= armed_r:
            crossed_at = (ts, cur)
    if armed_r is None:
        return None

    result = {
        "ticket": ticket, "plan_pct": plan_pct, "armed_r": armed_r,
        "armed_price": armed_price, "armed_ts": armed_ts,
        "crossed": crossed_at is not None, "cross_ts": crossed_at[0] if crossed_at else None,
        "save_kind": save["kind"], "save_peak_r": save["PeakR"],
    }

    if save["kind"] == "override":
        # R-unit clean: banked = plan% * rung R; remainder closed at the
        # override price. Same-ticket (single) positions.
        if crossed_at is None:
            result["banked_r"] = 0.0
            result["total_r"] = save["ProfitR"]
        else:
            frac = plan_pct / 100.0
            result["banked_r"] = frac * armed_r
            result["total_r"] = frac * armed_r + (1 - frac) * save["ProfitR"]
        result["save_r"] = save["ProfitR"]
    else:
        # Guard save: price-based. Entry from the rung's R distance.
        risk = save["OriginalRisk"]  # price units (guard's own ruler)
        entry = armed_price - armed_r * risk  # all five saves are buys
        if crossed_at is None:
            result["banked_r"] = 0.0
            result["total_r"] = save["breach_r"]
            result["save_r"] = save["breach_r"]
        else:
            frac = plan_pct / 100.0
            # Per-R banking: frac of the position at the rung R, rest at
            # the guard's executable price.
            save_price = save["price"]
            rung_price = armed_price
            banked_per_unit_r = frac * (rung_price - entry) / risk
            rest_per_unit_r = (1 - frac) * (save_price - entry) / risk
            result["banked_r"] = banked_per_unit_r
            result["total_r"] = banked_per_unit_r + rest_per_unit_r
            result["save_r"] = (save_price - entry) / risk
        result["entry"] = entry

    return result


def main() -> int:
    rows = list(journal_rows())
    saves = save_prices(rows)
    print("== profit-floor x TP1 backtest: rung banked on top of the five 2026-09-30 saves ==")
    print()
    hdr = (f"{'ticket':<12}{'save':<10}{'plan%':>6}{'rung R':>8}{'armed(UTC)':>12}"
           f"{'crossed':>8}{'banked R':>10}{'save R':>8}{'total R':>9}")
    print(hdr)
    total_extra = 0.0
    graded = 0
    for t in SAVE_TICKETS:
        save = saves.get(t)
        if save is None:
            print(f"{t:<12}  (no journaled save row found)")
            continue
        res = replay_ticket(rows, t, save)
        if res is None:
            print(f"{t:<12}{save['kind']:<10}  (no arm-eligible FX_PROFIT row: rung/plan/peak gate)")
            continue
        graded += 1
        extra = res["total_r"] - res["save_r"]
        total_extra += extra
        print(f"{t:<12}{res['save_kind']:<10}{res['plan_pct']:>6}{res['armed_r']:>8.2f}"
              f"{res['armed_ts'][:16]:>17}{'YES' if res['crossed'] else 'no':>8}"
              f"{res['banked_r']:>10.2f}{res['save_r']:>8.2f}{res['total_r']:>9.2f}")
    print()
    print(f"graded saves: {graded}/{len(SAVE_TICKETS)}; "
          f"total R added by the rung: +{total_extra:.2f}R")
    return 0


if __name__ == "__main__":
    sys.exit(main())
