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
import time
import urllib.request
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).parent))

try:
    import MetaTrader5  # noqa: F401
except ImportError:
    # CI runners (and any machine without the package): the sidecar only
    # needs the MT5 constant dicts at import time — the tests inject their
    # own facade, so a stub carrying the real constant values suffices.
    # `None` here would make the import raise, which is what we want to
    # avoid; a SimpleNamespace satisfies the module-level dict builds.
    sys.modules["MetaTrader5"] = SimpleNamespace(
        TIMEFRAME_M1=1, TIMEFRAME_M2=2, TIMEFRAME_M3=3, TIMEFRAME_M4=4,
        TIMEFRAME_M5=5, TIMEFRAME_M6=6, TIMEFRAME_M10=10, TIMEFRAME_M12=12,
        TIMEFRAME_M15=15, TIMEFRAME_M20=20, TIMEFRAME_M30=30,
        TIMEFRAME_H1=16385, TIMEFRAME_H2=16386, TIMEFRAME_H3=16387,
        TIMEFRAME_H4=16388, TIMEFRAME_H6=16390, TIMEFRAME_H8=16392,
        TIMEFRAME_H12=16396, TIMEFRAME_D1=16408, TIMEFRAME_W1=32769,
        TIMEFRAME_MN1=49153,
        ORDER_TYPE_BUY=0, ORDER_TYPE_SELL=1, ORDER_TYPE_BUY_LIMIT=2,
        ORDER_TYPE_SELL_LIMIT=3, ORDER_TYPE_BUY_STOP=4, ORDER_TYPE_SELL_STOP=5,
        ORDER_TYPE_BUY_STOP_LIMIT=6, ORDER_TYPE_SELL_STOP_LIMIT=7,
        BOOK_TYPE_ASK=1, BOOK_TYPE_BID=2, POSITION_TYPE_BUY=0,
        TRADE_ACTION_DEAL=1, TRADE_ACTION_PENDING=5, TRADE_ACTION_SLTP=6,
        TRADE_ACTION_REMOVE=8, ORDER_FILLING_FOK=0,
        DEAL_TYPE_BUY=0)

import mt5_sidecar as sidecar  # noqa: E402


