# FX training simulation — small-account growth

- run at: 2026-10-04 17:59:27Z
- config: start $10, risk 1% per trade, min-lot risk $0.02, $25/trade swing cap; per-symbol best-known routing from 5 sweep passes over the H1-complete tape (metal sam6/floor8/rr3, quiet-FX sam4/rr2/h120/floor3, EURUSD win40/rr3 + playbook-filtered roster, crypto sam3/rr2/floor4)
- tape: C:\Users\DELL\AppData\Roaming\tf\data\train-history
- spread: venue snapshot (spreads.json) — points into the regime veto, points × point as per-trade cost
- brain memory: 305 family cell(s) across 396 run(s), 1045631 measured trades, last trained 2026-10-04T17:59:27.2069046+00:00

> Evidence only. The simulator replays the production alpha roster and the
> production regime detector; it never trades. A human ports anything worth
> keeping. The paper soak and real-money gate still own every live path.

## Per-symbol result

| symbol | bars | trades | win | $10 → | net | worst DD | PF | verdict |
|---|---|---|---|---|---|---|---|---|
| EURUSD | 500000 | 20000 | 38% | $36275.19 | +$36265.19 | $41404.78 | 1.15 | GREW — $10 → $36275.19 (+362652%) over 20000 trades, win 38%, worst drawdown $41404.78. Evidence only: a human ports anything worth keeping. |
| XAUUSD | 200000 | 20000 | 44% | $18811.24 | +$18801.24 | $12477.99 | 1.09 | GREW — $10 → $18811.24 (+188012%) over 20000 trades, win 44%, worst drawdown $12477.99. Evidence only: a human ports anything worth keeping. |
| XAUUSDmicro | 190862 | 20000 | 44% | $18026.44 | +$18016.44 | $12619.44 | 1.08 | GREW — $10 → $18026.44 (+180164%) over 20000 trades, win 44%, worst drawdown $12619.44. Evidence only: a human ports anything worth keeping. |
| XAGUSD | 200000 | 20000 | 43% | $10773.44 | +$10763.44 | $12156.62 | 1.06 | GREW — $10 → $10773.44 (+107634%) over 20000 trades, win 43%, worst drawdown $12156.62. Evidence only: a human ports anything worth keeping. |
| XAUEUR | 200000 | 20000 | 44% | $1661.85 | +$1651.85 | $11295.81 | 1.01 | UNSTABLE — $10 → $1661.85 (+16519%) over 20000 trades, win 44%, but finished 85% below its $11400.57 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| USDJPY | 500000 | 20000 | 39% | $447.05 | +$437.05 | $9282.98 | 1.00 | UNSTABLE — $10 → $447.05 (+4370%) over 20000 trades, win 39%, but finished 95% below its $9730.02 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| GBPUSD | 500000 | 20000 | 39% | $45.44 | +$35.44 | $9600.64 | 1.00 | UNSTABLE — $10 → $45.44 (+354%) over 20000 trades, win 39%, but finished 100% below its $9623 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| AUDUSD | 500000 | 1063 | 29% | $0.00 | -$10.00 | $41.76 | 0.88 | BLOWN — the $10 account hit zero after 1063 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BCHUSD | 400000 | 1462 | 35% | $0.00 | -$10.00 | $10.03 | 0.63 | BLOWN — the $10 account hit zero after 1462 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BNBUSD | 400000 | 4086 | 39% | $0.00 | -$10.00 | $10.20 | 0.86 | BLOWN — the $10 account hit zero after 4086 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| BTCUSD | 300000 | 2162 | 36% | $0.00 | -$10.00 | $10.71 | 0.77 | BLOWN — the $10 account hit zero after 2162 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| DSHUSD | 278775 | 1950 | 37% | $0.00 | -$10.00 | $10.11 | 0.78 | BLOWN — the $10 account hit zero after 1950 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| NZDUSD | 500000 | 807 | 29% | $0.00 | -$10.00 | $11.62 | 0.56 | BLOWN — the $10 account hit zero after 807 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCAD | 500000 | 3737 | 35% | $0.00 | -$10.00 | $80.28 | 0.98 | BLOWN — the $10 account hit zero after 3737 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCHF | 500000 | 14674 | 38% | $0.00 | -$10.00 | $546.17 | 1.00 | BLOWN — the $10 account hit zero after 14674 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XAGEUR | 300000 | 6669 | 42% | $0.00 | -$10.00 | $166.26 | 0.99 | BLOWN — the $10 account hit zero after 6669 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPDUSD | 200000 | 3858 | 46% | $0.00 | -$10.00 | $10.00 | 0.82 | BLOWN — the $10 account hit zero after 3858 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPTUSD | 200000 | 6356 | 42% | $0.00 | -$10.00 | $102.99 | 0.98 | BLOWN — the $10 account hit zero after 6356 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |

