# Drift alert lifecycle

How the CI drift machinery behaves — the policy, not the chat history. The
moving parts:

| Piece | Where |
|---|---|
| Alert logic (all modes) | `scripts/drift-alert.sh` |
| Jobs on the CI pipeline (fail / green-note) | `.github/workflows/ci.yml` |
| No-show watchdog | `.github/workflows/schedule-watchdog.yml` |
| Weekly independent dispatch | `.github/workflows/ci-health-check-weekly.yml` |
| On-demand drill | CI workflow dispatch, `drift_drill=true` |

## Why an alert issue exists at all

A scheduled (or health-check) run failing usually means the *world* changed
— runner image, SDK patch level, upstream API — rather than someone breaking
`main`. A quiet red X on the Actions tab would go unnoticed, so failures are
recorded on a `ci-drift`-labeled issue with the run link, failing jobs, the
failing test id, and a curated excerpt from the failing job's log.

## Failures: classification and labels

The failing job's log is classified when the alert is raised:

- **infra** — runner/exception/timeout/network signatures in the log.
- **test** — an in-test assertion failure (candidate regression; historically
  this class has also caught time-of-day-sensitive test seeds).
- **flake** — a test failed but the same commit was green in another run;
  labeled `ci-flake`, and **repeat alerts for an already-recorded flake are
  throttled** (the update is skipped, no new comment) while the issue is open.
- Health-check failures run with flake classification disabled
  (`DRIFT_NO_FLAKE=1`): an earlier green run on the same commit says nothing
  about the environment *now*, and a known-flake label must never be able to
  silence a health check.

## Recovery: green notes, never auto-close

When the nightly, the watchdog, or a passing health check goes green,
the `drift-resolved`/watchdog/`ci-health-check-green` path posts a
keyword-safe comment on the open issue:

> ✅ **Run went green.** CI run *N* completed successfully …

and **leaves the issue open**. The issue is taken down by a human after
review. It is deliberately *not* closed automatically:

- GitHub's closing-keyword detection (in PR bodies and comments at merge
  time) has falsely closed alert issues in this repo twice; automatic state
  changes must not depend on text parsing.
- The open issue is the durable record that a drift episode happened.

The close path still exists and is exercised by the drill:
`DRIFT_RESOLVE_CLOSE=1` posts the note **and** closes the issue. The
`drift-drill` job sets it (resolve-mode drills rehearse the full
open → note → close cycle); the real `drift-resolved` and
`ci-health-check-green` jobs set it to `0`.

## Scheduler distrust: watchdog and weekly dispatch

GitHub delivers `schedule` events on a best-effort basis — slots in this
repo have been dropped entirely or delivered hours late, which is why the
loop "scheduled run closes the alert" can never be relied on alone:

- **Schedule watchdog** (`schedule-watchdog.yml`, daily 06:00 UTC) checks
  whether the CI workflow produced any schedule-event run in the past 26h
  (24h window + margin for documented lateness). Verdicts:
  - *no-show* → posts a "scheduled run never arrived" note on the drift
    issue (opens one if none exists) — a dropped slot is recorded, never
    silent.
  - *latest run succeeded* → posts the green note (same as above).
  - *latest run failed or still running* → does nothing; the CI run's own
    `drift-alert`/`drift-resolved` jobs own the issue.
  Rehearse via dispatch: `window_hours=0` forces the no-show leg; a huge
  window forces the resolve leg.
- **Staleness flag** (same watchdog run, second verdict): an open drift
  issue whose newest comment of *any* kind is older than 48h is flagged
  with a stale-alert comment — every nightly since either went unrecorded
  (scheduler) or nobody looked. The comment records the gap; the issue
  itself is untouched and stays open for a human.
- **Weekly health check** (`ci-health-check-weekly.yml`, Mondays 06:23 UTC)
  dispatches the CI pipeline on `main` with the **CI health check** input
  and confirms the run registered. This gives drift coverage a second,
  independent delivery path that does not depend on schedule events at all.
  A passing health check also posts the green note (via
  `ci-health-check-green`), so recovery visibility has the same
  scheduler-independent path as failure detection.

## On-demand rehearsal (drift drill)

Actions → CI → Run workflow → *Drift drill* rehearses the alert path against
the real API using the `ci-drift-drill` label (the real `ci-drift` issue is
never touched). `auto` mode resolves when a drill issue is open and opens
one otherwise; `fail`/`resolve` force a leg.

## Policy summary

1. Failures open or update one `ci-drift` issue; repeats of a known flake
   are throttled.
2. Green runs post a keyword-safe note — from the nightly, the watchdog,
   or a passing health check; nobody and nothing closes the issue by
   keyword.
3. The issue is closed by a human (or `DRIFT_RESOLVE_CLOSE=1` in the drill).
4. A missing nightly is itself an alert (no-show), not a silent pass.
5. Every record — fail, green, no-show, stale — is a comment, so the issue
   body plus history tells the whole story.
6. An open alert that goes quiet for 48h gets flagged by the watchdog;
   silence is itself a finding, never a pass.
