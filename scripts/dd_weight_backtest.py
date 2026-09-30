#!/usr/bin/env python3
"""Drawdown ADJUST-down backtest: replay every journaled FX_EXIT evaluation
at drawdown weight 1.0 instead of 2.0 and count decision flips.

Question (DRAWDOWN-PROMOTION-CASE.md matrix, ADJUST row): would the
drawdown family's weight drop 2.0 -> 1.0 have changed any exit decision
historically? The score is a plain weighted mean,

    score = sum(exit_i * weight_i) / sum(weight_i) * 100

and the resolver bands are pinned in FxExitBrain.Resolve:
    >= 85 full, >= 70 partial, >= 55 tighten, >= 35 monitor, else hold.

The journal already stores every engine vote (Exit + Weight) per
evaluation, so the replay is arithmetic on the rows — no market data
needed, no approximated engines. Overrides fire before the resolver and
carry Action "full" at score 100, so they are replayed unchanged and
counted separately.

Row format: envelope JSON with a Details string; Details = "<note>: {json
FxExitDecision with Votes[]}". Only rows that parse as decisions are
counted. Hard-floor rows (FX_FLOOR) are a separate guard layer and never
touch this score — they are joined in only to answer "would 1.0 have
held a guard-save ticket open" (expected: no, the guard acts on its own
state machine).
"""
from __future__ import annotations

import glob
import json
import os
import sys
from collections import defaultdict

# FxExitBrain.Resolve bands (pinned by tests; keep in sync).
FULL, PARTIAL, TIGHTEN, MONITOR = 85.0, 70.0, 55.0, 35.0

BASELINE_DD_WEIGHT = 2.0
PROPOSED_DD_WEIGHT = 1.0
KNOWN_ENGINES = {"structure", "momentum", "volatility", "time", "thesis",
                 "drawdown", "counterfactual", "giveback"}


def resolve(score: float) -> str:
    if score >= FULL:
        return "full"
    if score >= PARTIAL:
        return "partial"
    if score >= TIGHTEN:
        return "tighten"
    if score >= MONITOR:
        return "monitor"
    return "hold"


def parse_details(details: str):
    """Split '<note>: {json}' and return (note, decision_dict) or None."""
    brace = details.find("{")
    if brace < 0:
        return None
    note, payload = details[:brace].strip().rstrip(":").strip(), details[brace:]
    try:
        decision = json.loads(payload)
    except json.JSONDecodeError:
        return None
    if not isinstance(decision, dict) or "Votes" not in decision:
        return None
    return note, decision


def override_engine(dec: dict) -> str | None:
    """Journal serializes OverrideEngine as \"Override\" (see FX_EXIT rows).
    Accept both spellings for safety."""
    return dec.get("Override") or dec.get("OverrideEngine")


def replay(votes, dd_weight: float):
    """Weighted-mean score with the drawdown engine re-weighted."""
    wsum = wexit = 0.0
    for v in votes:
        w = float(v.get("Weight", 0.0))
        if v.get("Engine") == "drawdown":
            w = dd_weight
        wsum += w
        wexit += float(v.get("Exit", 0.0)) * w
    return (wexit / wsum * 100.0) if wsum > 0 else 0.0


def guard_save_tickets(journal_dir: str) -> set[int]:
    """Tickets the hard-floor guard full-closed (FX_FLOOR rows)."""
    tickets = set()
    for path in sorted(glob.glob(os.path.join(journal_dir, "journal_*.jsonl"))):
        with open(path, "r", encoding="utf-8") as fh:
            for line in fh:
                if '"Category":"FX_FLOOR"' not in line:
                    continue
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    continue
                details = str(row.get("Details", ""))
                for tok in details.replace("#", " ").split():
                    # Strip trailing punctuation — rows end tickets with
                    # colons or commas as often as not.
                    tok = tok.strip(":,.;")
                    if tok.isdigit():
                        tickets.add(int(tok))
    return tickets


