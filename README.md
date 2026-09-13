# Tf — Deriv Binary-Options Trader

A WPF desktop application for automated binary-options trading on the [Deriv](https://deriv.com) platform. Features multiple AI-powered brain engines, a deterministic growth engine, multi-account support, and comprehensive risk management.

![License](https://img.shields.io/badge/license-MIT-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-windows-purple)
[![CI](https://github.com/tashingagwatidzo776-max/ayla/actions/workflows/ci.yml/badge.svg)](https://github.com/tashingagwatidzo776-max/ayla/actions/workflows/ci.yml)
[![Coverage report](https://img.shields.io/badge/coverage-report-blue?logo=readthedocs)](https://tashingagwatidzo776-max.github.io/ayla/)
[![Coverage trend](https://img.shields.io/badge/coverage-trend-0969DA?logo=chartdotjs)](https://tashingagwatidzo776-max.github.io/ayla/trend.html)

## Pipeline health

| What | Where |
|---|---|
| Coverage trend & milestones chart | [trend.html on GitHub Pages](https://tashingagwatidzo776-max.github.io/ayla/trend.html) — one point per successful `main` run, annotated with gate raises and notable episodes |
| Drift alert (nightly failures, missed runs, recovery notes) | [open `ci-drift` issues](https://github.com/tashingagwatidzo776-max/ayla/issues?q=is%3Aissue+is%3Aopen+label%3Aci-drift) — empty means the pipeline is healthy |
| Scheduler-outage runbook | [`docs/scheduler-outage-runbook.md`](docs/scheduler-outage-runbook.md) — what a no-show verdict means and what to dispatch |
| Drift-alert policy | [`docs/drift-alert.md`](docs/drift-alert.md) — classification, green notes, throttling |

Health checks (nightly 03:17 UTC, weekly dispatch, or manual via **Actions → CI → Run workflow → CI health check**) run the full pipeline and post to the drift issue on both failure *and* recovery, so a glance at the issues list answers "is CI healthy right now?".

## Features

### Trading Engines
- **LLM Brain** — AI-powered decisions using OpenAI, DeepSeek, or local Ollama
- **Growth Brain** — Deterministic $5 challenge engine with bankroll management
- **Trend Following** — EMA crossover + ADX trend strength
- **Breakout** — Bollinger Bands + ATR breakouts
- **Mean Reversion** — RSI + Z-score configurable mean reversion
- **Ensemble** — Combines multiple brains with weighted voting

### Risk Management
- Master kill switch (halts all trading immediately)
- Per-account pause/resume
- Circuit breaker (auto-pauses after repeated failures)
- Stake limits, daily loss cap, confidence floor, post-loss cooldown
- Market hours awareness (Sydney/Tokyo/London/New York sessions)
- Forex holiday calendar
- Portfolio governor — combined daily drawdown cap across all growth accounts (latched trip, survives restart, manual re-arm)

### Multi-Account
- Connect multiple Deriv accounts simultaneously (one WebSocket each)
- Per-account brain selection and configuration
- Independent growth engine sessions per account
- Combined growth P&L view across accounts with portfolio-level risk governor
- Automatic engine restart with exponential backoff and a bounded restart budget
- Export/import accounts for backup and migration

### Portfolio Governor

The governor watches the **combined net P&L of all growth-engine accounts** against the plan's `PortfolioDailyDrawdownCap`:

- Trips (latches) when the day's combined drawdown exceeds the cap — every growth engine is halted, not just the losing account
- **Pre-trip warning** — when the combined drawdown reaches 80% of the cap, an amber banner, a Dashboard risk-rail alert, and a webhook fire once per arming cycle so you can stop engines manually before the trip
- The latch is journaled and **survives app restarts**; it is restored from the journal on the next launch
- Re-arm is manual: clear the banner in the Growth or Dashboard tab once you've reviewed the day (re-arming also re-arms the warning)
- Adding a new account while latched does not clear the latch; only an explicit re-arm does

### Auto-Restart & Failure Hardening

When a growth engine exits because its scheduler kept failing (broken broker connection, dead WebSocket), the hub restarts it automatically:

- Failed brain cycles back off `FailureBackoffSeconds` (default 5 s); the scheduler stops retrying after repeated consecutive failures and surfaces the exit reason
- Restarts use exponential backoff: `RestartBaseDelaySeconds` (5 s) × `RestartBackoffFactor` (3.0) per attempt
- Auto-restarts stop after `MaxAutoRestarts` (default 3); the account is marked "gave up" until you start it manually (which resets the budget)
- A governor trip suspends auto-restart for all accounts until the governor is re-armed

### Monitoring & Notifications
- System health dashboard (all accounts at a glance)
- Windows toast notifications for trade events
- Discord/Slack webhook integration
- Heartbeat monitoring with uptime tracking
- Trade journal with full-text search and date filtering
- Performance dashboard with equity curves and strategy comparison
- Intraday P&L curves (per account, persisted across sessions) and per-row sparklines

### Developer Tools
- Strategy optimizer with parameter tuning
- Backtest on real cached tick data
- Structured logging with rolling files
- API audit trail for dispute resolution
- GitHub Actions CI/CD

## Quick Start

### Prerequisites
- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A Deriv demo account ([sign up free](https://deriv.com))

### Build & Run

```bash
# Clone the repository
git clone https://github.com/your-username/tf.git
cd tf

# Build
dotnet build

# Run
dotnet run --project src/Tf.App
```

### First Run
On first launch, a setup wizard will guide you through:
1. Pasting your Deriv demo API token
2. Selecting a trading brain
3. Setting a daily budget
4. Configuring basic settings

## Configuration

All settings are stored under `%APPDATA%\tf\data\`:

| File | Purpose |
|------|---------|
| `settings.json` | App settings (API token encrypted separately) |
| `token.bin` | DPAPI-encrypted API token |
| `trades.json` | Trade log |
| `accounts.bin` | Multi-account vault (DPAPI-encrypted) |
| `growth-plan.json` | Growth engine parameters |
| `journal/` | Trade journal entries |
| `logs/` | Structured application logs |
| `heartbeats/` | Connection state history |
| `api_audit/` | API request/response audit trail |
| `tick_history/` | Cached tick data for backtesting |

### Growth plan parameters

`growth-plan.json` round-trips through the Growth tab UI. Defaults:

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `StartBudget` | 5.00 | Challenge budget per account |
| `RiskFraction` | 0.20 | Stake as a fraction of available bankroll |
| `MaxRecoverySteps` | 3 | Stake-reduction steps after losses |
| `DailyTargetFraction` | 1.00 | Daily profit target as a fraction of budget |
| `FloorFraction` | 0.40 | Bankroll floor below which the engine stops |
| `MinStake` | 1.00 | Minimum allowed stake |
| `IntervalMinutes` | 1 | Delay between brain cycles |
| `CooldownMinutesAfterLoss` | 1 | Pause after a losing trade |
| `FailureBackoffSeconds` | 5 | Backoff after a failed brain cycle (1–120) |
| `MaxAutoRestarts` | 3 | Auto-restarts before giving up (0–10, 0 disables) |
| `RestartBaseDelaySeconds` | 5 | Base delay for restart backoff |
| `RestartBackoffFactor` | 3.0 | Multiplier per restart attempt |
| `PortfolioDailyDrawdownCap` | *(unset)* | Combined daily drawdown cap that trips the governor (blank disables) |

## Project Structure

```
tf/
├── src/
│   ├── Tf.Core/           # Core library (brains, indicators, models)
│   ├── Tf.Deriv/          # Deriv WebSocket API client
│   └── Tf.App/            # WPF desktop application
├── tests/
│   └── Tf.Core.Tests/     # Unit tests (xUnit)
├── scripts/               # PowerShell utility scripts
└── .github/workflows/     # CI/CD
```

## Safety Features

- **Demo by default** — All accounts start in demo mode
- **Real money confirmation** — Explicit warning dialog when enabling real money
- **Kill switch** — One-click halt of all trading activity
- **Circuit breaker** — Auto-pauses after 5 consecutive connection failures
- **Market hours** — Only trades during active forex sessions
- **Holiday calendar** — Skips major forex holidays
- **Rate limiting** — Prevents API throttling

## Troubleshooting

### "Auto-connect failed"
- Check your API token is valid (Settings → Account)
- Ensure you have internet connectivity
- Try a different App ID if the default is rate-limited

### "No market data"
- Connect to a Deriv account first (Settings → Connect)
- Check the symbol is correct (default: frxEURUSD)
- Verify the token has market data permissions

### Growth engine not trading
- Ensure Autonomy is enabled (Settings → Autonomy)
- Check market hours (may be closed for holiday/weekend)
- Verify the account is connected (Health tab)
- Check circuit breaker status (Accounts tab)

### App won't start
- Ensure .NET 8.0 SDK is installed
- Check `%APPDATA%\tf\data\` exists and is writable
- Delete `settings.json` to reset configuration

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Run `dotnet test` to verify
5. Submit a pull request

CI runs on every PR and posts a coverage-diff comment (this PR's merged line coverage vs the last successful run on `main`, against the 60% gate); a nightly scheduled run at 03:17 UTC catches drift such as runner/SDK updates and upstream API changes.

## License

MIT License — see [LICENSE](LICENSE) for details.

## Disclaimer

⚠ **This software is for educational and demo purposes only.** Binary options trading carries significant risk of financial loss. Never trade with money you cannot afford to lose. The authors are not responsible for any financial losses incurred through use of this software.
