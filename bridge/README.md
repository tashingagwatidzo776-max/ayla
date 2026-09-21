# MT5 bridge

Connects DON G FX to a locally-running **MetaTrader 5** terminal so the
Terminal tab can read live MT5 data and place CFD/forex orders (market,
limit, stop, stop-limit, with SL/TP) on the logged-in account.

## Quick start

1. Start MetaTrader 5 and log in (e.g. the Deriv-Demo account).
2. Run the sidecar:

       python bridge/mt5_sidecar.py          # port 53190

   It attaches to the terminal (retries on transient IPC timeouts) and
   serves `http://127.0.0.1:53190` — loopback only, never exposed.
3. In DON G FX's Terminal tab, the MT5 card lights up automatically.

## Tests

    python bridge/test_mt5_sidecar.py

## Security

- Binds strictly to 127.0.0.1; any other bind address is refused.
- No credentials pass through the sidecar (the terminal is already logged
  in); it reads no files and runs no shells.
- Every order from the app still passes the in-app rails: kill switch,
  real-money gate, and the `Mt5MaxLots` cap.

See `docs/mt5-bridge.md` for the full capability matrix and API.
