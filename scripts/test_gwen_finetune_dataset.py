#!/usr/bin/env python3
"""Behavioural tests for scripts/gwen_finetune_dataset.py.

Synthetic journal in a temp dir in, labelled SFT rows out: pairing rules,
the two label corrections, and every drop path (vetoed, unmatched,
unresolved) — because a fine-tuning set built from a trading journal is
only worth building if it never guesses.

Run: python scripts/test_gwen_finetune_dataset.py   (exit 0 = pass)
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import gwen_finetune_dataset as g  # noqa: E402

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")


def row(category: str, details: str, ts: str) -> str:
    return json.dumps({"Category": category, "Details": details, "Timestamp": ts}) + "\n"


def build_journal(tmp: Path) -> None:
    """Four assessments and the trades they judged:

    A  concurred, EURUSD breakout(20), fill #42 closed +1.8R -> kept as-is
    B  vetoed (Pass=false)                                  -> dropped
    C  concurred, fill #43 closed -1.2R                      -> corrected to flat
    D  concurred, fill #44 closed WITHOUT a resolvable R    -> dropped
    E  concurred, but the only later same-symbol fill is a
       different alpha (breakout vs meanrev)                -> dropped
    """
    prompt = "GWEN confluence check - DON G FX desk.\nSNAPSHOT symbol=EURUSD ..."
    lines: list[str] = []

    # A: concurring verdict + fill + winning close.
    lines.append(row("FX_GWEN", f"EURUSD: GWEN concurs - long 80 {json.dumps({'Symbol': 'EURUSD', 'Side': 'buy', 'Alpha': 'breakout(20)', 'Bias': 'long', 'Confidence': 80, 'RrOk': True, 'Reasons': ['trend aligned'], 'LatencyMs': 900.0, 'Pass': True, 'Prompt': prompt})}",
                     "2026-10-22T10:00:00+00:00"))
    lines.append(row("FX_ORDER", f"paper-exec fill (demo): buy 0.1 lots EURUSD @ 1.08000 - ticket 42 {json.dumps({'Symbol': 'EURUSD', 'Signal': 'breakout(20)', 'Side': 'buy', 'Lots': 0.1})}",
                     "2026-10-22T10:01:00+00:00"))
    lines.append(row("FX_EXIT", f"EURUSD #42: closed #42 in profit {json.dumps({'Ticket': 42, 'RealizedR': 1.8})}",
                     "2026-10-22T10:30:00+00:00"))

    # B: vetoed assessment — never dispatched from that cycle.
    lines.append(row("FX_GWEN", f"EURUSD: GWEN confluence veto - flat 62 {json.dumps({'Symbol': 'EURUSD', 'Side': 'buy', 'Alpha': 'breakout(20)', 'Bias': 'flat', 'Confidence': 62, 'RrOk': False, 'Reasons': ['chop'], 'LatencyMs': 800.0, 'Pass': False, 'Prompt': prompt})}",
                     "2026-10-22T11:00:00+00:00"))

    # C: concurring verdict + fill + stopped-out close -> corrected.
    lines.append(row("FX_GWEN", f"EURUSD: GWEN concurs - long 71 {json.dumps({'Symbol': 'EURUSD', 'Side': 'buy', 'Alpha': 'breakout(20)', 'Bias': 'long', 'Confidence': 71, 'RrOk': True, 'Reasons': ['hh break'], 'LatencyMs': 700.0, 'Pass': True, 'Prompt': prompt})}",
                     "2026-10-22T12:00:00+00:00"))
    lines.append(row("FX_ORDER", f"paper-exec fill (demo): buy 0.1 lots EURUSD @ 1.08100 - ticket 43 {json.dumps({'Symbol': 'EURUSD', 'Signal': 'breakout(20)', 'Side': 'buy', 'Lots': 0.1})}",
                     "2026-10-22T12:01:00+00:00"))
    lines.append(row("FX_EXIT", f"EURUSD #43: closed #43 by stop {json.dumps({'Ticket': 43, 'RealizedR': -1.2})}",
                     "2026-10-22T12:40:00+00:00"))

    # D: fill exists but the close carries no R anywhere -> unresolved.
    lines.append(row("FX_GWEN", f"EURUSD: GWEN concurs - long 75 {json.dumps({'Symbol': 'EURUSD', 'Side': 'buy', 'Alpha': 'breakout(20)', 'Bias': 'long', 'Confidence': 75, 'RrOk': True, 'Reasons': ['aligned'], 'LatencyMs': 650.0, 'Pass': True, 'Prompt': prompt})}",
                     "2026-10-22T13:00:00+00:00"))
    lines.append(row("FX_ORDER", f"paper-exec fill (demo): buy 0.1 lots EURUSD @ 1.08200 - ticket 44 {json.dumps({'Symbol': 'EURUSD', 'Signal': 'breakout(20)', 'Side': 'buy', 'Lots': 0.1})}",
                     "2026-10-22T13:01:00+00:00"))
    lines.append(row("FX_EXIT", f"EURUSD #44: closed #44 {json.dumps({'Ticket': 44})}",
                     "2026-10-22T13:30:00+00:00"))

    # E: same symbol, different alpha later — must not pair.
    lines.append(row("FX_GWEN", f"EURUSD: GWEN concurs - long 66 {json.dumps({'Symbol': 'EURUSD', 'Side': 'buy', 'Alpha': 'meanrev(9)', 'Bias': 'long', 'Confidence': 66, 'RrOk': True, 'Reasons': ['range edge'], 'LatencyMs': 640.0, 'Pass': True, 'Prompt': prompt})}",
                     "2026-10-22T14:00:00+00:00"))
    lines.append(row("FX_ORDER", f"paper-exec fill (demo): buy 0.1 lots EURUSD @ 1.08300 - ticket 45 {json.dumps({'Symbol': 'EURUSD', 'Signal': 'breakout(20)', 'Side': 'buy', 'Lots': 0.1})}",
                     "2026-10-22T14:05:00+00:00"))
    lines.append(row("FX_EXIT", f"EURUSD #45: closed #45 in profit {json.dumps({'Ticket': 45, 'RealizedR': 0.9})}",
                     "2026-10-22T14:30:00+00:00"))

    # Noise the loader must ignore: an availability row (no Prompt/Bias).
    lines.append(row("FX_GWEN", json.dumps({"Symbol": "EURUSD", "Unavailable": True}),
                     "2026-10-22T09:00:00+00:00"))

    (tmp / "journal_2026-10-22.jsonl").write_text("".join(lines), encoding="utf-8")


def main() -> int:
    system_prompt = g.load_system_prompt()
    check("system prompt file loads and is non-empty", bool(system_prompt))
    check("system prompt is newline-stripped (matches registration)",
          not system_prompt.endswith("\n"))

    with tempfile.TemporaryDirectory(prefix="dg-gwen-sft-") as tmp_str:
        tmp = Path(tmp_str)
        build_journal(tmp)

        assessments, fills, closes = g.load_journal(str(tmp))
        check("five assessable verdicts (unavailable row skipped)",
              len(assessments) == 5)
        check("four brain fills found", len(fills) == 4)
        check("four closes found", len(closes) == 4)

        pairs, stats = g.pair_outcomes(assessments, fills, closes)
        check("exactly A and C pair (D unresolved, B vetoed, E alpha-mismatch)",
              stats["paired"] == 2)
        check("vetoed assessment dropped, never guessed",
              stats["dropped_vetoed"] == 1)
        check("unresolved close dropped (no fabricated outcome)",
              stats["dropped_unresolved"] == 1)
        check("alpha mismatch dropped", stats["dropped_unmatched"] == 1)

        rows = g.build_examples(pairs, system_prompt)
        check("two SFT rows emitted", len(rows) == 2)
        labels = sorted(r["metadata"]["label"] for r in rows)
        check("winner kept as concur-correct",
              "concur-correct" in labels and "concur-corrected-flat" in labels)

        by_label = {r["metadata"]["label"]: r for r in rows}
        kept = by_label["concur-correct"]
        kept_reply = json.loads(kept["messages"][2]["content"])
        check("winning reply passes through unedited",
              kept_reply["bias"] == "long" and kept_reply["confidence"] == 80)
        check("row shape is system/user/assistant",
              [m["role"] for m in kept["messages"]] == ["system", "user", "assistant"])
        check("system message is the shared prompt",
              kept["messages"][0]["content"] == system_prompt)
        check("user message is the verbatim journaled prompt",
              kept["messages"][1]["content"].startswith("GWEN confluence check"))
        check("metadata carries the realized R",
              kept["metadata"]["realized_r"] == 1.8)

        fixed = by_label["concur-corrected-flat"]
        fixed_reply = json.loads(fixed["messages"][2]["content"])
        check("stopped-out concur rewritten to flat",
              fixed_reply["bias"] == "flat" and fixed_reply["rr_ok"] is False)
        check("correction keeps the JSON contract (confidence + reasons)",
              0 <= fixed_reply["confidence"] <= 100
              and 1 <= len(fixed_reply["reasons"]) <= 3)
        check("corrected row keeps its outcome for audit",
              fixed["metadata"]["realized_r"] == -1.2)

        # Round trip through main(): file written, exit 0 with min-examples=1.
        out = tmp / "out" / "dataset.jsonl"
        rc = g.main(["--data-dir", str(tmp), "--out", str(out),
                     "--min-examples", "1"])
        check("main() exits 0", rc == 0)
        written = [json.loads(l) for l in out.read_text(encoding="utf-8").splitlines() if l]
        check("written JSONL parses back to the same rows", len(written) == len(rows))
        json.loads(written[0]["messages"][2]["content"])
        check("assistant content is valid JSON", True)

        # min-examples gates an empty dataset (exit 3), never a crash.
        empty = tmp / "empty-journal"
        empty.mkdir()
        rc_empty = g.main(["--data-dir", str(empty), "--out", str(out),
                           "--min-examples", "1"])
        check("empty journal exits 3 under --min-examples 1", rc_empty == 3)

    check("dataset builder has no third-party imports",
          "import openai" not in (HERE / "gwen_finetune_dataset.py").read_text(encoding="utf-8"))
    check("dataset and registration share one system prompt file",
          (HERE / "gwen_system_prompt.txt").exists()
          and (HERE / "register-gwen-model.ps1").exists()
          and "gwen_system_prompt.txt" in
          (HERE / "register-gwen-model.ps1").read_text(encoding="utf-8", errors="replace"))

    failed = [name for name, ok in results if not ok]
    print(f"{'FAIL' if failed else 'PASS'}: "
          f"{len(results) - len(failed)}/{len(results)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
