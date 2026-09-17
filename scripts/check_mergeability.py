#!/usr/bin/env python3
"""Merge-preview check for this repo's protected main branch.

Answers, before you reach the merge button, whether a PR that is CI-green can
actually merge. Catches the blocker classes that surfaced live on PR #51,
where every required status check was green yet the merge was refused:

1. "Require a pull request before merging" active while the only account with
   access is also the PR author. GitHub rejects self-approval, so no review
   can ever exist and the PR is permanently BLOCKED despite green CI.
2. An intended unblock that never registered (protection edit not saved, or
   made in the rulesets UI instead of the Branches page). This script always
   re-reads the live rule from the API rather than trusting intent.
3. "Do not allow bypassing the above settings" (enforce_admins) on top of
   the missing review - `gh pr merge --admin` cannot bypass it either.

Usage:
  python scripts/check_mergeability.py [PR_NUMBER] [--json]

Without a PR number it checks the PR whose head branch is the current branch
(same default as `gh pr view`).

Exit codes: 0 = mergeable now (or nothing to merge), 1 = blocked (findings
listed), 2 = usage or API error. Needs `gh` authenticated with repo admin
scope to read branch protection (404 is handled as "no rule active").
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys

OK = 0
BLOCKED = 1
ERROR = 2


class ApiError(Exception):
    pass


def _run(args: list[str]) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(args, capture_output=True, text=True, check=False)
    except FileNotFoundError:
        raise ApiError("gh CLI not found on PATH") from None


def gh_json(args: list[str]) -> object:
    proc = _run(["gh"] + args)
    if proc.returncode != 0:
        raise ApiError(f"`gh {' '.join(args[:3])}…` failed: {proc.stderr.strip()[:300]}")
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        raise ApiError(f"unparseable gh output: {proc.stdout[:200]!r} ({exc})") from None


def load_pr(number: int | None) -> dict:
    view = ["pr", "view"] + ([str(number)] if number else [])
    pr = gh_json(view + ["--json",
                         "number,title,state,author,baseRefName,mergeable,"
                         "mergeStateStatus,reviewDecision,reviews"])
    if not isinstance(pr, dict):
        raise ApiError("unexpected gh pr view payload")
    return pr


def load_protection(base: str) -> dict | None:
    """None means no classic protection rule on the base branch (HTTP 404)."""
    repo = gh_json(["repo", "view", "--json", "nameWithOwner"])["nameWithOwner"]
    proc = _run(["gh", "api", f"repos/{repo}/branches/{base}/protection"])
    if proc.returncode != 0:
        if "404" in proc.stderr:
            return None
        raise ApiError(f"protection lookup failed: {proc.stderr.strip()[:300]}")
    return json.loads(proc.stdout)


def load_collaborators() -> list[dict]:
    repo = gh_json(["repo", "view", "--json", "nameWithOwner"])["nameWithOwner"]
    proc = _run(["gh", "api", f"repos/{repo}/collaborators?affiliation=all"])
    if proc.returncode != 0:
        return []  # non-admin tokens cannot list collaborators; degrade gracefully
    return json.loads(proc.stdout)


def check(pr: dict) -> tuple[list[str], bool]:
    """Return (findings, mergeable)."""
    findings: list[str] = []
    mergeable = True
    state = pr["state"]
    author = pr["author"]["login"]

    if state != "OPEN":
        findings.append(f"ℹ️  PR state is {state} - nothing to merge.")
        return findings, True

    rules = load_protection(pr["baseRefName"])
    if rules is None:
        findings.append("⚠️  No classic branch-protection rule on "
                        f"{pr['baseRefName']}. If the repo uses rulesets, "
                        "verify them in Settings → Rules → Rulesets; this "
                        "script reads classic protection only.")
        rule_reviews = None
        enforce_admins = False
    else:
        reviews_rule = rules.get("required_pull_request_reviews") or {}
        rule_reviews = reviews_rule.get("required_approving_review_count", 0)
        enforce_admins = bool((rules.get("enforce_admins") or {}).get("enabled"))
        contexts = (rules.get("required_status_checks") or {}).get("contexts", [])
        findings.append(
            f"ℹ️  Protection on {pr['baseRefName']}: {rule_reviews} approving "
            f"review(s) required, admins "
            f"{'enforced' if enforce_admins else 'can bypass'}, required "
            f"checks: {', '.join(contexts) if contexts else 'none'}.")

    approvals = sum(1 for r in pr.get("reviews", [])
                    if r.get("state") == "APPROVED"
                    and r.get("author", {}).get("login") != author)

    if rule_reviews:
        if approvals >= rule_reviews:
            findings.append(f"✅ Approvals: {approvals}/{rule_reviews}.")
        else:
            mergeable = False
            writers = [c["login"] for c in load_collaborators()
                       if c.get("permissions", {}).get("push")]
            non_author = [w for w in writers if w != author]
            findings.append(f"❌ Approvals: {approvals}/{rule_reviews}.")
            if writers and not non_author:
                findings.append(
                    "❌ PERMANENT BLOCK: the only write-capable account is the "
                    f"PR author ({author}), and GitHub rejects self-approval. "
                    "No review can ever satisfy this rule. Fix: add a "
                    "collaborator with write access to approve, or temporarily "
                    "relax the review requirement (verify the change saved - "
                    "this script re-reads the live rule).")
            else:
                findings.append(
                    "❌ Needs an approving review from a write-capable account "
                    f"other than the author. Candidates: "
                    f"{', '.join(non_author) if non_author else '(none found)'}.")
            if enforce_admins:
                findings.append(
                    "❌ enforce_admins is on: `gh pr merge --admin` cannot "
                    "bypass the review requirement either.")

    status = pr.get("mergeStateStatus")
    if status == "DIRTY":
        mergeable = False
        findings.append("❌ Merge conflict against the base branch.")
    elif status == "UNSTABLE":
        mergeable = False
        findings.append("❌ Required checks not green (see Actions).")
    elif status == "BEHIND":
        findings.append("⚠️  Branch is behind the base; strict required checks "
                        "will update it on merge, or update locally first.")
    elif status not in ("CLEAN", "HAS_HOOKS", "UNKNOWN", None):
        mergeable = False
        findings.append(f"❌ Merge state: {status}.")

    return findings, mergeable


def main() -> int:
    # Windows consoles default to cp1252; the findings use emoji markers that
    # would raise UnicodeEncodeError on print. Force UTF-8 with replacement.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    ap = argparse.ArgumentParser(
        description="Fail early when a CI-green PR still cannot merge.")
    ap.add_argument("pr", nargs="?", type=int,
                    help="PR number (default: PR for the current branch)")
    ap.add_argument("--json", action="store_true",
                    help="machine-readable output")
    args = ap.parse_args()

    try:
        pr = load_pr(args.pr)
        findings, mergeable = check(pr)
    except ApiError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return ERROR

    if args.json:
        print(json.dumps({"number": pr["number"], "state": pr["state"],
                          "mergeable": mergeable, "findings": findings}))
    else:
        print(f"PR #{pr['number']} \"{pr['title'][:70]}\"  "
              f"[{pr['state']}]  base={pr['baseRefName']}")
        for line in findings:
            print(line)
        verdict = "MERGEABLE" if mergeable else "BLOCKED"
        print(f"Verdict: {verdict}")

    return OK if mergeable else BLOCKED


if __name__ == "__main__":
    sys.exit(main())