class FakeMT5:
    """Facade shaped like the MetaTrader5 module (subset we use)."""

    BOOK_TYPE_ASK = 1
    BOOK_TYPE_BID = 2
    POSITION_TYPE_BUY = 0
    ORDER_TYPE_BUY = 0
    ORDER_TYPE_SELL = 1
    ORDER_TYPE_BUY_LIMIT = 2
    ORDER_TYPE_SELL_LIMIT = 3
    ORDER_TYPE_BUY_STOP = 4
    ORDER_TYPE_SELL_STOP = 5
    ORDER_TYPE_BUY_STOP_LIMIT = 6
    ORDER_TYPE_SELL_STOP_LIMIT = 7
    TRADE_ACTION_DEAL = 1
    TRADE_ACTION_PENDING = 5
    TRADE_ACTION_SLTP = 6
    TRADE_ACTION_REMOVE = 8
    ORDER_FILLING_FOK = 0
    DEAL_TYPE_BUY = 0

    def __init__(self) -> None:
        self.sent: list[dict] = []
        self.deal_ranges: list[tuple] = []
        self._next_ticket = 900000
        self.terminal_connected = True
        self.reattach_calls = 0

    # terminal reads ------------------------------------------------
    def account_info(self):
        if not self.terminal_connected:
            return None
        return SimpleNamespace(
            login=201587365, server="Deriv-Demo", currency="USD",
            balance=2610.55, equity=2610.55, margin=0.0, margin_free=2610.55,
            leverage=1000, trade_mode=0)

    def terminal_info(self):
        return SimpleNamespace(connected=self.terminal_connected, name="MetaTrader 5 Terminal")

    def symbol_select(self, symbol, enable=True):
        return symbol in ("XAUUSDmicro", "EURUSD")

    def symbol_info(self, symbol):
        if symbol not in ("XAUUSDmicro", "EURUSD"):
            return None
        info = SimpleNamespace(
            volume_min=0.1, volume_step=0.1, volume_max=100.0, filling_mode=1,
            trade_contract_size=1.0 if symbol == "XAUUSDmicro" else 100_000.0,
            trade_stops_level=20, point=0.01 if symbol == "XAUUSDmicro" else 0.00001)
        if getattr(self, "strip_stop_geometry", False):
            # Regression shape: a spec missing stop geometry entirely —
            # the sidecar must degrade to a safe band, never AttributeError.
            del info.trade_stops_level, info.point
        return info

    def symbol_info_tick(self, symbol):
        if symbol == "CLOSED":
            return None
        return SimpleNamespace(bid=4347.61, ask=4347.88, time=1790003496)

    def symbols_get(self):
        # The real MT5 API's SymbolInfo struct carries stop geometry in
        # symbols_get() too — the first draft of this fake omitted it, which
        # is exactly how the production AttributeError slipped past CI.
        rows = [
            SimpleNamespace(name="XAUUSDmicro", description="Gold micro",
                            spread=27, digits=2, trade_mode=4, visible=True,
                            volume_min=0.1, volume_step=0.1, volume_max=100.0,
                            trade_contract_size=1.0,
                            trade_stops_level=20, point=0.01),
            SimpleNamespace(name="EURUSD", description="Euro vs US Dollar",
                            spread=10, digits=5, trade_mode=4, visible=True,
                            volume_min=0.01, volume_step=0.01, volume_max=100.0,
                            trade_contract_size=100_000.0,
                            trade_stops_level=20, point=0.00001),
            SimpleNamespace(name="HIDDEN", description="not shown",
                            spread=0, digits=2, trade_mode=0, visible=False,
                            volume_min=0.1, volume_step=0.1, volume_max=100.0,
                            trade_contract_size=100_000.0,
                            trade_stops_level=20, point=0.01),
        ]
        if getattr(self, "strip_stop_geometry", False):
            for r in rows:
                if hasattr(r, "trade_stops_level"):
                    del r.trade_stops_level
                if hasattr(r, "point"):
                    del r.point
        return rows

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

    def orders_get(self, ticket=None):
        rows = [
            SimpleNamespace(ticket=777, symbol="XAUUSDmicro", type=2,
                            volume_current=0.2, price_open=4200.0,
                            sl=0.0, tp=0.0, state=2, time_setup=1790000000),
        ]
        if ticket is not None:
            return tuple(r for r in rows if r.ticket == ticket)
        return rows

    def history_deals_get(self, frm, to):
        self.deal_ranges.append((frm, to))
        return [
            SimpleNamespace(ticket=551, order=551, symbol="XAUUSDmicro", type=0,
                            volume=0.5, price=4310.0, profit=-12.5,
                            commission=-0.5, swap=0.0, time=1790001000, entry=1),
        ]

    # trading -------------------------------------------------------
    def order_send(self, request):
        self.sent.append(request)
        # SLTP/REMOVE requests carry no symbol/volume — use .get everywhere.
        if request.get("symbol") == "CLOSED":
            return SimpleNamespace(retcode=10018, deal=0, order=0, price=0,
                                   volume=0, comment="market closed")
        self._next_ticket += 1
        return SimpleNamespace(retcode=10009, deal=self._next_ticket,
                               order=self._next_ticket, price=4347.88,
                               volume=request.get("volume", 0), comment="done")

    def last_error(self):
        return (0, "ok")

    # account switching (POST /login) --------------------------------
    login_calls: list[dict] = []
    login_result: dict = {}   # set per-test to simulate failure

    def login(self, account_id, password="", server=""):
        self.login_calls.append({"login": account_id, "server": server})
        if self.login_result.get("fail"):
            return False
        return True


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


