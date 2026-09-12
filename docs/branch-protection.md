# Branch protection for `main`

The CI pipeline (`.github/workflows/ci.yml`) runs three jobs on every push
and pull request targeting `main`:

| Job | What it does |
|---|---|
| `unit` | Builds Release and runs `Category=Unit` tests with coverage collection |
| `integration` | Runs `Category=Integration` fake-server E2E tests with coverage collection |
| `coverage-report` | Merges both cobertura files, generates the HTML report, and **fails if combined line coverage drops below 60%** |

To make all three mandatory before anything lands on `main`, enable branch
protection once (requires an administrator of the repository).

## GitHub UI (recommended)

1. Open **Settings → Branches → Add branch protection rule** (or
   *Add classic branch protection rule*).
2. Set **Branch name pattern** to `main`.
3. Tick **Require a pull request before merging** if PRs should be mandatory,
   then also tick **Require status checks to pass before merging**.
4. In the status-check search box, select all three from the list (they only
   appear after their first successful run on a pushed commit):
   - `unit`
   - `integration`
   - `coverage-report`
5. Tick **Require branches to be up to date before merging** so merges are
   blocked when `main` has moved on and checks are stale.
6. (Optional, recommended) Tick **Require signed commits** and
   **Include administrators** so the rules bind everyone.
7. Save. From now on, the merge button stays disabled until all three jobs
   are green and the 60% coverage gate passes.

## GitHub API (alternative)

With a token that has repository administration rights:

```bash
TOKEN=<your PAT with admin:repo>
OWNER=tashingagwatidzo776-max
REPO=ayla

curl -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Accept: application/vnd.github+json" \
  https://api.github.com/repos/$OWNER/$REPO/branches/main/protection \
  -d '{
    "required_status_checks": {
      "strict": true,
      "contexts": ["unit", "integration", "coverage-report"]
    },
    "enforce_admins": true,
    "required_pull_request_reviews": {
      "required_approving_review_count": 1
    },
    "restrictions": null,
    "allow_force_pushes": false,
    "allow_deletions": false
  }'
```

`contexts` must match the job names in `ci.yml` exactly. If the repo later
renames a job, update the protection rule in the same change.

## Notes

- The `coverage-report` job is the gate: it consumes both test jobs' cobertura
  artifacts and fails the run on any regression below 60% combined line
  coverage (measured at 63.3% when the gate was raised).
- Status checks only become selectable after each job has completed at least
  once on a commit pushed to the repo, so push once before configuring.
- On private repos, ensure Actions are enabled under
  **Settings → Actions → General**; the windows-latest runners bill against
  the free per-account minutes quota.
