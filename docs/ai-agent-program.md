# AI agent program — six brains, one risk boundary

> Status: **agents 1 (Journal Analyst) and 2 (Risk Narrator) shipped**; the
> type-6/7 support lab (`FxLabService`) is shipped; agents 3–4 specified below.
> This doc is the authority ladder for every AI component in DON G FX. It
> extends — never weakens — `docs/real-money-safety-audit.md`; the rails
> there stay the last word on anything that can place a trade.

## Why a program and not a bot

The removed Deriv integration proved the failure mode: an LLM wired into the
trading loop is un-auditable and un-soakable. The MT5 rewrite rebuilt every
rail around a **deterministic** brain. The AI program adds intelligence
*around* that core — reading, writing words, proposing, remembering — without
stepping over the boundary the removal drew. Two authority levels exist:

- **Suggest (L1)** — produces text/code/proposals. Cannot place, modify,
  close, size, or schedule any order. Needs no real-money re-audit.
- **Decide (L2)** — gated. Requires: deterministic wrappers around every
  output, the per-symbol paper soak bar, a supervisor veto re-run, a
  `real-money-safety-audit.md` row, and `check_safety_audit.py` coverage.

L2 is deliberately empty today. Nothing in this program needs it.

## The brain-type roster (which types this project runs)

| Type | Kind | Status | Where |
|---|---|---|---|
| 1 | Deterministic rule brain | ✅ the only trader | `FxAlphas` + `FxSupervisor` + vetoes |
| 2 | Statistical brain | ✅ | `FxScorecardService` grading; analyst stats engine |
| 3 | LLM narrator | ✅ agent 1 | `JournalAnalystService` → local Qwen3 |
| 4 | RAG brain | planned | analyst v2: retrieve journal/docs/news into the prompt |
| 5 | Tool-using agent | ❌ not needed | no whitelisted read-tools warranted yet |
| 6 | Planner/propose brain | agent 5 | `scripts/ai_alpha/` (offline, human-merged) |
| 7 | Self-learning brain | ✅ **strictly guarded** | prediction-memory loop, weights frozen |
| 8 | Multi-agent swarm | the program itself | agents advise; the type-1 core decides |

## The agents

| # | Agent | Authority | Lives where | Shipped |
|---|---|---|---|---|
| 1 | **Journal Analyst** | L1 | `JournalAnalystService` | ✅ |
| 2 | **Risk Narrator** | L1 | `RiskNarratorService` | ✅ |
| 3 | **News Sentinel** | L1 | phase 2 (advisory notes) | planned |
| 4 | **Setup Grader** | L1→L2 | phase 3 | planned |
| 5 | **Alpha Researcher** | L1 | `scripts/ai_alpha/` (offline) | ✅ harness |

### 1. Journal Analyst — shipped

Read-only storyteller. Each cycle it reads the trade journal
(`TradeJournal.GetRecent`), computes session stats locally, and posts a
narrative digest to the same Discord/Slack webhook settlements use.

- **LLM access:** OpenAI-compatible chat endpoint, configured by
  `TF_LLM_BASE_URL` (default `http://127.0.0.1:11434/v1` — local Ollama),
  `TF_LLM_MODEL` (default `qwen3:0.6b`), `TF_LLM_API_KEY` (optional;
  Ollama ignores it but the contract stays provider-agnostic). The key
  never touches `settings.json` — secrets don't live in settings files
  (a settings wipe already happened once).
- **Fallback:** no env config, model down, or HTTP failure → a
  deterministic template narrative computed from the same stats. The
  feature degrades to "useful offline", never to "broken".
- **Prediction memory (the guarded type-7 learning loop):** each cycle
  records what it expects next (activity/quiet per symbol, halt risk);
  the next cycle mechanically grades the due predictions against the
  journal and feeds its own hit-rate into the prompt. Weights stay
  frozen — it learns context, not parameters. State lives in
  `%LOCALAPPDATA%\tf\ai\analyst-predictions.json`, never next to the
  trading journal, and disabling `AnalystMemoryEnabled` stops both
  recording and grading.
- **Rails:** timer mirrors `MetricsDigestService` (never throws, overlap
  guard, silent when nothing to say, one `AI_CALL` journal entry per
  cycle: model, latency, llm-or-template).

### 2. Risk Narrator — shipped

When `FxSupervisor` halts (daily-loss cap, equity floor, kill switch,
bridge-down) or the governor latches, an LLM turns the halt context into a
human explanation posted within seconds. It narrates halts; it can never
re-arm, clear, or influence one. Re-arm stays a manual click.

- **Wiring:** subscribes to the journal's `EntryAdded` stream for narratable
  `FX_RISK` entries; posts to the same webhook settlements use; every call
  journals `AI_CALL` per the type-7 rules. Same env config as the analyst
  (`TF_LLM_BASE_URL`/`TF_LLM_MODEL`/`TF_LLM_API_KEY`), same template
  fallback when the LLM is unreachable.