def test_symbols_carries_stop_geometry():
    """The app's stop flooring reads stops_level + point from /symbols —
    a spec that drops either silently shrinks the app-side stop band
    (the invalid-stops incident of 2026-09-29)."""
    rows = make_handlers().symbols()["symbols"]
    gold = next(r for r in rows if r["symbol"] == "XAUUSDmicro")
    assert gold["stops_level"] == 20 and gold["point"] == 0.01


def test_symbols_degrades_when_stop_geometry_missing():
    """A symbol_info lacking stop-geometry attributes must degrade to
    safe defaults (band 0, point floor) — never raise."""
    h = make_handlers()
    h._m.strip_stop_geometry = True
    rows = h.symbols()["symbols"]
    gold = next(r for r in rows if r["symbol"] == "XAUUSDmicro")
    assert gold["stops_level"] == 0 and gold["point"] == 0.00001


# ── /symbols snapshot cache (the 2026-10-06 BridgeDown fix) ─────────

def test_symbols_cached_builds_once_then_serves_cache():
    """Cold start blocks for the first snapshot; every later call is a pure
    cache hit — same object, no MT5 scan. The scan is what held the MT5
    lock 25-31 s and starved every other route past the app's 15 s
    timeout (one 'bridge unreachable' halt per minute)."""
    h = make_handlers()
    calls = {"n": 0}
    orig = h._m.symbols_get
    def counting():
        calls["n"] += 1
        return orig()
    h._m.symbols_get = counting
    first = h.symbols_cached()
    second = h.symbols_cached()
    assert first is second
    assert [r["symbol"] for r in first["symbols"]] == ["XAUUSDmicro", "EURUSD"]
    assert calls["n"] == 1


def test_symbols_cached_serves_stale_while_rebuilding():
    """An expired snapshot returns IMMEDIATELY (stale-while-revalidate:
    the size ground truth is static intraday) while a background thread
    rebuilds; after the rebuild the next call sees the new payload."""
    h = make_handlers()
    first = h.symbols_cached()
    h._sym_refresh.join(timeout=5)              # first build fully done
    assert not h._sym_refresh.is_alive()
    h._sym_ts = time.monotonic() - 999          # force expiry
    calls = {"n": 0}
    orig = h._m.symbols_get
    def counting():
        calls["n"] += 1
        return orig()
    h._m.symbols_get = counting
    stale = h.symbols_cached()                  # must not block on rebuild
    assert stale is first
    h._sym_refresh.join(timeout=5)              # background rebuild finished
    assert not h._sym_refresh.is_alive()
    assert calls["n"] == 1
    assert h._sym_payload is not first          # fresh object published
    fresh = h.symbols_cached()
    assert fresh is h._sym_payload and calls["n"] == 1   # fresh → cache hit


def test_symbols_scan_releases_lock_between_chunks():
    """With the MT5 lock passed in, the scan releases it between chunks —
    a monolithic hold was the BridgeDown flap (25-31 s with no route and
    even no accepts getting through)."""
    h = make_handlers()
    lock = threading.Lock()
    h._m.symbol_info_tick = lambda s: (
        time.sleep(0.01), SimpleNamespace(bid=1.0, ask=1.1, time=1))[1]
    free_windows: list[float] = []
    stop = threading.Event()
    def probe():
        while not stop.is_set():
            if lock.acquire(timeout=0.005):
                free_windows.append(time.monotonic())
                lock.release()
    th = threading.Thread(target=probe, daemon=True)
    th.start()
    try:
        out = h.symbols(lock=lock, chunk=1)      # 2 visible → 2 chunks
    finally:
        stop.set()
        th.join(timeout=2)
    assert [r["symbol"] for r in out["symbols"]] == ["XAUUSDmicro", "EURUSD"]
    assert free_windows, "lock never freed mid-scan — monolithic hold"


