# FX training simulation — small-account growth

- run at: 2026-10-05 13:32:15Z
- config: start $10, risk 1% per trade, min-lot risk $0.02, $25/trade swing cap; per-symbol best-known routing from 7 sweep passes over the completed H4/D1/H1 tape (metal sam6/floor8/rr3 · pos roster, quiet majors quietonly or +quiet by symbol, EURUSD win40/rr3 + playbook-filtered roster, crypto win70/h90/f8/rr3 · pos5)
- tape: C:\Users\DELL\AppData\Roaming\tf\data\train-history
- spread: venue snapshot (spreads.json) — points into the regime veto, points × point as per-trade cost
- brain memory: 321 family cell(s) across 540 run(s), 3074972 measured trades, last trained 2026-10-05T13:32:15.7545465+00:00

> Evidence only. The simulator replays the production alpha roster and the
> production regime detector; it never trades. A human ports anything worth
> keeping. The paper soak and real-money gate still own every live path.

## Per-symbol result

| symbol | bars | trades | win | $10 → | net | worst DD | PF | verdict |
|---|---|---|---|---|---|---|---|---|
| EURUSD | 567623 | 20000 | 39% | $88047.69 | +$88037.69 | $14336.12 | 1.31 | GREW — $10 → $88047.69 (+880377%) over 20000 trades, win 39%, worst drawdown $14336.12. Evidence only: a human ports anything worth keeping. |
| USDJPY | 567625 | 20000 | 42% | $65501.78 | +$65491.78 | $6468.81 | 1.25 | GREW — $10 → $65501.78 (+654918%) over 20000 trades, win 42%, worst drawdown $6468.81. Evidence only: a human ports anything worth keeping. |
| XAUUSDmicro | 245089 | 20000 | 46% | $63361.24 | +$63351.24 | $6709.06 | 1.29 | GREW — $10 → $63361.24 (+633512%) over 20000 trades, win 46%, worst drawdown $6709.06. Evidence only: a human ports anything worth keeping. |
| XAUUSD | 522170 | 20000 | 45% | $61612.41 | +$61602.41 | $7700.31 | 1.33 | GREW — $10 → $61612.41 (+616024%) over 20000 trades, win 45%, worst drawdown $7700.31. Evidence only: a human ports anything worth keeping. |
| DSHUSD | 501255 | 20000 | 37% | $48730.76 | +$48720.76 | $10867.76 | 1.22 | GREW — $10 → $48730.76 (+487208%) over 20000 trades, win 37%, worst drawdown $10867.76. Evidence only: a human ports anything worth keeping. |
| XAUEUR | 431994 | 20000 | 45% | $42589.99 | +$42579.99 | $7523.67 | 1.20 | GREW — $10 → $42589.99 (+425800%) over 20000 trades, win 45%, worst drawdown $7523.67. Evidence only: a human ports anything worth keeping. |
| XAGEUR | 431983 | 20000 | 43% | $16523.71 | +$16513.71 | $13965.86 | 1.09 | GREW — $10 → $16523.71 (+165137%) over 20000 trades, win 43%, worst drawdown $13965.86. Evidence only: a human ports anything worth keeping. |
| BNBUSD | 408554 | 12972 | 39% | $12736.41 | +$12726.41 | $13488.73 | 1.08 | UNSTABLE — $10 → $12736.41 (+127264%) over 12972 trades, win 39%, but finished 51% below its $26225.15 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| BTCUSD | 429055 | 11507 | 37% | $8455.79 | +$8445.79 | $8685.42 | 1.05 | GREW — $10 → $8455.79 (+84458%) over 11507 trades, win 37%, worst drawdown $8685.42. Evidence only: a human ports anything worth keeping. |
| XAGUSD | 522626 | 20000 | 43% | $8204.19 | +$8194.19 | $12141.42 | 1.05 | GREW — $10 → $8204.19 (+81942%) over 20000 trades, win 43%, worst drawdown $12141.42. Evidence only: a human ports anything worth keeping. |
| GBPUSD | 556227 | 13833 | 40% | $7439.52 | +$7429.52 | $9914.17 | 1.06 | UNSTABLE — $10 → $7439.52 (+74295%) over 13833 trades, win 40%, but finished 57% below its $17286.54 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| AUDUSD | 556248 | 20000 | 39% | $145.56 | +$135.56 | $953.32 | 1.01 | UNSTABLE — $10 → $145.56 (+1356%) over 20000 trades, win 39%, but finished 85% below its $956.74 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| NZDUSD | 554438 | 20000 | 39% | $11.75 | +$1.75 | $4983.47 | 1.00 | UNSTABLE — $10 → $11.75 (+17%) over 20000 trades, win 39%, but finished 100% below its $4987.83 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| BCHUSD | 419168 | 3774 | 35% | $0.00 | -$10.00 | $59.08 | 0.97 | BLOWN — the $10 account hit zero after 3774 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCAD | 556252 | 10519 | 37% | $0.00 | -$10.00 | $67.90 | 0.98 | BLOWN — the $10 account hit zero after 10519 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCHF | 567535 | 1233 | 30% | $0.00 | -$10.00 | $38.20 | 0.82 | BLOWN — the $10 account hit zero after 1233 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPDUSD | 450913 | 5772 | 43% | $0.00 | -$10.00 | $274.00 | 0.99 | BLOWN — the $10 account hit zero after 5772 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPTUSD | 450872 | 1395 | 42% | $0.00 | -$10.00 | $10.00 | 0.61 | BLOWN — the $10 account hit zero after 1395 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |

