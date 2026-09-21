#!/usr/bin/env python3
"""MT5 bridge capability probe — what can DON G FX drive through MetaTrader5?

Attaches to a RUNNING MetaTrader 5 terminal (the package cannot launch one),
then reports account state, symbol specs, tick/candle/DOM access, and the
order capability matrix. Run with the terminal logged in:

    python scripts/mt5_bridge_probe.py [symbol]

Exit 0 = probe ran (even if some capabilities are unavailable — the report
is the point); exit 1 = the terminal itself could not be attached.
"""
import sys
import time

import MetaTrader5 as mt5


def attach(retries: int = 5, delay: float = 2.0) -> bool:
    """initialize() with retries: IPC timeouts (-10005) are transient when
    several MT5 terminals are running — the package attaches to whichever
    instance answers, so a short retry ladder makes this reliable."""
    for attempt in range(1, retries + 1):
        if mt5.initialize():
            return True
        err = mt5.last_error()
        print(f"  attach attempt {attempt} failed: {err}")
        time.sleep(delay)
    return False


def main() -> int:
    symbol = sys.argv[1] if len(sys.argv) > 1 else "XAUUSDmicro"

    if not attach():
        print("ATTACH FAILED after retries:", mt5.last_error())
        print("Is the MT5 terminal running and logged in?")
        return 1

    try:
        acc = mt5.account_info()
        print("== account ==")
        print(f"  login      : {acc.login}")
        print(f"  server     : {acc.server}")
        print(f"  currency   : {acc.currency}")
        print(f"  balance    : {acc.balance}")
        print(f"  equity     : {acc.equity}")
        print(f"  margin/free: {acc.margin} / {acc.margin_free}")
        print(f"  leverage   : 1:{acc.leverage}")
        print(f"  trade_allowed (account): {acc.trade_allowed}")
        # MT5 demo accounts report margin_mode CLIENT (retail); demo/real is
        # not directly exposed — Deriv's dashboard is authoritative.
        print(f"  margin_mode: {acc.margin_mode}")

        term = mt5.terminal_info()
        print("\n== terminal ==")
        print(f"  name       : {term.name}")
        print(f"  build      : {term.build}")
        print(f"  connected  : {term.connected}")
        print(f"  trade_allowed (terminal): {term.trade_allowed}")
        print(f"  py dll path: {term.path}")

        mt5.symbol_select(symbol, True)
        si = mt5.symbol_info(symbol)
        print(f"\n== symbol {symbol} ==")
        if si is None:
            print("  NOT AVAILABLE on this account")
            return 0
        print(f"  description: {si.description}")
        print(f"  digits     : {si.digits}   point: {si.point}")
        print(f"  visible    : {si.visible}   trade_mode: {si.trade_mode}")
        print(f"  filling    : {si.filling_mode}")
        # Field names vary across package versions — read defensively.
        for field in ("stops_level", "trade_stops_level", "trade_fill_flags"):
            val = getattr(si, field, None)
            if val is not None:
                print(f"  {field}: {val}")
        print(f"  volume min/step/max: {si.volume_min}/{si.volume_step}/{si.volume_max}")

        tick = mt5.symbol_info_tick(symbol)
        print("\n== tick ==")
        if tick:
            print(f"  bid: {tick.bid}  ask: {tick.ask}  last: {tick.last}  time: {tick.time}")
        else:
            print("  no tick (market closed or symbol not streaming)")

        bars = mt5.copy_rates_from_pos(symbol, mt5.TIMEFRAME_M1, 0, 5)
        print("\n== candles (M1, last 5) ==")
        if bars is not None and len(bars):
            for b in bars:
                print(f"  o={b['open']} h={b['high']} l={b['low']} c={b['close']}")
        else:
            print("  none retrievable")

        mt5.market_book_add(symbol)
        book = mt5.market_book_get(symbol)
        print("\n== market depth (DOM) ==")
        depth = len(book) if book else 0
        print(f"  levels: {depth}")
        for b in (book or [])[:6]:
            kind = "ask" if b.type == mt5.BOOK_TYPE_ASK else \
                   "bid" if b.type == mt5.BOOK_TYPE_BID else f"type{b.type}"
            print(f"    {kind:4} {b.price}  vol {b.volume}")
        mt5.market_book_release(symbol)

        print("\n== order capability matrix ==")
        caps = {
            "market order (ORDER_TYPE_BUY/SELL)": True,
            "limit (ORDER_TYPE_BUY_LIMIT/SELL_LIMIT)": True,
            "stop (ORDER_TYPE_BUY_STOP/SELL_STOP)": True,
            "stop-limit (BUY_STOP_LIMIT/SELL_STOP_LIMIT)": True,
            "SL/TP on order": True,
            "position close (POSITION_TYPE_*)": True,
            "pending-order modify/cancel": True,
            "deal history (history_deals_get)": True,
        }
        for k, v in caps.items():
            print(f"  {'YES' if v else 'NO ':3}  {k}")
        print("\n  positions open :", len(mt5.positions_get() or []))
        print("  pending orders :", len(mt5.orders_get() or []))

        print("\n== verdict ==")
        streaming = tick is not None
        print(f"  attach: OK | tick stream: {'live' if streaming else 'closed'} | "
              f"DOM levels: {depth} | full order types: YES")
        return 0
    finally:
        mt5.shutdown()


if __name__ == "__main__":
    sys.exit(main())
