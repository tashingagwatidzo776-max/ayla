# Near-miss giveback exit lever — investigation (2026-10-08)

**Question:** for cells like XAUUSDmicro/vol-breakout (and their XAGUSD
cousin), would an exit lever on giveback save the near-misses — trades that
peaked positive and closed deeply negative?

**Data:** the app's own journal, last 30 days, 98 measured closes; 66 tickets
have FX_PROFIT row series (PeakR/CurrentR/GivebackPct/GivebackClass/FloorR/
Reversal per cycle). Simulation exits at the FIRST triggering row's CurrentR
(snapshot exit, no slippage — an optimistic bound for the lever).

## 1. The near-miss census

Definition: peak ≥ +0.3R, closed ≤ −0.3R. **5 trades** qualify in the window
(plus ticket 8792571441 today, peak +0.29R → −1.58R, just under the peak
threshold — 6 if included):

| ticket | cell | peak | closed | peak→close giveback |
|---|---|---:|---:|---:|
| 8792007104 | XAUUSDmicro / kalman-trend | +0.86R | −0.87R | 1.73R |
| 8790896097 | XAUUSDmicro / kalman-trend | +0.86R | −0.85R | 1.71R |
| 8790641137 | XAUUSDmicro / ema-slope | +0.49R | −0.69R | 1.18R |
| 8791852710 | XAUUSDmicro / vol-breakout | +0.37R | −0.50R | 0.87R |
| 8791592240 | XAGUSD / vol-breakout | +1.09R | −0.46R | 1.55R |
| 8792571441 | XAUUSDmicro / (today) | +0.29R | −1.58R | 1.87R |

**≈ 8.9R of peak→close giveback over 6 trades** — concentrated exactly in the
cells already dragging the roster (XAU vol-breakout is the STALE-KEEP cell at
n=20 / −0.08R). On n=63, XAU's +0.81R average is carried by winners while
these six subtract.

## 2. Would a blanket giveback exit pay? No.

Levers simulated at first triggering row:

| lever | fired | saved | cost | **net** | whipsawed |
|---|---:|---:|---:|---:|---:|
| L1 exit at first CRITICAL giveback, peak ≥ 0.3R, cur ≤ 0 | 16 | +2.02R | −7.06R | **−5.04R** | 5 |
| L2 giveback ≥ 100% & peak ≥ 0.5R & cur ≤ 0.05 | 11 | +1.17R | −5.77R | **−4.60R** | 4 |
| L3 DETERIORATING-or-worse, peak ≥ 0.5R | 24 | +11.52R | −11.78R | **−0.26R** | 7 |
| L4 exit at first CRITICAL, peak ≥ 0.3R (any sign) | 25 | +8.60R | −8.49R | **+0.12R** | 7 |

Every blanket giveback trigger is net-negative or flat: giveback CRITICAL
fires on trades that later recover (5–7 winners per lever), and because
GivebackPct divides by peak, **small peaks make it fire deep in the hole**
(ticket 8791592240 CRITICAL'd at −0.98R while its peak was only 0.13R —
829% giveback on noise). The near-miss saving (~+1.8R on L1) is real but is
swamped by whipsaw cost.

**Conclusion: do NOT add a blanket giveback→exit lever.**

## 3. Where the protection actually failed (three concrete gaps)

Traced ticket 8791592240 (XAGUSD, floor armed at 0.3R, still closed −0.46R):

**Gap A — the profit floor's adaptive tighten is dead code below the first
schedule rung.** `FxProfitBrain.ProfitFloor` first resolves the schedule
floor, then:

```csharp
if (floor <= 0) return Math.Max(0, prevFloorR);   // early return
// adaptive tighten (reversalP > 0.55 → floor = currentR − 0.1)
// pre-tighten     (reversalP ≥ 0.40 → floor = currentR × 0.75)
```

The default schedule's first rung is **peak ≥ 1.0R → floor 0.3R**, so any
trade peaking below 1.0R returns here with floor = 0 and **never gets an
adaptive or pre-tightened floor, no matter how strong the reversal
evidence**. Result: 4 of the 5 near-misses (peaks 0.29–0.86R) had FloorR = 0
for their entire life. The floor schedule protects winners-of-scale, not
near-misses — the exact population this investigation is about.

**Gap B — the floor guard samples once per ~60s cycle and gets shot through.**
8791592240: 11:38 sample = +0.71R (floor 0.3R armed), 11:39 sample =
**−0.46R** — the breach row reports "executable −0.46R through the 0.3R
floor". A 1.17R move inside one bar meant no sample ever landed between
floor and hole; the exit (correctly commanded, event 8791592240|1|1, broker
confirmed) executed ~0.76R below where the floor should have sold it.

**Gap C — TIGHTEN_PROTECTION / MOVE_TO_PROTECTED are advisory by design and
nobody acts on them.** FxProfitBrain is spec §26 "reported, never executed";
`RecommendedAction` appears in the journal only (FxEngineHost.cs:1649,1662).
PROFIT_CRITICAL rows with giveback 257–829% recommended TIGHTEN_PROTECTION
every cycle while the position rode to its stop. The only executing levers
are the Exit Brain ensemble and FxProfitFloorGuard breach commands.

## 4. Options, ranked by evidence

1. **Fill the schedule gap (Gap A) — cheapest, targets the exact population.**
   Add low rungs so small peaks earn a floor, e.g. `(0.3, 0.0)` (breakeven
   once +0.3R is reached) and `(0.5, 0.15)`, or remove the early return so
   the adaptive/pre-tighten logic runs whenever schedule floor is 0 and
   reversal evidence is strong. Estimated effect on the census: 4/6
   near-misses would have had a floor at or above breakeven (~+3–4R swing
   on those six, subject to the same sampling lag as Gap B).
2. **Narrow the sampling gap (Gap B).** Advance the floor guard on the
   bridge's price updates (or at bar close inside the cycle) instead of once
   per cycle. Directly fixes 8791592240-class losses (~0.76R on that ticket).
   Needs a latency/bench check before committing (guard advance must stay
   cheap and thread-safe).
3. **Do not wire TIGHTEN_PROTECTION to exits (Gap C) as-is.** The section-2
   numbers are the evidence: acting on the advisory string at face value
   costs ~5R per 30 days. If the advisory is ever promoted to an action, it
   must be conditioned on peak size and reversal context, not on
   GivebackClass alone (giveback% is unstable on small peaks).
4. **No change to the roster gate.** The near-misses argue for better exits,
   not for cutting cells: XAU/kalman-trend is +1.02R avg over n=24 while
   carrying two of these casualties — an exclusion would throw away the
   edge the survivors produce.

## 5. Verdict

- A blanket giveback exit lever is **negative expectancy (−4.6 to −5.0R per
  30 days)** — the lever as stated should not be built.
- The real, evidenced levers are **floor-schedule coverage below 1.0R peak**
  (Gap A) and **floor-breach sampling latency** (Gap B); together they cover
  ~80% of the observed giveback damage without touching the advisory
  architecture (Gap C).
- Next step if approved: design pass on the floor schedule + guard advance
  cadence (spec + tests), keeping Profit Brain advisory per §26.

*Method note: simulation exits at snapshot CurrentR of the first triggering
FX_PROFIT row, ignores slippage/latency (optimistic for the levers), and
treats the recorded outcome (tier-1 RealizedR / tier-2 snapshot) as truth —
the same never-fabricated outcomes the win-rate report uses.*
