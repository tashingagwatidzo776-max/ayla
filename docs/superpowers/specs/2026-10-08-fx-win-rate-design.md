# FX Win Rate — Measure + Recent-Tape Filter (design)

**Date:** 2026-10-08 · **Status:** design approved by user (Approach 1: measure + filter)
**Constraint (user):** expectancy protected — only changes that raise win % without lowering
average R or starving the engine.

## Problem

Baseline (FX journal, 11 days to 2026-10-08): **47.5% win (29/61 closes with a known
outcome), avg +1.168R, total +71.3R.**

1. **Measurement gap:** 105 closes, but **44 have no recorded outcome** — FX_EXIT close
   rows carry no realized R, and closes with no preceding FX_PROFIT/FX_EXIT `ProfitR`
   row are unmeasurable. The app's Performance tab only reports the binary-options win
   rate; **no FX win-rate tool exists** (`scripts/weekly_pnl.py` covers TRADE_SETTLEMENT
   only).
2. **Actual drag (evidence, small n):** vol-breakout XAU 41% win (n=22, +0.65R);
   confidence-0.60 entries 41% win (n=27) vs 0.80 → 50% (n=20); sell side 44% vs buy 52%.
   `docs/soak/ROSTER-POLICY.md` already prescribes the cure (recent-tape expectancy
   filter) but **implementation was never built** — rule 1 governs today (full roster,
   cumulative cells tilt weights only).

## Approach (chosen)

Measure first, then filter on recent tape. Three deliverables:

### 1. Record realized R on every FX close (fixes the outcome gap)

There are exactly **five** writers of the `closed #N` row (verified by search):

| # | Site | Shape | Realized-R source |
|---|------|-------|-------------------|
| 1 | `FxEngineHost.cs:1668` | ensemble full close (`closed #N — deal …`) | **compute**: entry (`p`) vs current tick mid ÷ sized stop → `OutcomeSource = "close-price"` |
| 2 | `FxEngineHost.cs:2115` | profit-floor exit confirmed | **compute**: entry vs this cycle's mid ÷ sized stop (same law as row 1 — the deferred prune now hands its snapshot to the confirmation loop, 2026-10-08) → `"close-price"`; else `"unknown"` (was `"profit-snapshot"` pre-relabel — every floor confirm landed `unknown` before the handoff fix) |
| 3 | `FxEngineHost.cs:1221` | stale tracking retired (two-pass proof) | same snapshot fallback → `"profit-snapshot"`; else `null` |
| 4 | `FxEngineHost.cs:1768` | stale tracking retired (healthy positions read) | same snapshot fallback → `"profit-snapshot"`; else `null` |
| 5 | `FxPortfolioHost.cs:627` | ops reconcile close (`broker no longer holds the ticket`) | last journaled ProfitR for the ticket if present, else `null` → `"unknown"` |

Each writer adds **nullable payload fields** to the existing JSON payload:
`RealizedR` (double?) and `OutcomeSource` (`"close-price"` | `"profit-snapshot"` |
`"unknown"`). Rules:

- **Never fabricate.** If no trustworthy value exists → `RealizedR = null`,
  `OutcomeSource = "unknown"`.
- The human-readable Details text is unchanged — `FxJournalBook`, `trade_lifecycle`,
  `watch_*`, `FxExitWeeklyDigest` all key off the `closed #N` text and ignore unknown
  payload fields (verified: none parses the full payload strictly).
- Pure helper `FxRealizedR.Compute(entry, exit, stop, side)` lives in Core (testable,
  mirrors the R-unit law: sizing and outcome use the same stop distance).

### 2. Recent-tape roster filter (ROSTER-POLICY items 2–4 — the win-rate lever)

New **pure Core class `src/DongGfx.Core/Fx/FxRecentTape.cs`**:

- Cells keyed **(symbol, family)**; each holds a rolling window of the last
  **N = 30** settled trades (R each).
- `IsExcluded(symbol, family)` and `Record(symbol, family, r)`.
- **Asymmetric bar (default-keep):** n < 30 → never excluded, regardless of mean.
- **Exclusion:** n ≥ 30 AND window mean ≤ **−0.10R** counts as a *failing evaluation*;
  **two consecutive** failing evaluations → excluded. Any passing evaluation (n < 30, or
  mean > −0.10R) resets the fail streak, and **one passing window re-enters** an
  excluded family (ROSTER-POLICY §4 hysteresis).
- Evaluation runs on each `Record` (cheap: O(window) mean over ≤30 doubles).

**Wiring (FxEngineHost):**

- At the entry path beside the existing confidence gate (≈ `FxEngineHost.cs:657`):
  if `_recentTape.IsExcluded(Symbol, alpha)` → **refuse the entry** and journal a
  refusal row in the same style as the conf-gate row
  (`roster recent-tape excluded {alpha} — mean {X}R over last {n}`). Auditable, one row
  per refusal.
- The host keeps **ticket → family** from the entry decision (new field alongside
  `_entryRegimes`) and calls `Record(symbol, family, realizedR)` at every close writer
  that produced a non-null `RealizedR`.
- **Reseed on startup** by scanning the journal: fill rows (`FX_ORDER`, carry the
  signal/family) matched to their close rows (`RealizedR` preferred, else last
  decisive ProfitR) rebuild the windows, so deploys/restarts don't reset n to 0.
  If a fill's family is unparsable, that trade is skipped (windows degrade, never
  mis-binned). Reseed failure logs and continues with empty windows — the filter
  then default-keeps (safe direction).
