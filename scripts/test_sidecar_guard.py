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


def main() -> int:
    tests = [
        test_sidecar_compiles,
        test_guard_static_contract,
        test_second_bind_on_a_live_port_raises,
        test_default_server_documents_the_windows_hazard,
        test_already_running_true_against_a_live_health,
        test_already_running_false_on_a_dead_port,
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
