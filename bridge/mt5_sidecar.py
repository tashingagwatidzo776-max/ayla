#!/usr/bin/env python3
"""MT5 sidecar — loopback-only HTTP bridge between DON G FX and MetaTrader 5.

Attaches to a RUNNING MT5 terminal (--terminal <path> targets a specific
install and starts it when needed; the MetaTrader5 package cannot launch
one) and serves a small JSON API on 127.0.0.1 only:

    GET  /health                 liveness + attached account snapshot
                                 (re-attaches when the terminal dropped)
    GET  /account                balance/equity/margin/currency/leverage/login
    GET  /ticks/{symbol}         last bid/ask/time
    GET  /book/{symbol}          DOM levels (Deriv streams none -> client falls back)
    GET  /candles/{symbol}?tf=M1&n=120   OHLC series
    GET  /history/{symbol}?tf=M1&n=50000&start=0   deep paged OHLC
                                 (offline trainer only — the live route
                                 stays capped at 500)
    GET  /orders                 open (pending) orders
    POST /order                  market/limit/stop/stoplimit with SL/TP
    GET  /positions              open positions with live P/L
    POST /close/{ticket}[?lots=] close a position (optionally partially)
    POST /cancel/{ticket}        delete a pending order
    POST /modify                 change SL/TP on an open position
    POST /login                  switch the terminal account (3/min, body-only)
    GET  /deals?days=7           recent deal history
    GET  /deals?from=&to=        deal history over an explicit UTC range

Security: binds strictly to 127.0.0.1 (any other bind address is refused at
startup), reads no credentials, writes no files, runs no shells.

Run:  python bridge/mt5_sidecar.py [port]
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
import traceback
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, urlparse

import MetaTrader5 as mt5

DEFAULT_PORT = 53190
TIMEFRAMES = {
    "M1": mt5.TIMEFRAME_M1,
    "M2": mt5.TIMEFRAME_M2,
    "M3": mt5.TIMEFRAME_M3,
    "M4": mt5.TIMEFRAME_M4,
    "M5": mt5.TIMEFRAME_M5,
    "M6": mt5.TIMEFRAME_M6,
    "M10": mt5.TIMEFRAME_M10,
    "M12": mt5.TIMEFRAME_M12,
    "M15": mt5.TIMEFRAME_M15,
    "M20": mt5.TIMEFRAME_M20,
    "M30": mt5.TIMEFRAME_M30,
    "H1": mt5.TIMEFRAME_H1,
    "H2": mt5.TIMEFRAME_H2,
    "H3": mt5.TIMEFRAME_H3,
    "H4": mt5.TIMEFRAME_H4,
    "H6": mt5.TIMEFRAME_H6,
    "H8": mt5.TIMEFRAME_H8,
    "H12": mt5.TIMEFRAME_H12,
    "D1": mt5.TIMEFRAME_D1,
    "W1": mt5.TIMEFRAME_W1,
    "MN1": mt5.TIMEFRAME_MN1,
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


def _venue_lots(v):
    """Quantize a venue volume to a clean decimal lot count.

    MT5 volumes are float32, so a 0.1-lot fill comes back as
    0.10000000149011612. The app compares book lots against a double
    cap EXACTLY (FxPortfolioHost.AuditExposure), so a full-cap book of
    float32-noisy 0.1s tripped a spurious FX_RISK "exceeds the cap"
    WARN on every audit tick. Lot steps are >= 0.01, so 6 decimal
    places removes representation noise without touching real volume;
    None passes through unchanged (order results can omit it)."""
    if v is None:
        return None
    return round(float(v), 6)


def attach(retries: int = 20, delay: float = 3.0,
           terminal_path: str | None = None) -> bool:
    """initialize() with retries — IPC timeouts (-10005) are transient when
    several MT5 terminals are running. With --terminal the given
    terminal64.exe install is targeted (and started when it is not running),
    so a second MT5 install can never be attached by accident.

    The startup window is generous (default ~60 s) on purpose: a freshly
    launched terminal needs tens of seconds to boot and authorize, and a
    sidecar that gives up early exits without serving - which makes the
    external watchdog spawn ANOTHER sidecar (and another terminal) on its
    next tick, piling up duplicate terminals. Waiting instead absorbs the
    boot; the terminal process is already running, so repeated initialize()
    calls attach rather than launch more."""
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

    # /health re-attaches at most this often (the app polls every few
    # seconds; mt5.initialize is a heavyweight IPC handshake, not a poll).
    REATTACH_COOLDOWN_S = 30.0
    # /symbols serves a cached snapshot this long (stale-while-revalidate
    # rebuilds in the background). The full-catalog scan held the MT5 lock
    # 25-31s under load — every other route queued past the client's 15 s
    # timeout and the app journaled "MT5 bridge unreachable" flaps every
    # minute (2026-10-06). The fields the app sizes on (contract size,
    # volume steps, stops, point) are static intraday; bid/ask in this
    # payload is informational — the engine reads /ticks for quotes.
    SYMBOL_CACHE_TTL_S = 20.0

    def __init__(self, facade: Any, reattach: Any = None) -> None:
        self._m = facade
        # Optional recovery hook (production: attach() again). None keeps
        # the old behavior — reads only, never re-attaches.
        self._reattach = reattach
        self._last_attach_try = 0.0
        # /symbols snapshot cache (symbols_cached). Its own lock — never
        # the MT5 lock: the refresh thread takes the MT5 lock one chunk at
        # a time so concurrent routes keep flowing during a rebuild.
        self._sym_guard = threading.Lock()
        self._sym_ready = threading.Condition(self._sym_guard)
        self._sym_payload: dict | None = None
        self._sym_ts = 0.0
        self._sym_refresh: threading.Thread | None = None
        self._route_lock: Any = None   # bound by SidecarServer
        self._sym_last_diag: dict = {}

    # ── reads ──────────────────────────────────────────────────────

    def _ensure_attached(self) -> None:
        """attach() is a one-shot at startup, so a terminal that finished
        booting (or reconnected) after the sidecar left the sidecar
        permanently detached. /health is the app's first poll of every
        cycle, so it is the right place to retry — throttled, and any
        failure here only leaves the degraded snapshot, never raises."""
        if self._reattach is None:
            return
        try:
            term = self._m.terminal_info()
        except Exception:  # noqa: BLE001 — the facade failing must not kill /health
            return
        if getattr(term, "connected", False):
            return
        now = time.monotonic()
        if now - self._last_attach_try < self.REATTACH_COOLDOWN_S:
            return
        self._last_attach_try = now
        try:
            self._reattach()
        except Exception:  # noqa: BLE001
            pass

    def health(self) -> dict:
        self._ensure_attached()
        acc = self._m.account_info()
        term = self._m.terminal_info()
        return {
            "ok": acc is not None,
            "login": getattr(acc, "login", None),
            "server": getattr(acc, "server", None),
            "terminal_connected": getattr(term, "connected", False),
            # The terminal's OWN autotrading verdict (the green ▶ button AND
            # the Tools→Options master switch combined). Orders fail with
            # TRADE_RETCODE_CLIENT_DISABLED (10027) while this is false, so
            # the app surfaces it in the bridge status line at a glance.
            "trade_allowed": getattr(term, "trade_allowed", None),
        }

    _login_attempts: list[float] = []   # module-level rate-limit state

    def login(self, body: dict) -> dict:
        """POST /login — switch the terminal's signed-in account via
        mt5.login (the same switch the terminal's File->Login dialog
        performs). Credentials are read from the POST body only: never
        logged, never journaled, never echoed back. Rate-limited to 3
        attempts/minute. On success the caller should re-read /account and
        re-run the trade-mode gate verdict."""
        now = time.monotonic()
        type(self)._login_attempts = [t for t in type(self)._login_attempts if now - t < 60.0]
        if len(type(self)._login_attempts) >= 3:
            raise OrderError("too many login attempts - wait a minute")
        type(self)._login_attempts.append(now)

        raw_login = body.get("login")
        password = body.get("password") or ""
        server = (body.get("server") or "").strip()
        try:
            account_id = int(raw_login)
        except (TypeError, ValueError):
            raise OrderError("login must be the numeric account id")
        if not password or not server:
            raise OrderError("password and server are required")

        if not self._m.login(account_id, password=password, server=server):
            err = self._m.last_error()
            return {"ok": False, "error": f"login failed: {err}"}
        acc = self._m.account_info()
        if acc is None:
            return {"ok": False, "error": "login accepted but account unavailable"}
        return {
            "ok": True,
            "login": acc.login,
            "server": acc.server,
            "trade_mode": int(getattr(acc, "trade_mode", 0)),
            "balance": float(acc.balance),
            "currency": acc.currency,
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
            # The venue's own demo/real verdict (0=demo, 1=contest, 2=real).
            # Optional: an older terminal without it must not crash the
            # payload — the C# gate fails closed when the field is absent.
            "trade_mode": int(acc.trade_mode) if hasattr(acc, "trade_mode") else None,
        }

    def _symbol_or_404(self, symbol: str) -> Any:
        self._m.symbol_select(symbol, True)
        info = self._m.symbol_info(symbol)
        if info is None:
            raise OrderError(f"symbol {symbol} not available on this account")
        return info

    def symbols(self, lock: Any = None, chunk: int = 10) -> dict:
        """Tradable catalog with live quotes: snapshot bid/ask/spread/digits
        + trade mode for every visible symbol. The Terminal's Market Watch
        is fed from this (MT5-native, not Deriv).

        ``lock`` (the server's MT5 lock) is taken ONE CHUNK AT A TIME and
        released between chunks — a monolithic hold ran 25-31s under load,
        queueing every other route past the app's 15 s timeout and producing
        the 2026-10-06 BridgeDown flaps. Between chunks the lock is free and
        time.sleep yields the GIL, so queued routes and the accept loop run.
        Tests call this directly with lock=None (single-threaded).
        """
        t0 = time.monotonic()
        held = lock is not None
        if held:
            lock.acquire()
        try:
            infos = [i for i in (self._m.symbols_get() or [])
                     if getattr(i, "visible", False)]
        finally:
            if held:
                lock.release()
        get_ms = int((time.monotonic() - t0) * 1000)

        out: list[dict] = []
        t1 = time.monotonic()
        for start in range(0, len(infos), chunk):
            if held:
                lock.acquire()
            try:
                for info in infos[start:start + chunk]:
                    tick = self._m.symbol_info_tick(info.name)
                    out.append(self._symbol_row(info, tick))
            finally:
                if held:
                    lock.release()
            time.sleep(0.002)   # yield: interleave queued routes + accept loop
        self._sym_last_diag = {"n": len(out), "get_ms": get_ms,
                               "ticks_ms": int((time.monotonic() - t1) * 1000)}
        return {"symbols": out}

    @staticmethod
    def _symbol_row(info: Any, tick: Any) -> dict:
        return {
            "symbol": info.name,
            "description": info.description,
            "bid": tick.bid if tick else None,
            "ask": tick.ask if tick else None,
            "spread_points": info.spread,
            "digits": info.digits,
            "trade_mode": int(info.trade_mode),
            # Sizing ground truth: the engine must size in venue lots
            # (contract size), not guessed units. E.g. XAUUSDmicro is
            # "1 lot = 1 unit" with 0.1 step — 100× smaller than the
            # standard-gold contract the old price heuristic assumed.
            "volume_min": float(info.volume_min),
            "volume_step": float(info.volume_step),
            "volume_max": float(info.volume_max),
            "contract_size": float(info.trade_contract_size),
            # Stop geometry: the app floors its stop distances at the
            # venue's stops_level (an SL inside the band is rejected
            # outright by order_send). point converts points → price.
            "stops_level": int(getattr(info, "trade_stops_level", 0) or 0),
            "point": float(getattr(info, "point", 0.0) or 0.00001),
        }

    # ── /symbols snapshot cache ─────────────────────────────────────────

    def symbols_cached(self) -> dict:
        """Serve the catalog snapshot from cache (TTL SYMBOL_CACHE_TTL_S)
        with stale-while-revalidate: a stale snapshot returns instantly while
        a background thread rebuilds it chunked under the MT5 lock. Only the
        FIRST-ever call blocks (cold start, bounded below the client's 15 s
        timeout); after that no /symbols request can hold a lock long enough
        to starve another route."""
        now = time.monotonic()
        with self._sym_guard:
            payload = self._sym_payload
            if (payload is not None
                    and now - self._sym_ts < self.SYMBOL_CACHE_TTL_S):
                return payload
            self._start_sym_refresh_locked()
            if payload is not None:
                # stale-while-revalidate: warm paths never wait on a rebuild
                return payload
        # Cold start: wait (bounded) for the first snapshot. A miss raises
        # OrderError → 422 → GetJson reads null → the app retries next cycle.
        with self._sym_ready:
            self._sym_ready.wait_for(
                lambda: self._sym_payload is not None, timeout=14)
            if self._sym_payload is None:
                raise OrderError("symbol snapshot is still building — retry")
            return self._sym_payload

    def prewarm_symbols(self) -> None:
        """Kick the first snapshot build at startup so the app's first
        /symbols lands on a warm cache instead of paying the cold scan."""
        with self._sym_guard:
            self._start_sym_refresh_locked()

    def _start_sym_refresh_locked(self) -> None:
        # caller holds self._sym_guard
        if self._sym_refresh is not None and self._sym_refresh.is_alive():
            return
        self._sym_refresh = threading.Thread(
            target=self._symbols_refresh, name="sym-refresh", daemon=True)
        self._sym_refresh.start()

    def _symbols_refresh(self) -> None:
        try:
            t0 = time.monotonic()
            payload = self.symbols(lock=self._route_lock)
            took = int((time.monotonic() - t0) * 1000)
            with self._sym_guard:
                self._sym_payload = payload
                self._sym_ts = time.monotonic()
            diag = dict(self._sym_last_diag)
            req_log_write("%s SYSCACHE refresh ok n=%s get_ms=%s "
                          "ticks_ms=%s total_ms=%s" %
                          (_req_log_ts(), diag.get("n"), diag.get("get_ms"),
                           diag.get("ticks_ms"), took))
        except Exception:  # noqa: BLE001 — log, then the next call retries
            req_log_write("%s SYSCACHE refresh FAIL %s" %
                          (_req_log_ts(),
                           traceback.format_exc().replace("\n", " | ")))
        finally:
            with self._sym_ready:
                self._sym_ready.notify_all()

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
            raise OrderError(f"unsupported timeframe {tf!r} (use M1..MN1)")
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

    # One page of deep history the offline training tape is built from.
    # The live /candles route deliberately caps at 500 bars; the trainer
    # needs up to MaxBars (100000) per timeframe, which MUST arrive in
    # bounded pages — one unbounded call holds the request lock long
    # enough for the external watchdog's /account probe (45 s) to give up
    # and kill this process mid-fetch.
    HISTORY_PAGE_MAX = 50000

    def history(self, symbol: str, tf: str, n: int, start: int = 0) -> list:
        if tf not in TIMEFRAMES:
            raise OrderError(f"unsupported timeframe {tf!r} (use M1..MN1)")
        n = max(1, min(int(n), self.HISTORY_PAGE_MAX))
        start = max(0, int(start))
        self._symbol_or_404(symbol)
        rates = self._m.copy_rates_from_pos(symbol, TIMEFRAMES[tf], start, n)
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
                "volume": _venue_lots(p.volume),
                "price_open": p.price_open,
                "price_current": p.price_current,
                "profit": p.profit,
                "swap": p.swap,
                "sl": p.sl,
                "tp": p.tp,
                "time": p.time,
                # Order comment: the exit engine's ownership key ("donggfx-brain").
                "comment": getattr(p, "comment", ""),
            })
        return out

    def deals(self, days: int, date_from: str | None = None, date_to: str | None = None) -> list:
        """Deal history over an explicit UTC range (ISO 8601 dates or
        epoch seconds) or, when no range is given, the trailing `days`
        window (capped at 90 for backward compatibility)."""
        if date_from is not None or date_to is not None:
            def _parse(v: str, end_of_day: bool) -> datetime:
                v = str(v).strip()
                if v.isdigit():
                    return datetime.fromtimestamp(int(v), tz=timezone.utc)
                dt = datetime.fromisoformat(v.replace("Z", "+00:00"))
                if dt.tzinfo is None:
                    dt = dt.replace(tzinfo=timezone.utc)
                if end_of_day and len(v) <= 10:  # bare date -> include the whole day
                    dt = dt + timedelta(days=1) - timedelta(seconds=1)
                return dt
            frm = _parse(date_from, False) if date_from else datetime(2000, 1, 1, tzinfo=timezone.utc)
            to = _parse(date_to, True) if date_to else datetime.now(timezone.utc) + timedelta(days=1)
        else:
            days = max(1, min(int(days), 90))
            frm = datetime.now(timezone.utc) - timedelta(days=days)
            to = datetime.now(timezone.utc) + timedelta(days=1)
        rows = self._m.history_deals_get(frm, to) or []
        out = []
        for d in rows:
            if getattr(d, "entry", None) not in (None,) and d.entry in (1, 0):
                pass
            out.append({
                "ticket": d.ticket,
                "order": d.order,                    "symbol": d.symbol,
                    "side": "buy" if d.type == self._m.DEAL_TYPE_BUY else "sell",
                    "volume": _venue_lots(d.volume),
                "price": d.price,
                "profit": d.profit,
                "commission": d.commission,
                "swap": d.swap,
                "time": d.time,
            })
        return out

    def orders(self) -> list:
        """Open (pending) orders — MT5's Trade tab keeps them beside
        positions; until now a placed pending vanished from the app."""
        rows = self._m.orders_get() or []
        out = []
        for o in rows:
            out.append({
                "ticket": o.ticket,
                "symbol": o.symbol,
                "side": "buy" if o.type in (self._m.ORDER_TYPE_BUY,
                                            self._m.ORDER_TYPE_BUY_LIMIT,
                                            self._m.ORDER_TYPE_BUY_STOP,
                                            self._m.ORDER_TYPE_BUY_STOP_LIMIT) else "sell",
                "kind": {self._m.ORDER_TYPE_BUY: "market",
                         self._m.ORDER_TYPE_SELL: "market",
                         self._m.ORDER_TYPE_BUY_LIMIT: "limit",
                         self._m.ORDER_TYPE_SELL_LIMIT: "limit",
                         self._m.ORDER_TYPE_BUY_STOP: "stop",
                         self._m.ORDER_TYPE_SELL_STOP: "stop",
                         self._m.ORDER_TYPE_BUY_STOP_LIMIT: "stoplimit",
                         self._m.ORDER_TYPE_SELL_STOP_LIMIT: "stoplimit"}.get(o.type, "pending"),
                "volume": _venue_lots(o.volume_current),
                "price": o.price_open,
                "sl": o.sl,
                "tp": o.tp,
                "state": str(o.state),
                "time_setup": o.time_setup,
            })
        return out

    def cancel(self, ticket: int) -> dict:
        """Delete a pending order (TRADE_ACTION_REMOVE)."""
        rows = self._m.orders_get(ticket=int(ticket)) or ()
        if not rows:
            raise OrderError(f"pending order {ticket} not found")
        request = {"action": self._m.TRADE_ACTION_REMOVE, "order": int(ticket)}
        result = self._m.order_send(request)
        if result is None:
            raise OrderError(f"order_send returned None ({self._m.last_error()})")
        retcode = int(result.retcode)
        return {
            "retcode": retcode,
            "retcode_name": RETCODE_NAMES.get(retcode, f"retcode-{retcode}"),
            "ok": retcode == 10009,
            "cancelled_ticket": int(ticket),
        }

    def modify(self, body: dict) -> dict:
        """Modify SL/TP on an open position (TRADE_ACTION_SLTP), with the
        broker's stops-level enforced against the live tick."""
        try:
            ticket = int(body.get("ticket"))
        except (TypeError, ValueError):
            raise OrderError("ticket is required")
        rows = self._m.positions_get(ticket=ticket) or ()
        if not rows:
            raise OrderError(f"position {ticket} not found")
        p = rows[0]
        if body.get("sl") is None and body.get("tp") is None:
            raise OrderError("provide sl and/or tp")
        sl = float(body["sl"]) if body.get("sl") is not None else p.sl
        tp = float(body["tp"]) if body.get("tp") is not None else p.tp
        info = self._symbol_or_404(p.symbol)
        tick = self._m.symbol_info_tick(p.symbol)
        if tick is None:
            raise OrderError(f"no tick for {p.symbol} — market closed?")
        min_dist = (getattr(info, "trade_stops_level", 0) or 0) * (getattr(info, "point", 0.0) or 0.0)
        ref = tick.bid if p.type == self._m.POSITION_TYPE_BUY else tick.ask
        if sl and abs(ref - sl) < min_dist:
            raise OrderError(f"sl {sl} within stops level ({min_dist} of {ref})")
        if tp and abs(tp - ref) < min_dist:
            raise OrderError(f"tp {tp} within stops level ({min_dist} of {ref})")
        request = {
            "action": self._m.TRADE_ACTION_SLTP,
            "position": ticket,
            "symbol": p.symbol,
            "sl": sl,
            "tp": tp,
        }
        result = self._m.order_send(request)
        if result is None:
            raise OrderError(f"order_send returned None ({self._m.last_error()})")
        retcode = int(result.retcode)
        return {
            "retcode": retcode,
            "retcode_name": RETCODE_NAMES.get(retcode, f"retcode-{retcode}"),
            "ok": retcode == 10009,
            "modified_ticket": ticket,
            "sl": sl,
            "tp": tp,
        }

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
        # Normalize stop prices to the symbol's own digit count: a float
        # arriving from JSON can sit inside the stops_level band purely by
        # representation (…00000001), and MT5 rounds to digits anyway —
        # explicit normalization keeps rejections honest.
        digits = int(getattr(info, "digits", 5) or 5)
        if body.get("sl") is not None:
            request["sl"] = round(float(body["sl"]), digits)
        if body.get("tp") is not None:
            request["tp"] = round(float(body["tp"]), digits)

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
            "volume": _venue_lots(getattr(result, "volume", None)),
            "comment": getattr(result, "comment", ""),
        }

    def close(self, ticket: int, lots: float | None = None) -> dict:
        """Close a position in full, or partially when `lots` is given
        (volume-geometry validated against the symbol like /order)."""
        rows = self._m.positions_get(ticket=int(ticket)) or ()
        if not rows:
            raise OrderError(f"position {ticket} not found")
        p = rows[0]
        if lots is not None:
            info = self._symbol_or_404(p.symbol)
            step = info.volume_step or 0.01
            if lots <= 0 or lots > p.volume:
                raise OrderError(f"lots {lots} outside 0..{p.volume}")
            if abs(round(lots / step) * step - lots) > 1e-9:
                raise OrderError(f"lots {lots} not a multiple of {step}")
            if abs(lots - _venue_lots(p.volume)) < 1e-9:
                lots = None  # closing exactly the remaining volume = full close
        tick = self._m.symbol_info_tick(p.symbol)
        if tick is None:
            raise OrderError(f"no tick for {p.symbol} — market closed?")
        info = self._symbol_or_404(p.symbol)
        request = {
            "action": self._m.TRADE_ACTION_DEAL,
            "position": int(ticket),
            "symbol": p.symbol,
            "volume": _venue_lots(p.volume) if lots is None else lots,
            "type": self._m.ORDER_TYPE_SELL if p.type == self._m.POSITION_TYPE_BUY
            else self._m.ORDER_TYPE_BUY,
            "price": tick.bid if p.type == self._m.POSITION_TYPE_BUY else tick.ask,
            "type_filling": _filling_mode(info),
            "deviation": 20,
            # MT5 overwrites the POSITION's comment with the last applied
            # deal's comment, so a close-specific stamp silently un-owns a
            # partial-closed position for the exit brain (2026-10-08
            # #8792846716: donggfx-brain -> donggfx-close at the TP1 rung,
            # snapshots and profit floor dropped). Echo the position's own
            # comment so a partial close preserves ownership; an unstamped
            # (manual) position keeps a non-owning comment.
            "comment": getattr(p, "comment", "") or "",
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
            "closed_volume": _venue_lots(p.volume if lots is None else lots),
        }


