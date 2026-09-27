#!/usr/bin/env python3
"""Unit tests for scripts/soak_evening.py.

Covers the pieces that must never corrupt the evidence record: sections
append once and only once (idempotent per day), heading matching ignores
prose mentions (a real corruption bug this suite pins), --force replaces
in place, the narrative lands verbatim, and alpha reports are keyed per
(proposal, bars day, symbol) so reruns measure new data instead of
duplicating old verdicts. The backtest keying test runs the REAL
deterministic backtester over a generated CSV - no network, no LLM, no
app.

Run:  python scripts/test_soak_evening.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import pathlib
import sys
import tempfile
from datetime import datetime
from types import SimpleNamespace

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "ai_alpha"))

import soak_evening  # noqa: E402


# ── evidence append: once per section, verbatim body ──────────────────

def test_append_section_writes_title_and_body():
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        doc.write_text("# Demo soak report\n\n- existing\n", encoding="utf-8")
        assert soak_evening.append_section(doc, "AI analyst section", "line one\nline two")
        text = doc.read_text(encoding="utf-8")
        assert "## AI analyst section" in text
        assert "line one\nline two" in text
        assert text.startswith("# Demo soak report")   # original intact


def test_append_section_is_idempotent():
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        assert soak_evening.append_section(doc, "Alpha rerun", "- run A")
        assert not soak_evening.append_section(doc, "Alpha rerun", "- run B")
        text = doc.read_text(encoding="utf-8")
        assert text.count("## Alpha rerun") == 1 and "- run A" in text and "- run B" not in text


def test_append_section_ignores_prose_mentions_of_the_title():
    """The evening notes say 'First live AI analyst narrative captured...'
    in prose - that mention must NOT suppress the section append (the
    original substring match corrupted exactly this document)."""
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        doc.write_text("# Report\n\n- First live AI analyst narrative captured through"
                       " the production service.\n", encoding="utf-8")
        assert soak_evening.append_section(doc, "AI analyst narrative", "- body")
        text = doc.read_text(encoding="utf-8")
        assert text.count("## AI analyst narrative") == 1
        assert "First live AI analyst narrative captured" in text   # prose intact


def test_replace_section_moves_multiline_body_and_keeps_neighbors():
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        doc.write_text("head\n\n## AI analyst narrative\n\n- old line 1\n- old line 2\n"
                       "- old line 3\n\n## Alpha rerun\n\n- run X\n", encoding="utf-8")
        assert soak_evening.replace_section(
            doc, "AI analyst narrative", "- new body\nmore of the body")
        text = doc.read_text(encoding="utf-8")
        assert text.startswith("head\n")
        assert "- old line" not in text
        assert "- new body\nmore of the body" in text
        assert text.count("## AI analyst narrative") == 1
        assert "## Alpha rerun\n\n- run X" in text          # neighbor intact


def test_replace_section_never_matches_prose():
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        original = ("# Report\n\n- notes mention AI analyst narrative in prose\n"
                    "\n## Alpha rerun\n\n- run X\n")
        doc.write_text(original, encoding="utf-8")
        assert not soak_evening.replace_section(doc, "AI analyst narrative", "- new")
        assert doc.read_text(encoding="utf-8") == original   # untouched


def test_replace_section_missing_heading_returns_false():
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        doc.write_text("# Report\n", encoding="utf-8")
        assert not soak_evening.replace_section(doc, "AI analyst narrative", "- new")


def test_narrative_lands_verbatim_with_emoji_and_quotes():
    with tempfile.TemporaryDirectory() as tmp:
        doc = pathlib.Path(tmp) / "SOAK-test.md"
        narrative = "\U0001F916 The session \"saw\" 56 settled - 45W/11L."
        assert soak_evening.append_section(doc, "AI analyst narrative",
                                           soak_evening.analyst_section(narrative))
        text = doc.read_text(encoding="utf-8")
        assert narrative in text                     # verbatim, emoji intact
        assert "> " + narrative in text              # quoted block


def test_today_soak_path_uses_the_date():
    path = soak_evening.today_soak_path(datetime(2026, 9, 26, 21, 30))
    assert path.name == "SOAK-2026-09-26.md" and path.parent == soak_evening.SOAK_DIR


# ── analyst extraction (drive the regex through run_analyst) ─────────

def _with_fake_exe_and_smoke_output(smoke_out: str):
    """Run run_analyst with a stub 'built' exe whose 'run' prints smoke_out.

    The exe stub must EXIST so run_analyst skips its dotnet-build branch;
    subprocess.run is stubbed to capture the command and print the smoke
    output (the parser reads stdout+stderr like the real AnalystSmoke).
    """
    with tempfile.TemporaryDirectory() as tmp:
        exe = pathlib.Path(tmp) / "AnalystSmoke.exe"
        exe.write_text("stub", encoding="utf-8")
        calls = []

        def fake_run(cmd, **kw):
            calls.append(cmd)
            return SimpleNamespace(returncode=0, stdout=smoke_out, stderr="")

        import subprocess as sp
        orig_run, orig_exe = sp.run, soak_evening.ANALYST_EXE
        soak_evening.ANALYST_EXE = exe
        try:
            sp.run = fake_run  # type: ignore[assignment]
            result = soak_evening.run_analyst("qwen3:test")
        finally:
            sp.run = orig_run  # type: ignore[assignment]
            soak_evening.ANALYST_EXE = orig_exe
        # calls[0] is the ollama warm-up; the exe run is the last call
        assert calls and calls[-1][0] == str(exe), calls
        return result


def test_run_analyst_parses_smoke_output():
    smoke = ('[log] analyst LLM ok in 9000 ms\n=== NARRATIVE START ===\n'
             '\U0001F916 test narrative body\n=== NARRATIVE END ===\n')
    narrative, note = _with_fake_exe_and_smoke_output(smoke)
    assert narrative == "\U0001F916 test narrative body", (narrative, note)
    assert note == "ok"


def test_run_analyst_reports_nothing_to_say():
    smoke = ('=== NARRATIVE START ===\n'
             '(nothing to say - empty journal or toggle off)\n'
             '=== NARRATIVE END ===\n')
    narrative, note = _with_fake_exe_and_smoke_output(smoke)
    assert narrative is None and "nothing to say" in note


# ── alpha report keying: reruns test new data, not old verdicts ───────

def test_backtest_keys_report_per_proposal_bars_and_symbol():
    """Runs the REAL backtester on a generated CSV and checks the keyed
    report lands as PROP-x.<day>.<symbol>.report.json, not a duplicate of
    the plain report."""
    with tempfile.TemporaryDirectory() as tmp:
        root = pathlib.Path(tmp)
        bars = root / "XAUUSDmicro_20260926.csv"
        rows = ["ts,open,high,low,close"]
        price = 1.5
        for i in range(120):
            step = 0.0008 if i % 7 < 4 else -0.0006
            op = price
            cl = max(1e-4, op * (1 + step))
            rows.append(f"2026-09-26T{i // 60:02d}:{i % 60:02d},{op:.6f},"
                        f"{max(op, cl) * 1.0004:.6f},{min(op, cl) * 0.9996:.6f},{cl:.6f}")
            price = cl
        bars.write_text("\n".join(rows) + "\n", encoding="utf-8")

        proposal = root / "PROP-t.json"
        proposal.write_text(json.dumps({
            "name": "unit-test-donchian", "hypothesis": "test",
            "direction": "long_or_short",
            "entry": {"indicator": "donchian", "params": {"window": 10}},
            "exit": {"indicator": "fixed_bars", "params": {"bars": 6}},
            "risk": {"stop_bars": 2.0},
        }), encoding="utf-8")

        results = root / "results"
        report = soak_evening.backtest(proposal, bars, results)
        assert report is not None and report["trades"] >= 1, report
        keyed = results / "PROP-t.20260926.XAUUSDmicro.report.json"
        assert keyed.exists(), sorted(p.name for p in results.glob("*"))
        # the plain (unkeyed) report was consumed from the harness dir
        assert not (soak_evening.REPO / "scripts" / "ai_alpha" / "results"
                    / "PROP-t.report.json").exists()


def test_backtest_missing_report_returns_none():
    with tempfile.TemporaryDirectory() as tmp:
        assert soak_evening.backtest(
            pathlib.Path(tmp) / "nope.json",
            pathlib.Path(tmp) / "nope.csv",
            pathlib.Path(tmp) / "results") is None


# ── finding threshold: a 5-trade-bar-clearing verdict is called out ───

def test_run_alpha_surfaces_positive_verdict_as_finding():
    """A pre-planted keyed report with a positive verdict must surface as
    a finding naming the proposal, symbol and trade count."""
    with tempfile.TemporaryDirectory() as tmp:
        root = pathlib.Path(tmp)
        ai = root / "scripts" / "ai_alpha"
        (ai / "proposals").mkdir(parents=True)
        (ai / "results").mkdir()
        (ai / "proposals" / "PROP-good.json").write_text("{}", encoding="utf-8")
        day, sym = "20260926", "XAUUSDmicro"
        (ai / "results" / f"PROP-good.{day}.{sym}.report.json").write_text(json.dumps(
            {"name": "good", "hypothesis": "h", "trades": 12, "total_r": 4.2,
             "max_drawdown_r": -0.5,
             "expectancy_verdict": "positive expectancy - review for C# port"}),
            encoding="utf-8")
        csv = ai / "bars" / f"{sym}_{day}.csv"
        csv.parent.mkdir(parents=True, exist_ok=True)
        csv.write_text("ts,open,high,low,close\nt,1,1,1,1\n", encoding="utf-8")

        tick_dir = root / "ticks"
        tick_dir.mkdir()
        (tick_dir / f"{sym}_{day}.jsonl").write_text("", encoding="utf-8")  # symbol discovery
        real_convert, real_repo = soak_evening.convert_bars, soak_evening.REPO
        try:
            soak_evening.convert_bars = lambda s, d, o: csv  # type: ignore[assignment]
            soak_evening.REPO = root                         # type: ignore[assignment]
            lines, finding = soak_evening.run_alpha(tick_dir)
        finally:
            soak_evening.convert_bars = real_convert         # type: ignore[assignment]
            soak_evening.REPO = real_repo                    # type: ignore[assignment]

        assert finding, lines
        assert "PROP-good" in finding and sym in finding and "12 trades" in finding
        assert any("FINDING" in ln for ln in lines)


def test_run_alpha_no_ticks_no_crash():
    with tempfile.TemporaryDirectory() as tmp:
        lines, finding = soak_evening.run_alpha(pathlib.Path(tmp) / "absent")
        assert finding == "" and any("no tick archives" in ln for ln in lines)


def test_run_alpha_thin_tape_reported_not_failed():
    """Every symbol below the 50-bar floor is a skipped line, not an error."""
    with tempfile.TemporaryDirectory() as tmp:
        root = pathlib.Path(tmp)
        ai = root / "scripts" / "ai_alpha"
        (ai / "proposals").mkdir(parents=True)
        (ai / "results").mkdir()
        (ai / "proposals" / "PROP-x.json").write_text("{}", encoding="utf-8")
        tick_dir = root / "ticks"
        tick_dir.mkdir()
        (tick_dir / "XAUUSDmicro_20260926.jsonl").write_text("", encoding="utf-8")

        real_convert, real_repo = soak_evening.convert_bars, soak_evening.REPO
        try:
            soak_evening.convert_bars = lambda s, d, o: None  # type: ignore[assignment]
            soak_evening.REPO = root                          # type: ignore[assignment]
            lines, finding = soak_evening.run_alpha(tick_dir)
        finally:
            soak_evening.convert_bars = real_convert          # type: ignore[assignment]
            soak_evening.REPO = real_repo                     # type: ignore[assignment]

        assert finding == ""
        assert any("below the 50-bar floor" in ln for ln in lines)


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except Exception as e:  # noqa: BLE001
            failed += 1
            print(f"FAIL {t.__name__}: {type(e).__name__}: {e}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
