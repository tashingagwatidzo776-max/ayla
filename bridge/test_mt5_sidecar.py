#!/usr/bin/env python3
"""Unit tests for bridge/mt5_sidecar.py.

The order path moves real money, so its validation must be exact: bad
action/type pairs, out-of-range or off-step lots, missing prices for
pending types, unknown symbols and closed markets are all refused BEFORE
anything reaches the terminal. The handler tests run against a fake
facade — no MetaTrader5 package, no sockets; one loopback round-trip
proves the real server end-to-end.

Run:  python bridge/test_mt5_sidecar.py   (exit 0 = all pass)
"""
from __future__ import annotations

import json
import sys
import threading
import urllib.request
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).parent))

import mt5_sidecar as sidecar  # noqa: E402


class FakeMT5:
    """Facade shaped like the MetaTrader5 module (subset we use)."""

    BOOK_TYPE_ASK = 1
    BOOK_TYPE_BID = 2
    POSITION_TYPE_BUY = 0
    ORDER_TYPE_BUY = 0
    ORDER_TYPE_SELL = 1
    TRADE_ACTION_DEAL = 1
    TRADE_ACTION_PENDING = 5
    ORDER_FILLING_FOK = 0
    DEAL_TYPE_BUY = 0

    def __init__(self) -> None:
        self.sent: list[dict] = []
        self._next_ticket = 900000

    # terminal reads ------------------------------------------------
    def account_info(self):
        return SimpleNamespace(
            login=201587365, server="Deriv-Demo", currency="USD",
            balance=2610.55, equity=2610.55, margin=0.0, margin_free=2610.55,
            leverage=1000, trade_mode=0)

    def terminal_info(self):
        return SimpleNamespace(connected=True, name="MetaTrader 5 Terminal")

    def symbol_select(self, symbol, enable=True):
        return symbol in ("XAUUSDmicro", "EURUSD")

    def symbol_info(self, symbol):
        if symbol not in ("XAUUSDmicro", "EURUSD"):
            return None
        return SimpleNamespace(
            volume_min=0.1, volume_step=0.1, volume_max=100.0, filling_mode=1,
            trade_contract_size=1.0 if symbol == "XAUUSDmicro" else 100_000.0)

    def symbol_info_tick(self, symbol):
        if symbol == "CLOSED":
            return None
        return SimpleNamespace(bid=4347.61, ask=4347.88, time=1790003496)

    def symbols_get(self):
        return [
            SimpleNamespace(name="XAUUSDmicro", description="Gold micro",
                            spread=27, digits=2, trade_mode=4, visible=True,
                            volume_min=0.1, volume_step=0.1, volume_max=100.0,
                            trade_contract_size=1.0),
            SimpleNamespace(name="EURUSD", description="Euro vs US Dollar",
                            spread=10, digits=5, trade_mode=4, visible=True,
                            volume_min=0.01, volume_step=0.01, volume_max=100.0,
                            trade_contract_size=100_000.0),
            SimpleNamespace(name="HIDDEN", description="not shown",
                            spread=0, digits=2, trade_mode=0, visible=False,
                            volume_min=0.1, volume_step=0.1, volume_max=100.0,
                            trade_contract_size=100_000.0),
        ]

    def market_book_add(self, symbol):
        return False  # Deriv streams no depth

    def market_book_get(self, symbol):
        return []

    def market_book_release(self, symbol):
        return True

    def copy_rates_from_pos(self, symbol, tf, start, count):
        import numpy as np
        return np.array(
            [(1790003400 + 60 * i, 100.0 + i, 101.0 + i, 99.0 + i, 100.5 + i, 10)
             for i in range(count)],
            dtype=[("time", "<i8"), ("open", "<f8"), ("high", "<f8"),
                   ("low", "<f8"), ("close", "<f8"), ("tick_volume", "<i8")])

    def positions_get(self, ticket=None):
        rows = [
            SimpleNamespace(ticket=111, symbol="XAUUSDmicro", type=0, volume=0.5,
                            price_open=4300.0, price_current=4347.61, profit=23.8,
                            swap=0.0, sl=0.0, tp=0.0, time=1790000000),
            SimpleNamespace(ticket=222, symbol="EURUSD", type=1, volume=0.1,
                            price_open=1.0850, price_current=1.0840, profit=1.0,
                            swap=-0.1, sl=0.0, tp=0.0, time=1790000000),
        ]
        if ticket is not None:
            return tuple(r for r in rows if r.ticket == ticket)
        return rows

    def history_deals_get(self, frm, to):
        return [
            SimpleNamespace(ticket=551, order=551, symbol="XAUUSDmicro", type=0,
                            volume=0.5, price=4310.0, profit=-12.5,
                            commission=-0.5, swap=0.0, time=1790001000, entry=1),
        ]

    # trading -------------------------------------------------------
    def order_send(self, request):
        self.sent.append(request)
        if request["symbol"] == "CLOSED":
            return SimpleNamespace(retcode=10018, deal=0, order=0, price=0,
                                   volume=0, comment="market closed")
        self._next_ticket += 1
        return SimpleNamespace(retcode=10009, deal=self._next_ticket,
                               order=self._next_ticket, price=4347.88,
                               volume=request["volume"], comment="done")

    def last_error(self):
        return (0, "ok")


