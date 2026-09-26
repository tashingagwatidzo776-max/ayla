#!/usr/bin/env python3
"""Unit tests for scripts/flake_tracker.py.

The tracker's parsing and classification run against synthetic logs and
attempt sets — no GitHub API, no network. The retry guard hides real
failures, so these verdicts must be exact.

Run: python scripts/test_flake_tracker.py   (exit 0 = all pass)
"""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import flake_tracker as ft  # noqa: E402


LOG_ONE = """[xUnit.net 00:01:29.82]     DongGfx.App.Tests.TickArchiveTests.Add_Writes [FAIL]
  Failed DongGfx.App.Tests.TickArchiveTests.Add_Writes [30 s]
  Error Message:
   Assert.True() Failure
"""

LOG_TWO = """  Passed DongGfx.App.Tests.TickArchiveTests.Add_Writes [1 ms]
  Failed DongGfx.App.Tests.WebhookServiceTests.TestConnection_ServerError [10 s]
"""


def attempt(run, att, failed, conclusion="success", created="2026-09-25T10:00:00Z"):
    return {"run": run, "created": created, "run_conclusion": conclusion,
            "attempt": att, "failed": failed}


def test_split_attempts_without_marker():
    chunks = ft.split_attempts(LOG_ONE + "ok" + LOG_TWO)
    assert len(chunks) == 1


def test_split_attempts_with_marker():
    log = LOG_ONE + ft.FLAKE_MARKER + "\nRetrying...\n" + LOG_TWO
    chunks = ft.split_attempts(log)
    assert len(chunks) == 2
    # str.split removes the separator: attempt 2 starts right after it.
    assert ft.FLAKE_MARKER not in chunks[1]
    assert "Add_Writes" in chunks[0]
    assert "Retrying" in chunks[1]
    assert "TestConnection_ServerError" in chunks[1]


def test_failed_tests_extracts_names():
    failed = ft.failed_tests(LOG_ONE + LOG_TWO)
    assert "DongGfx.App.Tests.TickArchiveTests.Add_Writes" in failed
    assert "DongGfx.App.Tests.WebhookServiceTests.TestConnection_ServerError" in failed


def test_failed_tests_ignores_summaries_and_assemblies():
    log = LOG_ONE + "\n   Failed: 1\nPassed!  - Failed:     0\nTests.dll matched\n"
    failed = ft.failed_tests(log)
    assert failed == {"DongGfx.App.Tests.TickArchiveTests.Add_Writes"}


def test_hidden_retry_is_counted_and_flagged():
    attempts = [
        attempt(1, 1, ["A.X"], conclusion="success"),
        attempt(1, 2, [], conclusion="success"),
    ]
    per_test, hidden, _cross = ft.classify(attempts)
    assert per_test["A.X"]["attempts_failed"] == 1
    assert hidden["A.X"] == 1


def test_real_failure_retried_to_death_is_not_hidden():
    # Both attempts failed AND the run is red — that is a hard failure, not
    # something the guard hid.
    attempts = [
        attempt(1, 1, ["A.X"], conclusion="failure"),
        attempt(1, 2, ["A.X"], conclusion="failure"),
    ]
    per_test, hidden, _cross = ft.classify(attempts)
    assert per_test["A.X"]["attempts_failed"] == 2
    assert hidden.get("A.X", 0) == 0


def test_attempt2_only_failure_counts_as_a_failure():
    attempts = [
        attempt(1, 1, [], conclusion="success"),
        attempt(1, 2, ["A.Y"], conclusion="failure"),
    ]
    per_test, hidden, _cross = ft.classify(attempts)
    assert per_test["A.Y"]["attempts_failed"] == 1
    assert hidden.get("A.Y", 0) == 0


def test_cross_run_flake_detected():
    attempts = [
        attempt(1, 1, ["A.Z"], conclusion="success", created="2026-09-24T10:00:00Z"),
        attempt(2, 1, [], conclusion="success", created="2026-09-25T10:00:00Z"),
    ]
    _per_test, _hidden, cross = ft.classify(attempts)
    assert cross["A.Z"] is True


def test_no_cross_run_flake_when_never_retried():
    attempts = [
        attempt(1, 1, ["A.Z"], conclusion="success", created="2026-09-24T10:00:00Z"),
        attempt(2, 1, ["A.Z"], conclusion="success", created="2026-09-25T10:00:00Z"),
    ]
    _per_test, _hidden, cross = ft.classify(attempts)
    assert cross["A.Z"] is False


def test_chronic_threshold():
    attempts = [
        attempt(1, 1, ["A.Chronic", "B.Once"], conclusion="success"),
        attempt(1, 2, ["A.Chronic"], conclusion="success"),
        attempt(2, 1, ["A.Chronic"], conclusion="success"),
        attempt(2, 2, [], conclusion="success"),
    ]
    rows = ft.build_report(attempts, threshold=3)
    by_name = {r["test"]: r for r in rows}
    assert by_name["A.Chronic"]["chronic"] is True
    assert by_name["A.Chronic"]["attempts_failed"] == 3
    assert by_name["A.Chronic"]["hidden_retries"] == 1
    assert by_name["B.Once"]["chronic"] is False
    assert rows[0]["test"] == "A.Chronic"   # sorted by failures desc


def test_markdown_renders_rows_and_chronic_marker():
    rows = ft.build_report(
        [attempt(1, 1, ["A.Chronic"], conclusion="success"),
         attempt(1, 2, [], conclusion="success")], threshold=1)
    md = ft.render_markdown(rows, window=20, threshold=1)
    assert "`A.Chronic`" in md
    assert "**yes**" in md
    assert "Hidden by retry" in md


def test_markdown_empty_window_has_no_table():
    md = ft.render_markdown([], window=20, threshold=3)
    assert "No test failures found" in md
    assert "|" not in md


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"ok  {t.__name__}")
        except AssertionError as ex:
            failed += 1
            print(f"FAIL {t.__name__}: {ex}")
        except Exception as ex:  # noqa: BLE001
            failed += 1
            print(f"FAIL {t.__name__}: {ex.__class__.__name__}: {ex}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
