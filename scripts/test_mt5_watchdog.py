"""Tests for the external MT5 watchdog: probe -> kill -> spawn."""

import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mt5_watchdog as wd

NETSTAT = """  TCP    127.0.0.1:53190        0.0.0.0:0              LISTENING       10572
  TCP    127.0.0.1:53190        127.0.0.1:50166        FIN_WAIT_2      11604
  TCP    127.0.0.1:54321        0.0.0.0:0              LISTENING       999"""


def test_port_owner_parses_netstat():
    fake = lambda cmd: type("R", (), {"stdout": NETSTAT})()
    assert wd.port_owner(fake) == 10572


def test_port_owner_dead_when_unbound():
    fake = lambda cmd: type("R", (), {"stdout": "  TCP    127.0.0.1:54321        0.0.0.0:0              LISTENING       999"})()
    assert wd.port_owner(fake) == wd.DEAD_PID


def test_kill_noop_on_dead_pid():
    calls = []
    wd.kill(wd.DEAD_PID, runner=lambda cmd: calls.append(cmd))
    assert calls == []


def test_main_spawns_when_healthy():
    orig = (wd.healthy, wd.kill, wd.spawn)
    try:
        wd.healthy = lambda: True
        actions = []
        wd.kill = lambda pid, runner=None: actions.append(("kill", pid))
        wd.spawn = lambda runner=None: actions.append(("spawn",))
        assert wd.main() == 0
        assert actions == []
    finally:
        wd.healthy, wd.kill, wd.spawn = orig


def test_probe_targets_ping_quickly():
    """/ping with a SHORT timeout: a busy bridge (requests queued behind
    the MT5 lock) can never look wedged, and a genuine wedge is detected
    in seconds — not the old 45 s /account probe plus a per-minute retry
    (2026-10-19 weekend freeze stayed undetected for hours)."""
    assert wd.PROBE_PATH == "/ping"
    assert wd.PROBE_TIMEOUT <= 10


def test_healthy_probes_ping_url():
    calls = []

    class _Resp:
        status = 200

        def __enter__(self):
            return self

        def __exit__(self, *args):
            return False

    orig = wd.urllib.request.urlopen
    try:
        wd.urllib.request.urlopen = \
            lambda url, timeout: (calls.append((url, timeout)) or _Resp())
        assert wd.healthy() is True
    finally:
        wd.urllib.request.urlopen = orig
    assert calls == [(f"http://127.0.0.1:{wd.PORT}/ping", wd.PROBE_TIMEOUT)]


def test_main_kills_then_spawns_when_wedged():
    orig = (wd.healthy, wd.port_owner, wd.kill, wd.spawn)
    try:
        wd.healthy = lambda: False
        wd.port_owner = lambda: 4242
        actions = []
        wd.kill = lambda pid, runner=None: actions.append(("kill", pid))
        wd.spawn = lambda runner=None: actions.append(("spawn",))
        assert wd.main() == 0
        assert actions == [("kill", 4242), ("spawn",)]
    finally:
        wd.healthy, wd.port_owner, wd.kill, wd.spawn = orig


if __name__ == "__main__":
    for name, fn in sorted(list(globals().items())):
        if name.startswith("test_") and callable(fn):
            fn()
            print(f"  ok  {name}")
    print("all watchdog tests passed")
