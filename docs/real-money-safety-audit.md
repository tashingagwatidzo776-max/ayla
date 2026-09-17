# Real-money safety rails — coverage audit

Every code path that can place a Deriv trade, and which guardrails stand in
front of it. The rails are independent layers: a path is safe when *at least
one* rail can stop it, and every real-money path here is covered by several.
The single choke point for order execution is `DerivClient.BuyAsync` — it
takes a proposal id, so every strategy must pass `GetProposalAsync` first.

**Verified 2026-09-16** by grepping all `BuyAsync(` call sites under `src/`.
CI enforces this continuously: `scripts/check_safety_audit.py` (workflow-lint
job) fails when a call-site file has no coverage entry here.

## The rails

| Rail | Where | What it checks | Failure mode |
|---|---|---|---|
| Master kill switch | Dashboard latch, read via `_dashboard.IsKillSwitchEngaged` / hub kill-switch factories | Global engagement halts everything | Fail closed when latched |
| Portfolio drawdown governor | `MultiAccountHub` settlement handler + journal-restored latch | Combined daily net across all growth accounts breaches the plan's cap → stops every runner, latches until manual re-arm | Fail closed (latch survives restarts) |
| Real-money gate | `Tf.Core.Models.RealMoneyGate.Evaluate` | Config says real **and** Deriv's own `is_virtual` confirms real **and** the per-session unlock (typed `TRADE REAL MONEY` phrase) is armed. Any mismatch/unknown refuses with a specific reason | Fail closed — unverified counts as demo, never as real |
| Risk engine | `Tf.Core.Brain.RiskEngine.Evaluate` | Per-decision: kill switch, real-money verdict, confidence floor, stake bounds, concurrency limit, daily loss cap, post-loss cooldown | Rejects with a reason |

The real-money gate is enforced at four depths: engine **start**
(`MultiAccountHub.StartGrowthCore`, every start path including the automatic
restart ladder), engine **start again inside the runner itself**
(`GrowthRunner.StartAsync` re-evaluates the gate before anything else — even
a runner started outside the hub via `ObserveRunner` cannot begin a session
on a locked real account), engine **cycle** (the runner's per-cycle
`RiskContext` carries the gate verdict, so the risk engine blocks the trade
itself), and engine **mid-session** (per-settlement re-check stops the runner
and the hub drops it without a restart ladder).

## Path 1 — Growth engines (multi-account hub)

`GrowthViewModel (Start all / Restart) → MultiAccountHub.StartGrowth →
GrowthRunner → AutonomousScheduler → TradingBrain → DerivClient`

| Rail | Coverage |
|---|---|
| Start-time real-money gate | ✅ In `StartGrowthCore`, before any runner is created. Demo accounts pass through; a demo-flagged account the API says is real is refused as a config mismatch (the unlock cannot wave it through). |
| Runner-level start gate | ✅ `GrowthRunner.StartAsync` re-evaluates the gate first, before even the connection check — structurally closing the old `ObserveRunner` bypass: an externally started runner refuses exactly like a hub-started one. |
| Session unlock | ✅ In-tab unlock panel on the Growth tab: lists every locked real-money account with its API-verification state; one typed phrase arms all of them plus the manual surfaces for this session. Process-lifetime only, never persisted. |
| Per-cycle | ✅ `GrowthRunner.BuildRiskContext` evaluates the gate every cycle and the `RiskEngine` blocks on a refusal. |
| Per-settlement | ✅ `GrowthRunner.EnforceRealMoneyGateAfterSettlement` stops the engine after a settlement that flips the gate; fires a toast + webhook risk-rail alert; the hub drops the runner (`GrowthExitReason.RealMoneyGate`) with **no** restart ladder. |
| Kill switch | ✅ Scheduler self-exits; risk engine checks per decision. |
| Governor | ✅ Settlement handler with pre-trip warning; latch restored from the journal on launch. |

## Path 2 — Manual LLM/Brain tab

`BrainViewModel.RunCycle / StartAutonomy → TradingBrain → DerivClient`
(trading only when autonomy is enabled; otherwise decisions are advice only)

| Rail | Coverage |
|---|---|
| Real-money gate | ✅ Evaluated before every manual cycle and before autonomy starts (`ManualRealMoneyGate`, which wraps the shared gate and the session unlock). The verdict also flows into `BuildRiskContext`, so even a mid-cycle refusal blocks the trade at the risk engine. |
| Session unlock | ✅ Shared `ManualRealMoneyGate` unlock, armed by the Growth tab's in-tab unlock panel (same phrase, one pass together with the hub accounts); reset on app shutdown. |
| Kill switch | ✅ Kill-switch engaged fails the risk context every cycle; the scheduler exits on engagement. |
| Governor | n/a — this path trades the primary client, not a hub account; the governor watches growth-trade settlements, which this path does not produce. The kill switch + gate + risk engine cover it. |

## Path 3 — Manual demo trade (Trades tab)

`TradesViewModel.PlaceDemoTrade → DerivClient` (single manual trade)

| Rail | Coverage |
|---|---|
| Real-money gate | ✅ Evaluated before the connectivity check: demo passes; real needs the session unlock; unverified (never authorized) fails closed. A blocked state also relabels the button ("🔒 Real account locked"). |
| Session unlock | ✅ Shared `ManualRealMoneyGate` unlock, armed by the Growth tab's in-tab unlock panel. |
| Kill switch | ✅ Engaged kill switch refuses the trade outright. |
| Risk engine | ✅ The manual stake is bounded by the user's `ManualMaxStake` ceiling (Settings tab), enforced before any proposal is requested; a blocked real-money state also relabels the trade button. |

## Residual risks (accepted, documented)

