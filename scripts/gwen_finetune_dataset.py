#!/usr/bin/env python3
"""GWEN fine-tuning dataset builder — the honest path from journal to SFT rows.

Pairs each FX_GWEN assessment (which journals the VERBATIM prompt and the
model's reply) with the realized R of the trade it judged, and emits
(system, user, assistant) chat rows ready for a LoRA run (axolotl /
llama-factory shape). The model itself is served locally by Ollama as
`gwen-fx` (see scripts/register-gwen-model.ps1); this dataset is what a
future weight-level fine-tune trains on — prompt engineering ships now,
weights wait for evidence.

Pairing (never fabricated):
  1. Assessment = an FX_GWEN row with Payload.Prompt, Bias/Confidence/
     RrOk/Reasons and Pass (written by FxEngineHost.GwenConfluencePassesAsync).
  2. Trade = the first brain fill AFTER the assessment on the SAME symbol
     and the SAME alpha within --max-gap hours (the fill row's Signal
     payload is the alpha).
  3. Outcome = that ticket's close, resolved like scripts/fx_win_rate.py:
     tier 1 the close row's RealizedR, tier 2 the last hold-row R before
     the close. No resolved outcome = NO LABEL — counted, never guessed.

Labels (the only edit applied to the model's own reply):
  reply concurred  and realized R >= +KEEP_R   -> keep the reply (it was right)
  reply concurred  and realized R <= -STOP_R   -> rewrite to flat/rr_ok=false
                                                  (a full stop means not
                                                  concurring was better)
  reply concurred  and outcome in between      -> keep, tagged borderline (kept
                                                  as-is: ambiguous evidence is
                                                  not evidence of wrongness)
  reply vetoed (Pass=false)                    -> dropped: a vetoed signal
                                                  rarely dispatches, so any
                                                  later same-alpha fill is a
                                                  different cycle's trade.

Usage:
  python scripts/gwen_finetune_dataset.py                       # default data dir
  python scripts/gwen_finetune_dataset.py --data-dir DIR
  python scripts/gwen_finetune_dataset.py --out dataset.jsonl
  python scripts/gwen_finetune_dataset.py --max-gap 6 --min-examples 1

Exit codes: 0 = dataset produced (possibly empty with a warning), 1 = usage
error or unreadable output path.
"""
import argparse
import glob
import json
import os
import re
import sys
from datetime import datetime, timedelta, timezone

DATA_DIR = os.path.expandvars(r"%APPDATA%\tf\data\journal")
SYSTEM_PROMPT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                  "gwen_system_prompt.txt")

# Labeling thresholds — KEEP_R is a clear winner, STOP_R is a full stop-out
# (roughly -1R); between them the evidence is ambiguous and the reply stays.
KEEP_R = 0.5
STOP_R = -1.0

_FAMILY_PARAMS = re.compile(r"\(.*?\)")


