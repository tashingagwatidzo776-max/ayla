# FX training simulation — small-account growth

- run at: 2026-10-04 07:08:05Z
- config: start $10, risk 2% per trade, min-lot risk $0.05, 5-bar hold, 2R target
- tape: C:\Users\DELL\AppData\Roaming\tf\data\train-history
- spread: venue snapshot (spreads.json) — points into the regime veto, points × point as per-trade cost
- brain memory: 272 family cell(s) across 144 run(s), 146512 measured trades, last trained 2026-10-04T07:08:05.8289285+00:00

> Evidence only. The simulator replays the production alpha roster and the
> production regime detector; it never trades. A human ports anything worth
> keeping. The paper soak and real-money gate still own every live path.

## Per-symbol result

| symbol | bars | trades | win | $10 → | net | worst DD | PF | verdict |
|---|---|---|---|---|---|---|---|---|
| AUDUSD | 400000 | 1013 | 38% | $0.00 | -$10.00 | $14.14 | 0.84 | BLOWN — the $10 account hit zero after 1013 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BCHUSD | 400000 | 172 | 15% | $0.00 | -$10.00 | $10.64 | 0.14 | BLOWN — the $10 account hit zero after 172 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BNBUSD | 400000 | 215 | 21% | $0.00 | -$10.00 | $10.41 | 0.31 | BLOWN — the $10 account hit zero after 215 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BTCUSD | 300000 | 1148 | 39% | $0.00 | -$10.00 | $10.17 | 0.75 | BLOWN — the $10 account hit zero after 1148 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| DSHUSD | 200000 | 683 | 36% | $0.00 | -$10.00 | $12.43 | 0.65 | BLOWN — the $10 account hit zero after 683 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| EURUSD | 500000 | 1774 | 39% | $0.00 | -$10.00 | $14.68 | 0.90 | BLOWN — the $10 account hit zero after 1774 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| GBPUSD | 500000 | 3894 | 40% | $0.00 | -$10.00 | $228.68 | 0.99 | BLOWN — the $10 account hit zero after 3894 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| NZDUSD | 400000 | 893 | 36% | $0.00 | -$10.00 | $10.82 | 0.79 | BLOWN — the $10 account hit zero after 893 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCAD | 500000 | 814 | 36% | $0.00 | -$10.00 | $10.03 | 0.73 | BLOWN — the $10 account hit zero after 814 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCHF | 500000 | 598 | 34% | $0.00 | -$10.00 | $10.09 | 0.70 | BLOWN — the $10 account hit zero after 598 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDJPY | 400000 | 1687 | 40% | $0.00 | -$10.00 | $13.18 | 0.86 | BLOWN — the $10 account hit zero after 1687 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAGEUR | 300000 | 360 | 30% | $0.00 | -$10.00 | $10.17 | 0.43 | BLOWN — the $10 account hit zero after 360 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAGUSD | 200000 | 716 | 34% | $0.00 | -$10.00 | $11.48 | 0.69 | BLOWN — the $10 account hit zero after 716 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAUEUR | 200000 | 1193 | 38% | $0.00 | -$10.00 | $15.68 | 0.85 | BLOWN — the $10 account hit zero after 1193 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAUUSD | 200000 | 1190 | 39% | $0.00 | -$10.00 | $17.23 | 0.85 | BLOWN — the $10 account hit zero after 1190 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAUUSDmicro | 190862 | 1178 | 38% | $0.00 | -$10.00 | $16.34 | 0.85 | BLOWN — the $10 account hit zero after 1178 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPDUSD | 200000 | 143 | 8% | $0.00 | -$10.00 | $10.00 | 0.05 | BLOWN — the $10 account hit zero after 143 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPTUSD | 200000 | 220 | 24% | $0.00 | -$10.00 | $10.00 | 0.15 | BLOWN — the $10 account hit zero after 220 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |

## Brain memory (what the brain now remembers)