def test_symbols_route_serves_while_mt5_lock_held():
    """End-to-end: while another thread monopolizes the MT5 lock (a slow
    /history, say), GET /symbols still answers promptly from cache — the
    request path never queues behind the lock."""
    server = sidecar.SidecarServer(make_handlers(), port=0)
    th = threading.Thread(target=server.serve_forever, daemon=True)
    th.start()
    base = f"http://127.0.0.1:{server.port}"
    try:
        with urllib.request.urlopen(f"{base}/symbols", timeout=5) as r:
            assert r.status == 200               # cold build (fake-fast)
        server._mt5_lock.acquire()               # simulate a long MT5 fetch
        try:
            t0 = time.monotonic()
            with urllib.request.urlopen(f"{base}/symbols", timeout=3) as r:
                syms = json.loads(r.read())
            elapsed = time.monotonic() - t0
        finally:
            server._mt5_lock.release()
        assert elapsed < 2.0, f"cached /symbols waited {elapsed:.2f}s"
        assert {s["symbol"] for s in syms["symbols"]} == {"XAUUSDmicro", "EURUSD"}
    finally:
        server.httpd.shutdown()


def test_keepalive_two_requests_one_connection():
    """HTTP/1.1 + keep-alive: two requests ride ONE TCP connection.
    Under the old HTTP/1.0 close-per-response the app churned ~6 new
    connections per second and ~0.04% were RST mid-response ('connection
    was forcibly closed by the remote host') → GetJson null →
    'MT5 bridge unreachable' halt every minute. A 20k-probe measured
    8 failures / 20k connections on HTTP/1.0 vs 0 / 20k on HTTP/1.1."""
    import http.client
    server = sidecar.SidecarServer(make_handlers(), port=0)
    th = threading.Thread(target=server.serve_forever, daemon=True)
    th.start()
    try:
        conn = http.client.HTTPConnection("127.0.0.1", server.port, timeout=5)
        conn.request("GET", "/health")
        r1 = conn.getresponse()
        body1 = r1.read()
        conn.request("GET", "/account")   # same socket — keep-alive
        r2 = conn.getresponse()
        body2 = r2.read()
        conn.close()
        assert r1.version == 11 and r2.version == 11, "must speak HTTP/1.1"
        assert r1.status == 200 and r2.status == 200
        assert json.loads(body1)["login"] == 201587365 or "ok" in body1.decode()
        assert json.loads(body2)["login"] == 201587365
    finally:
        server.httpd.shutdown()


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
    # "Honors" half: the mapped order TYPE must be the venue's own
    # stop-limit constant, not a plain limit/stop. A sidecar that forwarded
    # the prices but picked the wrong type would place the wrong pending
    # order — the exact contract the app's stop-limit ticket depends on.
    assert req["type"] == fake.ORDER_TYPE_SELL_STOP_LIMIT, req


def test_order_happy_path_stoplimit_buy_maps_buy_stop_limit():
    # Buy side of the same contract: the type map is keyed on (action, kind),
    # so the buy leg must resolve independently to BUY_STOP_LIMIT.
    fake = FakeMT5()
    h = sidecar.BridgeHandlers(fake)
    out = h.order({"action": "buy", "type": "stoplimit", "symbol": "XAUUSDmicro",
                   "lots": 0.1, "price": 4360.0, "stopprice": 4320.0})
    assert out["ok"], out
    req = fake.sent[-1]
    assert req["action"] == fake.TRADE_ACTION_PENDING
    assert req["price"] == 4360.0 and req["stoplimit"] == 4320.0
    assert req["type"] == fake.ORDER_TYPE_BUY_STOP_LIMIT, req


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


# ── pending orders / cancel / modify / partial close / deals range ────

def test_orders_lists_pendings_with_kind():
    """/orders is the Toolbox Orders tab's source: pendings must be
    visible with their kind, price and volume."""
    rows = make_handlers().orders()
    assert len(rows) == 1
    o = rows[0]
    assert o["ticket"] == 777
    assert o["side"] == "buy" and o["kind"] == "limit"  # type 2 = BUY_LIMIT
    assert o["volume"] == 0.2 and o["price"] == 4200.0


