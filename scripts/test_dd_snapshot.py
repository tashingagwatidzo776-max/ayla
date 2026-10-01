#!/usr/bin/env python3
"""Snapshot tests: dd_weight_backtest.py against FROZEN real journal days.

The fixtures are byte-faithful samples of the live journals, frozen by
scripts/freeze_journal_sample.py (regenerate with care: the guard below
pins the exact bytes, so a re-freeze must move the pins in the same
reviewed commit):

  - journal-frozen-20260929.jsonl — 176 rows (the 4,169-FX_EXIT day:
    all override rows, all nonzero-drawdown rows, 1-in-50 background,
    both guard-save floor rows)
  - journal-frozen-20260930.jsonl — 131 rows (same selection on the
    save day: 4 profit-floor overrides, 120 resolver evaluations)

The contract: the pinned expectations below reproduce EXACTLY while the
FX_EXIT row schema holds. When a future schema change in the wild stops
reproducing them — score fields renamed, weights moved, votes reshaped —
these tests fail loudly instead of letting the flip counts silently
skew. Three layers:

  1. Frozen-content guard: each fixture's NEWLINE-NORMALIZED sha256
     must match the pin. The normalization (CRLF -> LF before hashing)
     matters: git's autocrlf may rewrite LF fixtures to CRLF on
     checkout, and an end-line-sensitive hash would then reject the
     very bytes it pinned (this shipped in PR #151 and was caught the
     next day — the 09-30 pin value is unchanged from #151's raw-bytes
     pin because that file's original bytes were pure LF).
  2. Pinned counts, two-day combined: 286 resolver evaluations, 14
     advisory flips across 7 tickets, 0 exit-boundary flips, 13
     override rows (9 drawdown + 4 profit-floor), 0 mismatches, both
     guard-save tickets seen, the full drawdown Exit distribution.
  3. Drift drill: a simulated Score-field rename trips the mismatch
     probe (235 mismatches, 0 fabricated flips) instead of lying.

Run: python scripts/test_dd_snapshot.py   (exit 0 = all pass)
"""
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import shutil
import tempfile
from contextlib import redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
# (day, newline-normalized sha256) — see layer 1 in the docstring.
FIXTURES = [
    ("20260929", "ba6d94e6bb9c32424c785e9776fa896286e04a86effeceb1dde19aeff9695847"),
    ("20260930", "ba939885643908b3cc11e9e5e8f5d0dc43113d5304ce99eeed6715f98e4b431d"),
]


