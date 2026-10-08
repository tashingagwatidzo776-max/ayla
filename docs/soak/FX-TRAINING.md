# FX training simulation — small-account growth

- run at: 2026-10-07 13:13:07Z
- config: start $10, risk 1% per trade, min-lot risk $0.02, $25/trade swing cap; per-symbol best-known routing from 7 sweep passes over the completed H4/D1/H1 tape (metal sam6/floor8/rr3 · pos roster, quiet majors quietonly or +quiet by symbol, EURUSD win40/rr3 + playbook-filtered roster, crypto win70/h90/f8/rr3 · pos5)
- tape: C:\Users\DELL\AppData\Roaming\tf\data\train-history
- spread: venue snapshot (spreads.json) — points into the regime veto, points × point as per-trade cost
- brain memory: 321 family cell(s) across 558 run(s), 3349009 measured trades, last trained 2026-10-07T13:13:07.1423239+00:00

> Evidence only. The simulator replays the production alpha roster and the
> production regime detector; it never trades. A human ports anything worth
> keeping. The paper soak and real-money gate still own every live path.

## Per-symbol result

| symbol | bars | trades | win | $10 → | net | worst DD | PF | verdict |
|---|---|---|---|---|---|---|---|---|
| BTCUSD | 429055 | 20000 | 39% | $102519.18 | +$102509.18 | $13706.52 | 1.36 | GREW — $10 → $102519.18 (+1025092%) over 20000 trades, win 39%, worst drawdown $13706.52. Evidence only: a human ports anything worth keeping. |
| EURUSD | 567623 | 20000 | 39% | $88410.19 | +$88400.19 | $14296.90 | 1.32 | GREW — $10 → $88410.19 (+884002%) over 20000 trades, win 39%, worst drawdown $14296.9. Evidence only: a human ports anything worth keeping. |
| XAUUSDmicro | 245811 | 20000 | 46% | $64944.19 | +$64934.19 | $6639.26 | 1.30 | GREW — $10 → $64944.19 (+649342%) over 20000 trades, win 46%, worst drawdown $6639.26. Evidence only: a human ports anything worth keeping. |
| XAUUSD | 522170 | 20000 | 45% | $64424.75 | +$64414.75 | $7638.26 | 1.34 | GREW — $10 → $64424.75 (+644147%) over 20000 trades, win 45%, worst drawdown $7638.26. Evidence only: a human ports anything worth keeping. |
| DSHUSD | 501255 | 20000 | 38% | $51706.72 | +$51696.72 | $10759.87 | 1.22 | GREW — $10 → $51706.72 (+516967%) over 20000 trades, win 38%, worst drawdown $10759.87. Evidence only: a human ports anything worth keeping. |
| XAUEUR | 431994 | 20000 | 45% | $51638.09 | +$51628.09 | $10628.54 | 1.23 | GREW — $10 → $51638.09 (+516281%) over 20000 trades, win 45%, worst drawdown $10628.54. Evidence only: a human ports anything worth keeping. |
| BNBUSD | 408554 | 20000 | 36% | $36813.46 | +$36803.46 | $8315.93 | 1.12 | GREW — $10 → $36813.46 (+368035%) over 20000 trades, win 36%, worst drawdown $8315.93. Evidence only: a human ports anything worth keeping. |
| USDJPY | 567625 | 20000 | 41% | $35749.71 | +$35739.71 | $7842.79 | 1.14 | GREW — $10 → $35749.71 (+357397%) over 20000 trades, win 41%, worst drawdown $7842.79. Evidence only: a human ports anything worth keeping. |
| XAGEUR | 431983 | 20000 | 45% | $34517.18 | +$34507.18 | $9003.96 | 1.19 | GREW — $10 → $34517.18 (+345072%) over 20000 trades, win 45%, worst drawdown $9003.96. Evidence only: a human ports anything worth keeping. |
| XAGUSD | 522626 | 20000 | 43% | $14035.32 | +$14025.32 | $11552.24 | 1.09 | GREW — $10 → $14035.32 (+140253%) over 20000 trades, win 43%, worst drawdown $11552.24. Evidence only: a human ports anything worth keeping. |
| AUDUSD | 556248 | 20000 | 40% | $308.07 | +$298.07 | $1373.21 | 1.01 | UNSTABLE — $10 → $308.07 (+2981%) over 20000 trades, win 40%, but finished 78% below its $1379.45 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| NZDUSD | 554438 | 20000 | 39% | $94.55 | +$84.55 | $6645.70 | 1.00 | UNSTABLE — $10 → $94.55 (+846%) over 20000 trades, win 39%, but finished 99% below its $6675.93 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| GBPUSD | 556227 | 8350 | 39% | $30.35 | +$20.35 | $679.78 | 1.00 | UNSTABLE — $10 → $30.35 (+204%) over 8350 trades, win 39%, but finished 96% below its $709.43 peak. Ballooning and not recovering is variance, not growth — a human must not port this without a drawdown gate. Evidence only. |
| BCHUSD | 419168 | 3811 | 34% | $0.00 | -$10.00 | $62.58 | 0.97 | BLOWN — the $10 account hit zero after 3811 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCAD | 556252 | 10545 | 37% | $0.00 | -$10.00 | $68.34 | 0.98 | BLOWN — the $10 account hit zero after 10545 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| USDCHF | 567535 | 1236 | 30% | $0.00 | -$10.00 | $38.57 | 0.82 | BLOWN — the $10 account hit zero after 1236 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPDUSD | 450913 | 8552 | 44% | $0.00 | -$10.00 | $673.49 | 1.00 | BLOWN — the $10 account hit zero after 8552 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |
| XPTUSD | 450872 | 1543 | 42% | $0.00 | -$10.00 | $10.00 | 0.67 | BLOWN — the $10 account hit zero after 1543 trades; the min-lot floor outran the risk fraction (record more/cleaner tape) |

