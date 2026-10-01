#!/usr/bin/env python3
"""Tests for scripts/audit_donggfx_tasks.py.

Verdict logic against stubbed task listings and stubbed git — no real
Task Scheduler, no real drift. Run:
python scripts/test_audit_donggfx_tasks.py   (exit 0 = all pass)
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location(
    "audit_donggfx_tasks", HERE / "audit_donggfx_tasks.py")
mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mod)

# Originals, saved before any test stubs them — a stale stub leaking into
# a later test (file_hash on a cleaned-up temp dir) fails it spuriously.
_REAL_HEAD_HASH = mod.head_hash
_REAL_LIST_TASKS = mod.list_tasks


def make_script(tmp, name="probe.py", body="print('x')\n") -> str:
    """Create <root>/scripts/<name> (the segment the task-path regex
    requires). Python 3.14's `with TemporaryDirectory() as t` yields the
    PATH STRING, not the object — accept both."""
    root = tmp.name if hasattr(tmp, "name") else tmp
    scripts = Path(root) / "scripts"
    scripts.mkdir(parents=True, exist_ok=True)
    p = scripts / name
    p.write_text(body, encoding="utf-8")
    return str(p)


def test_pythonw_blob_script_path_not_glued():
    """REGRESSION (first live run): the script regex must not swallow the
    interpreter into the path — 'pythonw.exe C:\\...py' extracts exactly
    the script, or a phantom 'missing on disk' finding appears."""
    blob = ('"C:\\Python314\\pythonw.exe" '
            '"C:\\Users\\DELL\\Desktop\\tf\\scripts\\daily_soak_evidence.py"')
    m = mod.SCRIPT_RE.search(blob)
    assert m is not None
    assert m.group(1).endswith("daily_soak_evidence.py")
    assert "pythonw" not in m.group(1)


def test_bare_python_is_a_finding():
    with tempfile.TemporaryDirectory() as tmp:
        script = make_script(tmp)
        v = mod.audit_task({"Name": "t", "Execute": "python.exe",
                            "Arguments": f'"{script}"'})
    assert any("bare python.exe" in f for f in v["findings"])


def test_cmd_wrapper_present_is_clean():
    with tempfile.TemporaryDirectory() as tmp:
        script = make_script(tmp)
        v = mod.audit_task({
            "Name": "t", "Execute": "cmd.exe",
            "Arguments": f'/c set PYTHONIOENCODING=utf-8&& "C:\\Py\\python.exe" "{script}"'})
    assert not v["findings"]
    assert any("wrapper present" in n for n in v["notes"])


def test_cmd_wrapper_without_encoding_is_a_finding():
    with tempfile.TemporaryDirectory() as tmp:
        script = make_script(tmp)
        v = mod.audit_task({"Name": "t", "Execute": "cmd.exe",
                            "Arguments": f'/c "C:\\Py\\python.exe" "{script}"'})
    assert any("without the encoding set" in f for f in v["findings"])


def test_pythonw_is_na_not_finding():
    with tempfile.TemporaryDirectory() as tmp:
        script = make_script(tmp)
        v = mod.audit_task({"Name": "t", "Execute": "pythonw.exe",
                            "Arguments": f'"{script}"'})
    assert not v["findings"]
    assert any("encoding N/A" in n for n in v["notes"])


def test_missing_script_is_a_finding():
    v = mod.audit_task({
        "Name": "t", "Execute": "cmd.exe",
        "Arguments": f'/c set PYTHONIOENCODING=utf-8&& '
                     f'"C:\\Py\\python.exe" "Z:\\nowhere\\scripts\\ghost.py"'})
    assert any("missing on disk" in f for f in v["findings"])


def test_drift_detection_matches_head(tmp_path=None):
    """local == HEAD -> note; differing bytes -> drift finding; untracked
    file -> note (a file git never saw cannot 'drift')."""
    with tempfile.TemporaryDirectory() as tmp:
        script = make_script(tmp)
        rel = os.path.relpath(script, mod.REPO)
        try:
            mod.head_hash = lambda r: mod.file_hash(script)   # same bytes
            v = mod.audit_task({"Name": "t", "Execute": "pythonw.exe",
                                "Arguments": f'"{script}"'})
            assert not v["findings"]
            assert any("matches git HEAD" in n for n in v["notes"])

            mod.head_hash = lambda r: "deadbeef"              # differs
            v = mod.audit_task({"Name": "t", "Execute": "pythonw.exe",
                                "Arguments": f'"{script}"'})
            assert any("drifted from git HEAD" in f for f in v["findings"])

            mod.head_hash = lambda r: None                    # untracked
            v = mod.audit_task({"Name": "t", "Execute": "pythonw.exe",
                                "Arguments": f'"{script}"'})
            assert not v["findings"]
            assert any("not tracked" in n for n in v["notes"])
        finally:
            mod.head_hash = _REAL_HEAD_HASH


def test_non_python_task_is_skipped():
    v = mod.audit_task({"Name": "t", "Execute": "powershell.exe",
                        "Arguments": "-File x.ps1"})
    assert not v["findings"]
    assert any("not a python task" in n for n in v["notes"])


def test_main_exit_codes():
    """0 on a clean fleet, 1 on findings, 2 when the probe itself dies.

    TF_DATA_DIR is pointed at a scratch dir for the whole test: main()
    records its verdict to the audit history, and a run without the env
    override would append to the REAL %APPDATA% history (live finding,
    2026-10-01)."""
    with tempfile.TemporaryDirectory() as scratch:
        os.environ["TF_DATA_DIR"] = scratch
        try:
            _test_main_exit_codes_inner()
        finally:
            del os.environ["TF_DATA_DIR"]


def _test_main_exit_codes_inner():
    try:
        with tempfile.TemporaryDirectory() as tmp:
            script = make_script(tmp)
            mod.list_tasks = lambda: [
                {"Name": "ok", "Execute": "pythonw.exe", "Arguments": f'"{script}"'}]
            mod.head_hash = lambda r: mod.file_hash(script)
            buf = io.StringIO()
            code = _run_main(buf)
            assert code == 0, buf.getvalue()

            mod.list_tasks = lambda: [
                {"Name": "bad", "Execute": "python.exe", "Arguments": f'"{script}"'}]
            buf = io.StringIO()
            code = _run_main(buf)
            assert code == 1, buf.getvalue()

        def boom():
            raise RuntimeError("powershell exploded")
        mod.list_tasks = boom
        buf = io.StringIO()
        code = _run_main(buf)
        assert code == 2, buf.getvalue()
    finally:
        mod.list_tasks = _REAL_LIST_TASKS
        mod.head_hash = _REAL_HEAD_HASH


def test_record_history_appends_and_dedups():
    """The weekly cadence's memory: each distinct verdict appends a line,
    an identical consecutive verdict refreshes in place (dedup), and the
    file caps at HISTORY_MAX entries."""
    with tempfile.TemporaryDirectory() as tmp:
        os.environ["TF_DATA_DIR"] = tmp
        try:
            mod.record_history(["t: drifted from git HEAD"], 8)
            mod.record_history(["t: drifted from git HEAD"], 8)  # same verdict
            mod.record_history([], 8)                            # clean pass
            p = Path(tmp) / "watcher" / "task-audit-history.json"
            hist = json.loads(p.read_text(encoding="utf-8"))
            assert len(hist) == 2, hist
            assert hist[0]["findings"] == ["t: drifted from git HEAD"]
            assert hist[0]["tasks"] == 8 and hist[0]["ts"]
            assert hist[1]["findings"] == []
            # Identical clean verdicts keep collapsing in place.
            mod.record_history([], 8)
            assert len(json.loads(p.read_text(encoding="utf-8"))) == 2
        finally:
            if "TF_DATA_DIR" in os.environ:
                del os.environ["TF_DATA_DIR"]


def test_record_history_survives_corrupt_file():
    """A corrupt/unreadable history is not a crash — the audit verdict
    stands and the file is rebuilt from this run."""
    with tempfile.TemporaryDirectory() as tmp:
        os.environ["TF_DATA_DIR"] = tmp
        try:
            d = Path(tmp) / "watcher"
            d.mkdir(parents=True)
            (d / "task-audit-history.json").write_text("{not json", encoding="utf-8")
            mod.record_history([], 5)
            hist = json.loads((d / "task-audit-history.json").read_text(encoding="utf-8"))
            assert len(hist) == 1 and hist[0]["tasks"] == 5
        finally:
            if "TF_DATA_DIR" in os.environ:
                del os.environ["TF_DATA_DIR"]


def test_record_history_honors_tmpdata_not_real_appdata():
    """The history lands under TF_DATA_DIR, never %APPDATA%\tf (the live
    app's dir) when the env override is set — the TmpData harness rule."""
    with tempfile.TemporaryDirectory() as tmp:
        os.environ["TF_DATA_DIR"] = tmp
        try:
            mod.record_history([], 3)
            assert (Path(tmp) / "watcher" / "task-audit-history.json").exists()
        finally:
            if "TF_DATA_DIR" in os.environ:
                del os.environ["TF_DATA_DIR"]


def _run_main(buf) -> int:
    code = None
    try:
        with contextlib.redirect_stdout(buf):
            mod.main()
    except SystemExit as ex:
        code = ex.code
    assert code is not None, "main() must exit explicitly"
    return code


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"  ok  {t.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"FAIL  {t.__name__}: {exc}")
        except Exception as exc:  # noqa: BLE001
            failed += 1
            print(f"ERROR {t.__name__}: {exc.__class__.__name__}: {exc}")
    print(f"{'FAIL' if failed else 'PASS'}: {len(tests) - failed}/{len(tests)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
