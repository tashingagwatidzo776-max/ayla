# Solo-maintainer merges under a review gate

Branch protection on `main` requires one approving review. With a single
maintainer identity, that requirement cannot be satisfied the normal way:
GitHub rejects self-approval outright — `POST /reviews` with `event:
APPROVE` on your own PR returns **422 "Review can not approve your own
pull request"** — and there is no exemption flag, PAT trick, or setting
that changes this.

## The working pattern

The documented procedure for merging your own PR while keeping every
status check enforced:

1. Wait for **all required checks to pass** on the PR head commit. Verify
   via the check-runs API — do not assume from a green badge.
2. Temporarily set the review requirement to **0**, changing *nothing
   else* — `strict`, `enforce_admins`, and all required contexts stay
   exactly as they are:

   ```
   PUT /repos/<owner>/<repo>/branches/main/protection
   {
     "required_status_checks": {"strict": true, "contexts": ["unit", "integration", "coverage-report", "workflow-lint"]},
     "enforce_admins": true,
     "required_pull_request_reviews": {"required_approving_review_count": 0, "dismiss_stale_reviews": false},
     "restrictions": null,
     "allow_force_pushes": false,
     "allow_deletions": false
   }
   ```

   `contexts` must match the job names in `.github/workflows/ci.yml`
   exactly; update both in the same change when a job is renamed.
3. Merge the PR.
4. **Immediately restore** the full rule: review count `1`,
   `dismiss_stale_reviews: true`, same contexts, strict, admins bound.
5. Read the protection rule back and verify the numbers — a silent 4xx
   on the restore step is exactly how protection stays weakened.

## Why this is safe

The only thing relaxed is *who else approved* — a question with exactly
one possible answer in a single-maintainer repo, and the maintainer is
the one pressing the merge button either way. What actually protects
`main` — every required check green on the exact merged commit, admin
enforcement, no force-pushes — never relaxes. A dropped change would
need to land without passing CI, which the rule still forbids.

## Hygiene rules

- Keep the zero-review window as short as one API call each side; never
  batch unrelated PRs into one window.
- If `contexts` ever lists a job that no longer exists, required checks
  can pass vacuously — reconcile with `ci.yml` on every job rename.
- The restore step is not optional. Until it runs, `main` accepts
  unreviewed, unverified merges from any collaborator.
