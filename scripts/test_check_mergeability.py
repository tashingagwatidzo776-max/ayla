#!/usr/bin/env python3
"""Fast unit tests for scripts/check_mergeability.py's verdict logic.

The blocked-PR shapes this check exists for (self-approval deadlock, missing
review with candidates available, admin bypass disabled) are exactly the
states that are awkward to reproduce on demand against the live API, so the
decision logic is tested here against synthetic PR fixtures with the gh
loaders stubbed. The live wiring is exercised by running the script itself
(see ci-local.ps1), not by these tests.

Run: python scripts/test_check_mergeability.py   (exit 0 = all pass)
"""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import check_mergeability as cm  # noqa: E402


def make_pr(state="OPEN", author="tashingagwatidzo776-max",
            reviews=None, merge_state="CLEAN", base="main"):
    return {
        "number": 1,
        "title": "synthetic",
        "state": state,
        "author": {"login": author},
        "baseRefName": base,
        "mergeStateStatus": merge_state,
        "reviews": reviews or [],
    }


def with_protection(rule_reviews=1, enforce_admins=True, contexts=("unit",)):
    rule = {"required_status_checks": {"contexts": list(contexts)},
            "enforce_admins": {"enabled": enforce_admins}}
    if rule_reviews is not None:
        rule["required_pull_request_reviews"] = {
            "required_approving_review_count": rule_reviews,
            "dismiss_stale_reviews": True,
        }
    return rule


def run_check(pr, protection=None, collaborators=()):
    """Runs check() with the gh loaders stubbed to the given fixtures."""
    cm.load_protection = lambda base: protection
    cm.load_collaborators = lambda: list(collaborators)
    return cm.check(pr)


def test_self_approval_deadlock():
    """1 review required, admins enforced, the only writer is the author."""
    pr = make_pr()
    findings, mergeable = run_check(
        pr, with_protection(),
        collaborators=[{"login": pr["author"]["login"],
                        "permissions": {"push": True}}])
    assert not mergeable, findings
    assert any("PERMANENT BLOCK" in f for f in findings), findings
    assert any("--admin" in f for f in findings), findings


def test_missing_review_with_candidates():
    """Blocked, but a fix exists: a second write-capable account is named."""
    pr = make_pr()
    findings, mergeable = run_check(
        pr, with_protection(),
        collaborators=[{"login": pr["author"]["login"],
                        "permissions": {"push": True}},
                       {"login": "helper2", "permissions": {"push": True}}])
    assert not mergeable, findings
    assert not any("PERMANENT BLOCK" in f for f in findings), findings
    assert any("helper2" in f for f in findings), findings


def test_author_approval_does_not_count():
    """GitHub rejects self-approval; a self-badge APPROVED must not satisfy."""
    author = "tashingagwatidzo776-max"
    pr = make_pr(author=author,
                 reviews=[{"author": {"login": author},
                           "state": "APPROVED"}])
    findings, mergeable = run_check(
        pr, with_protection(),
        collaborators=[{"login": author, "permissions": {"push": True}}])
    assert not mergeable, findings
    assert any("0/1" in f for f in findings), findings


def test_approved_by_other_account_is_mergeable():
    pr = make_pr(reviews=[{"author": {"login": "helper2"},
                           "state": "APPROVED"}])
    findings, mergeable = run_check(
        pr, with_protection(),
        collaborators=[{"login": pr["author"]["login"],
                        "permissions": {"push": True}},
                       {"login": "helper2", "permissions": {"push": True}}])
    assert mergeable, findings
    assert any("1/1" in f for f in findings), findings


def test_no_protection_rule_is_mergeable_with_ruleset_warning():
    """404 protection = no classic rule; mergeable but rulesets are flagged."""
    findings, mergeable = run_check(make_pr(), None,
                                    collaborators=[])
    assert mergeable, findings
    assert any("rulesets" in f for f in findings), findings


def test_merge_conflict_blocks():
    findings, mergeable = run_check(
        make_pr(merge_state="DIRTY"), with_protection(rule_reviews=None),
        collaborators=[])
    assert not mergeable, findings
    assert any("conflict" in f.lower() for f in findings), findings


def test_unstable_checks_block():
    findings, mergeable = run_check(
        make_pr(merge_state="UNSTABLE"), with_protection(rule_reviews=None),
        collaborators=[])
    assert not mergeable, findings
    assert any("checks" in f.lower() for f in findings), findings


def test_merged_pr_is_nothing_to_do():
    findings, mergeable = run_check(make_pr(state="MERGED"), with_protection(),
                                    collaborators=[])
    assert mergeable, findings
    assert any("MERGED" in f for f in findings), findings


def main() -> int:
    tests = [v for k, v in sorted(globals().items())
             if k.startswith("test_") and callable(v)]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"FAIL {t.__name__}: {exc}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