# Shared connection/request journal for the BridgeDown-flap diagnosis:
# every ACCEPTED socket and every ANSWERED request (status + duration)
# lands here, so an app-side fetch failure can be told apart from a
# request that never reached the handler at all. Size-capped rotate.
REQ_LOG = os.path.join(os.environ.get("APPDATA", ""), "tf", "data",
                       "logs", "sidecar-requests.log")


def _req_log_ts() -> str:
    return datetime.now(timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"


# Serialize appends: the :18 heartbeat burst has a dozen threads logging at
# once, and unlocked concurrent appends on Windows can drop or interleave
# lines — which would hide exactly the request the flap diagnosis is after.
_req_log_lock = threading.Lock()


def req_log_write(line: str) -> None:
    try:
        with _req_log_lock:
            if os.path.exists(REQ_LOG) and os.path.getsize(REQ_LOG) > 1_000_000:
                os.replace(REQ_LOG, REQ_LOG + ".1")
            with open(REQ_LOG, "a", encoding="utf-8") as fh:
                fh.write(line + "\n")
    except OSError:
        pass


class ExclusiveHTTPServer(ThreadingHTTPServer):
    """HTTP server that REFUSES to share its port. Python's default
    allow_reuse_address=True (SO_REUSEADDR) lets a second instance bind a
    port already in use on Windows — four double-bound sidecars meant
    connections landed on a random one (transient 'bridge unreachable',
    split-brain health, 2026-09-28). With reuse disabled the second bind
    fails loudly here, and main() exits gracefully instead.

    request_queue_size: socketserver's default listen backlog is 5, and
    the app's per-minute heartbeat bursts (candles + tick + account +
    deals for four staggered engines, plus the trade feed) arrive within
    milliseconds — SYNs beyond the backlog are REFUSED, the heartbeat's
    account read fails, and the supervisor flaps BridgeDown <-> cleared
    every minute (journal 2026-10-05/06: ~58-16 events/day while a probe
    at 150 ms cadence saw zero slow responses — the burst refused, the
    probe never collided). A deep backlog makes the burst queue instead
    of refuse.
    """

    allow_reuse_address = False
    request_queue_size = 256

    def get_request(self):
        # Log the ACCEPT itself: a client fetch that fails with no matching
        # request line never reached the handler (kernel refused the SYN or
        # the client aborted before sending) — the distinction the
        # BridgeDown-flap diagnosis needs.
        conn, addr = super().get_request()
        req_log_write("%s ACCEPT peer=%s:%s" %
                      (_req_log_ts(), addr[0], addr[1]))
        return conn, addr

    def handle_error(self, request, client_address) -> None:
        # Handler-thread crashes used to vanish into a hidden stderr; keep
        # them where the diagnosis can see them.
        req_log_write("%s ERROR peer=%s:%s %s" % (
            _req_log_ts(), client_address[0], client_address[1],
            traceback.format_exc().replace("\n", " | ")))


class SidecarServer:
    """Maps HTTP routes onto BridgeHandlers; binds loopback only."""

    def __init__(self, handlers: BridgeHandlers, port: int = DEFAULT_PORT) -> None:
        self._handlers = handlers
        self._mt5_lock = threading.Lock()  # MT5 API is not thread-safe
        # The /symbols background rebuild takes the SAME MT5 lock — but only
        # one chunk at a time (see BridgeHandlers.symbols).
        handlers._route_lock = self._mt5_lock
        outer = self

        class Handler(BaseHTTPRequestHandler):
            # HTTP/1.1 + keep-alive (2026-10-06 BridgeDown fix, part 2).
            # With the HTTP/1.0 default (close per response) EVERY request
            # paid a fresh TCP handshake: ~6 conn/s churned 20k TIME_WAITs,
            # and ~0.04% of clients got their connection RST mid-response
            # ("connection was forcibly closed by the remote host") with
            # zero ACCEPT logged — the app's per-minute GetJson null →
            # "MT5 bridge unreachable" halt. A 20k-probe against an HTTP/1.1
            # mirror: 1 connection, 0 failures (vs 8 failures / 20k
            # connections on HTTP/1.0). _send always emits Content-Length,
            # which HTTP/1.1 keep-alive requires.
            protocol_version = "HTTP/1.1"

            def log_message(self, *args: Any) -> None:  # quiet
                pass

            def log_request(self, code: Any = '-', size: Any = '-') -> None:
                # Called from send_response (after the MT5 lock is released),
                # so dt = route + lock wait - exactly the latency that can
                # eat the client's 15 s timeout. The status code matters: a
                # 4xx/5xx looks like a logged fetch but reads null app-side
                # (Mt5BridgeClient.GetJson rejects non-2xx) — the 422 from
                # account_info() is None would otherwise be invisible.
                # The peer port pairs this line with its ACCEPT exactly —
                # adjacency lies when several connections interleave.
                dt_ms = int((time.monotonic()
                             - getattr(self, "_t0", time.monotonic())) * 1000)
                try:
                    peer = "%s:%s" % self.connection.getpeername()[:2]
                except OSError:
                    peer = "?"
                req_log_write("%s %s %s peer=%s status=%s dt=%dms" % (
                    _req_log_ts(),
                    self.command or "?",
                    (self.path or "?").split("?")[0],
                    peer,
                    code, dt_ms))

            def handle_one_request(self) -> None:
                # A connection that is accepted but never yields a request
                # line (client vanished mid-handshake or sent nothing) used
                # to be indistinguishable from a request we never saw at
                # all — pin it down for the flap diagnosis. With keep-alive,
                # an EMPTY line after ≥1 served request is just the client
                # closing an idle pool connection — log the orphan only.
                super().handle_one_request()
                if (not getattr(self, "raw_requestline", b"")
                        and not hasattr(self, "_t0")):
                    try:
                        peer = "%s:%s" % self.connection.getpeername()[:2]
                    except OSError:
                        peer = "?"
                    req_log_write("%s EOF peer=%s (accepted, no request)" %
                                  (_req_log_ts(), peer))

            def _send(self, code: int, payload: dict) -> None:
                raw = json.dumps(payload).encode()
                try:
                    self.send_response(code)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(raw)))
                    self.end_headers()
                    self.wfile.write(raw)
                except OSError:
                    # Client went away mid-response (its own timeout aborts
                    # the socket) — not a server fault; drop it quietly...
                    # but SAY SO: a logged status=200 whose write failed
                    # reads null app-side (GetJson) and halts the supervisor
                    # while looking like a served fetch. Flap diagnosis.
                    try:
                        peer = "%s:%s" % self.connection.getpeername()[:2]
                    except OSError:
                        peer = "?"
                    req_log_write("%s WRITEFAIL peer=%s %s %s" %
                                  (_req_log_ts(), peer,
                                   self.command or "?",
                                   (self.path or "?").split("?")[0]))
                    self.close_connection = True

            def _route_get(self, path: str, qs: dict) -> None:
                h = outer._handlers
                if path == "/symbols":
                    # NOT under the MT5 lock: the full-catalog scan used to
                    # hold it 25-31s (2026-10-06 BridgeDown flaps), queueing
                    # every route past the app's 15 s timeout. The cached
                    # path only ever blocks on a cold start; rebuilds run
                    # chunked in the background (stale-while-revalidate).
                    try:
                        payload, code = h.symbols_cached(), 200
                    except OrderError as e:
                        payload, code = {"error": str(e)}, 422
                    except Exception as e:  # noqa: BLE001
                        payload, code = {"error": f"{type(e).__name__}: {e}"}, 500
                    self._send(code, payload)
                    return
                # The MetaTrader5 API is not thread-safe: the data fetch runs
                # under one lock, but the socket write happens OUTSIDE it —
                # a slow or aborted client write must never stall other
                # requests (2026-09-28 freeze + crash loop).
                with outer._mt5_lock:
                    try:
                        if path == "/health":
                            payload, code = h.health(), 200
                        elif path == "/account":
                            payload, code = h.account(), 200
                        elif path.startswith("/ticks/"):
                            payload, code = h.ticks(path.split("/", 2)[2]), 200
                        elif path.startswith("/book/"):
                            payload, code = h.book(path.split("/", 2)[2]), 200
                        elif path.startswith("/candles/"):
                            sym = path.split("/", 2)[2]
                            payload, code = {"candles": h.candles(
                                sym, (qs.get("tf") or ["M1"])[0], (qs.get("n") or ["120"])[0])}, 200
                        elif path.startswith("/history/"):
                            sym = path.split("/", 2)[2]
                            payload, code = {"bars": h.history(
                                sym, (qs.get("tf") or ["M1"])[0],
                                (qs.get("n") or ["50000"])[0],
                                (qs.get("start") or ["0"])[0])}, 200
                        elif path == "/positions":
                            payload, code = {"positions": h.positions()}, 200
                        elif path == "/orders":
                            payload, code = {"orders": h.orders()}, 200
                        elif path == "/deals":
                            payload, code = {"deals": h.deals(
                                (qs.get("days") or ["7"])[0],
                                (qs.get("from") or [None])[0],
                                (qs.get("to") or [None])[0])}, 200
                        else:
                            payload, code = {"error": f"no route {path}"}, 404
                    except OrderError as e:
                        payload, code = {"error": str(e)}, 422
                    except Exception as e:  # noqa: BLE001 — surface, never crash
                        payload, code = {"error": f"{type(e).__name__}: {e}"}, 500
                self._send(code, payload)

            def do_GET(self) -> None:  # noqa: N802 (http.server API)
                self._t0 = time.monotonic()
                parsed = urlparse(self.path)
                self._route_get(parsed.path.rstrip("/"), parse_qs(parsed.query))

            def do_POST(self) -> None:  # noqa: N802 (http.server API)
                self._t0 = time.monotonic()
                parsed = urlparse(self.path)
                with outer._mt5_lock:
                    try:
                        length = int(self.headers.get("Content-Length") or 0)
                        raw = self.rfile.read(length) if length else b"{}"
                        body = json.loads(raw or b"{}")
                        if parsed.path == "/order":
                            payload, code = outer._handlers.order(body), 200
                        elif parsed.path.startswith("/close/"):
                            qs = parse_qs(urlparse(self.path).query)
                            vol = (qs.get("lots") or [None])[0]
                            payload, code = outer._handlers.close(
                                parsed.path.rsplit("/", 1)[1],
                                float(vol) if vol is not None else None), 200
                        elif parsed.path.startswith("/cancel/"):
                            payload, code = outer._handlers.cancel(parsed.path.rsplit("/", 1)[1]), 200
                        elif parsed.path == "/modify":
                            payload, code = outer._handlers.modify(body), 200
                        elif parsed.path == "/login":
                            payload, code = outer._handlers.login(body), 200
                        else:
                            payload, code = {"error": f"no route {parsed.path}"}, 404
                    except OrderError as e:
                        payload, code = {"error": str(e)}, 422
                    except Exception as e:  # noqa: BLE001
                        payload, code = {"error": f"{type(e).__name__}: {e}"}, 500
                self._send(code, payload)

        host = "127.0.0.1"
        self.httpd = ExclusiveHTTPServer((host, port), Handler)
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


