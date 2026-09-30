# PR skeleton — giveback engine weight 1.0 (DO NOT MERGE until the gates are green)

Status: **SKELETON — placeholders everywhere.** This PR exists so the
promotion moment is mechanical, not improvised: when the ledger crosses the
bar, fill the stats, paste the fresh McDrill verdict, flip the weight,
review, ship. Until then this file is a checklist, not a change request.

## The one-line diff (review this, nothing else)

```csharp
// src/DongGfx.Core/Fx/FxExitBrain.cs — FxExitShadowVotes(), the giveback voice:
- new FxExitVote("giveback", ..., 0, ...)     // weight 0 — observation
+ new FxExitVote("giveback", ..., 1.0, ...)   // weight 1.0 — earned, capped
```

The weight rides the existing vote; no thresholds move
(`GivebackVoteRatio` 0.75 / `GivebackWatchRatio` 0.60 /
`GivebackOverrideRatio` 0.75 stay frozen), the hard floor stays a separate
command layer, and the roster fingerprint gate must be updated in the SAME
commit (EngineWeights roster test) — that is the reviewed change.

## Gate checklist (all must be green AT MERGE TIME, not at drafting)

- [ ] **Trades ≥ 100** (or an explicitly-reviewer-approved reduction to the
      ≥30 reassessment bar, justified in review): giveback ledger
      `settled = ___` (was 14 at drafting).
- [ ] **Hit ≥ 60%**: giveback `helped = ___ / ___` (was 6/14 = 43% at
      drafting). If hit < 60%, the engine is NOT promotable — close this
      PR and extend observation, no matter how strong the story.
- [ ] **Fresh McDrill STABLE with the weight applied**: run
      `dotnet run --project tools/McDrill` on the then-current journal;
      paste `baseline ___% / proposed ___%` flips — identical or within
      noise. Latest pre-weight run: 6.27% / 6.27% (2026-09-30, weight 0).
- [ ] **Capture trend corroborates**: the weekly capture series
      (fx-capture-trend.svg) must NOT have degraded since the saves began
      (2026-09-30 baseline: 22% W40 after 0%/0%).
- [ ] **Roster fingerprint + full suites green**: Core + App + watcher +
      lifecycle; CI green; no EngineWeights change beyond this vote.

## Reviewer notes

- The giveback vote's Exit is the giveback ratio itself (1 − P/L ÷ MFE,
  capped 0.9) — at weight 1.0 it can tip a 55-band (tighten) decision but
  CANNOT reach the full-exit band alone, and it can never override the
  hard floor (the guard commands before the ensemble runs).
- The drawdown engine keeps its own weight (2.0) — this PR changes one
  number and nothing else. Any threshold edit in the same diff voids the
  McDrill gate and reopens the case.
- Post-merge: the dashboard promotion card flips to "earned weight" and
  the weekly digest announces it; watch the next 20 settlements for
  regression before any further increase.

## Evidence pointers

- Case + gates: docs/soak/GIVEBACK-PROMOTION-CASE.md
- Save trail: docs/soak/HARD-FLOOR-INCIDENT-2026-09-30.md
- MC history: docs/soak/MC-DRILL.md
- Ledger: %APPDATA%/tf/data/fx-shadow/fx-shadow-*.jsonl (dashboard card
  reads the same files)