## Brain memory (what the brain now remembers)

| symbol | family | trades | win | total R | expectancy R | $ P/L |
|---|---|---|---|---|---|---|
| USDCHF | macd(12/26) | 23 | 100% | +44.5 | +1.933 | $+3.98 |
| USDJPY | macd(12/26) | 12 | 92% | +21.0 | +1.747 | $+376.39 |
| DSHUSD | ou-rev | 2 | 100% | +3.5 | +1.728 | $+0.13 |
| AUDUSD | bb-rev(20) | 10 | 80% | +16.2 | +1.624 | $+3.21 |
| XAGUSD | open-range(30) | 31 | 77% | +49.8 | +1.605 | $+3864.48 |
| EURUSD | ou-rev | 14 | 100% | +21.2 | +1.517 | $+1.90 |
| USDCAD | keltner(20x2) | 26 | 81% | +26.7 | +1.028 | $+10.24 |
| BNBUSD | open-range(30) | 4 | 100% | +3.4 | +0.841 | $+0.07 |
| XAGUSD | ou-rev | 62 | 60% | +49.4 | +0.796 | $+5368.05 |
| NZDUSD | z-rev(20) | 84 | 63% | +66.0 | +0.786 | $+3.14 |
| XAUUSDmicro | donchian-pullback(21) | 83 | 55% | +38.5 | +0.464 | $+783.09 |
| USDJPY | ou-rev | 75 | 85% | +33.2 | +0.443 | $+237.39 |
| EURUSD | bb-squeeze(20) | 137 | 58% | +60.1 | +0.439 | $+2.23 |
| GBPUSD | kalman-trend | 309 | 51% | +130.0 | +0.421 | $+22.01 |
| GBPUSD | ema-cross(9/21) | 3803 | 53% | +1555.3 | +0.409 | $-549.75 |
| XAUUSDmicro | vwap-trend(30) | 658 | 58% | +261.9 | +0.398 | $+4199.35 |
| XAUUSD | donchian-pullback(21) | 67 | 57% | +22.7 | +0.339 | $+356.12 |
| USDCHF | ou-rev | 102 | 55% | +34.5 | +0.338 | $-21.28 |
| USDJPY | ema-cross(9/21) | 26670 | 48% | +8969.8 | +0.336 | $+172056.09 |
| NZDUSD | vwap-rev(30) | 662 | 51% | +210.3 | +0.318 | $+66.69 |
| XAUUSDmicro | rsi2-rev(10) | 200 | 52% | +61.3 | +0.307 | $+2380.87 |
| XAUUSD | vwap-trend(30) | 525 | 52% | +149.3 | +0.284 | $+1440.62 |
| EURUSD | roc(10) | 98672 | 41% | +26690.7 | +0.270 | $+631848.68 |
| USDCAD | open-range(30) | 33 | 42% | +8.5 | +0.256 | $+11.84 |
| XAGUSD | rsi2-rev(10) | 574 | 48% | +138.3 | +0.241 | $+2409.52 |
| USDCHF | rsi-mom(14) | 12 | 50% | +2.9 | +0.241 | $-1.06 |
| XAUUSDmicro | ema-cross(9/21) | 20378 | 49% | +4829.6 | +0.237 | $+84352.73 |
| USDCAD | donchian(20) | 316 | 58% | +72.6 | +0.230 | $-0.07 |
| GBPUSD | band-fade(50) | 970 | 43% | +214.6 | +0.221 | $+4606.34 |
| DSHUSD | donchian(20) | 1574 | 38% | +328.0 | +0.208 | $+3681.21 |
| XAUUSDmicro | bb-squeeze(20) | 146 | 49% | +29.9 | +0.205 | $+108.06 |
| XAUEUR | ema-cross(9/21) | 20926 | 47% | +4274.8 | +0.204 | $+61513.54 |
| AUDUSD | rsi-mom(14) | 14 | 93% | +2.8 | +0.199 | $+0.26 |
| AUDUSD | vwap-rev(30) | 838 | 50% | +160.8 | +0.192 | $+35.22 |
| XAUUSD | ema-cross(9/21) | 39826 | 47% | +7560.0 | +0.190 | $+135945.44 |
| DSHUSD | vwap-trend(30) | 18044 | 39% | +3359.0 | +0.186 | $+77759.52 |
| XAUUSDmicro | ema-slope(21) | 6638 | 45% | +1228.0 | +0.185 | $+29288.41 |
| XAUUSD | bb-rev(20) | 203 | 52% | +36.1 | +0.178 | $-420.20 |
| USDJPY | bb-rev(20) | 218 | 45% | +35.9 | +0.165 | $+119.86 |
| GBPUSD | rsi2-rev(10) | 1163 | 47% | +187.4 | +0.161 | $-2122.16 |

