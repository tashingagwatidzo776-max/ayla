"""External watchdog for the MT5 bridge sidecar.

A wedged sidecar (hung native MT5 IPC call holding the request lock)
can never answer /health again, yet it still holds port 53190 - so the
sidecar's own already_running() probe cannot distinguish 'wedged' from
'healthy', and any second bind fails with 10048. Only an outside
process can recover. This watchdog, run every minute by the scheduled
task 'DongGfx MT5 bridge watchdog':

1. probes /health - an answer (any status) means alive, do nothing;
2. on no answer: kills whoever holds the port, then starts a fresh
   sidecar detached.

Worst-case outage is therefore probe timeout + one minute.
"""

from __future__ import annotations

import os
import subprocess
import sys
import time
import urllib.request

PORT = 53190
PROBE_TIMEOUT = 45  # data routes queue behind the request lock; a burst can
#                    legitimately take tens of seconds. Only a WEDGE (hung
#                    native MT5 call) never answers at all.
PROBE_PATH = "/account"  # an MT5-touching route: proves the IPC is responsive,
#                          which is exactly what the app needs to work
SIDECAR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bridge", "mt5_sidecar.py")
DEAD_PID = 0  # sentinel: port not bound at all


def healthy(timeout: float = PROBE_TIMEOUT, path: str = PROBE_PATH) -> bool:
    """True when something answers the probe (any HTTP status): the process,
    its HTTP stack, and the MT5 IPC are all alive."""
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{PORT}{path}", timeout=timeout) as resp:
            return resp.status != 0
    except Exception:  # noqa: BLE001 - any failure means not provably healthy
        return False


def port_owner(runner=None) -> int:
    """PID listening on the port, or DEAD_PID when nothing is bound."""
    run = runner or (lambda cmd: subprocess.run(cmd, capture_output=True, text=True))
    out = run(["netstat", "-ano"]).stdout
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 5 and parts[1] == f"127.0.0.1:{PORT}" and parts[3] == "LISTENING":
            return int(parts[4])
    return DEAD_PID


def kill(pid: int, runner=None) -> None:
    if pid == DEAD_PID:
        return
    run = runner or (lambda cmd: subprocess.run(cmd, capture_output=True))
    run(["taskkill", "/PID", str(pid), "/F"])
    time.sleep(2)


def spawn(runner=None) -> None:
    python = sys.executable
    run = runner or (lambda cmd: subprocess.Popen(cmd, creationflags=0x00000008))
    run([python, SIDECAR])


def main() -> int:
    if healthy():
        return 0
    kill(port_owner())
    spawn()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