## Brain memory (what the brain now remembers)

| symbol | family | trades | win | total R | expectancy R | $ P/L |
|---|---|---|---|---|---|---|
| BTCUSD | keltner(20x2) | 10 | 100% | +19.6 | +1.961 | $+2.13 |
| USDCHF | macd(12/26) | 23 | 100% | +44.5 | +1.933 | $+3.98 |
| DSHUSD | ou-rev | 2 | 100% | +3.5 | +1.728 | $+0.13 |
| AUDUSD | bb-rev(20) | 10 | 80% | +16.2 | +1.624 | $+3.21 |
| EURUSD | ou-rev | 14 | 100% | +21.2 | +1.517 | $+1.90 |
| XAGUSD | open-range(30) | 24 | 71% | +35.1 | +1.462 | $+3497.82 |
| XPTUSD | donchian-pullback(21) | 13 | 77% | +18.5 | +1.421 | $+14.02 |
| USDJPY | macd(12/26) | 4 | 75% | +5.1 | +1.278 | $-19.88 |
| USDCAD | keltner(20x2) | 26 | 81% | +26.7 | +1.028 | $+10.24 |
| USDJPY | keltner(20x2) | 6 | 67% | +5.9 | +0.976 | $+157.59 |
| BNBUSD | open-range(30) | 4 | 100% | +3.4 | +0.841 | $+0.07 |
| NZDUSD | z-rev(20) | 84 | 63% | +66.0 | +0.786 | $+3.14 |
| GBPUSD | kalman-trend | 204 | 54% | +138.3 | +0.678 | $+22.96 |
| BNBUSD | rsi2-rev(10) | 8 | 50% | +4.5 | +0.563 | $-19.72 |
| XPTUSD | ou-rev | 12 | 67% | +6.0 | +0.496 | $-0.30 |
| BNBUSD | bb-rev(20) | 17 | 47% | +7.6 | +0.446 | $-20.41 |
| EURUSD | bb-squeeze(20) | 137 | 58% | +60.1 | +0.439 | $+2.23 |
| XAGEUR | open-range(30) | 45 | 49% | +18.7 | +0.416 | $+6.66 |
| XPDUSD | bb-squeeze(20) | 21 | 62% | +8.7 | +0.413 | $+1.55 |
| BCHUSD | vwap-rev(30) | 2084 | 51% | +782.9 | +0.376 | $+496.44 |
| USDJPY | ou-rev | 67 | 84% | +23.6 | +0.352 | $-2.14 |
| XAUUSD | donchian-pullback(21) | 67 | 57% | +22.7 | +0.339 | $+356.12 |
| USDCHF | ou-rev | 102 | 55% | +34.5 | +0.338 | $-21.28 |
| XAUUSDmicro | donchian-pullback(21) | 67 | 57% | +22.6 | +0.338 | $+385.28 |
| BTCUSD | vwap-trend(30) | 88 | 44% | +28.8 | +0.327 | $+0.76 |
| XPTUSD | z-rev(20) | 90 | 51% | +29.0 | +0.323 | $-2.48 |
| NZDUSD | vwap-rev(30) | 661 | 51% | +208.4 | +0.315 | $+66.65 |
| XPDUSD | open-range(30) | 9 | 67% | +2.7 | +0.301 | $+0.12 |
| USDJPY | bb-rev(20) | 130 | 52% | +38.1 | +0.293 | $+174.80 |
| BCHUSD | donchian(20) | 50 | 48% | +13.2 | +0.263 | $+12.94 |
| GBPUSD | ema-cross(9/21) | 1192 | 48% | +311.5 | +0.261 | $-3440.55 |
| USDCAD | open-range(30) | 33 | 42% | +8.5 | +0.256 | $+11.84 |
| XAUUSDmicro | vwap-trend(30) | 466 | 52% | +117.4 | +0.252 | $+588.59 |
| XAUUSD | vwap-trend(30) | 461 | 52% | +112.8 | +0.245 | $+527.44 |
| USDCHF | rsi-mom(14) | 12 | 50% | +2.9 | +0.241 | $-1.06 |
| USDCAD | donchian(20) | 314 | 59% | +74.6 | +0.238 | $+0.04 |
| XAGUSD | rsi2-rev(10) | 93 | 47% | +22.1 | +0.237 | $+46.16 |
| DSHUSD | vwap-trend(30) | 44 | 55% | +10.0 | +0.227 | $+5.10 |
| XAGUSD | vwap-trend(30) | 253 | 55% | +55.9 | +0.221 | $+1680.14 |
| DSHUSD | vwap-rev(30) | 1754 | 44% | +387.6 | +0.221 | $+123.32 |

