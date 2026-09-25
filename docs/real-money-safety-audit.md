# Real-money safety rails — coverage audit

Every code path that can place a real trade, and which guardrails stand in
front of it. The rails are independent layers: a path is safe when *at least
one* rail can stop it, and every real-money path here is covered by several.
The single choke point for order execution is `Mt5BridgeClient.PlaceOrderAsync`
— it talks only to the loopback sidecar (127.0.0.1), which in turn requires a
live MetaTrader 5 terminal. The Deriv binary-options integration (and its
`DerivClient.BuyAsync` choke point) was removed; MT5/forex is the only
trading surface left.

**Verified 2026-09-22** by grepping all `PlaceOrderAsync(` call sites under
`src/` (2: `TerminalViewModel`, `FxEngineHost`). CI enforces this
continuously: `scripts/check_safety_audit.py` (workflow-lint job) fails when
a call-site file has no coverage entry here.

## The rails

| Rail | Where | What it checks | Failure mode |
|---|---|---|---|
| Master kill switch | Dashboard latch, read via `_dashboard.IsKillSwitchEngaged` on every trade path | Global engagement halts everything and cross-venue flattens open MT5 positions (`FxEmergencyFlattenAsync`) | Fail closed when latched |
| FX supervisor | `FxSupervisor`, constructed by `FxPortfolioHost`, evaluated by `FxEngineHost` **before every cycle and before every order** | Session daily-loss cap (`Mt5DailyLossCap`), absolute equity floor (`Mt5EquityFloor`), kill switch, portfolio governor (`IsGovernorLatched` on the Dashboard), bridge reachability while live | Halts and returns the live engine to paper; halt latches until explicit re-arm |
| Real-money gate | `DongGfx.Core.Models.RealMoneyGate.Evaluate` (wrapped by the shared `ManualRealMoneyGate` session unlock) | Config says real **and** the account type is verified real **and** the per-session unlock (typed `TRADE REAL MONEY` phrase) is armed. Unknown/unverified refuses with a specific reason | Fail closed — unverified counts as demo, never as real |
| Lot cap | `Mt5MaxLots` (Settings tab) | Single-order volume ceiling; **0 disables MT5 order placement entirely** | Fail closed by default (fresh install = 1.00 lot, 0 = refused before any bridge call) |
| Portfolio exposure veto | `FxExposureGuard` inside `FxPortfolioHost` | Total open lots across all FX-brain symbols vs `FxPortfolioMaxLots`; runs as a pre-order veto on every brain order | Refuses the order |
| News veto | `FxNewsVeto` + `data/news-calendar.json` | Refuses new brain orders inside the `NewsBlackoutMinutes` window around high-impact events | Refuses the order |
| Unlock staleness watch | `UnlockStalenessMonitor` (1-minute timer) | An armed session unlock older than `ArmStalenessHours` (0 disables) journals `REAL_MONEY_UNLOCK_STALE`, toasts, and posts a risk-rail webhook — repeating every full threshold; repeat unlock clicks cannot reset the clock | Alert-only by design (the gate itself never expires — an expiry would silently re-lock mid-position) |

The real-money gate is enforced at three depths: **manual order time**
(`TerminalViewModel.PlaceMt5Order` evaluates the gate before any bridge
call — the account's demo/real comes from the MT5 server name / login and a
real account demands the session unlock), **engine start / cycle / order**
(the `FxEngineHost` passes `ManualRealMoneyGate.IsUnlocked` into its
`realMoneyUnlocked` predicate; `FxSupervisor` independently re-runs every
cycle and again before each order), and **settings fail-closed**
(`Mt5MaxLots = 0` refuses placement no matter who asks).

## Path 1 — Terminal manual MT5 order (Terminal tab)

`TerminalViewModel.PlaceMt5Order → Mt5BridgeClient → loopback sidecar → MetaTrader 5`

| Rail | Coverage |
|---|---|
| Kill switch | ✅ Engaged kill switch refuses the order outright (checked first); the same latch triggers the cross-venue flatten. |
| Lot cap | ✅ `0 < lots ≤ Mt5MaxLots`, checked before the bridge is called; 0 disables placement entirely. |
| Real-money gate | ✅ The MT5 account's demo/real comes from the venue itself: `account_info().trade_mode` via the bridge (0 = demo → verified virtual, 2 = real → verified real); the server-name/login heuristic is a demo-only fallback for older sidecars and can never verify an account as real. A real account additionally requires the same session unlock as every other real-money path, evaluated before the order is sent. |
| Journal | ✅ Every order journals `MT5_ORDER` with the retcode, ticket, price and server. |
| Transport | ✅ Loopback-only sidecar; the C# client refuses non-loopback base addresses by construction (`Mt5BridgeClient` ctor). |
| FX-brain supervisor | ✅ The autonomous FX brain (`FxEngineHost`) routes its orders through the same ticket path, and an `FxSupervisor` gate runs **before every cycle and before every order**: MT5 daily-loss cap (`Mt5DailyLossCap`, latched until explicitly re-armed), absolute equity floor (`Mt5EquityFloor`), kill switch, and portfolio governor. Any halt returns the live engine to paper automatically. Halt/flatten events journal `FX_RISK` and post to the webhook. |
| Cross-venue stops | ✅ Kill switch and governor trip both stop the FX brain and flatten every open MT5 position (`FxEmergencyFlattenAsync`) — the MT5 leg is covered by the same emergency stops as the Deriv legs. |

