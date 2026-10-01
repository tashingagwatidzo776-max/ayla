# TP1-floor interaction design — skip the rung when the floor owns the exit

Status: **IMPLEMENTED** (the trailing-mode gate in `FxEngineHost`'s TP1
block, behind `FxExecuteTp1Partials`; journaled TP1-SKIP rows; pinned by
`Tp1_Partial_Never_Arms_On_Structure_Trail_The_Floor_Owns_The_Exit` and
`Tp1_Partial_Arms_And_Banks_On_A_Hybrid_Floor_Ticket`). Evidence: the
rung backtest on the five 2026-09-30 saves
(`scripts/tp1_save_backtest.py`, [WEEK-TWO-TP1-LOG.md](WEEK-TWO-TP1-LOG.md))
plus a signal probe across every arm row. Companion to the arm-then-cross
block in `FxEngineHost` (TP1 partial prototype).

## The question

Should the TP1 rung be skipped when the profit-floor schedule is close?
The backtest's sign split says the question is sharper than "close":

| Ticket | Save layer | TrailingMode | Regime | Floor behavior observed | Rung verdict |
|---|---|---|---|---|---|
| #9820161383 | giveback override | HYBRID_STRUCTURE_ATR | LowLiquidity | static 4.00R for hours (26–32% of peak) | **+1.78R** |
| #9820781127 | giveback override | HYBRID_STRUCTURE_ATR | LowLiquidity | static 4.00R (never caught price) | 0 (no cross) |
| #9820814920 | giveback override | HYBRID_STRUCTURE_ATR | LowLiquidity | static 4.00R | 0 (no cross) |
| #9820712902 | hard floor guard | STRUCTURE_TRAIL | Trend | **rode the peak to 90%** (4.00R → 25.27R by 08:58) | **−1.90R** |
| #9820719898 | hard floor guard | STRUCTURE_TRAIL | Trend | **rode the peak to 90%** | **−1.65R** |

## Finding 1 — no arm-time signal separates good rungs from bad

At arm time every one of the five rows carries the SAME signals:
Continuation 0.67, Reversal 0.33, momentum `weakening`, ProfitScore
66.3–67.1, Tp1Probability 0.65–0.80. A gate on any of them would block
all five rungs equally — including the +1.78R winner. The discriminating
variable is structural, not predictive.

## Finding 2 — the trailing mode IS the discriminator

- **STRUCTURE_TRAIL (Trend regime)**: the profit-floor schedule ratchets
  with the peak (90% tighten). A static rung below the ratchet is
  dominated — the floor closes LATER and HIGHER than the rung, so the
  rung only subtracts. Both guard-save tickets lost exactly this way.
- **HYBRID_STRUCTURE_ATR (LowLiquidity regime)**: the floor lags the
  peak (static at the 4R rung's neighborhood for hours). There the rung
  banks early exactly when the giveback override later confirms the
  crash (#9820161383: 36.1R peak → 9.01R close — the rung's design case).

The giveback override and the rung are COMPLEMENTS (bank early, override
catches the remainder). The hard floor and the rung are SUBSTITUTES (the
floor IS the better partial on tickets it saves).

## Recommendation

**Adopt the trailing-mode gate: skip TP1 arming when
`profit.TrailingMode == "STRUCTURE_TRAIL"`; keep the rung on
HYBRID_STRUCTURE_ATR.** One line in the arm gate, journaled in the
FX_PROFIT payload, graded live by the existing digest machinery.

The gate is conservative in the right direction: it does not block the
round-trip population the rung exists for (those predate FX_PROFIT rows
entirely — the 6 tickets of 09-28/29 have no TrailingMode telemetry, so
their close behavior is default/hybrid, not STRUCTURE_TRAIL), and it
does not spend the guard-save peak capture (−3.55R across the two
tickets, replayed).

Rebalance guard rails (same policy as every weight/threshold change):

1. **Evidence bar before shipping:** the two-day replay rerun
   (`tp1_save_backtest.py`) must show the STRUCTURE_TRAIL tickets at 0
   loss and the HYBRID tickets unchanged. On the current sample that is
   a null result by construction — the gate needs LIVE grading, not
   another replay, to earn its keep.
2. **McDrill before/after** with proposed gating (the gate changes the
   TP1 arm population, so the arm/EXEC telemetry distribution shifts).
3. **Reversal clause:** if live TP1-EXEC rows under the gate underperform
   the fleet-without-TP1 capture for 20 graded tickets, demote the gate
   (arm everywhere) and re-review.

## What NOT to gate on (evidence-negative)

- Arm-time Continuation/Reversal/momentum/probability: identical across
  all five (Finding 1).
- Giveback class at arm time: all NORMAL at first sighting; WARNING/
  DETERIORATING arrive hours later, after the rung question is decided.
- Floor-vs-rung distance at arm time: the two losers' rungs sat BELOW
  the eventual ratchet, but the ratchet had not started yet at arm time
  (floor was 4.00R for both classes); distance at arm is not predictive,
  the SCHEDULE is.

## Open items for the live grading

- Where it landed: `FxEngineHost` TP1 block — `tp1ArmEligible` minus
  `tp1GateBlocked` (`profit.TrailingMode == "STRUCTURE_TRAIL"`), with a
  once-per-ticket FX_PROFIT TP1-SKIP row (TrailingMode + PlanPct) so the
  graded-vs-advisory split stays auditable.
- Week-two log: the graded-ticket table should record TrailingMode per
  ticket so live grading can test the gate's prediction (rung helps
  hybrid, never arms on STRUCTURE_TRAIL).
- The gate ships behind the existing `FxExecuteTp1Partials` toggle: it
  only narrows WHO arms; arming behavior cannot change without an
  explicit operator act, and demotion (arm everywhere) is a one-line
  revert if 20 graded tickets underperform the fleet-without-TP1 capture.