def _norm_family(name):
    """'ema-slope(21)' -> 'ema-slope' so assessments and fills match
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


def load_system_prompt(path=SYSTEM_PROMPT_PATH):
    """One source of truth with the registered model's Modelfile SYSTEM
    (scripts/register-gwen-model.ps1 reads the same file)."""
    with open(path, encoding="utf-8") as fh:
        return fh.read().rstrip("\n")


def load_journal(data_dir):
    """One ordered pass over journal_*.jsonl.

    Returns (assessments, fills, closes):
      assessments: dicts with ts/symbol/alpha/pass/prompt/reply
      fills:       ticket -> dict(ts, symbol, alpha)
      closes:      list of dicts(ticket, ts, outcome|None)
    """
    assessments = []
    fills = {}
    closes = []
    last_r = {}   # ticket -> last journaled R (tier-2 snapshot)

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

                if cat == "FX_GWEN" and isinstance(payload, dict):
                    prompt = payload.get("Prompt")
                    bias = payload.get("Bias")
                    if isinstance(prompt, str) and isinstance(bias, str):
                        assessments.append({
                            "ts": ts,
                            "symbol": str(payload.get("Symbol") or ""),
                            "alpha": _norm_family(payload.get("Alpha")),
                            "pass": bool(payload.get("Pass")),
                            "prompt": prompt,
                            "reply": {
                                "bias": bias,
                                "confidence": int(payload.get("Confidence") or 0),
                                "rr_ok": bool(payload.get("RrOk")),
                                "reasons": [str(r) for r in
                                            (payload.get("Reasons") or [])[:3]],
                            },
                        })
                elif cat == "FX_ORDER" and ("fill" in details or "ticket " in details):
                    m_ticket = re.search(r"ticket (\d+)", text)
                    if m_ticket:
                        payload = payload or {}
                        fills[int(m_ticket.group(1))] = {
                            "ts": ts,
                            "symbol": str(payload.get("Symbol") or
                                          (re.search(r" lots (\S+) @", text).group(1)
                                           if re.search(r" lots (\S+) @", text) else "?")),
                            "alpha": _norm_family(payload.get("Signal")),
                        }
                elif cat == "FX_EXIT" and "closed #" in text:
                    m_ticket = re.search(r"closed #(\d+)", text)
                    ticket = int(m_ticket.group(1)) if m_ticket else 0
                    outcome = None
                    if isinstance(payload, dict):
                        rr = payload.get("RealizedR")
                        if isinstance(rr, (int, float)) and not isinstance(rr, bool):
                            outcome = float(rr)
                    if outcome is None and ticket in last_r:
                        outcome = last_r.pop(ticket)
                    closes.append({"ticket": ticket, "ts": ts, "outcome": outcome})
                    last_r.pop(ticket, None)
                elif isinstance(payload, dict):
                    t = payload.get("Ticket")
                    if isinstance(t, (int, float)) and not isinstance(t, bool):
                        for key in ("ProfitR", "CurrentR"):
                            v = payload.get(key)
                            if isinstance(v, (int, float)) and not isinstance(v, bool):
                                last_r[int(t)] = float(v)
                                break

    return assessments, fills, closes


def pair_outcomes(assessments, fills, closes, max_gap_hours=6.0):
    """Join each CONCURRING assessment to the trade it judged and that
    trade's settled outcome. Vetoed or unmatched assessments are counted,
    not guessed."""
    closes_by_ticket = {}
    for c in closes:
        closes_by_ticket.setdefault(c["ticket"], []).append(c)

    pairs, dropped_vetoed, dropped_unmatched, dropped_unresolved = [], 0, 0, 0
    for a in assessments:
        if not a["pass"]:
            dropped_vetoed += 1
            continue
        best = None
        gap = timedelta(hours=max_gap_hours)
        for ticket, fill in fills.items():
            if fill["symbol"] != a["symbol"] or fill["alpha"] != a["alpha"]:
                continue
            delta = fill["ts"] - a["ts"]
            if delta.total_seconds() < 0 or delta > gap:
                continue
            if best is None or delta < best[1]:
                best = (ticket, delta)
        if best is None:
            dropped_unmatched += 1
            continue
        ticket = best[0]
        outcome = None
        for c in closes_by_ticket.get(ticket, []):
            if c["ts"] >= fills[ticket]["ts"] and c["outcome"] is not None:
                outcome = c["outcome"]
                break
        if outcome is None:
            dropped_unresolved += 1
            continue
        pairs.append({**a, "ticket": ticket, "realized_r": outcome})

    stats = {
        "assessments": len(assessments),
        "paired": len(pairs),
        "dropped_vetoed": dropped_vetoed,
        "dropped_unmatched": dropped_unmatched,
        "dropped_unresolved": dropped_unresolved,
    }
    return pairs, stats


def label(pair):
    """The assistant row for one pair: the model's own reply, corrected
    only when the outcome is unambiguous evidence against it."""
    reply = dict(pair["reply"])
    r = pair["realized_r"]
    if r >= KEEP_R:
        return reply, "concur-correct"
    if r <= STOP_R:
        # The concurring trade stopped out: not concurring would have been
        # the better call — teach flat with conviction, honest reasons.
        return {
            "bias": "flat",
            "confidence": 65,
            "rr_ok": False,
            "reasons": ["trade stopped out", "edge was not there"],
        }, "concur-corrected-flat"
    return reply, "borderline-kept"


def build_examples(pairs, system_prompt):
    rows = []
    for p in pairs:
        assistant, tag = label(p)
        rows.append({
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": p["prompt"]},
                {"role": "assistant",
                 "content": json.dumps(assistant, ensure_ascii=False)},
            ],
            "metadata": {
                "symbol": p["symbol"],
                "alpha": p["alpha"],
                "realized_r": p["realized_r"],
                "label": tag,
            },
        })
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--data-dir", default=DATA_DIR,
                    help="journal directory (default: %(default)s)")
    ap.add_argument("--out", default=os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "ai_alpha",
        "gwen_finetune_dataset.jsonl"),
        help="output JSONL path")
    ap.add_argument("--max-gap", type=float, default=6.0,
                    help="max hours between assessment and fill to pair them")
    ap.add_argument("--min-examples", type=int, default=1,
                    help="exit 3 when the dataset has fewer rows")
    args = ap.parse_args(argv)

    try:
        system_prompt = load_system_prompt()
    except OSError as exc:
        print(f"gwen-finetune: cannot read system prompt: {exc}", file=sys.stderr)
        return 1

    assessments, fills, closes = load_journal(args.data_dir)
    pairs, stats = pair_outcomes(assessments, fills, closes, args.max_gap)
    rows = build_examples(pairs, system_prompt)

    try:
        os.makedirs(os.path.dirname(args.out), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as fh:
            for row in rows:
                fh.write(json.dumps(row, ensure_ascii=False) + "\n")
    except OSError as exc:
        print(f"gwen-finetune: cannot write {args.out}: {exc}", file=sys.stderr)
        return 1

    corrected = sum(1 for r in rows if r["metadata"]["label"] == "concur-corrected-flat")
    borderline = sum(1 for r in rows if r["metadata"]["label"] == "borderline-kept")
    print(f"gwen-finetune: {len(rows)} example(s) -> {args.out}")
    print(f"  assessments={stats['assessments']} paired={stats['paired']} "
          f"vetoed-dropped={stats['dropped_vetoed']} "
          f"unmatched-dropped={stats['dropped_unmatched']} "
          f"unresolved-dropped={stats['dropped_unresolved']}")
    print(f"  labels: correct={len(rows) - corrected - borderline} "
          f"corrected-flat={corrected} borderline-kept={borderline}")
    if not rows:
        print("  (no labeled pairs yet — run the GWEN gate in paper first; "
              "vetoed signals and open trades are never guessed)")

    return 0 if len(rows) >= args.min_examples else 3


if __name__ == "__main__":
    sys.exit(main())
