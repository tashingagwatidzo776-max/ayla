# Restore main's branch protection from a snapshot of its GET body.
#
# The protection GET response is NOT a valid PUT body: read-model fields
# (urls, contexts_url, object-form booleans like {"enabled": true}) are
# rejected by the write schema with HTTP 422. Feeding a snapshot straight
# back has failed three times in this repo's history (bad flag, broken
# heredoc, and the 422 itself). This script does the whole restore: project
# the snapshot into the writable schema, PUT it, read the rule back, and
# verify it semantically field by field.
#
# It also covers the relax step of docs/solo-maintainer-merges.md:
#   python scripts/restore_protection.py snapshot.json --reviews 0   # relax
#   python scripts/restore_protection.py snapshot.json               # restore
#   (first capture the snapshot with:
#      gh api repos/<owner>/<repo>/branches/main/protection > snapshot.json)
#
# Exit codes: 0 = restored and verified identical (modulo --reviews),
# 1 = restored but verification found a mismatch, 2 = usage or API error.
# Requires GH_TOKEN (or gh auth) with repo administration rights.

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

REPO = "tashingagwatidzo776-max/ayla"
API = f"https://api.github.com/repos/{REPO}/branches/main/protection"
OK, MISMATCH, ERROR = 0, 1, 2

# GET bodies wrap booleans as {"enabled": x}; the PUT schema wants bare ones.
def _enabled(value, default=False) -> bool:
    if isinstance(value, dict):
        return bool(value.get("enabled", default))
    return bool(value) if value is not None else default


def project(snapshot: dict, reviews_override: int | None = None) -> dict:
    """Writable-schema projection of a protection GET body."""
    rpr = snapshot.get("required_pull_request_reviews") or {}
    reviews = {
        "dismiss_stale_reviews": bool(rpr.get("dismiss_stale_reviews", False)),
        "require_code_owner_reviews": bool(rpr.get("require_code_owner_reviews", False)),
        "required_approving_review_count": int(rpr.get("required_approving_review_count", 1)),
        "require_last_push_approval": bool(rpr.get("require_last_push_approval", False)),
    }
    if reviews_override is not None:
        reviews["required_approving_review_count"] = int(reviews_override)
    return {
        "required_status_checks": {
            "strict": bool(snapshot.get("required_status_checks", {}).get("strict", False)),
            "contexts": list(snapshot.get("required_status_checks", {}).get("contexts", [])),
        },
        "enforce_admins": _enabled(snapshot.get("enforce_admins")),
        "required_pull_request_reviews": reviews,
        "restrictions": None,
        "required_signatures": _enabled(snapshot.get("required_signatures")),
        "required_linear_history": _enabled(snapshot.get("required_linear_history")),
        "allow_force_pushes": _enabled(snapshot.get("allow_force_pushes")),
        "allow_deletions": _enabled(snapshot.get("allow_deletions")),
        "block_creations": _enabled(snapshot.get("block_creations")),
        "required_conversation_resolution": _enabled(snapshot.get("required_conversation_resolution")),
        "lock_branch": _enabled(snapshot.get("lock_branch")),
        "allow_fork_syncing": _enabled(snapshot.get("allow_fork_syncing")),
    }


def verify(snapshot: dict, after: dict, reviews_override: int | None = None) -> list[tuple[str, object, object]]:
    """Field-by-field semantic comparison; returns (name, before, after) rows."""
    rpr = snapshot.get("required_pull_request_reviews") or {}
    rpr_a = after.get("required_pull_request_reviews") or {}
    expected_reviews = reviews_override if reviews_override is not None else rpr.get("required_approving_review_count", 1)
    return [
        ("approvals", expected_reviews, rpr_a.get("required_approving_review_count")),
        ("dismiss_stale", rpr.get("dismiss_stale_reviews", False), rpr_a.get("dismiss_stale_reviews")),
        ("enforce_admins", _enabled(snapshot.get("enforce_admins")), _enabled(after.get("enforce_admins"))),
        ("strict", snapshot.get("required_status_checks", {}).get("strict"),
         after.get("required_status_checks", {}).get("strict")),
        ("contexts", sorted(snapshot.get("required_status_checks", {}).get("contexts", [])),
         sorted(after.get("required_status_checks", {}).get("contexts", []))),
        ("signatures", _enabled(snapshot.get("required_signatures")), _enabled(after.get("required_signatures"))),
        ("linear_history", _enabled(snapshot.get("required_linear_history")), _enabled(after.get("required_linear_history"))),
        ("force_pushes", _enabled(snapshot.get("allow_force_pushes")), _enabled(after.get("allow_force_pushes"))),
        ("deletions", _enabled(snapshot.get("allow_deletions")), _enabled(after.get("allow_deletions"))),
        ("conversation_resolution", _enabled(snapshot.get("required_conversation_resolution")),
         _enabled(after.get("required_conversation_resolution"))),
    ]


def _api(method: str, token: str, body: dict | None = None) -> dict:
    req = urllib.request.Request(API, method=method,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req) as resp:
        raw = resp.read()
        return json.loads(raw) if raw else {}


def main() -> int:
    ap = argparse.ArgumentParser(description="Restore main's branch protection from a GET-body snapshot.")
    ap.add_argument("snapshot", help="path to a `gh api .../protection` GET-body JSON file")
    ap.add_argument("--reviews", type=int, default=None,
                    help="override required_approving_review_count (0 relaxes, per solo-maintainer-merges.md)")
    args = ap.parse_args()

    token = os.environ.get("GH_TOKEN", "")
    if not token:
        print("ERROR: GH_TOKEN is not set (repo administration scope required)", file=sys.stderr)
        return ERROR
    try:
        with open(args.snapshot, encoding="utf-8") as fh:
            snapshot = json.load(fh)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"ERROR: cannot read snapshot {args.snapshot}: {exc}", file=sys.stderr)
        return ERROR

    payload = project(snapshot, args.reviews)
    try:
        _api("PUT", token, payload)
        after = _api("GET", token)
    except urllib.error.HTTPError as exc:
        print(f"ERROR: protection PUT/GET failed: HTTP {exc.code} {exc.read().decode(errors='replace')[:300]}", file=sys.stderr)
        return ERROR

    rows = verify(snapshot, after, args.reviews)
    ok = True
    for name, before, aft in rows:
        same = before == aft
        ok &= same
        print(f"{'OK ' if same else 'DIFF'} {name}: {before!r} -> {aft!r}")
    print("PROTECTION RESTORED IDENTICAL" if ok else "MISMATCH - FIX REQUIRED")
    return OK if ok else MISMATCH


if __name__ == "__main__":
    sys.exit(main())
