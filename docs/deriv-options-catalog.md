# Deriv tradable catalog — what DON G FX can trade

Deriv's published offering, organized for this app. Live availability varies by region
and licensing; the authoritative runtime list is the API itself
(`{"active_symbols":"brief"}` for markets, `{"contracts_for":"<symbol>","currency":"USD"}`
for the contract types on a symbol). Note: from this machine's network the classic
`active_symbols` call currently returns an **empty list** (regional serving) — trading
itself works fine via the new-platform endpoints the app uses. The tables below are
Deriv's platform structure, not a per-region guarantee.

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

## Where this fits in the app

- `DerivClient.GetProposalAsync`/`BuyAsync` place **Rise/Fall** today; the proposal is
  fetched first and `BuyAsync` executes by proposal id, so any new contract shape enters
  through the same choke point.
- Adding a new contract type = enumerate it via `contracts_for`, extend the brain's
  decision model, and **add the path to `docs/real-money-safety-audit.md`** — every trade
  path must be covered by the kill switch, governor and real-money gate before it ships.