def make_handlers() -> sidecar.BridgeHandlers:
    return sidecar.BridgeHandlers(FakeMT5())


# ── validation (the money paths) ──────────────────────────────────────

def test_symbols_lists_visible_with_quotes():
    """Market Watch's source: only visible symbols, each with live quote
    fields; hidden catalog entries never leak into the list."""
    data = make_handlers().symbols()
    rows = data["symbols"]
    names = [r["symbol"] for r in rows]
    assert names == ["XAUUSDmicro", "EURUSD"], names
    gold = rows[0]
    assert gold["bid"] == 4347.61 and gold["ask"] == 4347.88
    assert gold["spread_points"] == 27 and gold["digits"] == 2
    assert gold["trade_mode"] == 4


def test_symbols_tolerates_missing_tick():
    """A symbol with no tick yet (session closed) still lists with null
    quote fields — the UI shows it greyed, not missing."""
    h = make_handlers()
    h._m.symbol_info_tick = lambda s: None if s == "EURUSD" else SimpleNamespace(bid=1.0, ask=1.1, time=1)
    rows = h.symbols()["symbols"]
    eurusd = next(r for r in rows if r["symbol"] == "EURUSD")
    assert eurusd["bid"] is None and eurusd["ask"] is None


def test_order_rejects_bad_action_type():
    h = make_handlers()
    for action, kind in [("buy", "weird"), ("sideways", "market"), ("", "")]:
        try:
            h.order({"action": action, "type": kind, "symbol": "XAUUSDmicro", "lots": 0.1})
            raise AssertionError(f"{action}/{kind} should be refused")
        except sidecar.OrderError:
            pass


def test_order_rejects_bad_lots():
    h = make_handlers()
    cases = [
        {"lots": 0.05},   # below volume_min
        {"lots": 999},    # above volume_max
        {"lots": 0.15},   # off-step
        {"lots": "abc"},  # not a number
        {},               # missing
    ]
    for body in cases:
        try:
            h.order({"action": "buy", "type": "market",
                     "symbol": "XAUUSDmicro", **body})
            raise AssertionError(f"lots {body} should be refused")
        except sidecar.OrderError:
            pass


def test_order_rejects_missing_price_for_pending():
    h = make_handlers()
    for kind in ("limit", "stop"):
        try:
            h.order({"action": "buy", "type": kind, "symbol": "XAUUSDmicro", "lots": 0.1})
            raise AssertionError(f"{kind} without price should be refused")
        except sidecar.OrderError:
            pass
    try:
        h.order({"action": "buy", "type": "stoplimit", "symbol": "XAUUSDmicro",
                 "lots": 0.1, "price": 4400.0})  # stopprice missing
        raise AssertionError("stoplimit without stopprice should be refused")
    except sidecar.OrderError:
        pass


def test_order_rejects_unknown_symbol_and_closed_market():
    h = make_handlers()
    try:
        h.order({"action": "buy", "type": "market", "symbol": "NOPE", "lots": 0.1})
        raise AssertionError("unknown symbol should be refused")
    except sidecar.OrderError:
        pass
    try:
        h.order({"action": "buy", "type": "market", "symbol": "CLOSED", "lots": 0.1})
        raise AssertionError("closed market should be refused")
    except sidecar.OrderError:
        pass


def test_order_happy_path_market():
    fake = FakeMT5()
    h = sidecar.BridgeHandlers(fake)
    out = h.order({"action": "buy", "type": "market", "symbol": "XAUUSDmicro",
                   "lots": 0.5, "sl": 4300.0, "tp": 4500.0})
    assert out["ok"] is True and out["retcode"] == 10009, out
    req = fake.sent[-1]
    assert req["action"] == fake.TRADE_ACTION_DEAL
    assert req["volume"] == 0.5
    assert req["sl"] == 4300.0 and req["tp"] == 4500.0
    assert req["price"] == 4347.88  # ask for buys