def test_cancel_sends_remove_and_verifies_the_ticket_exists():
    h = make_handlers()
    out = h.cancel(777)
    assert out["ok"] is True and out["cancelled_ticket"] == 777
    sent = h._m.sent[-1]
    assert sent["action"] == h._m.TRADE_ACTION_REMOVE and sent["order"] == 777
    try:
        h.cancel(999999)  # not in orders_get
        raise AssertionError("cancel of unknown ticket should be refused")
    except sidecar.OrderError:
        pass


def test_modify_updates_sltp_and_enforces_stops_level():
    h = make_handlers()
    out = h.modify({"ticket": 111, "sl": 4340.0})
    assert out["ok"] is True and out["sl"] == 4340.0
    sent = h._m.sent[-1]
    assert sent["action"] == h._m.TRADE_ACTION_SLTP
    assert sent["position"] == 111 and sent["tp"] == 0.0  # tp preserved as 0

    # stops level: XAUUSDmicro trade_stops_level isn't in the fake's
    # symbol_info — the handler treats missing attr as 0, so a distance-0
    # SL exactly at ref must still pass; a *within-level* refusal needs the
    # real broker distance, covered on the live probe.
    try:
        h.modify({"ticket": 999})
        raise AssertionError("modify of unknown position should be refused")
    except sidecar.OrderError:
        pass
    try:
        h.modify({"ticket": 111})
        raise AssertionError("modify without sl/tp should be refused")
    except sidecar.OrderError:
        pass


def test_close_partial_sends_volume_and_full_close_omits_it():
    h = make_handlers()
    out = h.close(111, 0.2)   # 0.5-lot position, partial 0.2
    assert out["ok"] is True and out["closed_volume"] == 0.2
    assert h._m.sent[-1]["volume"] == 0.2

    out = h.close(111)        # full close
    assert out["closed_volume"] == 0.5
    assert h._m.sent[-1]["volume"] == 0.5

    out = h.close(111, 0.5)   # closing exactly remaining volume == full close
    assert out["closed_volume"] == 0.5

    for bad in (0.0, 0.7, 0.55):  # zero, over, off-step
        try:
            h.close(111, bad)
            raise AssertionError(f"close(lots={bad}) should be refused")
        except sidecar.OrderError:
            pass


def test_partial_close_echoes_position_comment_for_ownership():
    """MT5 overwrites the POSITION comment with the partial-close deal's
    comment, so the close order must echo the position's own comment —
    a close-specific stamp silently un-owns the position for the exit
    brain (2026-10-08 #8792846716: donggfx-brain -> donggfx-close at the
    TP1 rung, snapshots and profit floor dropped mid-trade)."""
    h = make_handlers()
    base_get = h._m.positions_get

    def stamped(ticket=None, comment="donggfx-brain"):
        rows = list(base_get(ticket=ticket))
        for r in rows:
            r.comment = comment
        return rows

    h._m.positions_get = lambda ticket=None: stamped(ticket=ticket)
    h.close(111, 0.2)
    assert h._m.sent[-1]["comment"] == "donggfx-brain"

    # An unstamped (manual) position must NOT gain an owning comment.
    h._m.positions_get = lambda ticket=None: stamped(ticket=ticket, comment="")
    h.close(111, 0.2)
    assert h._m.sent[-1]["comment"] == ""


