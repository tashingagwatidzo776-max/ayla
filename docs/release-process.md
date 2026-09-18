# Release process

A release is a `v*` tag. Everything else in the chain is machinery that already
exists: the tag push triggers the real-money gate drill on the exact commit
being shipped, a hard `release-gate` job blocks the pipeline if the drill did
not pass, and `publish-exe` builds the self-contained Windows exe only behind
that gate. Nothing in CI pushes a binary anywhere public — the artifact is
uploaded to the run, and the GitHub Release (notes + asset) is a deliberate
human-driven step.

```
git push origin vX.Y.Z
        │
        ▼
gate-drill ............ rehearses the FULL real-money gate lifecycle on the tagged commit
        │               (Core decision/parse matrix + App E2E against the fake broker)
        ▼
release-gate .......... hard stop: fails unless gate-drill was green on this exact commit
        │
        ▼
publish-exe ........... dotnet publish (self-contained win-x64 single file)
        │               → artifact Tf-<tag>-win-x64 on the run
        ▼
gh release create ..... human step: release notes + the downloaded artifact
```

The invariant: **no binary ships from a commit whose gate drill has not passed
on that commit.** The last scheduled drill proves nothing about *this* commit;
the tag re-proves it.

## Cutting a release

Prerequisites — all four, checked in order:

1. Local `main` synced, working tree clean, on `main` itself (tags from dirty
   or unsynced trees are how "the artifact doesn't match the notes" happens).
2. Latest CI run on `main` is green (`gh run list --workflow CI --branch main --limit 1`).
3. No open drift: `gh issue list --label ci-drift --label ci-gate-drill --state open`
   returns nothing (except drill-rehearsal issues, which carry the `-drill` suffixed labels).
