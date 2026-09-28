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
