#!/usr/bin/env bash
# Shared logic for the nightly drift alert and the on-demand drift drill.
#
# Modes:
#   fail     open (or update) the $DRILL_LABEL issue for a failing run
#   resolve  post a keyword-safe 'run went green' note on the open
#            $DRILL_LABEL issue and leave it open; set DRIFT_RESOLVE_CLOSE=1
#            to also close it after posting (the drift drill sets this so it
#            can rehearse the close leg)
#   no-show  the scheduled run never arrived (scheduler dropped the slot):
#            post a keyword-safe note on the open issue, or open one if
#            none exists. No log/classification — nothing ran to examine.
#   stale    (watchdog only) the open issue's newest comment of any kind is
#            older than STALE_HOURS — the alert has gone quiet and nobody
#            has touched it. Comments on the issue and leaves it exactly
#            as it is. No-op when nothing is open.
#   frozen-axis (watchdog only) the app's committed pulse file shows settled
#            growth trades inside FROZEN_DAYS but the committed CSV's newest
#            row is older — the machine kept trading while the money axis
#            stopped advancing. Files on the $DRILL_LABEL issue.
#
# Inputs via environment:
#   DRIFT_MODE      'fail' | 'resolve' | 'no-show' | 'stale' | 'frozen-axis'
#   STALE_HOURS     (stale mode) quiet period before an open alert counts as
#                   stale (default 48)
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

: "${DRIFT_MODE:?DRIFT_MODE must be 'fail', 'resolve', 'no-show', 'stale', or 'frozen-axis'}"
# frozen-axis is a watchdog verdict like stale: it inspects committed
# artifacts, not a run, so it needs no run context.
if [ "$DRIFT_MODE" != "no-show" ] && [ "$DRIFT_MODE" != "stale" ] && [ "$DRIFT_MODE" != "frozen-axis" ]; then
  : "${RUN_URL:?}"
  : "${RUN_NUMBER:?}"
  : "${COMMIT_SHA:?}"
fi
: "${DRILL_LABEL:=ci-drift}" # watchdog no-show calls omit it; every real caller means the ci-drift issue
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
  # REST issues API, not the search-backed `gh issue list`: the search index
  # lags behind creation by seconds-to-minutes, so an issue created moments
  # ago (drill legs, alert bursts) is invisible to it. The live staleness
  # drill proved this — both legs ran 0.3s after creating their issue and
  # the search-backed lookup returned nothing. REST list is consistent
  # immediately. Search stays as a fallback in case REST filtering ever
  # misses a case it should see.
  local number
  number=$(gh api "repos/$GITHUB_REPOSITORY/issues?state=open&labels=$1&per_page=1" \
    --jq '.[0].number // empty' 2>/dev/null || true)
  if [ -z "$number" ]; then
    number=$(gh issue list --repo "$GITHUB_REPOSITORY" --state open --label "$1" \
      --json number --jq '.[0].number' 2>/dev/null || true)
  fi
  printf '%s' "$number"
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