- **Settings still decide `IsDemo` per account.** The gate cross-checks the
  config against the API verdict, so a wrong config is refused loudly rather
  than silently traded. The one direction the app can verify — the API says
  virtual while the config claims real — is fixable with one click from the
  unlock panel ("✓ Fix flag → demo": re-labels, persists, journals). The
  opposite direction (config claims demo, API says real) stays a manual fix:
  re-labelling to real is a human decision by definition.
- **The manual trade's stake is not bounded by the risk engine.** Closed by
  the `ManualMaxStake` ceiling (Settings tab): the Trades tab refuses any
  stake above it before requesting a proposal, so a mistyped stake cannot
  reach a real account. Ships capped at 10.00 (the risk engine's `MaxStake`),
  so a fresh install is bounded out of the box; 0/blank is an explicit
  opt-out that the Growth tab's go-live readiness panel flags red.
- **Locked-real-account visibility at startup.** Closed by the Growth tab's
  locked-account banners: session unlocks die with the process, so app start
  re-locks real accounts — the banners list them (with the API verification
  state) and carry an unlock affordance instead of leaving the state to be
  discovered on the next start click.

## Where the alerts go

Every real-money refusal/strip is: journaled (`GROWTH_STATE` /
`real-money-gate` with the decision), surfaced in the UI (status line +
activity log), and sent out-of-band as a risk-rail toast and webhook post —
the same channels the portfolio governor uses.

Arming is audited as loudly as refusing: the unlock panel's one-pass arm
writes a single `REAL_MONEY_UNLOCK_ARMED` journal entry (account names,
count, how many are API-verified real, whether the manual surfaces joined)
and posts a 🔓 webhook status. The Journal tab renders those entries with a
dedicated 🔓 line (like settlements), so the audit trail is readable in the
UI without JSON spelunking. Every metrics digest additionally carries the
current arm state (`DescribeUnlockState`: who is armed, since when, how many
growth trades settled inside each unlock window with their net P&L, and the
manual surfaces) — so monitoring sees real trading re-enabled after a
restart, and whether real mode was actually used. An unlock left armed past
the staleness threshold (default 4h) is flagged by the hub — journal
(`REAL_MONEY_UNLOCK_STALE`), toast, and webhook — repeating every full
threshold while still armed; repeat unlock clicks cannot reset the clock.

## Where the drill lives

The gate lifecycle is rehearsed weekly against fake brokers (no network, no
real funds) by the **Real-money gate drill** workflow
(`gate-drill.yml`): locked start refused → unlock arms → mid-session stop
→ hub drop, plus the manual-surface and parse fail-closed matrices. A failing
rehearsal files a `ci-gate-drill` drift alert; a green one records health.
PRs additionally get the safety-audit coverage-table diff posted as a comment
(`safety-audit-diff` job), so rail changes are reviewed before merge.

The classes the drill selects are listed in
[Test class coverage](#test-class-coverage) below.

Releases are gated on the drill: every `v*` tag runs it, and the workflow's
`release-gate` job fails the tag when the rehearsal did not pass on that
exact commit. The tag workflow's `publish-exe` job then builds the
self-contained Windows exe (same recipe as the local script) only after
`release-gate` passes, uploading it as a run artifact — CI cannot produce a
binary from an unproven gate. The local path enforces the same rule:
`scripts/publish_exe.ps1` refuses to publish from a release tag until a green
gate-drill run exists for it, so neither route ships from an unproven gate.

## Test class coverage

The drill selects the rail's own tests by `[Trait("Category", "RealMoney")]`
(PR #61), so every test class that exercises gate state must carry the trait.
`scripts/check_rail_traits.py` (workflow-lint job) fails when a gate-touching
class ships without it, and keeps this table honest in both directions:
classes listed here must exist on disk, and traited classes must be listed
here.

| Test class | What it exercises |
|---|---|
| `RealMoneyGateTests` (Tf.Core) | The gate's decision matrix: demo passthrough; locked, unverified, and config-mismatch refusals; fail-closed on unknown verification. |
| `RiskEngineRealMoneyTests` (Tf.Core) | The risk engine rejecting a decision when the gate refuses. |
| `AccountBalanceIsVirtualTests` (Tf.Core) | Deriv's `is_virtual` verification feeding the gate. |
| `JournalLoggingSurfaceTests` (Tf.Core) | The journal surface the gate writes through, including `REAL_MONEY_UNLOCK_ARMED` entries. |
| `ManualRealMoneyGateTests` (Tf.App) | The manual-surface unlock latch and its refusal matrix (Trades/Brain paths). |
| `RealMoneyUnlockArmTests` (Tf.App) | Unlock-panel arming: journal arm entries, activity logging, staleness. |
| `GrowthViewModelRestartTests` (Tf.App) | The Growth tab's unlock panel arming every listed account at once. |
| `GrowthViewModelReadinessTests` (Tf.App) | The go-live readiness panel's five checks: locked unlock / unverified account / disabled manual cap / absent governor cap / missing webhook each flip exactly the right leg red. |
| `ManualMaxStakeTests` (Tf.App) | The manual stake cap on the Trades tab path. |
| `MetricsDigestServiceTests` (Tf.App) | The digest's arm-state leg and rail table. |
| `RealMoneyGateHubTests` (Tf.App) | Hub start-time gate: demo passthrough, real/unverified refusals, idempotent refusal under concurrent starts. |
| `RealMoneyGateMidSessionTests` (Tf.App) | Runner-level re-evaluation at start and per-settlement mid-session stop. |
| `JournalFormatterTests` (Tf.App) | The Journal tab's dedicated unlock-arm/stale formatting and category filter. |