def already_running(port: int) -> bool:
    """True when a live sidecar already serves this port. A simple HTTP
    GET — the authoritative check: a bound-but-dead socket fails the
    request, a live responder answers. The watchdog's respawn of an
    already-healthy sidecar then exits 0 quietly instead of double-binding
    (which ExclusiveHTTPServer would now refuse anyway — this check just
    makes the common case silent)."""
    try:
        import urllib.request

        with urllib.request.urlopen(
            f"http://127.0.0.1:{port}/health", timeout=2
        ) as resp:
            return resp.status == 200
    except Exception:  # noqa: BLE001 — nothing listening / not a sidecar
        return False


def main() -> int:
    port, terminal_path = parse_args(sys.argv[1:])
    if already_running(port):
        # Watchdog respawn while a healthy sidecar holds the port: exit
        # quietly with success. Never kill the existing instance — the app
        # is using it right now.
        print(f"sidecar already running on 127.0.0.1:{port} — nothing to do")
        return 0
    target = f" (terminal: {terminal_path})" if terminal_path else ""
    print(f"attaching to MetaTrader 5{target} (retries on IPC timeout)…")
    if not attach(terminal_path=terminal_path):
        print("ATTACH FAILED:", mt5.last_error())
        print("Start the MT5 terminal and log in first.")
        return 1
    acc = mt5.account_info()
    print(f"attached: {acc.login} @ {acc.server} ({acc.balance} {acc.currency})")
    # /health re-attaches (throttled) when the terminal shows up late or
    # drops — startup attach no longer has to win a race with MT5 booting.
    handlers = BridgeHandlers(
        mt5, reattach=lambda: attach(retries=2, delay=1.0, terminal_path=terminal_path))
    server = SidecarServer(handlers, port)
    handlers.prewarm_symbols()   # first /symbols lands on a warm cache
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
