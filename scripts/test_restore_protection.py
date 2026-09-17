# Tests for scripts/restore_protection.py — the projection, verification, and
# relax logic run fully offline against synthetic snapshot fixtures (no API).
#
# Run: python scripts/test_restore_protection.py

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import restore_protection as rp

failures = 0


def check(name: str, cond: bool) -> None:
    global failures
    if not cond:
        failures += 1
        print(f"FAIL {name}")
    else:
        print(f"ok   {name}")


# A realistic GET body: object-form booleans, url fields, the works.
SNAPSHOT = {
    "url": "https://api.github.com/repos/o/r/branches/main/protection",
    "required_status_checks": {
        "url": "https://api.github.com/repos/o/r/branches/main/protection/required_status_checks",
        "strict": True,
        "contexts": ["unit", "integration", "coverage-report", "workflow-lint"],
        "contexts_url": "https://api.github.com/repos/o/r/branches/main/protection/required_status_checks/contexts",
        "checks": [{"context": "unit", "app_id": 15368}],
    },
    "enforce_admins": {"url": "https://api.github.com/repos/o/r/branches/main/protection/enforce_admins", "enabled": True},
    "required_pull_request_reviews": {
        "url": "https://api.github.com/repos/o/r/branches/main/protection/required_pull_request_reviews",
        "dismiss_stale_reviews": True,
        "require_code_owner_reviews": False,
        "required_approving_review_count": 1,
        "require_last_push_approval": False,
    },
    "restrictions": None,
    "required_signatures": {"enabled": False},
    "required_linear_history": {"enabled": False},
    "allow_force_pushes": {"enabled": False},
    "allow_deletions": {"enabled": False},
    "block_creations": {"enabled": False},
    "required_conversation_resolution": {"enabled": False},
    "lock_branch": {"enabled": False},
    "allow_fork_syncing": {"enabled": False},
}

# 1. Projection: read-model fields stripped, booleans unwrapped.
payload = rp.project(SNAPSHOT)
check("projection drops url fields", "url" not in json.dumps(payload))
check("projection unwraps enforce_admins", payload["enforce_admins"] is True)
check("projection keeps contexts", payload["required_status_checks"]["contexts"] == ["unit", "integration", "coverage-report", "workflow-lint"])
check("projection keeps strict", payload["required_status_checks"]["strict"] is True)
check("projection keeps review count", payload["required_pull_request_reviews"]["required_approving_review_count"] == 1)
check("projection keeps dismiss_stale", payload["required_pull_request_reviews"]["dismiss_stale_reviews"] is True)
check("projection nulls restrictions", payload["restrictions"] is None)

# 2. The relax step: only the review count moves, everything else holds.
relaxed = rp.project(SNAPSHOT, reviews_override=0)
check("relax zeroes review count", relaxed["required_pull_request_reviews"]["required_approving_review_count"] == 0)
check("relax keeps dismiss_stale", relaxed["required_pull_request_reviews"]["dismiss_stale_reviews"] is True)
check("relax keeps enforce_admins", relaxed["enforce_admins"] is True)
check("relax keeps strict", relaxed["required_status_checks"]["strict"] is True)

# 3. Verification passes when the read-back matches (restore and relax).
class FakeResp:
    def __init__(self, body): self.body = body
    def read(self): return json.dumps(self.body).encode()
    def __enter__(self): return self
    def __exit__(self, *a): return False

after_full = rp.project(SNAPSHOT)  # server echoes it back with unwrapped booleans
rows = rp.verify(SNAPSHOT, after_full)
check("verify: all rows OK on identical restore", all(b == a for _, b, a in rows) and len(rows) == 10)
after_relaxed = rp.project(SNAPSHOT, reviews_override=0)
rows = rp.verify(SNAPSHOT, after_relaxed, reviews_override=0)
check("verify: all rows OK on identical relax", all(b == a for _, b, a in rows) and len(rows) == 10)

# 3b. A relax that did not take (read-back still 1) IS a mismatch.
rows = rp.verify(SNAPSHOT, after_full, reviews_override=0)
check("verify: detects failed relax", any(n == "approvals" and b != a for n, b, a in rows))

# 4. Verification catches a real drift: admin enforcement silently missing.
after_broken = dict(after_full)
after_broken["enforce_admins"] = {"enabled": False}
rows = rp.verify(SNAPSHOT, after_broken)
check("verify: detects enforce_admins drift", any(n == "enforce_admins" and b != a for n, b, a in rows))

# 5. Verification catches a dropped required context.
after_dropped = dict(after_full)
after_dropped["required_status_checks"] = {"strict": True, "contexts": ["unit", "integration"]}
rows = rp.verify(SNAPSHOT, after_dropped)
check("verify: detects dropped contexts", any(n == "contexts" and b != a for n, b, a in rows))

# 6. Verification catches a restore that forgot to put the review count back.
rows = rp.verify(SNAPSHOT, after_full)  # snapshot says 1, read-back says 1 -> OK
check("verify: review count compared", ("approvals", 1, 1) in rows)

print()
print(f"{failures} failure(s)")
sys.exit(1 if failures else 0)