## Path 2 — FX brain autonomous orders

`FxPortfolioHost → FxEngineHost → Mt5BridgeClient → loopback sidecar → MetaTrader 5`

| Rail | Coverage |
|---|---|
| FX supervisor | ✅ `FxSupervisor.Evaluate` runs before every cycle **and** before every order: daily-loss cap (latched until re-armed via RE-ARM), equity floor, kill switch, governor, bridge-down. Any halt returns the live engine to paper automatically; halt/flatten events journal `FX_RISK` and post to the webhook. |
| Real-money gate | ✅ Demo/real is the venue's `account_info().trade_mode` (demo-only heuristic fallback — see Path 1) and the engine's `realMoneyUnlocked` predicate reads the shared `ManualRealMoneyGate`; a locked or unverified real account never passes a cycle. Autonomy off (`AutonomyEnabled` false) journals signals only — nothing is placed. |
| Exposure + news vetoes | ✅ Pre-order vetoes inside the host (`FxExposureGuard`, `FxNewsVeto`) refuse over-cap or news-blackout entries. |
| Paper soak | ✅ The brain starts in paper mode; go-live requires the per-symbol paper soak (`PaperSoakComplete`), and PAPER/LIVE are explicit user actions on the account bar. |
| Attribution | ✅ Settled deals flow into `PerformanceTracker` / `TradeJournal` through `FxTradeFeed` (deduplicated by ticket), tagged `FX` with a deterministic per-login account id. |

## Path 3 — Emergency flatten (order *reduction*, intentionally ungated)

`TerminalViewModel → Mt5BridgeClient.ClosePositionAsync` and
`DashboardViewModel.FxEmergencyFlattenAsync`

Closing a position reduces risk, so these paths bypass the unlock on
purpose: refusing to close because the unlock expired could cost real money
during a kill-switch event. They are still bounded — close-only (the bridge
has no modify-order endpoint on this path), triggered exclusively by the
kill switch, a supervisor halt, or an explicit user close click.

## Residual risks (accepted, documented)

- **Demo/real on a legacy sidecar is a heuristic.** With the venue's
  `trade_mode` (current sidecar) the gate reads the broker's own verdict;
  only when the field is absent does the server-name/login heuristic step
  in — and it can verify an account as demo, never as real, so a real
  account on a stale sidecar fails closed into `BlockedUnverified` until
  the sidecar is updated. A wrong flag fails toward demanding the unlock,
  never toward bypassing it.
- **There is no per-decision confidence engine** (the Deriv-era
  `RiskEngine` went away with the binary integration). Its jobs are split
  across rails that fail closed: supervisor caps, lot/exposure caps, the
  gate, and the kill switch — none of them depend on a decision payload.
- **Session unlocks are process-lifetime only.** Restart re-locks real
  trading by design; the digest's arm-state leg and the staleness watch make
  an armed unlock observable rather than silent.
- **The sidecar is a trusted local process.** It binds 127.0.0.1 only, but
  anything on the machine can reach it. The terminal's own risk settings
  (stop levels in MT5 itself) are the last line beyond this app.

## Where the alerts go