## Brain memory (what the brain now remembers)

| symbol | family | trades | win | total R | expectancy R | $ P/L |
|---|---|---|---|---|---|---|
| USDCHF | macd(12/26) | 23 | 100% | +44.5 | +1.933 | $+3.98 |
| USDJPY | macd(12/26) | 12 | 92% | +21.0 | +1.747 | $+376.39 |
| DSHUSD | ou-rev | 2 | 100% | +3.5 | +1.728 | $+0.13 |
| AUDUSD | bb-rev(20) | 10 | 80% | +16.2 | +1.624 | $+3.21 |
| XAGUSD | open-range(30) | 32 | 78% | +51.9 | +1.621 | $+3917.69 |
| EURUSD | ou-rev | 14 | 100% | +21.2 | +1.517 | $+1.90 |
| USDCAD | keltner(20x2) | 26 | 81% | +26.7 | +1.028 | $+10.24 |
| BNBUSD | open-range(30) | 4 | 100% | +3.4 | +0.841 | $+0.07 |
| XAGUSD | ou-rev | 68 | 60% | +56.1 | +0.825 | $+5387.81 |
| NZDUSD | z-rev(20) | 84 | 63% | +66.0 | +0.786 | $+3.14 |
| USDJPY | ou-rev | 78 | 86% | +39.2 | +0.502 | $+386.60 |
| XAUUSDmicro | donchian-pullback(21) | 85 | 55% | +40.5 | +0.477 | $+832.99 |
| EURUSD | bb-squeeze(20) | 137 | 58% | +60.1 | +0.439 | $+2.23 |
| GBPUSD | ema-cross(9/21) | 4129 | 54% | +1713.8 | +0.415 | $-405.67 |
| XAUUSDmicro | vwap-trend(30) | 682 | 58% | +280.0 | +0.411 | $+4653.32 |
| GBPUSD | kalman-trend | 322 | 51% | +129.2 | +0.401 | $+21.92 |
| XAUUSDmicro | rsi2-rev(10) | 208 | 53% | +72.2 | +0.347 | $+2653.26 |
| XAUUSD | donchian-pullback(21) | 67 | 57% | +22.7 | +0.339 | $+356.12 |
| USDCHF | ou-rev | 102 | 55% | +34.5 | +0.338 | $-21.28 |
| USDJPY | ema-cross(9/21) | 29687 | 48% | +9600.0 | +0.323 | $+181569.41 |
| NZDUSD | vwap-rev(30) | 662 | 51% | +210.3 | +0.318 | $+66.69 |
| XAUUSD | vwap-trend(30) | 533 | 52% | +154.0 | +0.289 | $+1556.65 |
| EURUSD | roc(10) | 110529 | 41% | +29978.9 | +0.271 | $+710037.44 |
| USDCAD | open-range(30) | 33 | 42% | +8.5 | +0.256 | $+11.84 |
| XAGUSD | rsi2-rev(10) | 634 | 48% | +153.8 | +0.243 | $+2723.18 |
| USDCHF | rsi-mom(14) | 12 | 50% | +2.9 | +0.241 | $-1.06 |
| XAUUSDmicro | ema-cross(9/21) | 22695 | 49% | +5435.0 | +0.239 | $+94976.10 |
| USDCAD | donchian(20) | 316 | 58% | +72.6 | +0.230 | $-0.07 |
| DSHUSD | donchian(20) | 1793 | 38% | +375.1 | +0.209 | $+4265.16 |
| XAUUSDmicro | bb-squeeze(20) | 149 | 48% | +30.9 | +0.207 | $+132.90 |
| XAUEUR | ema-cross(9/21) | 23367 | 47% | +4830.5 | +0.207 | $+70001.68 |
| GBPUSD | band-fade(50) | 1057 | 42% | +216.3 | +0.205 | $+4586.85 |
| AUDUSD | rsi-mom(14) | 14 | 93% | +2.8 | +0.199 | $+0.26 |
| XAUUSDmicro | ema-slope(21) | 6974 | 45% | +1369.5 | +0.196 | $+32698.24 |
| AUDUSD | vwap-rev(30) | 838 | 50% | +160.8 | +0.192 | $+35.22 |
| XAUUSD | ema-cross(9/21) | 44574 | 47% | +8522.8 | +0.191 | $+153514.40 |
| DSHUSD | vwap-trend(30) | 20615 | 39% | +3852.6 | +0.187 | $+89234.56 |
| USDJPY | bb-rev(20) | 223 | 46% | +40.0 | +0.179 | $+221.44 |
| XAUUSD | bb-rev(20) | 210 | 52% | +37.7 | +0.179 | $-381.78 |
| XAUUSDmicro | donchian(20) | 529 | 44% | +79.3 | +0.150 | $+3072.65 |

