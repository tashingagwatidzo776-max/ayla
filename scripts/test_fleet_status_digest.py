#!/usr/bin/env python3
"""Tests for scripts/fleet_status_digest.py and its register script.

The digest's payload builder and output parsers are pure (probe results
in, embed out), so green/red/amber verdicts, clipping and probe-failure
fallbacks are checkable without touching a webhook; the register
composition is asserted text-level like the other register suites.

Run: python scripts/test_fleet_status_digest.py   (exit 0 = pass)
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REGISTER = HERE / "register-fleet-digest.ps1"
DIGEST = HERE / "fleet_status_digest.py"

_spec = importlib.util.spec_from_file_location("fleet_status_digest", DIGEST)
mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mod)

results: list[tuple[str, bool]] = []


def check(name: str, condition: bool) -> None:
    results.append((name, bool(condition)))
    print(f"  {'ok ' if condition else 'FAIL'}  {name}")
    assert condition, name


GREEN_AUDIT = {"rc": 0,
               "verdict": "task-audit: OK - 18 task(s), 0 finding(s)",
               "findings": []}
GREEN_HEALTH = {"rc": 0, "healthy": True,
                "tasks": ["a", "b", "c", "d"], "findings": []}


def test_register_composition():
    src = REGISTER.read_text(encoding="utf-8", errors="replace")
    check("register script is idempotent (replaces task)",
          "Register-ScheduledTask" in src and "Unregister-ScheduledTask" in src)
    check("task name is the fleet status digest",
          "DongGfx fleet status digest" in src)
    check("registered action points at the digest script",
          "fleet_status_digest.py" in src)
    check("digest exists next to the register script", DIGEST.exists())
    check("weekly trigger defaults to Monday", "$DayOfWeek = 'Monday'" in src
          and "-Weekly" in src)
    check("default time follows the 08:30 audit", "$AtLocal = '09:00'" in src)
    check("no AtLogOn trigger (weekly report, not per boot)",
          "New-ScheduledTaskTrigger -AtLogOn" not in src)
    check("cmd wrapper sets PYTHONIOENCODING (the fleet audit's rule)",
          "set PYTHONIOENCODING=utf-8&&" in src)
    check("stdout/stderr captured to a log (Task Scheduler discards output)",
          "fleet-digest.log" in src and "[char]62" in src)
    check("action executes via cmd.exe (stdout-bearing python)",
          "-Execute 'cmd.exe'" in src)
    check("execution time limit is bounded", "ExecutionTimeLimit" in src)
    check("concurrent instances are ignored",
          "-MultipleInstances IgnoreNew" in src)
    check("survives battery/available-start quirks",
          "AllowStartIfOnBatteries" in src and "StartWhenAvailable" in src)
    check("register script fails closed on a missing digest",
          "Digest script not found" in src)


def test_green_payload():
    payload = mod.build_payload(GREEN_AUDIT, GREEN_HEALTH, "ts")
    embed = payload["embeds"][0]
    check("green title", embed["title"].endswith("GREEN"))
    check("green color", embed["color"] == mod.GREEN)
    check("both probes reported as fields", len(embed["fields"]) == 2)
    check("health field counts the watched tasks",
          "all 4 watcher task(s) healthy" in embed["fields"][1]["value"])
    check("audit verdict shown",
          "task-audit: OK" in embed["fields"][0]["value"])


def test_red_audit_payload():
    audit = {"rc": 1,
             "verdict": "task-audit: FAIL - 18 task(s), 1 finding(s)",
             "findings": ["FINDING: python task with no repo script path"]}
    payload = mod.build_payload(audit, GREEN_HEALTH, "ts")
    embed = payload["embeds"][0]
    check("red title", embed["title"].endswith("RED"))
    check("red color", embed["color"] == mod.RED)
    check("finding text travels inside the digest",
          "no repo script path" in embed["fields"][0]["value"])


def test_red_health_payload():
    health = {"rc": 1, "healthy": False, "tasks": ["a", "b", "c", "d"],
              "findings": ["DongGfx profit-floor watcher: last two "
                           "completions both exited 3 (history-file) - "
                           "persistent nonzero: TP1 gate regression "
                           "firing (exit 3)"]}
    payload = mod.build_payload(GREEN_AUDIT, health, "ts")
    embed = payload["embeds"][0]
    check("health failure turns the digest red", embed["title"].endswith("RED"))
    check("health finding names the watcher",
          "profit-floor" in embed["fields"][1]["value"])


def test_amber_when_a_probe_is_broken():
    health = {"rc": 2, "healthy": None, "tasks": [],
              "findings": ["probe indeterminate"]}
    payload = mod.build_payload(GREEN_AUDIT, health, "ts")
    check("broken probe is amber, never fake green",
          payload["embeds"][0]["title"].endswith("AMBER")
          and payload["embeds"][0]["color"] == mod.AMBER)
    audit_bad = {"rc": -1, "verdict": "task-audit: no verdict (rc=-1)",
                 "findings": []}
    payload2 = mod.build_payload(audit_bad, GREEN_HEALTH, "ts")
    check("dead audit probe is amber too",
          payload2["embeds"][0]["title"].endswith("AMBER"))


def test_long_findings_are_clipped():
    audit = {"rc": 1, "verdict": "task-audit: FAIL",
             "findings": ["FINDING: " + "x" * 500] * 10}
    payload = mod.build_payload(audit, GREEN_HEALTH, "ts")
    check("field values stay within the embed cap",
          all(len(f["value"]) <= mod.MAX_FIELD
              for f in payload["embeds"][0]["fields"]))


def test_parse_audit_output():
    out = ("DongGfx exit cycle watch\n"
           "  FINDING: python task with no repo script path\n"
           "task-audit: FAIL - 18 task(s), 1 finding(s)\n")
    parsed = mod.parse_audit_output(1, out)
    check("verdict line extracted", parsed["verdict"].startswith("task-audit: FAIL"))
    check("finding lines extracted", len(parsed["findings"]) == 1)
    check("rc preserved", parsed["rc"] == 1)
    empty = mod.parse_audit_output(3, "Traceback (most recent call last):")
    check("crash without verdict is surfaced, not faked",
          empty["verdict"].startswith("task-audit: no verdict"))


def test_parse_health_output_tolerates_trailing_lines():
    out = ('{"tasks": ["a"], "healthy": true, "findings": []}\n'
           "watcher-task-health: OK - all 4 watcher task(s) recent\n")
    parsed = mod.parse_health_output(0, out)
    check("json parsed despite trailing human line",
          parsed.get("healthy") is True)
    check("rc folded in", parsed["rc"] == 0)
    broken = mod.parse_health_output(2,
                                     "watcher-task-health: INDETERMINATE "
                                     "(RuntimeError: powershell failed)")
    check("exception probe comes back indeterminate",
          broken["healthy"] is None and broken["rc"] == 2)


def test_posts_with_the_fleet_ua_and_settings_url():
    src = DIGEST.read_text(encoding="utf-8", errors="replace")
    check("named User-Agent (Discord 1010 class)", "DongGfx-Watcher/1.0" in src)
    check("probe children run UTF-8 (clean capture, no mojibake)",
          "PYTHONIOENCODING" in src)
    check("webhook defaults to settings.json WebhookUrl", "WebhookUrl" in src)
    check("has a --no-alert observe mode", "--no-alert" in src)
    check("digest reports but never gates (exit 0 on delivery)",
          "never gates" in src)


def main() -> int:
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
