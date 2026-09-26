#!/usr/bin/env python3
"""Flake tracker: make fail-then-pass tests visible.

CI retries the unit job once (the flake guard), so a test can fail attempt
1, pass attempt 2, and the run goes green — the failure never surfaces
anywhere but inside one log file nobody opens. Over time chronic flakes
hide behind that guard and erode trust in every red run.

This script scans the last N CI runs on main via the GitHub API, pulls the
unit job's FULL log for every run (the retry boundary and both attempts
live in the same log), and builds a per-test failure history:

  - hidden retry   : failed attempt 1, absent from attempt 2, run green
                     (the guard hid it — visible only here)
  - cross-run flake: failed in an older run, passed in a newer one
  - chronic        : >= --threshold failed attempts within the window

Exit codes:
  0 = scan completed (findings do not fail the scan; this is visibility,
      not a gate — a tracker that blocks would just become a second gate
      to flake)
  1 = --strict and at least one chronic flake was found
  2 = usage/API error (gh missing, API failure)

Requires: gh authenticated. Read-only on the repo; --update-issue only
posts/updates an issue labeled ci-flakes (created if absent).
"""
import argparse
import json
import re
import subprocess
import sys
from collections import defaultdict

FLAKE_MARKER = "Unit tests failed on the first attempt - retrying once"
# "Failed DongGfx.App.Tests.SomeTests.Some_Method [...]" — xUnit's console
# summary of a failed test (both raw and [xUnit.net ...]-prefixed forms).
# Parameterized theories render as Name(serviceType: typeof(...)) — the
# parenthesized arguments are kept so theory cases are tracked distinctly.
FAIL_LINE = re.compile(
    r"Failed\s+([A-Za-z_][\w.]*(?:\.[A-Za-z_][\w`]*)+(?:\((?:[^()\[]|\([^()]*\))*\))?)\s*(?:\[|$)", re.M)
FAIL_BRACKET = re.compile(r"\[xUnit\.net [^\]]+\]\s+(Failed\s.+)")
ISSUE_LABEL = "ci-flakes"
ISSUE_TITLE = "CI flake tracker: tests failing then passing"


def gh(*args):
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args[:3])}... failed: {result.stderr.strip()[:300]}")
    return result.stdout


def parse_args(argv):
    p = argparse.ArgumentParser(description="CI flake tracker (fail-then-pass visibility)")
    p.add_argument("--runs", type=int, default=20, help="how many recent CI runs on main to scan")
    p.add_argument("--workflow", default="ci.yml")
    p.add_argument("--branch", default="main")
    p.add_argument("--threshold", type=int, default=3,
                   help="failed attempts within the window that count as chronic")
    p.add_argument("--strict", action="store_true",
                   help="exit 1 when a chronic flake is found (otherwise always 0)")
    p.add_argument("--update-issue", action="store_true",
                   help="post/refresh the flake report on the ci-flakes issue")
    p.add_argument("--json", dest="json_out", metavar="PATH",
                   help="also write the raw per-attempt findings as JSON")
    args = p.parse_args(argv)
    if args.runs < 1 or args.threshold < 1:
        p.error("--runs and --threshold must be >= 1")
    return args


def list_runs(args):
    raw = gh("run", "list", "--workflow", args.workflow, "--branch", args.branch,
             "--limit", str(args.runs), "--json",
             "databaseId,conclusion,createdAt")
    runs = json.loads(raw)
    return [r for r in runs if r.get("conclusion") in ("success", "failure")]


def unit_job_id(run_id):
    raw = gh("run", "view", str(run_id), "--json", "jobs",
             "--jq", '.jobs[] | select(.name == "unit") | .databaseId')
    ids = [line.strip() for line in raw.splitlines() if line.strip()]
    return int(ids[0]) if ids else None


def fetch_job_log(job_id):
    # Job logs contain ANSI color codes; gh refuses to emit them without
    # --allow-escape-sequences (the parser handles the escape-prefixed lines).
    return gh("api", f"repos/{{owner}}/{{repo}}/actions/jobs/{job_id}/logs",
              "--allow-escape-sequences")


def split_attempts(log):
    """Split a unit-job log into retry attempts: [attempt1, attempt2]."""
    parts = log.split(FLAKE_MARKER)
    if len(parts) == 1:
        return [parts[0]]
    return [parts[0], FLAKE_MARKER.join(parts[1:])]


def failed_tests(log_text):
    """Distinct failed test names in one attempt's log."""
    names = set()
    for m in FAIL_BRACKET.finditer(log_text):
        inner = FAIL_LINE.search(m.group(1))
        if inner:
            names.add(inner.group(1))
    names |= {m.group(1) for m in FAIL_LINE.finditer(log_text)}
    # Drop non-test residue defensively (assembly names, msbuild rows).
    return {n for n in names if not n.endswith(".dll") and "::" not in n and "/" not in n}


def scan(args):
    """Returns (attempts, meta). attempts = per-run per-attempt failure sets,
    chronological; attempt 1 is recorded even when it passed."""
    runs = list_runs(args)
    attempts = []
    meta = []
    for run in runs:
        rid = run["databaseId"]
        try:
            job = unit_job_id(rid)
            if job is None:
                meta.append({"run": rid, "note": "no unit job (skipped or not yet run)"})
                continue
            log = fetch_job_log(job)
        except RuntimeError as ex:
            meta.append({"run": rid, "note": str(ex)})
            continue

        chunks = split_attempts(log)
        for i, chunk in enumerate(chunks, start=1):
            attempts.append({
                "run": rid,
                "created": run.get("createdAt"),
                "run_conclusion": run.get("conclusion"),
                "attempt": i,
                "failed": sorted(failed_tests(chunk)),
            })
        meta.append({"run": rid, "attempts": len(chunks)})
    return attempts, meta


