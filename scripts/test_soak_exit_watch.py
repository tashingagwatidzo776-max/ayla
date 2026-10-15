#!/usr/bin/env python3
"""Tests for scripts/soak_exit_watch.py and its register script.

The pairing logic (start/exit lines -> orphan starts), the grace +
dedup alert decision and the embed builder are pure, so termination
detection is checkable without a webhook or a real soak log; the
register composition is asserted text-level like the other register
suites. The end-to-end main() pass runs against a tmp log + tmp state
with a stubbed post — the real registration and the real webhook are
explicit human acts, never side effects of the test suite.

Run: python scripts/test_soak_exit_watch.py   (exit 0 = pass)
"""
from __future__ import annotations

import importlib.util
import sys
import tempfile
from datetime import datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
REGISTER = HERE / "register-soak-exit-watch.ps1"
WATCH = HERE / "soak_exit_watch.py"

_spec = importlib.util.spec_from_file_location("soak_exit_watch", WATCH)
mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mod)

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")
    assert condition, name


START = "=== soak_evening 2026-10-12 20:30:01 ==="
EXIT = "exit: 0 at 2026-10-12 21:05:33 (duration 35.5 min)"
EXIT_LEGACY = "exit: 1"


def test_parse_events_extracts_markers_and_skips_garbage():
    events = mod.parse_events([START, "some python output", EXIT,
                               "not even close", ""])
    check("start line parsed with its timestamp",
          events[0][0] == "start"
          and events[0][1] == datetime(2026, 10, 12, 20, 30, 1))
    check("exit line parsed (timestamp unused, pairing is order-based)",
          events[1][0] == "exit")
    check("garbage lines skipped, never fatal", len(events) == 2)
    legacy = mod.parse_events([START, EXIT_LEGACY])
    check("legacy exit line (no timestamp) still closes the run",
          [k for k, _ in legacy] == ["start", "exit"])
    # The pre-2026-10 wrapper wrote python output + exit through a bare
    # `>>` = UTF-16LE; under the utf-8 read those lines arrive as
    # NUL-interleaved text. A COMPLETED legacy run must not false-alert.
    utf16_lines = [START,
                   "exit: 0".encode("utf-16-le").decode("utf-8",
                                                        errors="replace")]
    check("UTF-16LE legacy exit line still closes the run (the 09-26 "
          "lesson)", mod.find_orphans(mod.parse_events(utf16_lines)) == [])


def test_orphan_pairing():
    clean = mod.parse_events([START, EXIT])
    check("start + exit = no orphan", mod.find_orphans(clean) == [])
    death = mod.parse_events(
        [START,
         "=== soak_evening 2026-10-13 20:30:30 ===",   # next start, no exit
         EXIT])
    orphans = mod.find_orphans(death)
    check("start closed by the NEXT start is an orphan (the 10-12 alpha "
          "signature)", len(orphans) == 1
          and orphans[0] == datetime(2026, 10, 12, 20, 30, 1))
    check("the second (clean) run is not an orphan", len(orphans) == 1)
    eof = mod.parse_events([START])
    check("start still open at EOF is an orphan (killed mid-run)",
          mod.find_orphans(eof) == [datetime(2026, 10, 12, 20, 30, 1)])
    check("exit-only lines produce no orphans",
          mod.find_orphans(mod.parse_events([EXIT])) == [])


def test_grace_and_dedup_decision():
    now = datetime(2026, 10, 13, 12, 0, 0)
    fresh = now - timedelta(minutes=100)
    old = now - timedelta(minutes=300)
    check("orphan inside the grace window is not alerted",
          mod.new_alerts([fresh], set(), 270, now) == [])
    check("orphan past the grace window is alerted",
          mod.new_alerts([old], set(), 270, now) == [old])
    check("already-alerted start is deduped",
          mod.new_alerts([old], {old.strftime(mod.TS_FMT)}, 270, now) == [])
    check("a fresh state file does not suppress the first alert",
          mod.new_alerts([old], set(), 270, now) != [])


def test_payload_shape():
    starts = [datetime(2026, 10, 12, 20, 30, 1)]
    payload = mod.build_payload(starts, 270, "C:\\log\\soak_evening.log", "ts")
    embed = payload["embeds"][0]
    check("termination title", "terminated" in embed["title"])
    check("red color", embed["color"] == 0xE74C3C)
    check("orphan start named in the field",
          "2026-10-12 20:30:01" in embed["fields"][0]["value"])
    check("grace and log path travel with the alert",
          "270" in embed["fields"][1]["value"]
          and "soak_evening.log" in embed["fields"][1]["value"])
    multi = mod.build_payload(starts * 3, 270, "l", "ts")
    check("multiple orphans count in the title",
          multi["embeds"][0]["title"].startswith("Soak runs terminated"))


def test_state_roundtrip_and_loss_tolerance():
    with tempfile.TemporaryDirectory() as td:
        path = str(Path(td) / "sub" / "state.json")
        check("no state file -> nothing alerted yet",
              mod.load_alerted(path) == set())
        mod.save_alerted(path, {"a", "b"})
        check("alerted set round-trips", mod.load_alerted(path) == {"a", "b"})
        Path(path).write_text("{broken json", encoding="utf-8")
        check("corrupt state file -> empty set, never a crash",
              mod.load_alerted(path) == set())
        mod.save_alerted(path, {str(i) for i in range(50)})
        check("state file persists EVERY alerted key (a cap would evict "
              "the oldest and re-alert them forever)",
              len(mod.load_alerted(path)) == 50)


