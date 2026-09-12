#!/usr/bin/env bash
# Shared logic for the nightly drift alert and the on-demand drift drill.
#
# Modes:
#   fail     open (or update) the ci-drift issue for a failing scheduled run
#   resolve  close the open ci-drift issue for a green scheduled run
#
# Inputs via environment:
#   DRIFT_MODE      'fail' | 'resolve'
#   RUN_URL         URL of the CI run that produced the verdict
#   RUN_NUMBER      CI run number, for the alert body
#   COMMIT_SHA      commit the run was on
#   DRILL_LABEL     label to use ('ci-drift' for real runs, 'ci-drift-drill' for drills)
#   ISSUE_TITLE     issue title used when creating
#   FAILING_JOBS    (fail mode) comma-separated names of the failing jobs
#
# Requires GH_TOKEN and gh. Idempotent: repeated 'fail' runs append to the
# same issue; 'resolve' is a no-op when there is nothing open.
set -euo pipefail

: "${DRIFT_MODE:?DRIFT_MODE must be 'fail' or 'resolve'}"
: "${RUN_URL:?}"
: "${RUN_NUMBER:?}"
: "${COMMIT_SHA:?}"
: "${DRILL_LABEL:?}"
: "${ISSUE_TITLE:=}" # only used by fail mode
: "${FAILING_JOBS:=}"

label_issue() {
  # Make sure the label exists, then return the number of the open issue.
  gh label create "$DRILL_LABEL" --repo "${GITHUB_REPOSITORY:?}" \
    --description "Scheduled CI run failing - possible environment drift" \
    --color D93F0B --force >/dev/null 2>&1 || true
  gh issue list --repo "$GITHUB_REPOSITORY" --state open --label "$DRILL_LABEL" \
    --json number --jq '.[0].number'
}

if [ "$DRIFT_MODE" = "resolve" ]; then
  existing=$(label_issue)
  if [ -n "$existing" ]; then
    gh issue close "$existing" --repo "$GITHUB_REPOSITORY" \
      --comment "CI run [${RUN_NUMBER}](${RUN_URL}) is green again — closing automatically."
    echo "Closed drift issue #$existing"
  else
    echo "No open $DRILL_LABEL issue; nothing to close"
  fi
  exit 0
fi

# ---- fail mode ----
server="${GITHUB_SERVER_URL:-https://github.com}"
marker="<!-- ${DRILL_LABEL}-alert -->"
body=$(mktemp)
{
  echo "$marker"
  echo "# Nightly CI drift alert"
  echo ""
  echo "A CI run completed with failures — this usually means the environment drifted (runner image, SDK patch level, upstream API), not that someone broke main."
  echo ""
  echo "- Run: [${RUN_NUMBER}](${RUN_URL})"
  echo "- Commit: ${COMMIT_SHA}"
  echo "- Failing jobs: ${FAILING_JOBS:-unknown}"
  echo ""
  echo "Recent scheduled runs: ${server%/}/${GITHUB_REPOSITORY}/actions/workflows/ci.yml?query=event%3Aschedule"
  echo ""
  echo "What changed upstream: runner-image releases (actions/runner-images), the .NET 8 SDK patch level on windows-latest, or the Deriv WebSocket API / LLM provider endpoints. Once fixed, the next green scheduled run closes this issue automatically."
} > "$body"
cat "$body"

gh label create "$DRILL_LABEL" --repo "$GITHUB_REPOSITORY" \
  --description "Scheduled CI run failing - possible environment drift" \
  --color D93F0B --force >/dev/null 2>&1 || true

existing=$(label_issue)
if [ -n "$existing" ]; then
  gh issue comment "$existing" --repo "$GITHUB_REPOSITORY" --body-file "$body"
  echo "Updated drift issue #$existing"
else
  gh issue create --repo "$GITHUB_REPOSITORY" --title "$ISSUE_TITLE" \
    --body-file "$body" --label "$DRILL_LABEL" --assignee "${GITHUB_REPOSITORY_OWNER:-}"
fi