def _load_module():
    spec = importlib.util.spec_from_file_location(
        "dd_weight_backtest", HERE / "dd_weight_backtest.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


MOD = _load_module()


def normalized_sha256(path: Path) -> str:
    raw = path.read_bytes()
    return hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()


def fixture_path(day: str) -> Path:
    return HERE / "fixtures" / f"journal-frozen-{day}.jsonl"


def run_on_fixtures(days: list[str], drift: bool = False) -> str:
    """Copy the frozen fixtures into a temp journal dir (optionally
    drift: rename the Score field via decode/rename/re-serialize — no
    hand-written escapes) and run the backtest there."""
    with tempfile.TemporaryDirectory() as tmp:
        jdir = Path(tmp) / "tf" / "data" / "journal"
        jdir.mkdir(parents=True)
        for day in days:
            src = fixture_path(day)
            if not drift:
                shutil.copy(src, jdir / f"journal_{day}.jsonl")
                continue
            lines = []
            for line in src.read_text(encoding="utf-8").splitlines():
                if not line.strip():
                    continue
                row = json.loads(line)
                details = row.get("Details", "")
                brace = details.find("{")
                if brace >= 0:
                    try:
                        dec = json.loads(details[brace:])
                        if isinstance(dec, dict) and "Score" in dec:
                            dec["EnsembleScore"] = dec.pop("Score")
                            head = details[:brace].rstrip().rstrip(":").strip()
                            row["Details"] = f"{head}: " + json.dumps(dec)
                    except json.JSONDecodeError:
                        pass
                lines.append(json.dumps(row, separators=(",", ":")))
            (jdir / f"journal_{day}.jsonl").write_text(
                "\n".join(lines) + "\n", encoding="utf-8")

        old = os.environ.get("APPDATA")
        os.environ["APPDATA"] = tmp
        buf = io.StringIO()
        try:
            with redirect_stdout(buf):
                rc = MOD.main()
        finally:
            if old is None:
                os.environ.pop("APPDATA", None)
            else:
                os.environ["APPDATA"] = old
        assert rc == 0, rc
    return buf.getvalue()


def test_fixtures_are_frozen():
    for day, sha in FIXTURES:
        digest = normalized_sha256(fixture_path(day))
        assert digest == sha, (
            f"fixture journal-frozen-{day}.jsonl bytes changed (sha256 "
            f"{digest}). The pins in test_snapshot_expectations were "
            "derived from specific frozen bytes — re-freeze and update "
            "BOTH together in one reviewed commit, or revert the fixture.")
    print(f"PASS {len(FIXTURES)} frozen fixtures match their sha256 pins "
          "(newline-normalized: autocrlf checkouts stay valid)")


def test_snapshot_expectations_two_day():
    text = run_on_fixtures([day for day, _ in FIXTURES])
    # Per-file and total rows (pinned from the two frozen days).
    assert "journal_20260929.jsonl           166      11          9" in text
    assert "journal_20260930.jsonl           120       3          4" in text
    assert "TOTAL                            286      14         13" in text
    assert "decision flips 2.0 -> 1.0: 14 of 286 (4.90%)" in text
    # The headline contract: no exit-band decision ever flipped.
    assert "EXIT-boundary flips (touch partial/full): 0 across 0 tickets" in text
    assert "advisory-only flips (hold/monitor/tighten): 14 across 7 tickets" in text
    assert "#9820712902: 2 rows, hold->monitor GUARD-SAVE" in text
    assert "#9820719898: 2 rows, hold->monitor GUARD-SAVE" in text
    # Overrides replay unchanged; integrity probes clean.
    assert "override-tier rows (replayed unchanged): 13" in text
    assert "drawdown: 9" in text
    assert "profit-floor: 4" in text
    assert "score-reproduction mismatches (schema drift probe): 0" in text
    assert "(skipped 1 unparseable / non-roster / mismatched rows)" in text
    assert "guard-save tickets seen (FX_FLOOR layer): [9820712902, 9820719898]" in text
    # The drawdown Exit distribution pins the vote shape itself.
    assert "exit=  0.0: 112" in text
    assert "exit= 0.65: 81" in text
    assert "exit=  1.0: 34" in text
    print("PASS two-day snapshot expectations reproduce exactly on frozen bytes")


def test_schema_drift_fails_loudly():
    text = run_on_fixtures([day for day, _ in FIXTURES], drift=True)
    # The drifted signature, pinned: 222 resolver rows mismatch-skip
    # (logged Score unreadable -> 0.0 default vs the replayed real
    # score), the 13 override rows add their own mismatches (logged 0.0
    # != 100), 64 rows whose true logged score was ~0 still pass the
    # equality probe, and the flip count collapses to 0 of 64 instead
    # of fabricating flips from garbage.
    assert "score-reproduction mismatches (schema drift probe): 235" in text
    assert "decision flips 2.0 -> 1.0: 0 of 64 (0.00%)" in text
    assert "(skipped 223 unparseable / non-roster / mismatched rows)" in text
    print("PASS schema drift fails loudly via the mismatch probe (two-day)")


if __name__ == "__main__":
    test_fixtures_are_frozen()
    test_snapshot_expectations_two_day()
    test_schema_drift_fails_loudly()
    print("ALL PASS")
