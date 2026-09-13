# Scheduler-outage runbook

What to do when the nightly CI run did not arrive — or did not go green.
Reads in two minutes; assumes the setup documented in `docs/drift-alert.md`.

## The morning checks, in order

1. **06:00 UTC — schedule watchdog runs** (`Schedule watchdog` workflow).
   Its verdict is in the step log ("watchdog verdict: ...") and its
   effects are on the `ci-drift` issue:

   | Verdict | Meaning | Your move |
   |---|---|---|
   | `resolve` | Nightly ran and was green; a ✅ "Run went green" note was posted on an open issue | Nothing. If the alert issue's story is complete, retire it (see `docs/drift-alert.md`) |
   | `skip` | Nightly ran but **failed** (or was mid-run); the CI run's own `drift-alert` job owns the issue | Nothing yet — let the CI run's alert land, then triage the failure there |
   | `no-show` | No schedule run within the 26h window; a ⏰ "Scheduled run never arrived" note was posted | The scheduler dropped the slot. Read on |
   | `stale` | An open drift issue's newest comment is older than 48h — the alert went quiet without resolution | Look at the thread, then triage it or retire it properly. The watchdog has already commented |

2. **Check the run itself**, never just the verdict: the watchdog log line
   `latest schedule run: #N (...h old, conclusion: ...)` tells you exactly
   what the nightly did. Actions → CI → filter `event:schedule`.

## What a no-show verdict means

Nothing ran, so **no drift verdict exists for last night** — the world
could have moved (runner image, SDK patch, upstream API) without anyone
noticing. The no-show note on the issue records the gap; it is not a pass.

## What to dispatch manually (in order of preference)

1. **CI health check** — Actions → CI → Run workflow → tick
   *CI health check* → run on `main`. Full pipeline (~15 min); a failure
   alerts on the drift issue exactly like a failed nightly, with the same
   log-excerpt classification. This is the primary substitute for a
   dropped nightly.
2. **Drift drill** (only to verify the *alerting* machinery): Actions → CI
   → Run workflow → *Drift drill*; uses the `ci-drift-drill` label and
   never touches the real issue.
3. **Watchdog re-run** — Actions → Schedule watchdog → Run workflow, with
   `window_hours=0` to force the no-show leg or a large value (e.g. `9999`)
   to force the resolve leg. Rehearsal/debug only; never use it to
   manufacture a green record — the resolve leg posts a green note for
   whatever run the window finds, so it is only meaningful when a real
   green run actually exists.

## When to wait instead of acting

- **Delivery is best-effort.** This repo has seen slots delivered >1h40m
  late and dropped entirely. A run created after the watchdog's 06:00
  check still counts for the *next* day's window (26h), and a
  `drift-alert`/`drift-resolved` verdict from it is just as real.
- **If the manual health check is green**, drift is covered for today —
  the no-show note stays as history; no further action is needed.
- **If the manual health check fails**, triage it like a nightly failure
  (the alert issue will carry the excerpt; classification rules are in
  `docs/drift-alert.md`).

## Escalation pattern (observed 2026-09-13)

Three armed slots dropped or delivered hours late in 24h is a scheduler
outage, not bad luck: stop treating the cron as a given, run the health
check daily (manually or via the Monday `ci-health-check-weekly.yml`
dispatch), and let the watchdog record every gap until delivery recovers.
