# Required status checks — setup checklist

Makes the CI pipeline mandatory before anything lands on `main`. One-time
setup; run it once after the first successful CI run on the repo.

## What becomes required

| Check | Enforces |
|---|---|
| `unit` | Release build + `Category=Unit` tests with coverage |
| `integration` | Fake-server E2E tests (`Category=Integration`) with coverage |
| `coverage-report` | Merged coverage report + **60% combined line-coverage gate** |

Plus `strict: true` — a PR's branch must be up to date with `main` before the
merge button unlocks.

## Pre-flight checklist

- [ ] CI has completed at least once on a pushed commit (status checks only
      become selectable/configurable after their first run).
- [ ] **GitHub Pages enabled:** Settings → Pages → Build and deployment →
      Source: **GitHub Actions**. One-time toggle; without it
      `coverage-pages.yml` fails on its first deploy.
- [ ] Actions enabled (private repos): Settings → Actions → General →
      "Allow all actions".
- [ ] For the API path: a token with admin rights — classic PAT with `repo`
      scope, or fine-grained PAT with **Administration: read & write** on this
      repository.

## GitHub UI path (no token needed)

1. **Settings → Branches → Add classic branch protection rule**
   (or *Add branch ruleset* → New branch ruleset).
2. **Branch name pattern:** `main`
3. ☑ **Require a pull request before merging** (recommended; skip this if you
   want direct pushes to stay allowed but checks still enforced).
4. ☑ **Require status checks to pass before merging** → search and select
   `unit`, `integration`, `coverage-report`.
5. ☑ **Require branches to be up to date before merging**.
6. ☑ **Do not allow bypassing the above settings** / *Include administrators*
   so the rules bind admins too.
7. **Save changes.**

## Exact API call (once an admin token exists)

```bash
export GH_TOKEN=<paste-a-PAT-with-admin-rights>
OWNER=tashingagwatidzo776-max
REPO=ayla

curl -sS -X PUT \
  -H "Authorization: Bearer $GH_TOKEN" \
  -H "Accept: application/vnd.github+json" \
  "https://api.github.com/repos/$OWNER/$REPO/branches/main/protection" \
  -d @- <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "checks": [
      { "context": "unit" },
      { "context": "integration" },
      { "context": "coverage-report" }
    ]
  },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "required_approving_review_count": 1,
    "dismiss_stale_reviews": true
  },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_linear_history": false,
  "lock_branch": false
}
JSON
```

- `"checks"` is the modern payload (job-name contexts; also accepts `app_id`).
  The older `"contexts": ["unit", "integration", "coverage-report"]` works too.
- To enforce checks **without** mandating PR reviews (direct pushes allowed),
  replace the `required_pull_request_reviews` object with `null`.
- `enforce_admins: true` keeps admins from pushing around the gate.

## Verify it took effect

```bash
curl -sS -H "Authorization: Bearer $GH_TOKEN" \
  "https://api.github.com/repos/$OWNER/$REPO/branches/main/protection" \
  | jq '.required_status_checks'
```

Expected: `"strict": true` and the three contexts listed. Common errors:
`404` with a fine-grained token means the **Administration** permission wasn't
granted to the repo; `403` means the token lacks admin rights.

## Maintenance notes

- The three contexts mirror the job names in `.github/workflows/ci.yml`.
  If a job is ever renamed, update the protection rule **in the same change**
  or merges hang waiting for a check that no longer exists.
- The 60% gate lives inside the `coverage-report` job; see the step
  *Enforce minimum line coverage* in `ci.yml`. Raise the gate there (and in
  the matching `::error` message) when the measured coverage improves.

## Creating a fine-grained PAT (step by step)

1. GitHub → Settings → Developer settings → **Personal access tokens →
   Fine-grained tokens** → *Generate new token*.
2. **Token name** something auditable, e.g. `ayla-branch-protection`; set an
   expiry you will actually remember.
3. **Resource owner:** `tashingagwatidzo776-max`.
4. **Repository access:** *Only select repositories* → `ayla`.
5. **Permissions:** Repository permissions → **Administration: Read and
   write** — this one permission covers both reading and updating branch
   protection. Everything else can stay *No access*.
6. Generate, copy the token (it is shown once), and keep it in your credential
   helper or `GH_TOKEN` — never in the repo.

The API call in this doc then works verbatim with `Authorization: Bearer
<token>`. Failure modes: `404` ⇒ the token's repository list doesn't include
`ayla` or *Administration* wasn't granted; `403` ⇒ token lacks admin rights;
and on a **private** repo on the free plan the endpoint itself returns
`403 "Upgrade to GitHub Pro or make this repository public"` — branch
protection and Pages both need a public repo on the free plan, which is why
`ayla` is public.

The PAT is only needed when (re-)applying the rule via API or after a job
rename; the protection rule itself persists until changed.