def test_order_happy_path_stoplimit_sends_both_prices():
    fake = FakeMT5()
    h = sidecar.BridgeHandlers(fake)
    out = h.order({"action": "sell", "type": "stoplimit", "symbol": "XAUUSDmicro",
                   "lots": 0.1, "price": 4300.0, "stopprice": 4340.0})
    assert out["ok"], out
    req = fake.sent[-1]
    assert req["action"] == fake.TRADE_ACTION_PENDING
    assert req["price"] == 4300.0 and req["stoplimit"] == 4340.0


def test_close_requires_known_position():
    h = make_handlers()
    try:
        h.close(999999)
        raise AssertionError("unknown ticket should be refused")
    except sidecar.OrderError:
        pass


def test_reads_shape():
    h = make_handlers()
    acc = h.account()
    assert acc["login"] == 201587365 and acc["server"] == "Deriv-Demo"
    # The venue's demo/real verdict rides on /account; the C# real-money
    # gate maps 0→virtual, 2→real and refuses on anything else.
    assert acc["trade_mode"] == 0
    assert h.health()["ok"] is True
    tick = h.ticks("XAUUSDmicro")
    assert tick["bid"] == 4347.61 and tick["ask"] == 4347.88
    assert h.book("XAUUSDmicro")["levels"] == []  # Deriv: no DOM
    candles = h.candles("XAUUSDmicro", "M5", 3)
    assert len(candles) == 3 and candles[0]["open"] == 100.0
    try:
        h.candles("XAUUSDmicro", "M7", 5)
        raise AssertionError("bad timeframe should be refused")
    except sidecar.OrderError:
        pass
    positions = h.positions()
    assert [p["ticket"] for p in positions] == [111, 222]
    assert positions[0]["side"] == "buy"
    deals = h.deals(7)
    assert len(deals) == 1 and deals[0]["profit"] == -12.5


# ── the real loopback server, end to end ──────────────────────────────

def test_loopback_round_trip():
    server = sidecar.SidecarServer(make_handlers(), port=0)  # ephemeral port
    th = threading.Thread(target=server.serve_forever, daemon=True)
    th.start()
    try:
        base = f"http://127.0.0.1:{server.port}"
        with urllib.request.urlopen(f"{base}/account", timeout=5) as r:
            acc = json.loads(r.read())
        assert acc["login"] == 201587365

        # /symbols carries the venue's lot geometry (sizing ground truth)
        with urllib.request.urlopen(f"{base}/symbols", timeout=5) as r:
            syms = {s["symbol"]: s for s in json.loads(r.read())["symbols"]}
        gold = syms["XAUUSDmicro"]
        assert gold["volume_min"] == 0.1 and gold["volume_step"] == 0.1
        assert gold["volume_max"] == 100.0 and gold["contract_size"] == 1.0
        assert syms["EURUSD"]["contract_size"] == 100_000.0

        req = urllib.request.Request(
            f"{base}/order",
            data=json.dumps({"action": "buy", "type": "market",
                             "symbol": "XAUUSDmicro", "lots": 0.1}).encode(),
            headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=5) as r:
            out = json.loads(r.read())
        assert out["ok"] is True and out["retcode"] == 10009

        # invalid orders surface as 422 with the reason
        try:
            req = urllib.request.Request(
                f"{base}/order",
                data=json.dumps({"action": "buy", "type": "market",
                                 "symbol": "NOPE", "lots": 0.1}).encode(),
                headers={"Content-Type": "application/json"}, method="POST")
            urllib.request.urlopen(req, timeout=5)
            raise AssertionError("unknown symbol should 422")
        except urllib.error.HTTPError as e:
            assert e.code == 422
    finally:
        server.httpd.shutdown()


def test_parse_args_defaults():
    from mt5_sidecar import parse_args
    assert parse_args([]) == (53190, None)


def test_parse_args_port_positional():
    from mt5_sidecar import parse_args
    assert parse_args(["6000"]) == (6000, None)


def test_parse_args_terminal_flag_alone():
    from mt5_sidecar import parse_args
    port, path = parse_args(["--terminal", "C:/Program Files/MetaTrader 5 Terminal/terminal64.exe"])
    assert port == 53190
    assert path.endswith("terminal64.exe")


def test_parse_args_port_and_terminal():
    from mt5_sidecar import parse_args
    assert parse_args(["6111", "--terminal", "C:/mt5/terminal64.exe"]) == (6111, "C:/mt5/terminal64.exe")


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except Exception as e:  # noqa: BLE001
            failed += 1
            print(f"FAIL {t.__name__}: {type(e).__name__}: {e}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