4. The release-prep PRs (if any) are merged — see
   [Merging release-prep PRs](#merging-release-prep-prs-the-admin-merge) below.

Then:

```bash
# 1. Draft the notes from the merged PRs (see "Release notes" below).
$EDITOR release-notes-vX.Y.Z.md

# 2. Tag (annotated) and push — the push is the trigger.
git tag -a vX.Y.Z -m "Tf vX.Y.Z"
git push origin vX.Y.Z

# 3. Watch the chain (three jobs, in order: gate-drill → release-gate → publish-exe).
gh run watch $(gh run list --workflow gate-drill.yml --branch vX.Y.Z --limit 1 --json databaseId --jq '.[0].databaseId')

# 4. Download and verify the artifact.
gh run download <run-id> --name Tf-vX.Y.Z-win-x64 -D dist/vX.Y.Z
# Sanity: PE32+ console/GUI x86-64, ~70 MB self-contained, launches to the UI.

# 5. Create the release — draft first (notes only), then attach the asset.
gh release create vX.Y.Z --draft --title "Tf vX.Y.Z" --notes-file release-notes-vX.Y.Z.md
gh release upload vX.Y.Z dist/vX.Y.Z/Tf.exe
# ...verify the draft renders correctly, then:
gh release edit vX.Y.Z --draft=false
```

Draft-first is deliberate: a published release notifies watchers; a draft does
not. A draft with a broken note or wrong artifact costs nothing to fix.

**When the local link to the blob host is slow or down** (artifact downloads
and asset uploads both traverse it), attach the asset from GitHub's own
infrastructure instead — Actions → Actions, no throttled hop:

```bash
# Create the draft (notes only) locally as above, then dispatch:
gh workflow run publish-release.yml -f tag=vX.Y.Z   # run id input optional
gh run watch $(gh run list --workflow publish-release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
# The job downloads the tag's artifact on the runner, verifies the version
# stamp matches the tag, prints the sha256, and uploads the asset (--clobber,
# idempotent). Publishing the draft stays a local one-liner (tiny API call):
gh release edit vX.Y.Z --draft=false
```

The workflow requires the draft to exist first and refuses to run for a tag
without a successful gate-drill run — the gate → publish ordering is enforced
there too. v0.0.2 shipped through this path when a provider-side throttle
reduced the blob host to ~11 KB/s.

### Release notes

For the first release, notes summarize the project; afterwards, draft from the
PRs merged since the last tag:

```bash
gh pr list --state merged --limit 50 \
  --json number,title,mergedAt \
  --jq '.[] | select(.mergedAt > "<previous tag date>") | "#\(.number) \(.title)"'
```

Group by area (engines, risk/real-money safety, monitoring, CI/repo machinery)
rather than listing PRs raw — the notes are for the person deciding whether to
run the binary, not for the reviewers who already read the PRs. Mention the
real-money safety state explicitly in every release: which rails changed, and
the audit doc's coverage table hash if it moved.

## What each job means — and what a failure means

| Job | What it does | If it fails | What to do |
|---|---|---|---|
| `gate-drill` | Runs the real-money gate test suite on the tagged commit — every test class carrying the `Category=RealMoney` trait (Core decision/parse matrix + journal arm audit; App manual-surface gate, unlock arming/audit, Growth-tab panels, digest arm leg, manual stake cap, hub E2E and mid-session stop). Selection is by trait, not name: a new rail class joins the drill the moment it carries `Category=RealMoney`. All against the fake broker: no network, no real funds. | The gate is broken **or flaky on the exact commit being shipped**. An alert issue auto-files on `ci-gate-drill` with a log excerpt. | **Do not ship.** Fix on `main` via PR, merge, then tag again (see rollback below — never "re-run until green"). |
| `release-gate` | The hard stop: fails unless `gate-drill`'s result on this run is `success`. No code of its own. | Almost always transitive — the drill failed or was skipped. A red `release-gate` over a green drill means an Actions/infra hiccup. | Drill red → fix as above. Drill green but gate red → re-run the *release-gate* job; it is safe to re-run because the gate proof (the drill) still holds on this commit. |
| `publish-exe` | Builds the self-contained win-x64 single-file exe (same recipe as `scripts/publish_exe.ps1`) and uploads it as the run artifact. Only runs behind `release-gate`. | A build/publish failure (SDK, runner disk, packaging), or the artifact step found no file (`if-no-files-found: error`). | The gate proof is unaffected — re-run the job after checking the publish log. No re-tag needed. |

Two related local behaviors, same invariant:

- `scripts/publish_exe.ps1` **refuses to publish locally** from a `v*` tag with
  no passing drill — it queries `gh run list --workflow gate-drill.yml --branch <tag>`
  and throws `RELEASE BLOCKED`. Building from a non-tag commit skips the check.
- `scripts/ci-local.ps1` is the pre-push mirror of the required checks (see
  [Pre-push hook](#pre-push-hook-stale-copy-caveat)) — it keeps a release-prep
  push from ever creating a PR that cannot merge or will fail CI.

## Rollback: deleting a bad tag

A tag is disposable by design. Deleting it removes the trigger target — the
chain can never re-run for that tag — and it takes the release with it.

```bash
# Release + remote tag + local ref, in one command:
gh release delete vX.Y.Z --yes --cleanup-tag

# If the release was never created (chain failed before step 5):
git push origin :refs/tags/vX.Y.Z   # delete the remote tag
git tag -d vX.Y.Z                   # delete the local tag
```

Rules that keep rollback boring:

- **Never reuse a tag name for a different commit.** Anything that consumed the
  artifact identified it by tag; a silently re-pointed tag is indistinguishable
  from the original. Delete, then tag the fixed commit as `vX.Y.(Z+1)` (or a
  new pre-release suffix). Only re-use the exact name if the re-tag points at
  the *same* commit and you deleted everything first.
- **Already-downloaded copies are out in the wild.** Deleting a release removes
  the download, not the binary. If a bad binary actually shipped published,
  the fix is communicate + supersede (cut the corrected release immediately),
  not "unpublish and pretend".
- **A failed chain does not need a rollback** — no release was created, so
  deleting the tag is only hygiene. Keep the tag if you plan to re-tag the
  same commit after a re-run; the drill is what must be green, and re-running
  a *skipped/infra-failed* leg is legitimate. Re-running a *failing* drill is
  not (flake fixes go through `main` first — this exact situation happened
  between `v0.0.1-rc1` and the fix in PR #53). The same applies when a tag's
  *first* drill run fails: delete the tag, fix via `main`, re-tag the fixed
  commit — never re-run the drill on a red tag to squeeze a binary out.

Tags are not covered by branch protection — anyone with write access can push
one. The gate, not permissions, is the release invariant: a bad tag still
cannot produce a binary, because `release-gate` sits in front of `publish-exe`.

## Merging release-prep PRs (the admin merge)

Branch protection on `main` requires 1 approving review with admins enforced,
and the release manager is typically also the PR author — self-approval is
rejected by GitHub with 422. The sanctioned procedure is the one documented in
[`docs/solo-maintainer-merges.md`](solo-maintainer-merges.md):
**snapshot → relax → merge → restore → verify** — the review requirement is
temporarily set to **0** rather than deleting the rule, so `strict`,
`enforce_admins`, and every required context stay enforced the whole time.

```bash
export GH_TOKEN=$(printf "protocol=https\nhost=github.com\n" | git credential fill | grep '^password=' | cut -d= -f2)

# 1. Snapshot the full rule verbatim (for reference and verification).
gh api repos/tashingagwatidzo776-max/ayla/branches/main/protection > /tmp/protection.json

# 2. Wait for ALL required checks to pass on the PR head commit (verify, do
#    not assume): gh pr checks <N>

# 3. Relax ONLY the review count to 0 (PUT via the projection script).
python scripts/restore_protection.py /tmp/protection.json --reviews 0

# 4. Merge.
gh pr merge <N> --merge

# 5. Restore the full rule from the snapshot.
python scripts/restore_protection.py /tmp/protection.json

# 6. Confirm the script printed PROTECTION RESTORED IDENTICAL. If any restore
#    step fails, STOP and restore before doing anything else — until the
#    restore lands, main accepts unreviewed, unverified merges.
```

Two hard-won details, each of which has actually bitten:

- **The protection GET response is not a valid PUT body.** Read-model fields —
  `url`s, `contexts_url`, object-form booleans like `{"enabled": true}` — are
  rejected by the write schema with HTTP 422. `scripts/restore_protection.py`
  exists precisely to project the snapshot into the writable schema; feeding a
  snapshot file straight to `gh api -X PUT --input` fails every time. (History:
  a bad flag, a broken heredoc terminator, and the 422 itself.)
- **Verify semantically, field by field.** A silent 4xx on the restore is
  exactly how protection stays weakened while everyone believes it is back.
  The script reads the rule back and diffs it against the snapshot; trust its
  `PROTECTION RESTORED IDENTICAL` verdict, nothing less.

The relax window is seconds and only ever relaxes *who else approved* — a
question with exactly one possible answer in a single-maintainer repo. What
actually protects `main` (every required check green on the merged commit,
admin enforcement, no force-pushes) never relaxes.

## Pre-push hook (stale-copy caveat)

`scripts/git-hooks/pre-push` runs `scripts/ci-local.ps1` (build → unit →
integration → workflow lint → safety-audit coverage → merge preview) before
every branch push, so CI failures and unmergeable PRs are caught locally.
Enable it in a clone with:

```bash
git config core.hooksPath scripts/git-hooks
```

The hook file lives in the repo, but Git does not auto-update already-enabled
clones: after pulling a commit that changes `scripts/git-hooks/pre-push`, an
enabled clone keeps running the **old** copy until you re-run the
`git config core.hooksPath scripts/git-hooks` command above.

A push that delivers an **open PR's head branch** (a rebase, a fix, any head
update) is *remedial*: every merge-preview finding describes the pre-push
head, so findings are reported and **deferred** (exit 3) instead of blocking
the push — CI re-runs on the pushed head and the merge gate re-checks at
merge time. Before this, delivering a rebase to the strict, review-protected
main deadlocked: the hook blocked the one push that fixed the block, and
only `--no-verify` could land it (PR #60's case; `check_mergeability.py`
exits 3, `ci-local.ps1` maps it to DEFERRED). Hard blocks survive where the
push cannot be the remedy: merge conflicts (DIRTY), pre-PR pushes, and all
non-merge-preview checks.

Bypass for a deliberate, exceptional push: `git push --no-verify`. Faster
iteration on non-test changes: `TF_CI_LOCAL_SKIP_BUILD=1` (or
`git config hooks.ciLocalSkipBuild true`) skips the build step.

## History

| Tag | What happened |
|---|---|
| `v0.0.4` | First-run wizard fixes shipped: settings merge (no more clobber), token floor enforced, skip writes the flag (#72); `publish_exe.ps1` now cleans its output dir first (#73). Chain green first-run on `73180fc`; the hardened attach succeeded on the **first** dispatch — no retries needed, first release where nothing went sideways. |
| `v0.0.3` | Shipped the go-live readiness panel. Chain green first-run on `5e7f375`; the release asset attached server-side on the second dispatch — the first failed with GitHub's `Error creating asset temp dir` and **the workflow reported success anyway** (curl without `--fail`), which became PR #69: `--fail-with-body`, 3 retries, and a stored-size verification. Soak evidence now accumulates in `docs/soak/` and the weekly drill enforces its freshness (#68). |
| `v0.0.1-rc1` | Dry run: first full walk of drill → release-gate → publish-exe; artifact downloaded and verified; tag deleted afterwards. Its first run exposed a flaky gate test (async race), fixed via PR #53 before the real release. |
| `v0.0.1` | First real release. Its first tag run failed the drill on a cross-collection race around the static manual-unlock latch (parallel test classes resetting the shared gate mid-assertion) — tag deleted, fix pinned the three gate classes into one xunit collection, re-tagged on the fixed commit. |
| `v0.0.2` | First fully green first-run chain (drill → gate → publish on `c7df244`, all three jobs). The manual asset attach was blocked by a provider-side blob-host throttle (~11 KB/s single-stream); shipped via the new `publish-release.yml` path (Actions → Actions). The local download still completed by assembling 8 parallel ranged streams — recorded here because the artifact endpoint under-reported its size to the HEAD probe (use the API's `size_in_bytes`). |