- No new AppSettings: N / threshold / fail-count are `const`s in `FxRecentTape`
  (policy-level constants, revisit only via the weekly re-validation).

**Why this raises win rate without hurting expectancy:** it only removes cells whose
*recent* 30-trade mean is clearly negative — by construction those cells drag both win %
and avg R. Cumulative cells keep their current role (weight tilt only, never cut).
Default-keep + n≥30 means thin-data families (rsi2-rev n=2, bb-rev n=1) are untouched;
the filter starts by excluding **nothing on day one** (vol-breakout/XAU reaches n=30
first).

### 3. `scripts/fx_win_rate.py` (new report)

- Parses journal `journal_*.jsonl` (fills → closes → outcomes), pattern after
  `weekly_pnl.py`.
- **Outcome resolution, in priority order:**
  1. close-row `RealizedR` (when `OutcomeSource != "unknown"`);
  2. fallback: last FX_EXIT/FX_PROFIT `ProfitR` row for the ticket before the close
     (the mechanism that produced today's 61 measured closes);
  3. else **no outcome** — excluded from the denominator, counted in a coverage line.
- **Output:** overall n / win % / avg R / expectancy / coverage %, then breakdowns
  **by family, symbol, side, confidence** (each: n, win%, avg R), plus a
  "candidates for exclusion" section listing cells that currently fail the recent-tape
  bar — the pre-deployment sanity check that the filter's future cuts are the right ones.
- Exit code 0 always (report, not a gate).

### 4. `scripts/fx_outcome_coverage.py` (companion dashboard, added 2026-10-08)

- Per-DAY table of close rows by `OutcomeSource` (`close-price` /
  `profit-snapshot` / `unknown` / `(no-payload)`) so a coverage
  regression is visible on the day it starts, plus a per-WRITER matrix
  (floor-confirm / stale-retire / ops-reconcile / operator-reconcile /
  ensemble-close) so a source a close shape should never carry — e.g.
  floor confirms landing `unknown` — is impossible to miss.
- Same journal inputs, `--days`/`--since`/`--data-dir` flags and
  exit-0 report contract as `fx_win_rate.py`; test in
  `scripts/test_fx_outcome_coverage.py` (wired into CI's integration job).

## Verification plan

- **Core tests** (`DongGfx.Core.Tests`): `FxRecentTape` — rolling window eviction,
  default-keep n<30, threshold at exactly −0.10R, two-fail-to-exclude, one-pass-to-
  re-enter, (symbol,family) isolation; `FxRealizedR.Compute` — buy/sell sign, zero-stop
  guard, snapshot semantics.
- **App tests** (`DongGfx.App.Tests`): excluded family writes the refusal row and
  does not enter; close rows carry `RealizedR`/`OutcomeSource` (known and unknown
  paths); reseed rebuilds a window from a fixture journal; existing assertions
  (one `closed #` per ticket etc.) unchanged.
- **Python**: `scripts/test_fx_win_rate.py` fixture journal covering all three outcome
  tiers + unknown coverage; `python scripts/check_script_hygiene.py` stays 0.
- **Suites must stay green:** Core 344+, App 642+, Python 143+ (counts grow with new
  tests; no existing test weakened).
- **Deploy** via `deploy-app.ps1`, then run `fx_win_rate.py` against the live journal
  and paste before/after numbers (win % **and** avg R — expectancy-protected check).

## Spec self-review (recorded)

1. **Refusal-row shape verified** — `FxEngineHost.cs:657-668`: the conf gate writes
   `Journal("FX_ORDER", "confidence gate — {alpha} conf {X} < min {Y}, not dispatched",
   {Symbol, Alpha, Confidence, MinEntryConfidence})` then `return`s. The recent-tape
   refusal row copies this shape exactly (same category, payload keys
   `Symbol/Alpha/WindowN/WindowMeanR`), placed immediately after the conf gate so a
   refusal never consumes the dispatch cooldown.
2. **Reseed inputs verified against the live journal** — fill payloads carry
   `Signal` (family, e.g. `"kalman-trend"`, `"ou-rev"`, `"hurst-trend"`), `Price`
   (entry), and `SizedStopDistance` (R unit); symbol rides the Details text
   (`sell 0.1 lots XAGUSD @ 59.737 — ticket N`). All reseed inputs are therefore
   journal-recoverable. (A naive grep for `"Signal"` misses these rows — the embedded
   payload uses `\u0022`-escaped quotes.) Fills without a parsable `Signal` are
   skipped, never guessed.
3. **Roster constants match ROSTER-POLICY** — policy §2 says "n ≥ ~30" and "clearly
   negative", §4 says two-consecutive-fail / one-pass re-enter: pinned here as
   N=30, mean ≤ −0.10R, fail×2, pass×1. Values are policy knobs, not settings.
4. **Writer #5 feasibility** — `FxPortfolioHost` already reads the journal
   (`SizedStopFromJournal`), so the "last journaled ProfitR" lookup is available at
   the reconcile close; `null`/`"unknown"` remains the honest fallback.

## Non-goals

- No exit-side changes (near-miss giveback — 16/32 losers peaked >0 — is a separate
  follow-up lever, not in this change).
- No confidence-gate retune (MinEntryConfidence stays as-is; conf-0.60 is observed but
  cutting it would starve the engine — out of the approved scope).
- No change to cumulative-cell weight tilt (FxBrainMemory) or the roster itself.
- The chronic `TradeJournal.Flush` sharing-violation bug stays out of scope (reported
  separately).