def run_main(args: list[str], post):
    """Run mod.main() with a stubbed post_webhook; returns (rc, posts)."""
    posted: list[dict] = []
    real_post, real_argv = mod.post_webhook, sys.argv

    def capture(url, body):
        posted.append(body)
        return 204 if post is None else post(url, body)
    mod.post_webhook = capture
    sys.argv = ["soak_exit_watch.py", *args]
    try:
        rc = mod.main()
    finally:
        mod.post_webhook, sys.argv = real_post, real_argv
    return rc, posted


def test_main_end_to_end_with_tmp_log_and_state():
    with tempfile.TemporaryDirectory() as td:
        log = Path(td) / "soak_evening.log"
        state = str(Path(td) / "state.json")
        old = (datetime.now() - timedelta(minutes=300))
        log.write_text(f"=== soak_evening {old:%Y-%m-%d %H:%M:%S} ===\n",
                       encoding="utf-8")
        rc, posted = run_main(
            ["--log", str(log), "--state", state, "--webhook-url",
             "http://example.invalid/hook"], None)
        check("overdue orphan + working post -> exit 0", rc == 0)
        check("exactly one embed posted", len(posted) == 1)
        check("posted embed names the orphan start",
              f"{old:%Y-%m-%d %H:%M:%S}" in
              posted[0]["embeds"][0]["fields"][0]["value"])
        rc2, posted2 = run_main(
            ["--log", str(log), "--state", state, "--webhook-url",
             "http://example.invalid/hook"], None)
        check("second pass dedupes: no repeat alert, exit 0",
              rc2 == 0 and posted2 == [])

        # A failed POST must leave the state unsaved so the next pass retries.
        log2 = Path(td) / "soak2.log"
        old2 = datetime.now() - timedelta(minutes=400)
        log2.write_text(f"=== soak_evening {old2:%Y-%m-%d %H:%M:%S} ===\n",
                        encoding="utf-8")
        state2 = str(Path(td) / "state2.json")

        def boom(url, body):
            raise RuntimeError("webhook POST failed: HTTP 403")
        rc3, _ = run_main(["--log", str(log2), "--state", state2,
                           "--webhook-url", "http://example.invalid/hook"],
                          boom)
        check("failed POST -> exit 1", rc3 == 1)
        check("failed POST leaves state unsaved (next pass retries)",
              mod.load_alerted(state2) == set())
        rc4, posted4 = run_main(["--log", str(log2), "--state", state2,
                                 "--webhook-url", "http://example.invalid/hook"],
                                None)
        check("retried next pass -> posted once, exit 0",
              rc4 == 0 and len(posted4) == 1)

        # Dry run: verdict printed, no POST, no state write.
        rc5, posted5 = run_main(["--log", str(log2), "--state",
                                 str(Path(td) / "state3.json"), "--no-alert",
                                 "--webhook-url", "http://example.invalid/hook"],
                                None)
        check("--no-alert dry run: no post, no state, exit 0",
              rc5 == 0 and posted5 == []
              and mod.load_alerted(str(Path(td) / "state3.json")) == set())

        # Missing log file = no soak ever ran: clean pass, nothing paged.
        rc6, posted6 = run_main(["--log", str(Path(td) / "nope.log"),
                                 "--state", str(Path(td) / "state4.json")],
                                None)
        check("missing log -> clean exit 0, no post", rc6 == 0 and posted6 == [])


def test_register_composition():
    src = REGISTER.read_text(encoding="utf-8", errors="replace")
    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "Unregister-ScheduledTask" in src)
    check("task name is the soak-exit watch",
          "DongGfx soak-exit watch" in src)
    check("registered action points at the watcher",
          "soak_exit_watch.py" in src)
    check("watcher exists next to the register script", WATCH.exists())
    check("grace window is passed through",
          "--grace-minutes" in src and "$GraceMinutes = 270" in src)
    check("default cadence is 15 minutes", "$EveryMinutes = 15" in src)
    check("repetition interval uses the cadence parameter",
          "New-TimeSpan -Minutes $EveryMinutes" in src)
    check("task runs at logon", "AtLogOn" in src)
    check("cmd wrapper sets PYTHONIOENCODING (the fleet audit's rule)",
          "set PYTHONIOENCODING=utf-8&&" in src)
    check("stdout/stderr captured to a log (Task Scheduler discards output)",
          "soak-exit-watch.log" in src and "[char]62" in src)
    check("action executes via cmd.exe (stdout-bearing python)",
          "-Execute 'cmd.exe'" in src)
    check("execution time limit is bounded", "ExecutionTimeLimit" in src)
    check("concurrent instances are ignored",
          "-MultipleInstances IgnoreNew" in src)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in src and "StartWhenAvailable" in src)
    check("register script fails closed on a missing watcher",
          "Watcher not found" in src)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
        except AssertionError:
            failed += 1
    print(f"{'FAIL' if failed else 'PASS'}: {len(tests) - failed}/{len(tests)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