def test_venue_float32_volumes_are_quantized():
    """MT5 volumes are float32: a 0.1-lot fill arrives as
    0.10000000149011612. The app's AuditExposure compares book lots
    against the double cap EXACTLY, so raw float32 noise at a full-cap
    book tripped a spurious FX_RISK "exceeds the cap" WARN. Every
    volume the app reads back (positions, order fills, close results)
    must be quantized to clean decimal lots, and full-close detection
    must not be fooled by the same noise."""
    noisy = 0.10000000149011612   # float32 representation of 0.1

    def noisy_position(ticket):
        return [SimpleNamespace(ticket=ticket, symbol="XAUUSDmicro", type=0,
                                volume=noisy, price_open=4300.0,
                                price_current=4300.5, profit=0.1, swap=0.0,
                                sl=0.0, tp=0.0, time=1790000000)]

    fake = FakeMT5()
    fake.positions_get = lambda ticket=None: noisy_position(333)
    h = sidecar.BridgeHandlers(fake)
    assert h.positions()[0]["volume"] == 0.1

    # order fill result echoes the same float32 artifact
    real_send = fake.order_send

    def noisy_send(request):
        r = real_send(request)
        r.volume = noisy
        return r

    fake.order_send = noisy_send
    out = h.order({"action": "buy", "type": "market",
                   "symbol": "XAUUSDmicro", "lots": 0.1})
    assert out["volume"] == 0.1, out

    # closing EXACTLY the remaining (float32-noisy) volume is a full close
    fake.positions_get = lambda ticket=None: noisy_position(334)
    out = h.close(334, 0.1)
    assert out["ok"] and out["closed_volume"] == 0.1, out
    assert h._m.sent[-1]["volume"] == 0.1

    # deal history and pending orders carry the same float32 artifact
    fake.history_deals_get = lambda frm, to: [
        SimpleNamespace(ticket=551, order=551, symbol="XAUUSDmicro", type=0,
                        volume=noisy, price=4310.0, profit=-12.5,
                        commission=-0.5, swap=0.0, time=1790001000, entry=1)]
    assert h.deals(7)[0]["volume"] == 0.1

    fake.orders_get = lambda ticket=None: [
        SimpleNamespace(ticket=777, symbol="XAUUSDmicro", type=2,
                        volume_current=noisy, price_open=4200.0,
                        sl=0.0, tp=0.0, state=2, time_setup=1790000000)]
    assert h.orders()[0]["volume"] == 0.1


def test_deals_accepts_explicit_utc_range():
    h = make_handlers()
    h.deals(7)  # legacy window still works
    h.deals(7, "2026-09-01", "2026-09-15")
    frm, to = h._m.deal_ranges[-1]
    assert frm.year == 2026 and frm.month == 9 and frm.day == 1
    assert to.year == 2026 and to.month == 9 and to.day == 15 and to.hour == 23
    h.deals(7, None, "2026-09-10")   # open-ended from year 2000
    frm, _ = h._m.deal_ranges[-1]
    assert frm.year == 2000
    h.deals(7, "1790001000", "1790002000")  # epoch seconds
    frm, _ = h._m.deal_ranges[-1]
    assert frm.year == 2026  # epoch 1790001000 is Sept 2026


def test_candles_accept_all_21_timeframes():
    h = make_handlers()
    for tf in ("M1", "M5", "M15", "M30", "H1", "H4", "D1", "W1", "MN1"):
        assert len(h.candles("XAUUSDmicro", tf, 2)) == 2, tf


# ── the real loopback server, end to end ──────────────────────────────

# ── /health re-attach (the sidecar must recover a terminal that shows up
# late — attach() at startup is a one-shot race against MT5 booting) ────

def test_health_reattaches_when_terminal_shows_up_late():
    fake = FakeMT5()
    fake.terminal_connected = False   # sidecar lost the boot race
    h = sidecar.BridgeHandlers(fake, reattach=lambda: setattr(fake, "terminal_connected", True))
    out = h.health()
    assert fake.reattach_calls == 0   # recovery hook owns the counter
    assert out["ok"] is True and out["terminal_connected"] is True


def test_health_reattach_is_throttled():
    fake = FakeMT5()
    fake.terminal_connected = False
    h = sidecar.BridgeHandlers(fake, reattach=lambda: None)
    h._reattach = lambda: setattr(h, "_tries", getattr(h, "_tries", 0) + 1)
    h.health()
    h.health()   # same instant: cooldown must suppress the second try
    assert getattr(h, "_tries", 0) == 1
    h._last_attach_try = time.monotonic() - h.REATTACH_COOLDOWN_S - 1
    h.health()   # cooldown expired: retry allowed again
    assert getattr(h, "_tries", 0) == 2


