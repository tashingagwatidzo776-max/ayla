#!/usr/bin/env bash
# Shared logic for the nightly drift alert and the on-demand drift drill.
#
# Modes:
#   fail     open (or update) the $DRILL_LABEL issue for a failing run
#   resolve  post a keyword-safe 'run went green' note on the open
#            $DRILL_LABEL issue and leave it open; set DRIFT_RESOLVE_CLOSE=1
#            to also close it after posting (the drift drill sets this so it
#            can rehearse the close leg)
#
# Inputs via environment:
#   DRIFT_MODE      'fail' | 'resolve'
#   RUN_URL         URL of the CI run that produced the verdict
#   RUN_NUMBER      CI run number, for the alert body
#   COMMIT_SHA      commit the run was on
#   DRILL_LABEL     label to use ('ci-drift' for real runs, 'ci-drift-drill' for drills)
#   ISSUE_TITLE     issue title used when creating
#   FAILING_JOBS    (fail mode) comma-separated names of the failing jobs
#   GH_RUN_ID       (fail mode, real runs) run id whose failing-job log is
#                   summarized into the alert; empty for drills, which keep a
#                   simulated body and skip classification
#
# Classification (real runs only):
#   infra   runner/exception/timeout/network signatures in the log
#   flake   a test failed but the same commit already had a green CI run
#   test    any other in-test failure (candidate regression)
#
# Flake throttle: when the failing test is already recorded on the open
# ci-drift issue, further failures add nothing — the update is skipped.
# Disabled for health checks (DRIFT_NO_FLAKE=1): dispatch runs have no
# scheduled-run counterpart, so flake detection is unreliable there and a
# known-flake label must never be able to silence a health check.
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
: "${GH_RUN_ID:=}"
: "${GITHUB_REPOSITORY:?GH_REPOSITORY/GITHUB_REPOSITORY must be set}"
: "${GITHUB_SERVER_URL:=https://github.com}"

label_color() { # label_name
  case "$1" in
    ci-flake) echo FBCA04 ;;
    *) echo D93F0B ;;
  esac
}

ensure_label() { # label_name [description]
  gh label create "$1" --repo "$GITHUB_REPOSITORY" \
    --description "${2:-CI run failing - drift or health-check issue}" \
    --color "$(label_color "$1")" --force >/dev/null 2>&1 || true
}

open_issue_for_label() { # label_name -> prints issue number or empty
  gh issue list --repo "$GITHUB_REPOSITORY" --state open --label "$1" \
    --json number --jq '.[0].number'
}

classify_and_summarize() {
  # Sets: CLASS, TEST_ID, EXCERPT. Requires a fetched job log in $1.
  local log="$1"
  CLASS="test"
  TEST_ID=""
  EXCERPT=""

  if grep -aqE '##\[error\].*(shutdown signal|lost communication|runner was terminated)|System\.(Exception|InvalidOperationException|NullReferenceException)|Unhandled exception|TimeoutException|SocketException|connection reset|HTTP 5[0-9][0-9]|timed out' "$log"; then
    CLASS="infra"
  fi
  # An in-test assertion failure outranks generic infra noise in the log
  # (e.g. a retry wrapper may log a transient exception and still fail an
  # assert); if we can name a failing test, it is a test-level failure.
  # Actions log lines carry a timestamp prefix, so match "Failed <id>"
  # anywhere in the line rather than anchoring to line start.
  TEST_ID=$(grep -aoE 'Failed [A-Za-z0-9_.]+\.[A-Za-z0-9_]+' "$log" | head -1 | cut -d' ' -f2 || true)
  if [ -n "$TEST_ID" ]; then
    if [ "${DRIFT_NO_FLAKE:-0}" = "1" ]; then
      : # health checks: never mislabel as flake (and never throttle)
    elif [ "$(gh run list --repo "$GITHUB_REPOSITORY" --commit "$COMMIT_SHA" \
            --json conclusion --jq '[.[] | select(.conclusion == "success")] | length' 2>/dev/null || echo 0)" -gt 0 ]; then
      CLASS="flake"
    fi
  fi

  # Curated excerpt: the lines that name the failure, capped for readability.
  EXCERPT=$({ grep -aE 'Error Message|Expected:|Actual:|\[FAIL\]|Failed [A-Za-z0-9_.]+\.[A-Za-z0-9_]+|##\[error\]|Unhandled exception|TimeoutException' "$log" || true; } | tail -25)
  if [ -z "$EXCERPT" ]; then
    EXCERPT=$(tail -15 "$log")
  fi
  EXCERPT=$(printf '%s' "$EXCERPT" | head -c 4000)
}

fetch_failing_job_log() { # -> prints path to log file, or empty
  [ -n "$GH_RUN_ID" ] || return 0
  local job_id log
  job_id=$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$GH_RUN_ID/jobs?per_page=100" \
    --jq '[.jobs[] | select(.conclusion == "failure")][0].id' 2>/dev/null || true)
  [ -n "$job_id" ] || return 0
  log=$(mktemp)
  # Log download can briefly 404 while the run is finalizing; degrade gently.
  gh api "repos/$GITHUB_REPOSITORY/actions/jobs/$job_id/logs" > "$log" 2>/dev/null || { rm -f "$log"; return 0; }
  printf '%s' "$log"
}