if [ "$DRIFT_MODE" = "frozen-axis" ]; then
  # Frozen-axis mode (watchdog only): the app's own pulse file
  # (docs/growth-pulse.json, written beside the CSV by the export refresh and
  # committed by the same publish cycle) shows settled growth trades inside
  # the look-back window, but docs/growth-bankroll.csv's newest row is older
  # than FROZEN_DAYS — the machine kept trading while the money axis stopped
  # advancing. This is the silent failure mode no push failure can ever
  # report (a publish that never happens raises no error anywhere; an export
  # reduction that went wrong can freeze the axis with byte-identical CSVs),
  # so the watchdog compares two committed artifacts against each other
  # instead of waiting for an error that cannot exist. Honest no-ops: no
  # pulse yet (the app has not run from this checkout since the pulse landed,
  # or the app is simply off), pulse stale (nothing new owed), CSV fresh
  # enough (axis is advancing).
  : "${FROZEN_DAYS:=5}"
  case "$FROZEN_DAYS" in
    ''|*[!0-9]*)
      echo "::error::frozen_days must be a non-negative integer (got '$FROZEN_DAYS')"
      exit 1 ;;
  esac

  csv="$GITHUB_WORKSPACE/docs/growth-bankroll.csv"
  pulse="$GITHUB_WORKSPACE/docs/growth-pulse.json"

  verdict=$(python - "$csv" "$pulse" "$FROZEN_DAYS" <<'PY'
import csv, json, sys
from datetime import datetime, timezone

csv_path, pulse_path, frozen_days = sys.argv[1], sys.argv[2], int(sys.argv[3])
now = int(datetime.now(timezone.utc).timestamp())

newest_csv = 0
accounts = []
try:
    with open(csv_path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                newest_csv = max(newest_csv, int(r["epoch_seconds"]))
            except (KeyError, TypeError, ValueError):
                pass
            if r.get("account"):
                accounts.append(r["account"])
except FileNotFoundError:
    pass

# The pulse is the machine-side ground truth (written beside the CSV by the
# app's export refresh; committed by the same publish cycle). No pulse yet —
# the app has not run from this checkout since the pulse feature landed —
# means the check cannot distinguish "app off, nothing owed" from "publish
# path dead", so it skips honestly rather than crying wolf.
pulse_last = 0
pulse_count = 0
pulse_accounts = []
try:
    pulse = json.load(open(pulse_path, encoding="utf-8"))
    pulse_last = int(pulse.get("last_settled_epoch", 0) or 0)
    pulse_count = int(pulse.get("settled_trades", 0) or 0)
    pulse_accounts = [a for a in pulse.get("accounts", []) if a]
except (FileNotFoundError, json.JSONDecodeError, TypeError, ValueError):
    pass

age_days = (now - newest_csv) / 86400 if newest_csv else float("inf")
pulse_recent = pulse_last > 0 and (now - pulse_last) < frozen_days * 86400
if age_days < frozen_days:
    print(f"no-op: axis is advancing (newest row {age_days:.1f}d old, "
          f"pulse last settled {pulse_last or 'never'})")
    sys.exit(0)
if not pulse_recent:
    print(f"no-op: CSV is frozen ({age_days:.1f}d old) but the pulse shows no settled "
          f"growth trades inside the window — nothing new owed to the axis")
    sys.exit(0)

fromiso = lambda e: datetime.fromtimestamp(e, tz=timezone.utc).strftime("%Y-%m-%d %H:%M")

# Everything below is eval'd by the calling shell, so every value is
# shell-quoted: account names, timestamps, and ages routinely contain spaces
# or commas, and an unquoted value would parse as a second command under
# `set -e` and kill the alert before the issue is filed.
from shlex import quote
if newest_csv:
    newest_desc = f"{age_days:.1f} days old (epoch {newest_csv})"
else:
    newest_desc = "missing from the checkout"
body = (
    f"NEWEST_CSV_DESC={quote(newest_desc)}\n"
    f"PULSE_LAST_SETTLED_EPOCH={quote(str(pulse_last))}\n"
    f"PULSE_LAST_SETTLED_UTC={quote(fromiso(pulse_last))}\n"
    f"PULSE_SETTLED_TRADES={quote(str(pulse_count))}\n"
    f"PULSE_ACCOUNTS={quote(','.join(pulse_accounts) or '(none)')}"
)
print(body)
PY
) || { echo "::error::frozen-axis probe failed (unreadable snapshot or CSV)"; exit 1; }

  case "$verdict" in
    no-op:*)
      echo "$verdict"
      exit 0 ;;
  esac

  # Frozen: file on the ci-bankroll-drift issue (create if none is open).
  eval "$verdict"
  ensure_label "$DRILL_LABEL"
  existing=$(open_issue_for_label "$DRILL_LABEL")
  note=$(mktemp)
  {
    echo "<!-- ${DRILL_LABEL}-frozen -->"
    echo "🥶 **The money axis is frozen while the app keeps trading.** "
    echo "The committed pulse file (docs/growth-pulse.json) shows ${PULSE_SETTLED_TRADES} settled growth trade(s) with the newest at ${PULSE_LAST_SETTLED_UTC} UTC "
    echo "(account(s): ${PULSE_ACCOUNTS}), but docs/growth-bankroll.csv's newest row is "
    echo "${NEWEST_CSV_DESC}."
    echo ""
    echo "The bankroll auto-publish path stopped advancing without any publish failure to report — the usual suspects: the app's export refresh or publish timer stopped (app closed?), the committed CSV stopped being committed, or the export reduction itself drifted (the pulse advances while the CSV does not). The trend page's money axis is silently stale until this is fixed."
    echo ""
    echo "This check compares two committed artifacts against each other (pulse vs CSV) precisely because a publish that never happens raises no error anywhere."
  } > "$note"
  if [ -n "$existing" ]; then
    gh issue comment "$existing" --repo "$GITHUB_REPOSITORY" --body-file "$note"
    rm -f "$note"
    echo "Posted frozen-axis note on issue #$existing"
  else
    gh issue create --repo "$GITHUB_REPOSITORY" \
      --title "$ISSUE_TITLE" \
      --body-file "$note" --label "$DRILL_LABEL" \
      --assignee "${GITHUB_REPOSITORY_OWNER:-}"
    rm -f "$note"
    echo "Opened frozen-axis drift issue"
  fi
  exit 0