Every refusal/strip is journaled (`MT5_ORDER`, `FX_RISK`,
`REAL_MONEY_UNLOCK_STALE`, `real-money-gate` categories), surfaced in the UI
(status lines + the Journal tab's dedicated 🔓/⏰ formatters), and sent
out-of-band as a risk-rail toast and webhook post — the same channels the
supervisor uses.

Arming stays observable: every metrics digest carries the current arm state
(real-money session unlock `ARMED` / silence = nothing armed), so monitoring
sees real trading re-enabled after a restart. The staleness watch repeats
its journal + toast + webhook every full `ArmStalenessHours` threshold while
the unlock stays armed.

## Where the drill lives

The gate lifecycle is rehearsed weekly against fake brokers (no network, no
real funds) by the **Real-money gate drill** workflow (`gate-drill.yml`),
which selects exactly the `[Trait("Category", "RealMoney")]` test classes
below. A failing rehearsal files a `ci-gate-drill` drift alert; a green one
records health. PRs additionally get the safety-audit coverage-table diff
posted as a comment (`safety-audit-diff` job), so rail changes are reviewed
before merge.

Releases are gated on the drill: every `v*` tag runs it, and the workflow's
`release-gate` job fails the tag when the rehearsal did not pass on that
exact commit. `scripts/publish_exe.ps1` refuses to publish from a release
tag until a green gate-drill run exists for it, so neither route ships from
an unproven gate.

## Test class coverage

The drill selects the rail's own tests by `[Trait("Category", "RealMoney")]`,
so every test class that exercises gate state must carry the trait.
`scripts/check_rail_traits.py` (workflow-lint job) fails when a gate-touching
class ships without it, and keeps this table honest in both directions:
classes listed here must exist on disk, and traited classes must be listed
here.

| Test class | What it exercises |
|---|---|
| `AccountSwitchGuardTests` (DongGfx.App) | The MT5 account-switch guards: a successful switch stops the FX portfolio brain (badge OFF, host stopped), resets the manual real-money session unlock (a fresh account starts locked), journals the guard action as `MT5_SESSION`, and stays safe/idempotent with no brain running — all through the account-switch continuation the login dialog invokes (the composition root's OnMt5AccountSwitched). |
| `RealMoneyGateTests` (DongGfx.Core) | The gate's decision matrix: demo passthrough; locked, unverified, virtual-account and config-mismatch refusals; fail-closed on unknown verification; `Explain` never renders an empty refusal. |
| `Mt5AccountGateVerdictTests` (DongGfx.App) | The bridge's venue-verdict mapping: `trade_mode` → `RealMoneyGate.VerdictFromTradeMode`, demo-contest→true / real→false / absent-or-unknown→null (fail-closed), and the `DetermineApiVerifiedVirtual` demo-heuristic fallback the mapping preserves. |
| `JournalLoggingSurfaceTests` (DongGfx.Core) | The journal surface the gate writes through, including `REAL_MONEY_UNLOCK_ARMED` entries. |
| `ManualRealMoneyGateTests` (DongGfx.App) | The shared session unlock latch: fresh gate locked, arm/reset round-trip, `ArmedAt` stamped once (repeat arms keep the original staleness clock), and the full refusal matrix (unverified/locked/virtual/mismatch). |
| `TerminalViewModelTests` (DongGfx.App) | The Terminal tab's MT5 order card rails: kill-switch refusal (manual + emergency stop latching the global switch), zero-lot fail-closed, over-cap refusal, bridge-down hint, bridge-backed account bar and deal history, plus the brain switch's settings persistence and symbol sync. |
| `TerminalViewModelCoverageTests` (DongGfx.App) | Command-path battery for the same Terminal rails: FX brain go-live refusals (no host, incomplete paper soak), emergency flatten (position close + bridge-down), the order card's full rail ladder (kill switch → zero cap → over-cap lots → limit/stop-limit price fields → venue demo/real gate → fill/refusal → journal), close-position branches, market watch load/refresh with the bridge-down and broken-catalog branches, and the account bar. |
| `TerminalContextCommandsTests` (DongGfx.App) | Market Watch right-click commands: New Order pre-selects the ticket symbol without sending (no bypass of the ticket's rails), Create Alert refuses no-quote rows and arms engine-deduped above/below alerts, Open Chart switches the selection, and every command tolerates null rows. |
| `TerminalChartTradingTests` (DongGfx.App) | Chart trading rails: the chart's market buy/sell routes through the SAME guarded executor (kill switch, zero-cap fail-close, over-cap refusal, real-venue fail-closed without the session unlock), dragged SL/TP lines modify only that leg via the journaled modify path, garbage drag specs are no-ops, and the chart's price lines rebuild per poll for the selected symbol with advisory entries and ticket-carrying SL/TP legs. |
| `DashboardAndSettingsCoverageTests` (DongGfx.App) | Dashboard risk-rail alerting (governor latched/warned, kill switch engage/release), the Terminal's EmergencyStop latching the dashboard switch, and the Settings save pipeline's real-money-adjacent surface (mode label, webhook validation, quiet save, busy guard) with the real settings.json backed up and restored. |
| `UnlockAndJournalCoverageTests` (DongGfx.App) | The real-money unlock staleness watch: disabled/disabled-by-zero silence, within-threshold silence, once-per-threshold alert with repeat on the next threshold, and re-arm after reset — against the real ManualRealMoneyGate, a real temp-dir journal, and a capture notifier. Also the Journal tab's filter pipeline and the tick chart's data path. |
| `AppStartupWiringTests` (DongGfx.App) | The app's real DI composition (App.ConfigureServices): every runtime service resolves, the singleton contract holds, the settings pass wires webhook/digest/unlock state, and the Terminal's fxHostFactory produces a paper-mode host over the configured symbols. Exercises the real ManualRealMoneyGate's arm/reset and the gate-dependent state providers. |
| `CoverageSprintTests` (DongGfx.App) | The settings store's load branches (missing file → safe demo defaults, corrupt JSON → defaults not a throw, full risk-field round-trip through Save/Load) against the real `%APPDATA%\tf\data` file with backup/restore around every test; the composition root's property routing and idle Shutdown no-op; and the Terminal row records' MT5-close-button visibility rule. |
| `PriceAlertEngineTests` (DongGfx.App) | The MT5-style price alerts: crossing ticks fire toast + real temp-dir journal entries (flushed) + an inert webhook; add/dedupe and direction validation; inside-the-band silence; other-symbol and non-price ticks ignored; one-shot self-removal vs repeating re-arm; list management. |
| `JournalFormatterTests` (DongGfx.App) | The Journal tab's dedicated unlock-arm/stale formatting and category filter. |
| `MetricsDigestServiceTests` (DongGfx.App) | The digest's arm-state leg, safety-audit table change detection (post once, then silent), and the FX-brain state line. |
