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

The spawn honors data/mt5-bridge.json (the same terminal path + port the
app publishes), so the watchdog, sidecar and app all target the SAME MT5
install - with two MT5 installs on one machine, "whatever initialize()
finds first" is not good enough for an order-routing path (a sidecar that
attaches to the wrong install reports -6 'Authorization failed'). When the
config is absent or unreadable the sidecar falls back to auto-discovery.

De-elevation note: MT5 refuses to talk to an elevated client
(-6 'Terminal: Authorization failed'), so this watchdog MUST run as a
least-privilege scheduled task; the detached sidecar it spawns then
inherits the same non-elevated token and can attach.

Worst-case outage is therefore probe timeout + one minute.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.request
from datetime import datetime

PORT = 53190
PROBE_TIMEOUT = 45  # data routes queue behind the request lock; a burst can
#                    legitimately take tens of seconds. Only a WEDGE (hung
#                    native MT5 call) never answers at all.
PROBE_PATH = "/account"  # an MT5-touching route: proves the IPC is responsive,
#                          which is exactly what the app needs to work
SIDECAR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bridge", "mt5_sidecar.py")
DEAD_PID = 0  # sentinel: port not bound at all
DATA_DIR_ENV = "TF_DATA_DIR"  # mirrors SettingsService.DataDirEnvVar
CONFIG_NAME = "mt5-bridge.json"


def data_dir() -> str:
    """The shared data dir the app publishes mt5-bridge.json into:
    TF_DATA_DIR when set, else %APPDATA%\\tf\\data."""
    override = os.environ.get(DATA_DIR_ENV, "").strip()
    if override:
        return override
    return os.path.join(os.environ.get("APPDATA", ""), "tf", "data")


def bridge_config() -> dict:
    """terminalPath + port from data/mt5-bridge.json; {} when absent/corrupt."""
    try:
        with open(os.path.join(data_dir(), CONFIG_NAME), encoding="utf-8") as fh:
            cfg = json.load(fh)
            return cfg if isinstance(cfg, dict) else {}
    except Exception:  # noqa: BLE001 - config is best-effort, never fatal
        return {}


def log_path() -> str:
    return os.path.join(data_dir(), "logs", "mt5-watchdog-py.log")


def sidecar_log_path() -> str:
    return os.path.join(data_dir(), "logs", "mt5-sidecar.log")


def log(line: str) -> None:
    """Append one timestamped line; a logging failure must never break recovery."""
    try:
        path = log_path()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now():%Y-%m-%d %H:%M:%S} {line}\n")
    except Exception:  # noqa: BLE001
        pass


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
    run = runner or (lambda cmd: subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace"))
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


def spawn_command(python: str) -> list[str]:
    """The sidecar argv: config'd terminal path + port when published."""
    cfg = bridge_config()
    cmd = [python, SIDECAR]
    terminal = str(cfg.get("terminalPath") or "").strip()
    if terminal:
        cmd += ["--terminal", terminal]
    try:
        port = int(cfg.get("port") or PORT)
    except (TypeError, ValueError):
        port = PORT
    cmd.append(str(port))
    return cmd


def spawn(runner=None) -> None:
    python = sys.executable
    cmd = spawn_command(python)
    log(f"spawning sidecar: {' '.join(cmd)}")
    if runner is not None:
        runner(cmd)
        return
    # Detached so the watchdog can exit while the sidecar lives. Redirect
    # stdout/stderr to a log: a detached pythonw.exe has no console, so an
    # attach failure (e.g. "-6 Terminal: Authorization failed") would
    # otherwise be invisible - exactly the silent failure that let the
    # bridge stay down for days.
    try:
        os.makedirs(os.path.dirname(sidecar_log_path()), exist_ok=True)
        out = open(sidecar_log_path(), "ab")
    except Exception:  # noqa: BLE001
        out = subprocess.DEVNULL
    subprocess.Popen(cmd, creationflags=0x00000008, stdout=out, stderr=subprocess.STDOUT)


def main() -> int:
    if healthy():
        return 0
    log("sidecar unhealthy - killing port owner and respawning")
    kill(port_owner())
    spawn()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
