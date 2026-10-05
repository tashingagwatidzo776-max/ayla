# FX training simulation — small-account growth

- run at: 2026-10-04 12:13:42Z
- config: start $10, risk 1% per trade, min-lot risk $0.02, 60-bar hold, 3R target
- tape: C:\Users\DELL\AppData\Roaming\tf\data\train-history
- spread: venue snapshot (spreads.json) — points into the regime veto, points × point as per-trade cost
- brain memory: 295 family cell(s) across 342 run(s), 483783 measured trades, last trained 2026-10-04T12:13:42.5463226+00:00

> Evidence only. The simulator replays the production alpha roster and the
> production regime detector; it never trades. A human ports anything worth
> keeping. The paper soak and real-money gate still own every live path.

## Per-symbol result

| symbol | bars | trades | win | $10 → | net | worst DD | PF | verdict |
|---|---|---|---|---|---|---|---|---|
| XAGUSD | 200000 | 20000 | 39% | $667511.08 | +$667501.08 | $503360.14 | 1.21 | GREW — $10 → $667511.08 (+6675011%) over 20000 trades, win 39%, worst drawdown $503360.14. Evidence only: a human ports anything worth keeping. |
| XAUUSD | 200000 | 20000 | 38% | $6745.40 | +$6735.40 | $19763.24 | 1.03 | GREW — $10 → $6745.4 (+67354%) over 20000 trades, win 38%, worst drawdown $19763.24. Evidence only: a human ports anything worth keeping. |
| XAUUSDmicro | 190862 | 20000 | 38% | $4109.76 | +$4099.76 | $12465.61 | 1.03 | GREW — $10 → $4109.76 (+40998%) over 20000 trades, win 38%, worst drawdown $12465.61. Evidence only: a human ports anything worth keeping. |
| XAUEUR | 200000 | 20000 | 38% | $55.45 | +$45.45 | $15323.64 | 1.00 | GREW — $10 → $55.45 (+455%) over 20000 trades, win 38%, worst drawdown $15323.64. Evidence only: a human ports anything worth keeping. |
| GBPUSD | 500000 | 20000 | 37% | $10.11 | +$0.11 | $211246.02 | 1.00 | GREW — $10 → $10.11 (+1%) over 20000 trades, win 37%, worst drawdown $211246.02. Evidence only: a human ports anything worth keeping. |
| AUDUSD | 400000 | 2544 | 35% | $0.00 | -$10.00 | $10.15 | 0.79 | BLOWN — the $10 account hit zero after 2544 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BCHUSD | 400000 | 1154 | 31% | $0.00 | -$10.00 | $10.03 | 0.67 | BLOWN — the $10 account hit zero after 1154 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BNBUSD | 400000 | 4109 | 36% | $0.00 | -$10.00 | $11.16 | 0.86 | BLOWN — the $10 account hit zero after 4109 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BTCUSD | 300000 | 1297 | 32% | $0.00 | -$10.00 | $10.77 | 0.73 | BLOWN — the $10 account hit zero after 1297 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| DSHUSD | 200000 | 3208 | 36% | $0.00 | -$10.00 | $27.67 | 0.89 | BLOWN — the $10 account hit zero after 3208 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| EURUSD | 500000 | 1176 | 30% | $0.00 | -$10.00 | $13.62 | 0.74 | BLOWN — the $10 account hit zero after 1176 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| NZDUSD | 400000 | 976 | 30% | $0.00 | -$10.00 | $10.97 | 0.67 | BLOWN — the $10 account hit zero after 976 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCAD | 500000 | 6478 | 35% | $0.00 | -$10.00 | $1585.20 | 1.00 | BLOWN — the $10 account hit zero after 6478 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCHF | 500000 | 2287 | 35% | $0.00 | -$10.00 | $32.93 | 0.90 | BLOWN — the $10 account hit zero after 2287 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDJPY | 400000 | 8292 | 36% | $0.00 | -$10.00 | $187.89 | 0.99 | BLOWN — the $10 account hit zero after 8292 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAGEUR | 300000 | 1716 | 33% | $0.00 | -$10.00 | $10.00 | 0.71 | BLOWN — the $10 account hit zero after 1716 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPDUSD | 200000 | 466 | 19% | $0.00 | -$10.00 | $10.00 | 0.16 | BLOWN — the $10 account hit zero after 466 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPTUSD | 200000 | 4490 | 36% | $0.00 | -$10.00 | $180.17 | 0.99 | BLOWN — the $10 account hit zero after 4490 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |

## Brain memory (what the brain now remembers)

