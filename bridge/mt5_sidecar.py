#!/usr/bin/env python3
"""MT5 sidecar — loopback-only HTTP bridge between DON G FX and MetaTrader 5.

Attaches to a RUNNING MT5 terminal (--terminal <path> targets a specific
install and starts it when needed; the MetaTrader5 package cannot launch
one) and serves a small JSON API on 127.0.0.1 only:

    GET  /health                 liveness + attached account snapshot
    GET  /account                balance/equity/margin/currency/leverage/login
    GET  /ticks/{symbol}         last bid/ask/time
    GET  /book/{symbol}          DOM levels (Deriv streams none -> client falls back)
    GET  /candles/{symbol}?tf=M1&n=120   OHLC series
    POST /order                  market/limit/stop/stoplimit with SL/TP
    GET  /positions              open positions with live P/L
    POST /close/{ticket}         close a position
    GET  /deals?days=7           recent deal history

Security: binds strictly to 127.0.0.1 (any other bind address is refused at
startup), reads no credentials, writes no files, runs no shells.

Run:  python bridge/mt5_sidecar.py [port]
"""
from __future__ import annotations

import json
import sys
import time
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, urlparse

import MetaTrader5 as mt5

DEFAULT_PORT = 53190
TIMEFRAMES = {
    "M1": mt5.TIMEFRAME_M1,
    "M5": mt5.TIMEFRAME_M5,
    "M15": mt5.TIMEFRAME_M15,
    "M30": mt5.TIMEFRAME_M30,
    "H1": mt5.TIMEFRAME_H1,
}

ORDER_TYPES = {
    ("buy", "market"): mt5.ORDER_TYPE_BUY,
    ("sell", "market"): mt5.ORDER_TYPE_SELL,
    ("buy", "limit"): mt5.ORDER_TYPE_BUY_LIMIT,
    ("sell", "limit"): mt5.ORDER_TYPE_SELL_LIMIT,
    ("buy", "stop"): mt5.ORDER_TYPE_BUY_STOP,
    ("sell", "stop"): mt5.ORDER_TYPE_SELL_STOP,
    ("buy", "stoplimit"): mt5.ORDER_TYPE_BUY_STOP_LIMIT,
    ("sell", "stoplimit"): mt5.ORDER_TYPE_SELL_STOP_LIMIT,
}

RETCODE_NAMES = {
    10009: "done",
    10004: "requote",
    10006: "rejected",
    10013: "invalid-request",
    10014: "invalid-volume",
    10015: "invalid-price",
    10016: "invalid-stops",
    10018: "market-closed",
    10019: "no-money",
    10027: "client-disabled",
    10030: "unsupported-filling",
}


class OrderError(ValueError):
    """A request the sidecar refuses without touching the terminal."""


def attach(retries: int = 6, delay: float = 2.0,
           terminal_path: str | None = None) -> bool:
    """initialize() with retries — IPC timeouts (-10005) are transient when
    several MT5 terminals are running. With --terminal the given
    terminal64.exe install is targeted (and started when it is not running),
    so a second MT5 install can never be attached by accident."""
    for _ in range(retries):
        if terminal_path:
            if mt5.initialize(path=terminal_path):
                return True
        elif mt5.initialize():
            return True
        time.sleep(delay)
    return False


def _filling_mode(symbol_info: Any) -> int:
    flags = getattr(symbol_info, "filling_mode", 1)
    if flags & 1:
        return mt5.ORDER_FILLING_FOK
    if flags & 2:
        return mt5.ORDER_FILLING_IOC
    return mt5.ORDER_FILLING_RETURN