fi

if [ "$DRIFT_MODE" = "stale" ]; then
  # Staleness flag: an open drift issue with no workflow comment for
  # STALE_HOURS means every nightly since either went unrecorded (scheduler
  # dropping slots again) or nobody looked. Comment and leave the issue
  # exactly as it is — the gap is recorded, never repaired silently.
  existing=$(open_issue_for_label "$DRILL_LABEL")
  if [ -z "$existing" ]; then
    echo "No open $DRILL_LABEL issue; staleness check is a no-op"
    exit 0
  fi
  # The alert counts as stale when the newest comment of ANY kind (workflow
  # or human) is older than the threshold: an actively tended thread is not
  # stale, whatever wrote the last word. Only bot comments would mis-flag
  # an issue a human is already triaging.
  newest=$(gh api "repos/$GITHUB_REPOSITORY/issues/$existing/comments?per_page=100" \
    --jq 'sort_by(.created_at) | reverse | .[0].created_at // empty')
  if [ -n "$newest" ]; then
    ts=$(date -d "$newest" +%s)
    hours=$(( ($(date +%s) - ts) / 3600 ))
    echo "newest workflow comment on #$existing: $newest ($hours h ago)"
  else
    hours=999999
    echo "open drift issue #$existing has no workflow comments at all"
  fi
  if [ "$hours" -lt "${STALE_HOURS:-48}" ]; then
    echo "Issue #$existing is fresh; nothing to do"
    exit 0
  fi
  note=$(mktemp)
  {
    echo "<!-- ${DRILL_LABEL}-stale -->"
    echo "🧭 **Stale alert check.** The newest workflow comment on this issue was ${hours}h ago (threshold: ${STALE_HOURS:-48}h)."
    echo ""
    echo "Nothing went green and nothing failed today — the alert has gone quiet. Scheduled runs may be being dropped again (see the scheduler-outage runbook), or the issue needs human triage. It stays open until reviewed."
  } > "$note"
  gh issue comment "$existing" --repo "$GITHUB_REPOSITORY" --body-file "$note"
  rm -f "$note"
  echo "Posted staleness note on drift issue #$existing; left open"
  exit 0
fi

if [ "$DRIFT_MODE" = "no-show" ]; then
  ensure_label "$DRILL_LABEL"
  existing=$(open_issue_for_label "$DRILL_LABEL")
  note=$(mktemp)
  {
    echo "<!-- ${DRILL_LABEL}-noshow -->"
    echo "⏰ **Scheduled run never arrived.** As of ${NOSHOW_CHECKED_AT:-$(date -u '+%Y-%m-%d %H:%M UTC')}, no schedule-event CI run exists in the past ${NOSHOW_WINDOW_HOURS:-26} hours."
    echo ""
    echo "GitHub delivers scheduled workflows on a best-effort basis — under load, slots can be delayed by hours or dropped entirely (observed repeatedly in this repo). The nightly gate itself did not run, so no drift verdict exists for last night."
    echo ""
    echo "Options: dispatch a run manually with the **CI health check** input for on-demand coverage, or wait for the next nightly slot. This note records the gap; nothing is taken down automatically."
  } > "$note"
  if [ -n "$existing" ]; then
    gh issue comment "$existing" --repo "$GITHUB_REPOSITORY" --body-file "$note"
    rm -f "$note"
    echo "Posted no-show note on drift issue #$existing"
  else
    gh issue create --repo "$GITHUB_REPOSITORY" \
      --title "${ISSUE_TITLE:-Nightly CI drift alert: scheduled run did not arrive}" \
      --body-file "$note" --label "$DRILL_LABEL" \
      --assignee "${GITHUB_REPOSITORY_OWNER:-}"
    rm -f "$note"
    echo "Opened no-show drift issue"
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
  echo "What changed upstream: runner-image releases (actions/runner-images), the .NET 8 SDK patch level on windows-latest, or the MT5 bridge / sidecar contract. Once fixed, the next green scheduled run (or weekly health check) posts a 'went green' note on this issue; the issue stays open for review — that record is deliberately never a closing keyword."
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