## Best per-family record per symbol

- AUDUSD: **bb-rev(20)** — 10 trades, 80% win, expectancy +1.624R, $+3.21
- BCHUSD: **vwap-rev(30)** — 2084 trades, 51% win, expectancy +0.376R, $+496.44
- BNBUSD: **open-range(30)** — 4 trades, 100% win, expectancy +0.841R, $+0.07
- BTCUSD: **keltner(20x2)** — 10 trades, 100% win, expectancy +1.961R, $+2.13
- DSHUSD: **ou-rev** — 2 trades, 100% win, expectancy +1.728R, $+0.13
- EURUSD: **ou-rev** — 14 trades, 100% win, expectancy +1.517R, $+1.90
- GBPUSD: **kalman-trend** — 204 trades, 54% win, expectancy +0.678R, $+22.96
- NZDUSD: **z-rev(20)** — 84 trades, 63% win, expectancy +0.786R, $+3.14
- USDCAD: **keltner(20x2)** — 26 trades, 81% win, expectancy +1.028R, $+10.24
- USDCHF: **macd(12/26)** — 23 trades, 100% win, expectancy +1.933R, $+3.98
- USDJPY: **macd(12/26)** — 4 trades, 75% win, expectancy +1.278R, $-19.88
- XAGEUR: **open-range(30)** — 45 trades, 49% win, expectancy +0.416R, $+6.66
- XAGUSD: **open-range(30)** — 24 trades, 71% win, expectancy +1.462R, $+3497.82
- XAUEUR: **ema-cross(9/21)** — 1582 trades, 48% win, expectancy +0.088R, $+1506.16
- XAUUSD: **donchian-pullback(21)** — 67 trades, 57% win, expectancy +0.339R, $+356.12
- XAUUSDmicro: **donchian-pullback(21)** — 67 trades, 57% win, expectancy +0.338R, $+385.28
- XPDUSD: **bb-squeeze(20)** — 21 trades, 62% win, expectancy +0.413R, $+1.55
- XPTUSD: **donchian-pullback(21)** — 13 trades, 77% win, expectancy +1.421R, $+14.02