def classify(attempts):
    """Returns (per_test, hidden_retries, cross_run).

    per_test:      {name: {attempts_failed, runs_failed, last_failed_at}}
                   — counts EVERY failed attempt, including ones the retry hid.
    hidden_retries:{name: count} — attempt-1 failures a green run's retry hid.
    cross_run:     {name: bool} — failed in an older run, passed in a newer one.
    """
    per_test = {}
    by_run = defaultdict(dict)      # run id -> {attempt: set(failed names)}
    run_meta = {}                   # run id -> (created, conclusion)
    for a in attempts:
        run_meta[a["run"]] = (a.get("created") or "", a.get("run_conclusion"))
        by_run[a["run"]][a["attempt"]] = set(a["failed"])
        for name in a["failed"]:
            entry = per_test.setdefault(
                name, {"attempts_failed": 0, "runs_failed": set(), "last_failed_at": ""})
            entry["attempts_failed"] += 1
            entry["runs_failed"].add(a["run"])
            entry["last_failed_at"] = max(entry["last_failed_at"], a.get("created") or "")

    hidden_retries = defaultdict(int)
    for run, atts in by_run.items():
        conclusion = run_meta[run][1]
        if conclusion != "success":
            continue
        first = atts.get(1, set())
        second = atts.get(2, set())
        for name in first - second:
            hidden_retries[name] += 1

    cross_run = {}
    for name, entry in per_test.items():
        cross_run[name] = any(
            created > entry["last_failed_at"]
            and name not in atts.get(1, set()) | atts.get(2, set())
            for run, atts in by_run.items()
            for (created, _conclusion) in [run_meta[run]]
        )

    return per_test, hidden_retries, cross_run


def build_report(attempts, threshold):
    per_test, hidden_retries, cross_run = classify(attempts)
    rows = []
    for name, entry in per_test.items():
        rows.append({
            "test": name,
            "attempts_failed": entry["attempts_failed"],
            "hidden_retries": hidden_retries.get(name, 0),
            "cross_run_flake": cross_run.get(name, False),
            "chronic": entry["attempts_failed"] >= threshold,
            "last_failed_at": entry["last_failed_at"],
        })
    rows.sort(key=lambda r: (-r["attempts_failed"], r["test"]))
    return rows


def render_markdown(rows, window, threshold):
    lines = [
        "## Flake report",
        "",
        f"Window: the last {window} CI runs on `main`. A test failing attempt 1 and "
        "passing attempt 2 never fails the job (the flake guard) - this report "
        "makes those failures visible. **Chronic** = at least "
        f"{threshold} failed attempts in the window.",
        "",
    ]
    if not rows:
        lines.append("No test failures found in the window - the guard is quiet because it can be.")
        return "\n".join(lines)
    lines.append("| Test | Failed attempts | Hidden by retry | Cross-run flake | Chronic | Last failure |")
    lines.append("|---|---|---|---|---|---|")
    for r in rows:
        lines.append(
            f"| `{r['test']}` | {r['attempts_failed']} | {r['hidden_retries']} | "
            f"{'yes' if r['cross_run_flake'] else 'no'} | "
            f"{'**yes**' if r['chronic'] else 'no'} | {r['last_failed_at'][:10] or 'n/a'} |")
    return "\n".join(lines)


def update_issue(report_md):
    """Create or refresh the ci-flakes issue with the report."""
    found = gh("issue", "list", "--label", ISSUE_LABEL, "--state", "open",
               "--json", "number", "--jq", ".[0].number // empty").strip()
    body = ("<!-- flake-tracker-report -->\n" + report_md +
            "\n\n_Auto-generated by `scripts/flake_tracker.py --update-issue`; "
            "refreshed on each run. The record below the latest report is history._")
    if found:
        gh("issue", "comment", found, "--body", body)
        return f"commented on #{found}"
    out = gh("issue", "create", "--label", ISSUE_LABEL,
             "--title", ISSUE_TITLE, "--body", body)
    return f"created {out.strip()}"


def main(argv):
    args = parse_args(argv)
    try:
        attempts, meta = scan(args)
    except RuntimeError as ex:
        print(f"::error::{ex}")
        return 2

    rows = build_report(attempts, args.threshold)
    chronic = [r for r in rows if r["chronic"]]
    report_md = render_markdown(rows, args.runs, args.threshold)

    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as f:
            json.dump({"attempts": attempts, "meta": meta, "rows": rows}, f, indent=2)

    scanned = len({a["run"] for a in attempts})
    skipped_notes = [m for m in meta if "note" in m]
    print(f"Scanned {scanned} run(s) with a unit job "
          f"({len(skipped_notes)} skipped: {', '.join(str(m['run']) for m in skipped_notes) or 'none'}).")
    for note in skipped_notes:
        print(f"  ::warning::run {note['run']} skipped: {note['note']}")
    if rows:
        print()
        print(report_md)
        if chronic:
            print(f"\nCHRONIC: {len(chronic)} test(s) reached the threshold of {args.threshold} failed attempts.")
    else:
        print("No test failures found in the window - the guard is quiet because it can be.")

    if args.update_issue:
        print(f"issue: {update_issue(report_md)}")

    if args.strict and chronic:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