- **Toggle:** `RiskNarratorEnabled` (Settings → AI analyst row), applied
  live through the settings factory.

## Agent-6/7 support: the genetic lab (`FxLabService`)

Nightly, journal-only walk-forward evidence for the alpha families. It
replays the journal's own `FX_DECISION` bars (no market feed, no bridge),
optimizes a small momentum blend with `FxGenetic`, and gates every result
through `FxWalkForward.Approved` (≥60% OOS folds positive, no fold below
the loss cap). Verdicts land in the journal as `FX_LAB` entries —
**evidence for a human, never a promotion**: porting an approved parameter
set is still a reviewed PR into `FxAlphas`. Rails: no order-path reference
at all, silent under 120 decisions per symbol, `FxLabEnabled` toggle in
Settings, timer guardrails mirror the digest service.

The full **20-family roster** (`FxFamilies.All`) is what the engine, the
scorecard, and the lab all evaluate: the 6 original families, the 2 that
existed but were never wired (`vol-breakout`, `ou-rev`), and 12 new small
alphas composed from the existing indicator primitives (MACD cross, RSI(2)
reversion, Keltner, Kalman slope, ADX pullback, Donchian pullback, EMA
slope, Bollinger squeeze, opening-range breakout, VWAP trend, RSI momentum,
Hurst-gated momentum). `MultiTimeframe` stays out of the roster — it needs
a second bar feed the single-stream cycle does not carry.

**Operator tooling:** the Terminal's **LAB RUN** button invokes the same
walk-forward pass on demand (`FxLabService.RunNowAsync` — the nightly
toggle does not gate an explicit click; the ≥120-decision guard still
refuses thin journals with a reason on the FX status line). A weekly
**`FxLabWeeklyDigest`** rolls the last 7 days of `FX_LAB` entries into one
webhook post plus an append to `docs/soak/FX-LAB-WEEKLY.md` (uncommitted,
like every evidence edit).

### 3. News Sentinel — planned (phase 2)

LLM summaries of upcoming calendar events posted ahead of
`FxNewsVeto` blackouts. The deterministic veto stays the only hard stop.

### 4. Setup Grader — planned (phase 3)

An LLM critique of the features behind each signal (regime, spread, halt
density), posted alongside the signal. L2 promotion only via the full soak
pipeline: deterministic feature → per-symbol 10/10 bar → audit row.

### 5. Alpha Researcher — harness shipped (`scripts/ai_alpha/`)

Offline only, no bridge/sidecar/webhook access. Loop:

1. `propose.py` asks local Qwen3 for an alpha: name, hypothesis, params,
   pseudo-code → `proposals/PROP-*.json`.
2. `backtest.py` deterministically replays the proposal over archived bars
   (tick archive export) and writes an equity/DD report. The LLM never
   grades its own work.
3. Results land in a PR. A human merges winning alphas as deterministic C#
   in `FxAlphas`, where they inherit the paper soak, supervisor veto,
   scorecard grading and audit coverage every alpha has.

The LLM proposes; the machine measures; the human decides.

## Type-7 strict guard rules (non-negotiable)

1. **Weights frozen in-app.** "Learning" = context + graded prediction
   memory. Fine-tuning happens only offline, only for agent 5, only via
   human-merged PRs.
2. **Text out, never orders.** No LLM output is parsed into an order,
   size, or schedule; the type-1 core never reads agent output.
3. **Every call is journaled** as `AI_CALL` (model, latency, fallback
   flag) — the audit trail is the feature.
4. **One toggle per agent, persisted, kill-switch obeyed.** Toggles apply
   live (read-through settings factory, same pattern as the milestone
   gate); disabled means no posts, no calls, no memory writes.
5. **Mechanical grading only.** Hit-rates are computed from journal
   outcomes, never self-assessed by the model.
6. **Audit-doc coverage.** Any agent that someday reaches a path a rail can
   refuse gets a `real-money-safety-audit.md` row; `check_safety_audit.py`
   enforces it in CI.

## Rollout order

| Phase | Ship | Gate to next |
|---|---|---|
| 1 (now) | Journal Analyst + prediction memory; alpha-researcher harness | a week of clean analyst posts; graded hit-rate reported |
| 2 | Risk Narrator + News Sentinel | halt narratives within 60s, zero false re-arms |
| 3 | Setup Grader (L1) → offline fine-tune experiments | grader critiques reviewed 2 weeks; any L2 promotion follows the full pipeline |

Promotion L1→L2 additionally requires: deterministic output wrapper,
per-symbol soak bar, supervisor veto re-run, audit row, and a migration
note in this doc.