class BridgeHandlers:
    """Endpoint logic, transport-free: tests drive this with a fake facade."""

    def __init__(self, facade: Any) -> None:
        self._m = facade

    # ── reads ──────────────────────────────────────────────────────

    def health(self) -> dict:
        acc = self._m.account_info()
        term = self._m.terminal_info()
        return {
            "ok": acc is not None,
            "login": getattr(acc, "login", None),
            "server": getattr(acc, "server", None),
            "terminal_connected": getattr(term, "connected", False),
        }

    def account(self) -> dict:
        acc = self._m.account_info()
        if acc is None:
            raise OrderError("terminal attached but account unavailable")
        return {
            "login": acc.login,
            "server": acc.server,
            "currency": acc.currency,
            "balance": acc.balance,
            "equity": acc.equity,
            "margin": acc.margin,
            "margin_free": acc.margin_free,
            "leverage": acc.leverage,
        }

    def _symbol_or_404(self, symbol: str) -> Any:
        self._m.symbol_select(symbol, True)
        info = self._m.symbol_info(symbol)
        if info is None:
            raise OrderError(f"symbol {symbol} not available on this account")
        return info

    def symbols(self) -> dict:
        """Tradable catalog with live quotes: symbol_select every visible
        symbol once, then snapshot bid/ask/spread/digits + trade mode. The
        Terminal's Market Watch is fed from this (MT5-native, not Deriv)."""
        out = []
        for info in (self._m.symbols_get() or []):
            if not getattr(info, "visible", False):
                continue
            tick = self._m.symbol_info_tick(info.name)
            out.append({
                "symbol": info.name,
                "description": info.description,
                "bid": tick.bid if tick else None,
                "ask": tick.ask if tick else None,
                "spread_points": info.spread,
                "digits": info.digits,
                "trade_mode": int(info.trade_mode),
            })
        return {"symbols": out}

    def ticks(self, symbol: str) -> dict:
        self._symbol_or_404(symbol)
        tick = self._m.symbol_info_tick(symbol)
        if tick is None:
            return {"symbol": symbol, "bid": None, "ask": None, "time": None}
        return {
            "symbol": symbol,
            "bid": tick.bid,
            "ask": tick.ask,
            "time": tick.time,
        }

    def book(self, symbol: str) -> dict:
        self._symbol_or_404(symbol)
        if not self._m.market_book_add(symbol):
            return {"symbol": symbol, "levels": []}
        try:
            book = self._m.market_book_get(symbol) or []
            levels = [
                {
                    "side": "ask" if b.type == self._m.BOOK_TYPE_ASK
                    else "bid" if b.type == self._m.BOOK_TYPE_BID else "other",
                    "price": b.price,
                    "volume": b.volume,
                }
                for b in book
            ]
        finally:
            self._m.market_book_release(symbol)
        return {"symbol": symbol, "levels": levels}

    def candles(self, symbol: str, tf: str, n: int) -> list:
        if tf not in TIMEFRAMES:
            raise OrderError(f"unsupported timeframe {tf!r} (use M1..H1)")
        n = max(1, min(int(n), 500))
        self._symbol_or_404(symbol)
        rates = self._m.copy_rates_from_pos(symbol, TIMEFRAMES[tf], 0, n)
        if rates is None:
            return []
        return [
            {
                "time": int(r["time"]),
                "open": float(r["open"]),
                "high": float(r["high"]),
                "low": float(r["low"]),
                "close": float(r["close"]),
                "volume": int(r["tick_volume"]),
            }
            for r in rates
        ]

    def positions(self) -> list:
        rows = self._m.positions_get() or []
        out = []
        for p in rows:
            out.append({
                "ticket": p.ticket,
                "symbol": p.symbol,
                "side": "buy" if p.type == self._m.POSITION_TYPE_BUY else "sell",
                "volume": p.volume,
                "price_open": p.price_open,
                "price_current": p.price_current,
                "profit": p.profit,
                "swap": p.swap,
                "sl": p.sl,
                "tp": p.tp,
                "time": p.time,
            })
        return out

    def deals(self, days: int) -> list:
        days = max(1, min(int(days), 90))
        frm = datetime.now(timezone.utc) - timedelta(days=days)
        rows = self._m.history_deals_get(frm, datetime.now(timezone.utc) + timedelta(days=1)) or []
        out = []
        for d in rows:
            if getattr(d, "entry", None) not in (None,) and d.entry in (1, 0):
                pass
            out.append({
                "ticket": d.ticket,
                "order": d.order,
                "symbol": d.symbol,
                "side": "buy" if d.type == self._m.DEAL_TYPE_BUY else "sell",
                "volume": d.volume,
                "price": d.price,
                "profit": d.profit,
                "commission": d.commission,
                "swap": d.swap,
                "time": d.time,
            })
        return out

    # ── writes ─────────────────────────────────────────────────────

    def order(self, body: dict) -> dict:
        action = str(body.get("action", "")).lower()
        kind = str(body.get("type", "market")).lower()
        symbol = str(body.get("symbol", "")).strip()
        if (action, kind) not in ORDER_TYPES:
            raise OrderError(
                f"action/type must be one of buy|sell × market|limit|stop|stoplimit "
                f"(got {action!r} × {kind!r})")
        if not symbol:
            raise OrderError("symbol is required")

        info = self._symbol_or_404(symbol)
        try:
            lots = float(body.get("lots"))
        except (TypeError, ValueError):
            raise OrderError("lots must be a number")
        step = info.volume_step or 0.01
        if lots < info.volume_min or lots > info.volume_max:
            raise OrderError(
                f"lots {lots} outside {info.volume_min}..{info.volume_max}")
        if abs(round(lots / step) * step - lots) > 1e-9:
            raise OrderError(f"lots {lots} not a multiple of {step}")

        price = body.get("price")
        stopprice = body.get("stopprice")
        if kind in ("limit", "stop") and price is None:
            raise OrderError(f"{kind} orders need a price")
        if kind == "stoplimit" and (price is None or stopprice is None):
            raise OrderError("stoplimit orders need price (limit) and stopprice (trigger)")

        tick = self._m.symbol_info_tick(symbol)
        if tick is None:
            raise OrderError(f"no tick for {symbol} — market closed?")

        request = {
            "action": self._m.TRADE_ACTION_DEAL if kind == "market" else self._m.TRADE_ACTION_PENDING,
            "symbol": symbol,
            "volume": lots,
            "type": ORDER_TYPES[(action, kind)],
            "type_filling": _filling_mode(info),
            "deviation": int(body.get("deviation", 20)),
            "magic": int(body.get("magic", 201587365)),
            "comment": str(body.get("comment", "donggfx"))[:31],
        }
        if kind == "market":
            request["price"] = tick.ask if action == "buy" else tick.bid
        elif kind == "stoplimit":
            request["price"] = float(price)
            request["stoplimit"] = float(stopprice)
        else:
            request["price"] = float(price)
        if price is not None and kind != "stoplimit":
            request["price"] = float(price)
        if body.get("sl") is not None:
            request["sl"] = float(body["sl"])
        if body.get("tp") is not None:
            request["tp"] = float(body["tp"])

        result = self._m.order_send(request)
        if result is None:
            raise OrderError(f"order_send returned None ({self._m.last_error()})")
        retcode = int(result.retcode)
        return {
            "retcode": retcode,
            "retcode_name": RETCODE_NAMES.get(retcode, f"retcode-{retcode}"),
            "ok": retcode == 10009,
            "deal": getattr(result, "deal", 0) or None,
            "order": getattr(result, "order", 0) or None,
            "price": getattr(result, "price", 0) or None,
            "volume": getattr(result, "volume", None),
            "comment": getattr(result, "comment", ""),
        }

    def close(self, ticket: int) -> dict:
        rows = self._m.positions_get(ticket=int(ticket)) or ()
        if not rows:
            raise OrderError(f"position {ticket} not found")
        p = rows[0]
        tick = self._m.symbol_info_tick(p.symbol)
        if tick is None:
            raise OrderError(f"no tick for {p.symbol} — market closed?")
        info = self._symbol_or_404(p.symbol)
        request = {
            "action": self._m.TRADE_ACTION_DEAL,
            "position": int(ticket),
            "symbol": p.symbol,
            "volume": p.volume,
            "type": self._m.ORDER_TYPE_SELL if p.type == self._m.POSITION_TYPE_BUY
            else self._m.ORDER_TYPE_BUY,
            "price": tick.bid if p.type == self._m.POSITION_TYPE_BUY else tick.ask,
            "type_filling": _filling_mode(info),
            "deviation": 20,
            "comment": "donggfx-close",
        }
        result = self._m.order_send(request)
        if result is None:
            raise OrderError(f"order_send returned None ({self._m.last_error()})")
        retcode = int(result.retcode)
        return {
            "retcode": retcode,
            "retcode_name": RETCODE_NAMES.get(retcode, f"retcode-{retcode}"),
            "ok": retcode == 10009,
            "closed_ticket": int(ticket),
        }