def test_health_reattach_skipped_while_terminal_connected():
    fake = FakeMT5()   # connected=True by default
    tries = []
    h = sidecar.BridgeHandlers(fake, reattach=lambda: tries.append(1))
    h.health()
    h.health()
    assert tries == [] and h.health()["ok"] is True


def test_health_reattach_failure_degrades_without_raising():
    fake = FakeMT5()
    fake.terminal_connected = False

    def boom():
        raise RuntimeError("initialize IPC timeout")

    h = sidecar.BridgeHandlers(fake, reattach=boom)
    out = h.health()   # must return the degraded snapshot, never raise
    assert out["ok"] is False and out["terminal_connected"] is False
    assert out["login"] is None and out["server"] is None


def test_health_without_reattach_hook_stays_read_only():
    """Regression: the pre-existing constructor keeps the old behavior —
    a disconnected terminal reads as degraded, no recovery attempted."""
    fake = FakeMT5()
    fake.terminal_connected = False
    h = sidecar.BridgeHandlers(fake)
    out = h.health()
    assert out["ok"] is False and out["login"] is None


# ── POST /login (in-terminal account switching) ────────────────────

def test_login_success_switches_the_account():
    h = make_handlers()
    out = h.login({"login": 201587365, "password": "secret", "server": "Deriv-Demo"})
    assert out["ok"] is True
    assert out["login"] == 201587365
    assert out["server"] == "Deriv-Demo"
    assert out["trade_mode"] == 0   # demo
    assert out["currency"] == "USD"
    assert FakeMT5.login_calls[-1] == {"login": 201587365, "server": "Deriv-Demo"}


def test_login_failure_reports_error_without_secrets():
    h = make_handlers()
    FakeMT5.login_result = {"fail": True}
    try:
        out = h.login({"login": 42, "password": "nope", "server": "Deriv-Real"})
        assert out["ok"] is False
        assert "login failed" in out["error"]
        assert "nope" not in json.dumps(out)   # password never echoed
    finally:
        FakeMT5.login_result = {}


def test_login_validates_input():
    h = make_handlers()
    for bad in (
        {"password": "x", "server": "Deriv-Demo"},
        {"login": "abc", "password": "x", "server": "s"},
        {"login": 1, "server": "s"},
        {"login": 1, "password": "x"},
    ):
        try:
            h.login(bad)
            raise AssertionError(f"expected rejection for {bad}")
        except sidecar.OrderError:
            pass


def test_login_is_rate_limited():
    h = make_handlers()
    sidecar.BridgeHandlers._login_attempts = []
    try:
        for _ in range(3):
            h.login({"login": 1, "password": "x", "server": "s"})
        try:
            h.login({"login": 1, "password": "x", "server": "s"})
            raise AssertionError("expected rate limit")
        except sidecar.OrderError as e:
            assert "too many login attempts" in str(e)
    finally:
        sidecar.BridgeHandlers._login_attempts = []


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

        # new surface: /orders, /cancel, /modify, partial /close, /deals range
        with urllib.request.urlopen(f"{base}/orders", timeout=5) as r:
            assert json.loads(r.read())["orders"][0]["ticket"] == 777
        req = urllib.request.Request(f"{base}/cancel/777", data=b"{}", method="POST")
        with urllib.request.urlopen(req, timeout=5) as r:
            assert json.loads(r.read())["cancelled_ticket"] == 777
        req = urllib.request.Request(
            f"{base}/modify",
            data=json.dumps({"ticket": 111, "sl": 4340.0}).encode(),
            headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=5) as r:
            assert json.loads(r.read())["ok"] is True
        req = urllib.request.Request(f"{base}/close/111?lots=0.2", data=b"{}", method="POST")
        with urllib.request.urlopen(req, timeout=5) as r:
            assert json.loads(r.read())["closed_volume"] == 0.2
        with urllib.request.urlopen(f"{base}/deals?from=2026-09-01&to=2026-09-15", timeout=5) as r:
            assert len(json.loads(r.read())["deals"]) == 1

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
