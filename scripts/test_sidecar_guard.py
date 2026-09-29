#!/usr/bin/env python3
"""Sidecar single-instance guard tests.

The 2026-09-28 incident: the bridge watchdog respawned sidecars while old
ones still held port 53190, and Python's default allow_reuse_address=True
let FOUR instances double-bind the same loopback port on Windows —
connections landed on a random instance (transient "bridge unreachable",
split-brain health). The guard: ExclusiveHTTPServer refuses port sharing
(allow_reuse_address = False, a second bind raises) and main() exits
quietly (exit 0) when a healthy sidecar already answers /health.

Run:  python scripts/test_sidecar_guard.py
"""
from __future__ import annotations

import http.server
import os
import py_compile
import socket
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "bridge"))

# CI has no MetaTrader5 package (Windows-runner-only dependency), and the
# guard surface under test here never touches a terminal — stub the module
# before any mt5_sidecar import, exactly like bridge/test_mt5_sidecar.py
# does. On machines WITH the package the real module is used untouched.
try:
    import MetaTrader5  # noqa: F401
except ImportError:
    import types as _types
    sys.modules["MetaTrader5"] = _types.SimpleNamespace(
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

PASS = 0
FAIL = 0
FAILURES: list[tuple[str, str]] = []


def check(name: str, fn) -> None:
    global PASS, FAIL
    try:
        fn()
        PASS += 1
        print(f"  ok  {name}")
    except Exception as e:  # noqa: BLE001 — a failing check must not stop the rest
        FAIL += 1
        FAILURES.append((name, f"{type(e).__name__}: {e}"))
        print(f"FAIL  {name}: {type(e).__name__}: {e}")


def free_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def test_sidecar_compiles():
    py_compile.compile(
        str(ROOT / "bridge" / "mt5_sidecar.py"), doraise=True)


def test_guard_static_contract():
    src = (ROOT / "bridge" / "mt5_sidecar.py").read_text(encoding="utf-8")
    assert "class ExclusiveHTTPServer(ThreadingHTTPServer)" in src
    assert "allow_reuse_address = False" in src
    assert "self.httpd = ExclusiveHTTPServer((host, port), Handler)" in src
    # No direct ThreadingHTTPServer construction left in the server path.
    assert "ThreadingHTTPServer((host" not in src
    # The graceful already-running exit precedes attach in main().
    assert "def already_running(" in src
    assert src.index("if already_running(port):") < src.index("if not attach(")


def test_second_bind_on_a_live_port_raises():
    import mt5_sidecar

    port = free_loopback_port()
    first = mt5_sidecar.ExclusiveHTTPServer(("127.0.0.1", port), http.server.BaseHTTPRequestHandler)
    try:
        # The whole point: a second exclusive instance must FAIL to bind a
        # live port — loudly, at construction — instead of silently sharing.
        try:
            second = mt5_sidecar.ExclusiveHTTPServer(
                ("127.0.0.1", port), http.server.BaseHTTPRequestHandler)
            second.server_close()
            raise AssertionError(
                "second ExclusiveHTTPServer bound a live port — the guard is not active")
        except OSError:
            pass
    finally:
        first.server_close()


def test_default_server_documents_the_windows_hazard():
    # Informational, and the incident's exact mechanism: on Windows the
    # hijack needs BOTH sockets to opt in (SO_REUSEADDR), so default +
    # default double-binds — which is how four old sidecars shared 53190.
    # Any pairing with the new ExclusiveHTTPServer (no SO_REUSEADDR on the
    # first socket) refuses. On platforms where the OS refuses regardless,
    # this probe is informational only, never a failure.
    port = free_loopback_port()
    holder = http.server.ThreadingHTTPServer(("127.0.0.1", port), http.server.BaseHTTPRequestHandler)
    try:
        try:
            hazard = http.server.ThreadingHTTPServer(
                ("127.0.0.1", port), http.server.BaseHTTPRequestHandler)
            hazard.server_close()
            print("      (platform allows default-vs-default double-bind — the incident mechanism, now guarded)")
        except OSError:
            print("      (platform refuses double-bind even default-vs-default)")
    finally:
        holder.server_close()


def test_already_running_true_against_a_live_health():
    import mt5_sidecar

    class Health(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 — http.server API
            body = b'{"ok": true}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args):
            pass

    port = free_loopback_port()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Health)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        assert mt5_sidecar.already_running(port) is True
    finally:
        server.shutdown()
        server.server_close()


def test_already_running_false_on_a_dead_port():
    import mt5_sidecar

    port = free_loopback_port()   # nothing listening here
    assert mt5_sidecar.already_running(port) is False


class _Info:
    """Minimal stand-in for an MT5 symbol_info object."""

    def __init__(self, stops_level, point, digits):
        self.name = "EURUSD"
        self.description = "Euro vs US Dollar"
        self.visible = True
        self.spread = 10
        self.trade_mode = 4
        self.volume_min = 0.01
        self.volume_step = 0.01
        self.volume_max = 100.0
        self.filling_mode = 1
        self.trade_contract_size = 100_000.0
        self.trade_stops_level = stops_level
        self.point = point
        self.digits = digits


class _SymbolFacade:
    """Facade exposing only symbols() with a canned symbol_info."""

    # MT5 trade-action constants the order path reads off the facade.
    TRADE_ACTION_DEAL = 1
    TRADE_ACTION_PENDING = 5

    def __init__(self, info):
        self._info = info

    def symbols_get(self):
        return [self._info]

    def symbol_select(self, _symbol, enable=True):
        return True

    def symbol_info(self, _symbol):
        return self._info

    def symbol_info_tick(self, _symbol):
        return None


def test_symbols_payload_includes_stop_geometry():
    import mt5_sidecar

    handlers = mt5_sidecar.BridgeHandlers(_SymbolFacade(_Info(25, 0.00001, 5)))
    out = handlers.symbols()
    sym = out["symbols"][0]
    assert sym["stops_level"] == 25
    assert sym["point"] == 0.00001


def test_order_normalizes_sl_to_symbol_digits():
    import mt5_sidecar

    calls = {}

    class OrderFacade(_SymbolFacade):
        def __init__(self, info):
            super().__init__(info)

        def symbol_info_tick(self, _symbol):
            class Tick:
                bid = 1.15000
                ask = 1.15003
            return Tick()

        def order_send(self, request):
            calls["sl"] = request.get("sl")
            calls["tp"] = request.get("tp")

            class R:
                retcode = 10009  # TRADE_RETCODE_DONE

            return R()

    info = _Info(0, 0.00001, 5)
    handlers = mt5_sidecar.BridgeHandlers(OrderFacade(info))

    body = {"action": "buy", "type": "market", "symbol": "EURUSD",
            "lots": 0.01, "sl": 1.1494823711, "tp": 1.1530099999}
    handlers.order(body)
    assert calls["sl"] == 1.14948, calls
    assert calls["tp"] == 1.15301, calls


def main() -> int:
    tests = [
        test_sidecar_compiles,
        test_guard_static_contract,
        test_second_bind_on_a_live_port_raises,
        test_default_server_documents_the_windows_hazard,
        test_already_running_true_against_a_live_health,
        test_already_running_false_on_a_dead_port,
        test_symbols_payload_includes_stop_geometry,
        test_order_normalizes_sl_to_symbol_digits,
    ]
    print("sidecar single-instance guard tests")
    for t in tests:
        check(t.__name__, t)
    print(f"\n{PASS} passed, {FAIL} failed")
    if FAILURES:
        print("\nFailures:")
        for name, detail in FAILURES:
            print(f"  - {name}: {detail}")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
