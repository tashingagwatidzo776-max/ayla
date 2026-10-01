#!/usr/bin/env python3
"""Snapshot test: dd_weight_backtest.py against a FROZEN real journal day.

The fixture scripts/fixtures/journal-frozen-20260930.jsonl is a
byte-faithful sample of the live 2026-09-30 journal (override rows,
nonzero-drawdown rows, a 1-in-50 background sample, guard-save floor
rows — 131 rows; regenerate with scripts/freeze_journal_sample.py and
NEVER casually: its whole value is being frozen).

The contract: the pinned expectations below reproduce EXACTLY while the
FX_EXIT row schema holds. When a future schema change in the wild stops
reproducing them — score fields renamed, weights moved, votes reshaped —
this test fails loudly instead of letting the flip counts silently
skew. Two layers:

  1. Pinned counts: 120 resolver evaluations, 4 override rows (all
     profit-floor), 3 advisory flips across 2 guard-save tickets, 0
     exit-boundary flips, 0 score-reproduction mismatches, both
     guard-save tickets seen.
  2. Frozen-byte guard: the fixture's sha256 must stay
     ba939885643908b3cc11e9e5e8f5d0dc43113d5304ce99eeed6715f98e4b431d —
     the pins are only meaningful against the exact frozen bytes. If the
     fixture legitimately needs re-freezing (a deliberately regenerated
     sample), the hash pin and the expectation pins must move TOGETHER
     in one reviewed commit.

Run: python scripts/test_dd_snapshot.py   (exit 0 = all pass)
"""
from __future__ import annotations

import hashlib
import importlib.util
import io
import os
import shutil
import tempfile
from contextlib import redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURE = HERE / "fixtures" / "journal-frozen-20260930.jsonl"
FIXTURE_SHA256 = "ba939885643908b3cc11e9e5e8f5d0dc43113d5304ce99eeed6715f98e4b431d"


def _load_module():
    spec = importlib.util.spec_from_file_location(
        "dd_weight_backtest", HERE / "dd_weight_backtest.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


MOD = _load_module()


def run_on_fixture() -> str:
    with tempfile.TemporaryDirectory() as tmp:
        jdir = Path(tmp) / "tf" / "data" / "journal"
        jdir.mkdir(parents=True)
        shutil.copy(FIXTURE, jdir / "journal_20260930.jsonl")
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


def test_fixture_is_frozen():
    digest = hashlib.sha256(FIXTURE.read_bytes()).hexdigest()
    assert digest == FIXTURE_SHA256, (
        f"fixture bytes changed (sha256 {digest}). The pins in "
        "test_snapshot_expectations were derived from specific frozen "
        "bytes — re-freeze and update BOTH together in one reviewed "
        "commit, or revert the fixture.")
    print("PASS fixture bytes match the frozen sha256")


def test_snapshot_expectations():
    text = run_on_fixture()
    # Pinned from the frozen 2026-09-30 sample (see module docstring).
    assert "journal_20260930.jsonl           120       3          4" in text
    assert "decision flips 2.0 -> 1.0: 3 of 120 (2.50%)" in text
    assert "EXIT-boundary flips (touch partial/full): 0 across 0 tickets" in text
    assert "advisory-only flips (hold/monitor/tighten): 3 across 2 tickets" in text
    assert "#9820712902: 2 rows, hold->monitor GUARD-SAVE" in text
    assert "#9820719898: 1 rows, hold->monitor GUARD-SAVE" in text
    assert "override-tier rows (replayed unchanged): 4" in text
    assert "profit-floor: 4" in text
    assert "score-reproduction mismatches (schema drift probe): 0" in text
    assert "guard-save tickets seen (FX_FLOOR layer): [9820712902, 9820719898]" in text
    assert "drawdown vote Exit distribution" in text
    assert "exit=  0.0: 39" in text
    assert "exit= 0.65: 81" in text
    print("PASS snapshot expectations reproduce exactly on frozen bytes")


def test_schema_drift_fails_loudly():
    """A future FX_EXIT schema change must trip the pins, not pass."""
    with tempfile.TemporaryDirectory() as tmp:
        # Simulate the app renaming its Score field (a plausible schema
        # evolution) WITHOUT hand-writing escapes: decode each row with
        # json.loads, rename the key inside the decoded Details payload,
        # re-serialize with json.dumps. Weight drift is caught even
        # earlier — by the roster guard (rows skip as non-roster) — so
        # the score rename is the clean mismatch-probe demonstration.
        import json as _json
        drifted_lines = []
        for line in FIXTURE.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            row = _json.loads(line)
            details = row.get("Details", "")
            brace = details.find("{")
            if brace >= 0:
                try:
                    dec = _json.loads(details[brace:])
                    if isinstance(dec, dict) and "Score" in dec:
                        dec["EnsembleScore"] = dec.pop("Score")
                        head = details[:brace].rstrip().rstrip(":").strip()
                        row["Details"] = f"{head}: " + _json.dumps(dec)
                except _json.JSONDecodeError:
                    pass
            drifted_lines.append(_json.dumps(row, separators=(",", ":")))
        drifted = Path(tmp) / "drifted.jsonl"
        drifted.write_text("\n".join(drifted_lines) + "\n", encoding="utf-8")
        jdir = Path(tmp) / "tf" / "data" / "journal"
        jdir.mkdir(parents=True)
        shutil.copy(drifted, jdir / "journal_20260930.jsonl")
        old = os.environ.get("APPDATA")
        os.environ["APPDATA"] = tmp
        buf = io.StringIO()
        try:
            with redirect_stdout(buf):
                MOD.main()
        finally:
            if old is None:
                os.environ.pop("APPDATA", None)
            else:
                os.environ["APPDATA"] = old
        text = buf.getvalue()
    # The drifted signature, pinned: 96 resolver rows mismatch-skip
    # (logged Score unreadable -> 0.0 default vs the replayed real
    # score), the 4 override rows add their own mismatches (logged 0.0
    # != 100), 24 rows whose true logged score was ~0 still pass the
    # equality probe, and the flip count collapses to 0 of 24 instead
    # of fabricating flips from garbage.
    assert "score-reproduction mismatches (schema drift probe): 100" in text
    assert "decision flips 2.0 -> 1.0: 0 of 24 (0.00%)" in text
    assert "(skipped 96 unparseable / non-roster / mismatched rows)" in text
    print("PASS schema drift fails loudly via the mismatch probe")


if __name__ == "__main__":
    test_fixture_is_frozen()
    test_snapshot_expectations()
    test_schema_drift_fails_loudly()
    print("ALL PASS")