| symbol | family | trades | win | total R | expectancy R | $ P/L |
|---|---|---|---|---|---|---|
| XPTUSD | donchian-pullback(21) | 10 | 100% | +19.7 | +1.967 | $+14.05 |
| BTCUSD | keltner(20x2) | 10 | 100% | +19.6 | +1.961 | $+2.13 |
| USDCHF | macd(12/26) | 14 | 100% | +26.9 | +1.922 | $+2.65 |
| XAGUSD | open-range(30) | 6 | 83% | +10.5 | +1.756 | $+3090.85 |
| AUDUSD | bb-rev(20) | 10 | 80% | +16.2 | +1.624 | $+3.21 |
| DSHUSD | ou-rev | 1 | 100% | +1.6 | +1.589 | $+0.05 |
| EURUSD | ou-rev | 14 | 100% | +21.2 | +1.517 | $+1.90 |
| USDCAD | keltner(20x2) | 17 | 88% | +24.0 | +1.411 | $+8.48 |
| XAGEUR | ou-rev | 7 | 71% | +7.9 | +1.127 | $+5.07 |
| BNBUSD | open-range(30) | 1 | 100% | +0.8 | +0.841 | $+0.02 |
| NZDUSD | z-rev(20) | 77 | 65% | +64.2 | +0.834 | $+3.16 |
| GBPUSD | keltner(20x2) | 24 | 58% | +15.9 | +0.664 | $-920.41 |
| XAUUSD | bb-rev(20) | 54 | 59% | +34.3 | +0.635 | $-411.15 |
| XAUUSDmicro | bb-rev(20) | 54 | 59% | +34.1 | +0.631 | $-284.75 |
| GBPUSD | kalman-trend | 168 | 51% | +102.9 | +0.612 | $+19.26 |
| USDCAD | open-range(30) | 15 | 53% | +8.6 | +0.573 | $+11.26 |
| DSHUSD | vwap-trend(30) | 28 | 71% | +14.3 | +0.510 | $+5.34 |
| XAGEUR | rsi2-rev(10) | 31 | 52% | +15.8 | +0.510 | $+13.15 |
| BCHUSD | donchian(20) | 41 | 59% | +19.4 | +0.473 | $+13.15 |
| XAGUSD | rsi2-rev(10) | 48 | 54% | +22.1 | +0.460 | $-18.33 |
| USDJPY | bb-rev(20) | 25 | 76% | +11.5 | +0.458 | $+1.31 |
| XPTUSD | ou-rev | 3 | 67% | +1.4 | +0.451 | $+0.15 |
| EURUSD | bb-squeeze(20) | 137 | 58% | +60.1 | +0.439 | $+2.23 |
| XPDUSD | bb-squeeze(20) | 15 | 67% | +6.5 | +0.435 | $+1.51 |
| XAUUSD | bb-squeeze(20) | 74 | 53% | +30.6 | +0.413 | $+62.47 |
| XAUUSDmicro | vwap-trend(30) | 196 | 57% | +80.2 | +0.409 | $-179.58 |
| XAUUSDmicro | bb-squeeze(20) | 74 | 53% | +30.2 | +0.408 | $+43.80 |
| XPTUSD | z-rev(20) | 75 | 53% | +30.5 | +0.407 | $-2.38 |
| XAUUSD | vwap-trend(30) | 191 | 56% | +75.1 | +0.393 | $-251.24 |
| BCHUSD | vwap-rev(30) | 2084 | 51% | +782.9 | +0.376 | $+496.44 |
| USDCAD | donchian(20) | 200 | 68% | +75.1 | +0.376 | $+5.76 |
| XAGUSD | vwap-trend(30) | 130 | 56% | +47.7 | +0.367 | $+1669.87 |
| USDCAD | donchian-pullback(21) | 51 | 37% | +18.4 | +0.360 | $+2.54 |
| USDCHF | keltner(20x2) | 11 | 45% | +3.8 | +0.344 | $+0.73 |
| NZDUSD | vwap-rev(30) | 629 | 52% | +211.1 | +0.336 | $+67.18 |
| XAGEUR | bb-rev(20) | 49 | 51% | +15.9 | +0.325 | $+4.19 |
| XAGUSD | bb-rev(20) | 18 | 50% | +5.7 | +0.319 | $-3838.07 |
| BTCUSD | vwap-trend(30) | 76 | 43% | +22.9 | +0.302 | $+0.55 |
| USDCHF | z-rev(20) | 165 | 56% | +49.8 | +0.302 | $+2.48 |
| NZDUSD | bb-rev(20) | 42 | 64% | +11.6 | +0.277 | $+1.02 |

## Best per-family record per symbol

- AUDUSD: **bb-rev(20)** — 10 trades, 80% win, expectancy +1.624R, $+3.21
- BCHUSD: **donchian(20)** — 41 trades, 59% win, expectancy +0.473R, $+13.15
- BNBUSD: **open-range(30)** — 1 trades, 100% win, expectancy +0.841R, $+0.02
- BTCUSD: **keltner(20x2)** — 10 trades, 100% win, expectancy +1.961R, $+2.13
- DSHUSD: **ou-rev** — 1 trades, 100% win, expectancy +1.589R, $+0.05
- EURUSD: **ou-rev** — 14 trades, 100% win, expectancy +1.517R, $+1.90
- GBPUSD: **keltner(20x2)** — 24 trades, 58% win, expectancy +0.664R, $-920.41
- NZDUSD: **z-rev(20)** — 77 trades, 65% win, expectancy +0.834R, $+3.16
- USDCAD: **keltner(20x2)** — 17 trades, 88% win, expectancy +1.411R, $+8.48
- USDCHF: **macd(12/26)** — 14 trades, 100% win, expectancy +1.922R, $+2.65
- USDJPY: **bb-rev(20)** — 25 trades, 76% win, expectancy +0.458R, $+1.31
- XAGEUR: **ou-rev** — 7 trades, 71% win, expectancy +1.127R, $+5.07
- XAGUSD: **open-range(30)** — 6 trades, 83% win, expectancy +1.756R, $+3090.85
- XAUEUR: **ema-cross(9/21)** — 415 trades, 40% win, expectancy +0.015R, $-958.32
- XAUUSD: **bb-rev(20)** — 54 trades, 59% win, expectancy +0.635R, $-411.15
- XAUUSDmicro: **bb-rev(20)** — 54 trades, 59% win, expectancy +0.631R, $-284.75
- XPDUSD: **bb-squeeze(20)** — 15 trades, 67% win, expectancy +0.435R, $+1.51
- XPTUSD: **donchian-pullback(21)** — 10 trades, 100% win, expectancy +1.967R, $+14.05