def main() -> int:
    journal_dir = os.path.join(os.environ.get("APPDATA", ""), "tf", "data", "journal")
    if not os.path.isdir(journal_dir):
        print(f"journal dir not found: {journal_dir}", file=sys.stderr)
        return 2

    guard = guard_save_tickets(journal_dir)

    per_file = []
    total = skipped = overrides = 0
    score_mismatches = 0
    flips = []
    exit_flips = []      # any flip touching partial/full
    advisory_flips = []  # hold/monitor/tighten <-> hold/monitor/tighten
    band_edges = []
    override_kinds = defaultdict(int)
    dd_exit_hist = defaultdict(int)

    for path in sorted(glob.glob(os.path.join(journal_dir, "journal_*.jsonl"))):
        day = os.path.basename(path)
        n = f = o = 0
        with open(path, "r", encoding="utf-8") as fh:
            for line in fh:
                if '"Category":"FX_EXIT"' not in line:
                    continue
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    skipped += 1
                    continue
                parsed = parse_details(str(row.get("Details", "")))
                if parsed is None:
                    skipped += 1
                    continue
                note, dec = parsed
                votes = dec.get("Votes") or []
                if not votes:
                    skipped += 1
                    continue
                ticket = dec.get("Ticket")
                logged_score = float(dec.get("Score", 0.0))
                ov = override_engine(dec)

                # drawdown logged weight must be the roster weight for the
                # replay to be a clean counterfactual.
                dd_w = next((float(v.get("Weight", 0.0)) for v in votes
                             if v.get("Engine") == "drawdown"), None)
                if dd_w is None or abs(dd_w - BASELINE_DD_WEIGHT) > 1e-9:
                    # Rows predating the roster; skip rather than lie.
                    skipped += 1
                    continue

                s20 = replay(votes, BASELINE_DD_WEIGHT)
                s10 = replay(votes, PROPOSED_DD_WEIGHT)

                if ov is not None:
                    # Overrides bypass the resolver entirely (score pinned
                    # to 100): a weight change can never flip them.
                    o += 1
                    overrides += 1
                    override_kinds[ov] += 1
                    if abs(s20 - logged_score) > 0.5 and logged_score != 100.0:
                        score_mismatches += 1
                    continue

                # Sanity: the logged score must reproduce from the logged
                # weights, else the row schema drifted and replay lies.
                if abs(s20 - logged_score) > 0.5:
                    score_mismatches += 1
                    skipped += 1
                    continue

                a20, a10 = resolve(s20), resolve(s10)
                n += 1
                total += 1

                dd_exit = next((round(float(v.get("Exit", 0.0)), 2) for v in votes
                                if v.get("Engine") == "drawdown"), 0.0)
                dd_exit_hist[dd_exit] += 1

                if a20 != a10:
                    f += 1
                    held_open = ticket in guard
                    entry = (day, row.get("Timestamp", "")[:19], ticket, note,
                             a20, a10, round(s20, 2), round(s10, 2),
                             "GUARD-SAVE TICKET" if held_open else "")
                    flips.append(entry)
                    exits = {"partial", "full"}
                    if a20 in exits or a10 in exits:
                        exit_flips.append(entry)
                    else:
                        advisory_flips.append(entry)

                # Band-edge proximity: how close was this evaluation to
                # flipping WITHOUT a weight change (fragility probe).
                for edge in (FULL, PARTIAL, TIGHTEN, MONITOR):
                    if abs(s20 - edge) <= 2.0:
                        band_edges.append((day, ticket, round(s20, 2), edge))
                        break

        if n or f or o:
            per_file.append((day, n, f, o))

    print("== drawdown weight backtest: 2.0 (baseline) vs 1.0 (proposed) ==")
    print(f"journal dir: {journal_dir}")
    print()
    print(f"{'file':<28}{'evals':>8}{'flips':>8}{'overrides':>11}")
    for day, n, f, o in per_file:
        print(f"{day:<28}{n:>8}{f:>8}{o:>11}")
    print(f"{'TOTAL':<28}{total:>8}{len(flips):>8}{overrides:>11}")
    if skipped:
        print(f"(skipped {skipped} unparseable / non-roster / mismatched rows)")

    print()
    print(f"decision flips 2.0 -> 1.0: {len(flips)} of {total} "
          f"({(100.0 * len(flips) / total if total else 0):.2f}%)")
    print(f"  EXIT-boundary flips (touch partial/full): {len(exit_flips)} "
          f"across {len({e[2] for e in exit_flips})} tickets")
    for day, ts, ticket, note, a20, a10, s20, s10, tag in exit_flips:
        print(f"  {day[8:]} {ts} #{ticket} {note[:30]:<30} "
              f"{a20}@{s20:>6} -> {a10}@{s10:>6} {tag}")
    adv_tickets = {e[2] for e in advisory_flips}
    print(f"  advisory-only flips (hold/monitor/tighten): "
          f"{len(advisory_flips)} across {len(adv_tickets)} tickets")
    guard_adv = [e for e in advisory_flips if "GUARD-SAVE" in e[8]]
    print(f"    ... of which guard-save ticket rows: {len(guard_adv)} "
          f"(all non-exit bands: position stays open either way)")
    per_ticket = defaultdict(list)
    for e in advisory_flips:
        per_ticket[e[2]].append(e)
    for ticket, rows in sorted(per_ticket.items()):
        bands = "/".join(sorted({f"{r[4]}->{r[5]}" for r in rows}))
        tag = " GUARD-SAVE" if any("GUARD-SAVE" in r[8] for r in rows) else ""
        print(f"    #{ticket}: {len(rows)} rows, {bands}{tag}")

    print()
    print(f"override-tier rows (replayed unchanged): {overrides}")
    for kind, count in sorted(override_kinds.items()):
        print(f"  {kind}: {count}")
    print(f"score-reproduction mismatches (schema drift probe): {score_mismatches}")
    print(f"guard-save tickets seen (FX_FLOOR layer): {sorted(guard)}")

    print()
    print(f"evaluations within 2.0 score of a resolver band edge: {len(band_edges)}")
    for day, ticket, s20, edge in band_edges[:10]:
        print(f"  {day[8:]} #{ticket} score {s20} vs edge {edge}")

    print()
    print("drawdown vote Exit distribution (all evals):")
    for exit_val in sorted(dd_exit_hist):
        print(f"  exit={exit_val:>5}: {dd_exit_hist[exit_val]}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
