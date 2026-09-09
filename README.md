# Tf — Deriv Binary-Options Trader

A WPF desktop application for automated binary-options trading on the [Deriv](https://deriv.com) platform. Features multiple AI-powered brain engines, a deterministic growth engine, multi-account support, and comprehensive risk management.

![License](https://img.shields.io/badge/license-MIT-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-windows-purple)
![Build](https://img.shields.io/badge/build-passing-brightgreen)

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

### Multi-Account
- Connect multiple Deriv accounts simultaneously (one WebSocket each)
- Per-account brain selection and configuration
- Independent growth engine sessions per account
- Export/import accounts for backup and migration

### Monitoring & Notifications
- System health dashboard (all accounts at a glance)
- Windows toast notifications for trade events
- Discord/Slack webhook integration
- Heartbeat monitoring with uptime tracking
- Trade journal with full-text search and date filtering
- Performance dashboard with equity curves and strategy comparison

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

## License

MIT License — see [LICENSE](LICENSE) for details.

## Disclaimer

⚠ **This software is for educational and demo purposes only.** Binary options trading carries significant risk of financial loss. Never trade with money you cannot afford to lose. The authors are not responsible for any financial losses incurred through use of this software.