if [ "$DRIFT_MODE" = "resolve" ]; then
  ensure_label "$DRILL_LABEL"
  existing=$(open_issue_for_label "$DRILL_LABEL")
  if [ -n "$existing" ]; then
    # Keyword-safe green note: the durable, visible record that the pipeline
    # is healthy again. Worded without closing keywords and without '#N'
    # references, so GitHub's closing-keyword detection has nothing to act
    # on — visibility must not depend on it (it has falsely closed the alert
    # issue twice via PR-body text at merge time).
    note=$(mktemp)
    {
      echo "<!-- ${DRILL_LABEL}-green -->"
      echo "✅ **Run went green.** CI run [${RUN_NUMBER}](${RUN_URL}) on \`${COMMIT_SHA}\` completed successfully — the pipeline is healthy again as of this run."
      echo ""
      echo "This issue is being left open on purpose: the note above is the record that the failure stopped. It is taken down by a human after review (or by DRIFT_RESOLVE_CLOSE=1 on the resolve job), never by closing-keyword text."
    } > "$note"
    gh issue comment "$existing" --repo "$GITHUB_REPOSITORY" --body-file "$note"
    rm -f "$note"
    if [ "${DRIFT_RESOLVE_CLOSE:-0}" = "1" ]; then
      gh issue close "$existing" --repo "$GITHUB_REPOSITORY" \
        --comment "Resolved after review — the green run recorded above shows the pipeline healthy again."
      echo "Closed drift issue #$existing (DRIFT_RESOLVE_CLOSE=1)"
    else
      echo "Posted green note on drift issue #$existing; left open"
    fi
  else
    echo "No open $DRILL_LABEL issue; nothing to do"
  fi
  exit 0
fi

# ---- fail mode ----
server="${GITHUB_SERVER_URL%/}"

CLASS=""
TEST_ID=""
EXCERPT=""
LOG_PATH=$(fetch_failing_job_log || true)
if [ -n "${LOG_PATH:-}" ] && [ -s "$LOG_PATH" ]; then
  classify_and_summarize "$LOG_PATH"
  rm -f "$LOG_PATH"
else
  CLASS="drill" # no run log available (drills, or a log that never materialized)
fi

ensure_label "$DRILL_LABEL"
if [ "$CLASS" = "flake" ]; then
  ensure_label "ci-flake" "Known flaky test - the failure recurs intermittently, not on every run"
fi

existing=$(open_issue_for_label "$DRILL_LABEL")

# Throttle repeat failures of an already-recorded flake: the open issue
# already carries this test id, so another comment adds only noise.
if [ "$CLASS" = "flake" ] && [ -n "$existing" ] && [ -n "$TEST_ID" ] \
   && gh issue view "$existing" --repo "$GITHUB_REPOSITORY" --json body,comments \
      --jq '.body + " " + ([.comments[].body] | join(" "))' 2>/dev/null | grep -qF "$TEST_ID"; then
  echo "Flake $TEST_ID already recorded on #$existing; throttling repeat alert"
  exit 0
fi

body=$(mktemp)
trap 'rm -f "$body"' EXIT
{
  echo "<!-- ${DRILL_LABEL}-alert -->"
  echo "# Nightly CI drift alert"
  echo ""
  echo "A CI run completed with failures — this usually means the environment drifted (runner image, SDK patch level, upstream API), not that someone broke main."
  echo ""
  echo "- Run: [${RUN_NUMBER}](${RUN_URL})"
  echo "- Commit: ${COMMIT_SHA}"
  echo "- Failing jobs: ${FAILING_JOBS:-unknown}"
  case "$CLASS" in
    infra)
      echo "- Classification: **environment/infrastructure** (runner, timeout, or network signature in the log)"
      ;;
    test)
      echo "- Classification: **test failure** (assertion — candidate regression or a time-sensitive seed)"
      ;;
    flake)
      echo "- Classification: **flaky test** — the same commit was green in another run. Repeat alerts for this test are throttled while this issue is open."
      ;;
    *)
      : # drill: no classification line
      ;;
  esac
  if [ -n "$TEST_ID" ]; then
    echo "- Failing test: \`$TEST_ID\`"
  fi
  echo ""
  echo "Recent scheduled runs: ${server}/${GITHUB_REPOSITORY}/actions/workflows/ci.yml?query=event%3Aschedule"
  echo ""
  echo "What changed upstream: runner-image releases (actions/runner-images), the .NET 8 SDK patch level on windows-latest, or the Deriv WebSocket API / LLM provider endpoints. Once fixed, the next green scheduled run (or weekly health check) posts a 'went green' note on this issue; the issue stays open for review — that record is deliberately never a closing keyword."
  if [ -n "$EXCERPT" ]; then
    echo ""
    echo "### Error summary (from the failing job's log)"
    echo ""
    echo '```text'
    echo "$EXCERPT"
    echo '```'
  fi
} > "$body"
cat "$body"

if [ -n "$existing" ]; then
  gh issue comment "$existing" --repo "$GITHUB_REPOSITORY" --body-file "$body"
  echo "Updated drift issue #$existing"
else
  extra_label_args=()
  if [ "$CLASS" = "flake" ]; then
    extra_label_args+=(--label "ci-flake")
  fi
  gh issue create --repo "$GITHUB_REPOSITORY" --title "$ISSUE_TITLE" \
    --body-file "$body" --label "$DRILL_LABEL" "${extra_label_args[@]}" \
    --assignee "${GITHUB_REPOSITORY_OWNER:-}"
fi