## Best per-family record per symbol

- AUDUSD: **bb-rev(20)** — 10 trades, 80% win, expectancy +1.624R, $+3.21
- BCHUSD: **bb-squeeze(20)** — 28 trades, 32% win, expectancy +0.016R, $-0.58
- BNBUSD: **open-range(30)** — 4 trades, 100% win, expectancy +0.841R, $+0.07
- BTCUSD: **ema-cross(9/21)** — 34980 trades, 38% win, expectancy +0.083R, $+26948.88
- DSHUSD: **ou-rev** — 2 trades, 100% win, expectancy +1.728R, $+0.13
- EURUSD: **ou-rev** — 14 trades, 100% win, expectancy +1.517R, $+1.90
- GBPUSD: **kalman-trend** — 309 trades, 51% win, expectancy +0.421R, $+22.01
- NZDUSD: **z-rev(20)** — 84 trades, 63% win, expectancy +0.786R, $+3.14
- USDCAD: **keltner(20x2)** — 26 trades, 81% win, expectancy +1.028R, $+10.24
- USDCHF: **macd(12/26)** — 23 trades, 100% win, expectancy +1.933R, $+3.98
- USDJPY: **macd(12/26)** — 12 trades, 92% win, expectancy +1.747R, $+376.39
- XAGEUR: **open-range(30)** — 111438 trades, 43% win, expectancy +0.056R, $+82840.84
- XAGUSD: **open-range(30)** — 31 trades, 77% win, expectancy +1.605R, $+3864.48
- XAUEUR: **ema-cross(9/21)** — 20926 trades, 47% win, expectancy +0.204R, $+61513.54
- XAUUSD: **donchian-pullback(21)** — 67 trades, 57% win, expectancy +0.339R, $+356.12
- XAUUSDmicro: **donchian-pullback(21)** — 83 trades, 55% win, expectancy +0.464R, $+783.09
- XPDUSD: **open-range(30)** — 9472 trades, 43% win, expectancy -0.006R, $+28.44
- XPTUSD: **vwap-rev(30)** — 20075 trades, 45% win, expectancy -0.008R, $+592.01
