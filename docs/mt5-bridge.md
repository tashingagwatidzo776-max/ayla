# MT5 bridge — driving MetaTrader 5 from DON G FX

DON G FX routes CFD/forex orders through a locally-running **MetaTrader 5
terminal** using the official `MetaTrader5` Python package
(`pip install MetaTrader5`). This document records the verified capability
matrix and the bridge architecture.

## Live probe results (2026-09-21, Deriv-Demo)

Probed with `python scripts/mt5_bridge_probe.py` against the running terminal
(`C:\Program Files\MetaTrader 5\terminal64.exe`):

| Item | Value |
|---|---|
| Attach | OK (needs 1–2 retries; `-10005 IPC timeout` is transient when several terminals run) |
| Account | 201587365 · Deriv-Demo server · USD |
| Balance / equity | 2 610.55 / 2 610.55, no open positions |
| Leverage | 1:1000 |
| Trading allowed | account ✅ terminal ✅ connected ✅ (build 6182) |
| Symbol (XAUUSDmicro) | digits 2, point 0.01, filling mode 1 (FOK), stops level 20 pts, volume 0.1–100 step 0.1 |
| Ticks | **live** (bid/ask stream; `last` unused on CFD symbols) |
| Candles | `copy_rates_from_pos` M1 OHLC works |
| Market depth | **0 levels — Deriv does not stream DOM** → the terminal's price ladder is synthetic (built from bid/ask) |
| Order types | market, limit, stop, **stop-limit**, SL/TP, position close, pending modify/cancel, deal history — **all YES** |

## Architecture

```
DON G FX (C#, Terminal tab)
   │  HTTP JSON, loopback only
   ▼
bridge/mt5_sidecar.py  (127.0.0.1:53190)
   │  MetaTrader5 package (IPC into terminal64.exe)
   ▼
MetaTrader 5 terminal → Deriv-Demo / Deriv real MT5 server
```

- The sidecar is **optional**: when it is not running, the terminal shows a
  synthetic DOM and a "start bridge" hint (bridge-down state; nothing places).
- The sidecar binds strictly to `127.0.0.1` and refuses any other bind
  address — no external exposure, ever.
- MT5 demo/real is resolved from `account_info()` (login prefix / server
  name) and fed into the same real-money gate as every other trade path.

## Sidecar API

| Endpoint | Purpose |
|---|---|
| `GET /health` | liveness + attached account snapshot |
| `GET /account` | balance, equity, margin, currency, leverage, login, server |
| `GET /ticks/{symbol}` | last bid/ask/time |
| `GET /book/{symbol}` | DOM levels (empty on Deriv → client builds synthetic) |
| `GET /candles/{symbol}?tf=M1&n=120` | OHLC series |
| `POST /order` | `{action: buy\|sell, type: market\|limit\|stop\|stoplimit, symbol, lots, price?, sl?, tp?}` → deal/retcode |
| `GET /positions` | open positions with live P/L |
| `POST /close/{ticket}` | close a position |
| `GET /deals?days=7` | recent deal history |

Error model: `{"error": "..."}` with a 4xx/5xx status; `retcode` from
`order_send` is passed through (e.g. `10009 TRADE_RETCODE_DONE`).

## Security model

- Loopback bind only; the C# client refuses any non-loopback base URL.
- No credentials ever transit the sidecar (the terminal is already logged
  in); the sidecar never writes files and never shells out.
- Every order from DON G FX passes the same rails:
  kill switch, real-money gate, `Mt5MaxLots` cap — and is journaled.
