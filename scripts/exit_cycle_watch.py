"""Watch the open XAU position through its exit and log the automation's
response: venue flat transition -> book-recovery task action -> brain
watchdog re-arm. Writes %APPDATA%\\tf\\data\\logs\\exit-cycle-watch.log."""
import datetime as dt
import json
import pathlib
import time
import urllib.request

DATA = pathlib.Path.home() / "AppData/Roaming/tf/data"
LOG = DATA / "logs/exit-cycle-watch.log"
RECOVERY = DATA / "logs/book-recovery.log"
BRAIN = DATA / "logs/brain-watchdog.log"
JOURNAL = DATA / "journal/journal_20261006.jsonl"


def log(msg):
    ts = dt.datetime.now(dt.timezone.utc).strftime("%H:%M:%SZ")
    with open(LOG, "a", encoding="utf-8") as fh:
        fh.write(f"{ts} {msg}\n")


def positions():
    try:
        with urllib.request.urlopen("http://127.0.0.1:53190/positions", timeout=6) as r:
            return json.loads(r.read().decode())["positions"]
    except Exception:
        return None   # unknown, not flat


def tail_new(path, state_key):
    """Append any new lines from a watcher log since last seen offset."""
    try:
        pos = state.get(state_key, 0)
        data = path.read_bytes()
        if len(data) > pos:
            chunk = data[pos:].decode("utf-8", errors="replace")
            state[state_key] = len(data)
            for ln in chunk.splitlines():
                if ln.strip():
                    log(f"{path.name}: {ln}")
    except OSError:
        pass


def newest_audit_after(ts_iso):
    try:
        with open(JOURNAL, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "exposure audit" in line:
                    t = json.loads(line)["Timestamp"]
                    if t > ts_iso:
                        return t
    except OSError:
        pass
    return None


state = {"recovery": 0, "brain": 0}
# start offsets at current EOF so we only log NEW lines
for key, p in (("recovery", RECOVERY), ("brain", BRAIN)):
    try:
        state[key] = p.stat().st_size
    except OSError:
        state[key] = 0

# single-instance lock (the scheduled task respawns us every 5 min)
LOCK = DATA / "logs/exit-cycle-watch.pid"
try:
    if LOCK.exists():
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False,
                                               int(LOCK.read_text().strip() or 0))
        if h:
            ctypes.windll.kernel32.CloseHandle(h)
            raise SystemExit(0)   # previous instance alive
    LOCK.write_text(str(__import__("os").getpid()))
except SystemExit:
    raise
except Exception:
    pass   # fail open: an extra watcher is better than none

log("exit-cycle watch started")
was_flat = False
deploy_ts = "2026-10-06T15:10:00"   # epsilon build live from here
while True:
    try:
        pos = positions()
        if pos is None:
            log("positions read failed (bridge blip?)")
        else:
            if pos and not was_flat:
                p0 = pos[0]
                log(f"position open: #{p0['ticket']} {p0['side']} {p0['volume']} "
                    f"{p0['symbol']} @ {p0['price_open']} profit={p0['profit']}")
                was_flat = True
            elif not pos and was_flat:
                log("POSITION EXITED (venue flat)")
                was_flat = False
        tail_new(RECOVERY, "recovery")
        tail_new(BRAIN, "brain")
    except Exception as ex:
        log(f"watch error: {ex}")
    time.sleep(30)