## Best per-family record per symbol

- AUDUSD: **bb-rev(20)** — 10 trades, 80% win, expectancy +1.624R, $+3.21
- BCHUSD: **bb-squeeze(20)** — 28 trades, 32% win, expectancy +0.016R, $-0.58
- BNBUSD: **open-range(30)** — 4 trades, 100% win, expectancy +0.841R, $+0.07
- BTCUSD: **ema-cross(9/21)** — 44627 trades, 39% win, expectancy +0.126R, $+89136.23
- DSHUSD: **ou-rev** — 2 trades, 100% win, expectancy +1.728R, $+0.13
- EURUSD: **ou-rev** — 14 trades, 100% win, expectancy +1.517R, $+1.90
- GBPUSD: **ema-cross(9/21)** — 4129 trades, 54% win, expectancy +0.415R, $-405.67
- NZDUSD: **z-rev(20)** — 84 trades, 63% win, expectancy +0.786R, $+3.14
- USDCAD: **keltner(20x2)** — 26 trades, 81% win, expectancy +1.028R, $+10.24
- USDCHF: **macd(12/26)** — 23 trades, 100% win, expectancy +1.933R, $+3.98
- USDJPY: **macd(12/26)** — 12 trades, 92% win, expectancy +1.747R, $+376.39
- XAGEUR: **open-range(30)** — 131438 trades, 44% win, expectancy +0.062R, $+117348.02
- XAGUSD: **open-range(30)** — 32 trades, 78% win, expectancy +1.621R, $+3917.69
- XAUEUR: **ema-cross(9/21)** — 23367 trades, 47% win, expectancy +0.207R, $+70001.68
- XAUUSD: **donchian-pullback(21)** — 67 trades, 57% win, expectancy +0.339R, $+356.12
- XAUUSDmicro: **donchian-pullback(21)** — 85 trades, 55% win, expectancy +0.477R, $+832.99
- XPDUSD: **open-range(30)** — 9472 trades, 43% win, expectancy -0.006R, $+28.44
- XPTUSD: **vwap-rev(30)** — 20075 trades, 45% win, expectancy -0.008R, $+592.01
