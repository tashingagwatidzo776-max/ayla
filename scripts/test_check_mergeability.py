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
    """Runs check() with the gh loaders stubbed to the given fixtures.

    current_branch is stubbed to None (no remedial context) unless the test
    passes a head branch explicitly to check()."""
    cm.load_protection = lambda base: protection
    cm.load_collaborators = lambda: list(collaborators)
    cm.current_branch = lambda: None
    findings, mergeable, _remedial = cm.check(pr)
    return findings, mergeable


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


# --- remedial pushes (the pushed branch is the open PR's head) ---

REMEDIATE_FIXTURE_PR = {"number": 1, "title": "synthetic", "state": "OPEN",
                        "author": {"login": "tashingagwatidzo776-max"},
                        "baseRefName": "main", "headRefName": "feature/x",
                        "mergeStateStatus": "CLEAN", "reviews": []}


def remedial_check(merge_state, reviews=None):
    cm.load_protection = lambda base: with_protection()
    cm.load_collaborators = lambda: [{"login": "tashingagwatidzo776-max",
                                      "permissions": {"push": True}}]
    cm.current_branch = lambda: "feature/x"
    pr = dict(REMEDIATE_FIXTURE_PR, mergeStateStatus=merge_state,
              reviews=reviews or [])
    return cm.check(pr, head_branch="feature/x")


def test_remedial_review_deficit_defers_instead_of_blocking():
    """#60's deadlock: 1 review required, the only writer is the author, and
    the push delivers the PR's own head. Must NOT block the push."""
    findings, mergeable, remedial = remedial_check("BEHIND")
    assert remedial, findings
    assert mergeable, findings
    assert any("deferred" in f.lower() for f in findings), findings
    assert not any("PERMANENT BLOCK" in f for f in findings), findings


def test_remedial_behind_defers_merge_state():
    """BEHIND on the pre-push head is exactly what a rebase push remedies."""
    findings, mergeable, remedial = remedial_check("BEHIND")
    assert remedial and mergeable, findings
    assert any("PRE-push" in f for f in findings), findings


def test_remedial_unstable_defers_merge_state():
    """Required checks pending on the pre-push head: the push re-runs CI."""
    findings, mergeable, remedial = remedial_check("UNSTABLE")
    assert remedial and mergeable, findings


def test_remedial_dirty_still_blocks():
    """A merge conflict is not cured by pushing the same branch - the PR
    needs a conflict resolution, so this stays a hard block."""
    findings, mergeable, remedial = remedial_check("DIRTY")
    assert remedial, findings
    assert not mergeable, findings
    assert any("conflict" in f.lower() for f in findings), findings


def test_pre_pr_review_deficit_still_blocks():
    """Same deficit as the remedial case, but the pushed branch is NOT the
    PR head (e.g. first push of a new branch whose PR is not open yet):
    the hard block must survive."""
    cm.load_protection = lambda base: with_protection()
    cm.load_collaborators = lambda: [{"login": "tashingagwatidzo776-max",
                                      "permissions": {"push": True}}]
    cm.current_branch = lambda: "feature/other"
    pr = dict(REMEDIATE_FIXTURE_PR)  # headRefName=feature/x, branch differs
    findings, mergeable, remedial = cm.check(pr, head_branch="feature/other")
    assert not remedial and not mergeable, findings
    assert any("PERMANENT BLOCK" in f for f in findings), findings


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
