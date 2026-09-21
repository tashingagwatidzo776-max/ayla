# Deriv tradable catalog — what DON G FX can trade

Deriv's published offering, organized for this app. Live availability varies by region
and licensing; the authoritative runtime list is the API itself. **Verified live on
2026-09-21** from this machine: the new-platform OTP socket answers
`{"active_symbols":"brief"}` with **89 tradable symbols** for the demo account
(fields: `underlying_symbol`, `underlying_symbol_name`, `exchange_is_open`, `pip_size`) —
the full per-symbol list is at the bottom of this document.

## Markets (what you can trade on)

| Market | Examples | Hours |
|---|---|---|
| **Synthetic indices** (Deriv's own, RNG-driven, 24/7) | Volatility 10/25/50/75/100 (+ 1s variants), Boom 300/500/600/900/1000, Crash 300/500/600/900/1000, Jump 10/25/50/75/100, Step index, Range Break 100/200, DEX 900/1500, Drift-switch | 24/7 — never closed |
| **Basket indices** (synthetic FX/energy baskets) | AUD, EUR, GBP, USD, Gold baskets | 24/7 |
| **Forex** | Majors (EUR/USD, GBP/USD, USD/JPY…), minors, exotics; most with **-OTC** weekend variants | Weekdays; OTC 24/7 |
| **Stock indices** | Wall Street, US Tech 100, SP500, Germany 40, Japan 225 (+ OTC variants) | Session hours; OTC 24/7 |
| **Commodities** | Gold, Silver, Platinum, Palladium, Oil (+ OTC variants) | Session hours; OTC 24/7 |
| **Cryptocurrencies** | BTC, ETH, LTC, XRP, BCH, EOS and more | 24/7 |

The app's weekend `MarketIsClosed` comes from the session-hours rows — synthetics and
OTC variants keep trading when the live markets shut.

## Contract types (what you can buy on those underlyings)

| Contract | Direction semantics |
|---|---|
| **Ups & Downs** (Rise / Fall) | Higher or lower than entry at expiry — *the type DON G FX places today* |
| **Highs & Lows** (Higher / Lower) | Touch-or-exceed vs. strictly-lower barrier |
| **Touch / No Touch** | Does price touch the barrier before expiry |
| **In / Out** (Stays In / Goes Out) | Stays inside vs. exits the range |
| **Asians** (Asian Up / Asian Down) | Exit average vs. entry average |
| **Digits** (Matches/Differs, Even/Odd, Over/Under) | Last digit of the quote |
| **Accumulators** | Continuous compounding while price stays in a growth zone |
| **Multipliers** (MultiUp / MultiDown) | Leveraged position with stop-out — not an expiry option |
| **Turbos** (Long/Short call/put) | Barrier knock-out options |
| **Vanilla options** (where offered) | Standard European call/put |

## Verified live catalog — 89 symbols (2026-09-21, demo account DOT92951338)

### Commodities (4)

| Symbol | Name | pip |
|---|---|---|
| frxXAUUSD | Gold/USD | 0.01 |
| frxXPDUSD | Palladium/USD | 0.01 |
| frxXPTUSD | Platinum/USD | 0.01 |
| frxXAGUSD | Silver/USD | 0.0001 |

### Cryptocurrency (2)

| Symbol | Name | pip |
|---|---|---|
| cryBTCUSD | BTC/USD | 0.001 |
| cryETHUSD | ETH/USD | 0.00001 |

### Forex (25)

| Symbol | Name | pip |
|---|---|---|
| frxAUDCAD | AUD/CAD | 0.00001 |
| frxAUDCHF | AUD/CHF | 0.00001 |
| frxAUDJPY | AUD/JPY | 0.001 |
| frxAUDNZD | AUD/NZD | 0.00001 |
| frxAUDUSD | AUD/USD | 0.00001 |
| frxEURAUD | EUR/AUD | 0.00001 |
| frxEURCAD | EUR/CAD | 0.00001 |
| frxEURCHF | EUR/CHF | 0.00001 |
| frxEURGBP | EUR/GBP | 0.00001 |
| frxEURJPY | EUR/JPY | 0.001 |
| frxEURNZD | EUR/NZD | 0.00001 |
| frxEURUSD | EUR/USD | 0.00001 |
| frxGBPAUD | GBP/AUD | 0.00001 |
| frxGBPCAD | GBP/CAD | 0.00001 |
| frxGBPCHF | GBP/CHF | 0.00001 |
| frxGBPJPY | GBP/JPY | 0.001 |
| frxGBPNZD | GBP/NZD | 0.00001 |
| frxGBPUSD | GBP/USD | 0.00001 |
| frxNZDJPY | NZD/JPY | 0.001 |
| frxNZDUSD | NZD/USD | 0.00001 |
| frxUSDCAD | USD/CAD | 0.00001 |
| frxUSDCHF | USD/CHF | 0.00001 |
| frxUSDJPY | USD/JPY | 0.001 |
| frxUSDMXN | USD/MXN | 0.0001 |
| frxUSDPLN | USD/PLN | 0.0001 |

### Stock indices (12)

| Symbol | Name | pip |
|---|---|---|
| OTC_AS51 | Australia 200 | 0.01 |
| OTC_SX5E | Euro 50 | 0.01 |
| OTC_FCHI | France 40 | 0.01 |
| OTC_GDAXI | Germany 40 | 0.01 |
| OTC_HSI | Hong Kong 50 | 0.01 |
| OTC_N225 | Japan 225 | 0.01 |
| OTC_AEX | Netherlands 25 | 0.01 |
| OTC_SSMI | Swiss 20 | 0.01 |
| OTC_FTSE | UK 100 | 0.01 |
| OTC_SPC | US 500 | 0.01 |
| OTC_NDX | US Tech 100 | 0.01 |
| OTC_DJI | Wall Street 30 | 0.01 |

### Synthetic indices (46) — the 24/7 market the engine trades

| Symbol | Name | pip |
|---|---|---|
| WLDAUD | AUD Basket | 0.001 |
| WLDGBP | GBP Basket | 0.001 |
| WLDXAU | Gold Basket | 0.001 |
| WLDUSD | USD Basket | 0.001 |
| WLDEUR | EUR Basket | 0.001 |
| RDBEAR | Bear Market Index | 0.0001 |
| RDBULL | Bull Market Index | 0.0001 |
| BOOM1000 | Boom 1000 Index | 0.001 |
| BOOM150N | Boom 150 Index | 0.00001 |
| BOOM300N | Boom 300 Index | 0.001 |
| BOOM50 | Boom 50 Index | 0.001 |
| BOOM500 | Boom 500 Index | 0.001 |
| BOOM600 | Boom 600 Index | 0.001 |
| BOOM900 | Boom 900 Index | 0.001 |
| CRASH1000 | Crash 1000 Index | 0.001 |
| CRASH150N | Crash 150 Index | 0.00001 |
| CRASH300N | Crash 300 Index | 0.001 |
| CRASH50 | Crash 50 Index | 0.001 |
| CRASH500 | Crash 500 Index | 0.001 |
| CRASH600 | Crash 600 Index | 0.001 |
| CRASH900 | Crash 900 Index | 0.001 |
| JD10 | Jump 10 Index | 0.01 |
| JD100 | Jump 100 Index | 0.01 |
| JD25 | Jump 25 Index | 0.01 |
| JD50 | Jump 50 Index | 0.01 |
| JD75 | Jump 75 Index | 0.01 |
| RB100 | Range Break 100 Index | 0.1 |
| RB200 | Range Break 200 Index | 0.1 |
| stpRNG | Step Index 100 | 0.1 |
| stpRNG2 | Step Index 200 | 0.1 |
| stpRNG3 | Step Index 300 | 0.1 |
| stpRNG4 | Step Index 400 | 0.1 |
| stpRNG5 | Step Index 500 | 0.1 |
| R_10 | Volatility 10 Index | 0.001 |
| 1HZ10V | Volatility 10 (1s) Index | 0.01 |
| R_25 | Volatility 25 Index | 0.001 |
| 1HZ25V | Volatility 25 (1s) Index | 0.01 |
| 1HZ30V | Volatility 30 (1s) Index | 0.001 |
| R_50 | Volatility 50 Index | 0.0001 |
| 1HZ50V | Volatility 50 (1s) Index | 0.01 |
| R_75 | Volatility 75 Index | 0.0001 |
| 1HZ75V | Volatility 75 (1s) Index | 0.01 |
| 1HZ90V | Volatility 90 (1s) Index | 0.001 |
| R_100 | **Volatility 100 Index — the app's current trading symbol** | 0.01 |
| 1HZ100V | Volatility 100 (1s) Index | 0.01 |

## Where this fits in the app

- `DerivClient.GetProposalAsync`/`BuyAsync` place **Rise/Fall** today; the proposal is
  fetched first and `BuyAsync` executes by proposal id, so any new contract shape enters
  through the same choke point.
- Adding a new contract type = enumerate it via `contracts_for`, extend the brain's
  decision model, and **add the path to `docs/real-money-safety-audit.md`** — every trade
  path must be covered by the kill switch, governor and real-money gate before it ships.