class SidecarServer:
    """Maps HTTP routes onto BridgeHandlers; binds loopback only."""

    def __init__(self, handlers: BridgeHandlers, port: int = DEFAULT_PORT) -> None:
        self._handlers = handlers
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args: Any) -> None:  # quiet
                pass

            def _send(self, code: int, payload: dict) -> None:
                raw = json.dumps(payload).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)

            def _route_get(self, path: str, qs: dict) -> None:
                h = outer._handlers
                try:
                    if path == "/health":
                        self._send(200, h.health())
                    elif path == "/account":
                        self._send(200, h.account())
                    elif path == "/symbols":
                        self._send(200, h.symbols())
                    elif path.startswith("/ticks/"):
                        self._send(200, h.ticks(path.split("/", 2)[2]))
                    elif path.startswith("/book/"):
                        self._send(200, h.book(path.split("/", 2)[2]))
                    elif path.startswith("/candles/"):
                        sym = path.split("/", 2)[2]
                        self._send(200, {"candles": h.candles(
                            sym, (qs.get("tf") or ["M1"])[0], (qs.get("n") or ["120"])[0])})
                    elif path == "/positions":
                        self._send(200, {"positions": h.positions()})
                    elif path == "/deals":
                        self._send(200, {"deals": h.deals((qs.get("days") or ["7"])[0])})
                    else:
                        self._send(404, {"error": f"no route {path}"})
                except OrderError as e:
                    self._send(422, {"error": str(e)})
                except Exception as e:  # noqa: BLE001 — surface, never crash
                    self._send(500, {"error": f"{type(e).__name__}: {e}"})

            def do_GET(self) -> None:  # noqa: N802 (http.server API)
                parsed = urlparse(self.path)
                self._route_get(parsed.path.rstrip("/"), parse_qs(parsed.query))

            def do_POST(self) -> None:  # noqa: N802
                parsed = urlparse(self.path)
                try:
                    length = int(self.headers.get("Content-Length") or 0)
                    raw = self.rfile.read(length) if length else b"{}"
                    body = json.loads(raw or b"{}")
                    if parsed.path == "/order":
                        self._send(200, outer._handlers.order(body))
                    elif parsed.path.startswith("/close/"):
                        self._send(200, outer._handlers.close(parsed.path.rsplit("/", 1)[1]))
                    else:
                        self._send(404, {"error": f"no route {parsed.path}"})
                except OrderError as e:
                    self._send(422, {"error": str(e)})
                except Exception as e:  # noqa: BLE001
                    self._send(500, {"error": f"{type(e).__name__}: {e}"})

        host = "127.0.0.1"
        self.httpd = ThreadingHTTPServer((host, port), Handler)
        self.port = self.httpd.server_address[1]

    def serve_forever(self) -> None:
        self.httpd.serve_forever()


def parse_args(argv: list[str]) -> tuple[int, str | None]:
    """(port, terminal_path). Port stays positional for backward
    compat; --terminal points at a specific terminal64.exe."""
    port = DEFAULT_PORT
    path: str | None = None
    i = 0
    while i < len(argv):
        if argv[i] == "--terminal" and i + 1 < len(argv):
            path = argv[i + 1]
            i += 2
        else:
            port = int(argv[i])
            i += 1
    return port, path


def main() -> int:
    port, terminal_path = parse_args(sys.argv[1:])
    target = f" (terminal: {terminal_path})" if terminal_path else ""
    print(f"attaching to MetaTrader 5{target} (retries on IPC timeout)…")
    if not attach(terminal_path=terminal_path):
        print("ATTACH FAILED:", mt5.last_error())
        print("Start the MT5 terminal and log in first.")
        return 1
    acc = mt5.account_info()
    print(f"attached: {acc.login} @ {acc.server} ({acc.balance} {acc.currency})")
    server = SidecarServer(BridgeHandlers(mt5), port)
    print(f"sidecar listening on http://127.0.0.1:{server.port} (loopback only)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        mt5.shutdown()
    return 0


if __name__ == "__main__":
    sys.exit(main())
