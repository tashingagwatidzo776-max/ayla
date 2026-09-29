# Portfolio-cap incident chronicle — 2026-09-29

One operator-precedence bug at the bottom, four layered defenses on top —
each verified against the live demo, each with a pinning test.

## Timeline

- **00:06–01:52Z** — the six Sunday-reopen losers fill (dead tape; led to
  the separate dead-tape regime floor fix).
- **04:34–06:06Z** — fills carry SLs (stopped-path PROVEN), but exposure
  climbs to ~0.53 lots under a 0.10 cap. Under congestion `/positions`
  degrades to an EMPTY list while `/account` answers → the guard read a
  flat book. Fix 1: two agreeing reads, refuse on disagreement (`d4b6bb0`).
- **08:21–09:06Z** — the agreeing-reads rule fails: BOTH reads degrade to
  empty *in agreement*, and the new book-prune wiped the local book
  (`SetEquals(∅,∅)`). 7 fills to 1.00 lots. Fix 2: prune only on reads
  that are certainly real (non-empty AND agreeing) (`edeb040`).
- **09:45–09:51Z** — after a clean-environment relaunch, the floor was zero
  again: fresh hosts seed their books FROM venue reads, which lied empty.
  Two more fills. Fix 3: `FxJournalBook` derives open lots from the local
  journal (fills open tickets, close confirmations retire them) — the
  journal cannot be lied to by any venue read (`f76cb4f`).
- **10:33Z** — one more fill **with the journal book live**. Root cause
  finally: the veto chain was
  `small.VetoAsync(...) ?? exposure.VetoAsync(...)` — `??` on two Tasks
  coalesces on the task REFERENCE (always non-null), so **the exposure
  guard never executed at all** once the small-account guard was wired in
  that morning. Every cap failure after ~08:00 rode this one bug. Fix 4:
  await both guards, coalesce on the RESULT (`9ae310c`).

## Post-fix verification (live)

- 8 positions / 0.80 lots open, cap 0.10 → **0 fills since the fix**.
- CAP refusal journaling real exposure numbers again.
- Guards now fail closed through: dead bridge, disagreeing reads,
  degraded-empty agreeing reads, fresh-restart books, and a dormant
  small-account guard.

## Standing takeaway

Every guard fix was verified by a unit test before shipping — but the
CHAIN that composes the guards had no test. A veto rail is only as strong
as its weakest composition. The chain now has its pin
(`Exposure_Chained_After_Dormant_Small_Guard_Still_Vetoes`).