| symbol | family | trades | win | total R | expectancy R | $ P/L |
|---|---|---|---|---|---|---|
| XAGEUR | ou-rev | 1 | 100% | +2.0 | +1.978 | $+1.22 |
| XPTUSD | donchian-pullback(21) | 2 | 100% | +3.9 | +1.967 | $+2.92 |
| BTCUSD | keltner(20x2) | 2 | 100% | +3.9 | +1.961 | $+0.74 |
| XAUEUR | bb-rev(20) | 1 | 100% | +1.9 | +1.948 | $+0.10 |
| XAUUSD | bb-rev(20) | 9 | 100% | +17.4 | +1.937 | $+1.42 |
| XAUUSDmicro | bb-rev(20) | 9 | 100% | +17.4 | +1.929 | $+1.34 |
| AUDUSD | bb-rev(20) | 1 | 100% | +1.9 | +1.904 | $+0.62 |
| USDCAD | keltner(20x2) | 7 | 100% | +13.3 | +1.900 | $+0.67 |
| USDCHF | macd(12/26) | 7 | 100% | +12.9 | +1.843 | $+1.25 |
| GBPUSD | keltner(20x2) | 8 | 88% | +12.0 | +1.499 | $+0.69 |
| EURUSD | ou-rev | 7 | 100% | +10.2 | +1.454 | $+1.00 |
| NZDUSD | z-rev(20) | 28 | 82% | +35.2 | +1.258 | $+2.92 |
| XAGUSD | vwap-trend(30) | 37 | 84% | +39.5 | +1.069 | $+1.93 |
| XAGUSD | bb-rev(20) | 1 | 100% | +0.9 | +0.948 | $+0.11 |
| XAGUSD | rsi2-rev(10) | 11 | 73% | +10.4 | +0.943 | $+0.63 |
| USDCAD | donchian-pullback(21) | 14 | 50% | +12.7 | +0.907 | $+0.97 |
| XAUUSD | bb-squeeze(20) | 16 | 69% | +14.5 | +0.904 | $+0.74 |
| XAUUSDmicro | bb-squeeze(20) | 16 | 69% | +14.3 | +0.894 | $+0.63 |
| XAUUSDmicro | vwap-trend(30) | 52 | 71% | +41.9 | +0.806 | $+3.93 |
| XAUUSD | vwap-trend(30) | 51 | 71% | +41.1 | +0.806 | $+4.11 |
| DSHUSD | vwap-trend(30) | 8 | 100% | +5.5 | +0.686 | $+2.06 |
| USDCAD | donchian(20) | 63 | 89% | +41.0 | +0.650 | $+2.91 |
| GBPUSD | kalman-trend | 84 | 50% | +44.8 | +0.533 | $+9.22 |
| USDCHF | z-rev(20) | 59 | 63% | +30.3 | +0.514 | $+1.47 |
| EURUSD | bb-squeeze(20) | 70 | 60% | +34.4 | +0.491 | $+1.31 |
| USDCHF | keltner(20x2) | 2 | 50% | +0.9 | +0.455 | $+0.15 |
| XPDUSD | bb-squeeze(20) | 3 | 67% | +1.3 | +0.435 | $+0.32 |
| XPTUSD | z-rev(20) | 15 | 53% | +6.1 | +0.407 | $-0.50 |
| BCHUSD | vwap-rev(30) | 308 | 52% | +121.7 | +0.395 | $+88.72 |
| BTCUSD | vwap-trend(30) | 32 | 47% | +12.0 | +0.375 | $+0.39 |
| BCHUSD | donchian(20) | 6 | 50% | +2.2 | +0.363 | $+0.21 |
| NZDUSD | bb-rev(20) | 21 | 67% | +6.8 | +0.326 | $+0.57 |
| GBPUSD | vwap-rev(30) | 984 | 53% | +303.7 | +0.309 | $+145.19 |
| AUDUSD | rsi-mom(14) | 7 | 100% | +2.0 | +0.291 | $+0.16 |
| NZDUSD | vwap-rev(30) | 173 | 55% | +48.9 | +0.283 | $+16.74 |
| XAGEUR | bb-rev(20) | 17 | 53% | +4.8 | +0.280 | $+1.36 |
| EURUSD | vwap-rev(30) | 489 | 49% | +125.5 | +0.257 | $+17.80 |
| XPTUSD | bb-squeeze(20) | 7 | 43% | +1.7 | +0.247 | $+3.59 |
| DSHUSD | vwap-rev(30) | 258 | 45% | +62.7 | +0.243 | $+32.24 |
| USDJPY | bb-rev(20) | 7 | 100% | +1.4 | +0.205 | $+0.18 |

## Best per-family record per symbol

- AUDUSD: **bb-rev(20)** — 1 trades, 100% win, expectancy +1.904R, $+0.62
- BCHUSD: **vwap-rev(30)** — 308 trades, 52% win, expectancy +0.395R, $+88.72
- BNBUSD: **bb-squeeze(20)** — 7 trades, 43% win, expectancy +0.042R, $-2.95
- BTCUSD: **keltner(20x2)** — 2 trades, 100% win, expectancy +1.961R, $+0.74
- DSHUSD: **vwap-trend(30)** — 8 trades, 100% win, expectancy +0.686R, $+2.06
- EURUSD: **ou-rev** — 7 trades, 100% win, expectancy +1.454R, $+1.00
- GBPUSD: **keltner(20x2)** — 8 trades, 88% win, expectancy +1.499R, $+0.69
- NZDUSD: **z-rev(20)** — 28 trades, 82% win, expectancy +1.258R, $+2.92
- USDCAD: **keltner(20x2)** — 7 trades, 100% win, expectancy +1.900R, $+0.67
- USDCHF: **macd(12/26)** — 7 trades, 100% win, expectancy +1.843R, $+1.25
- USDJPY: **bb-rev(20)** — 7 trades, 100% win, expectancy +0.205R, $+0.18
- XAGEUR: **ou-rev** — 1 trades, 100% win, expectancy +1.978R, $+1.22
- XAGUSD: **vwap-trend(30)** — 37 trades, 84% win, expectancy +1.069R, $+1.93
- XAUEUR: **bb-rev(20)** — 1 trades, 100% win, expectancy +1.948R, $+0.10
- XAUUSD: **bb-rev(20)** — 9 trades, 100% win, expectancy +1.937R, $+1.42
- XAUUSDmicro: **bb-rev(20)** — 9 trades, 100% win, expectancy +1.929R, $+1.34
- XPDUSD: **bb-squeeze(20)** — 3 trades, 67% win, expectancy +0.435R, $+0.32
- XPTUSD: **donchian-pullback(21)** — 2 trades, 100% win, expectancy +1.967R, $+2.92
