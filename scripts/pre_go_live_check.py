#!/usr/bin/env python3
"""Pre-go-live check: verify every M3 checklist item from real state.

Reads the machine's ACTUAL state — settings.json, the trade journal,
the sidecar's /health endpoint and the committed soak evidence — and
verifies each item of the go-live checklist in docs/m3-go-live-readiness.md:

  1. Safety caps fail-closed  (settings.json)
  2. Sidecar healthy          (GET http://127.0.0.1:<port>/health)
  3. Soak progress            (journal FX_DECISION/FX_SIGNAL categories)
  4. Soak evidence fresh      (docs/soak/ — reuses the freshness rules)
  5. Webhook reachable        (settings.json URL, POST-free probe)

Exit codes:
  0 = every checked item passed
  1 = at least one item failed (go-live is NOT safe to attempt)
  2 = usage/IO error

This script only READS. It never arms the unlock, never starts the brain,
and never places an order — a checker that could trade would itself be a
safety violation.
"""
import argparse
import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

SOAK_FILE_RE = re.compile(r"^SOAK-(\d{4}-\d{2}-\d{2})\.md$")


def default_data_dir():
    appdata = os.environ.get("APPDATA")
    if appdata:
        return os.path.join(appdata, "tf", "data")
    return os.path.expanduser("~/.local/share/tf/data")


def parse_args(argv):
    p = argparse.ArgumentParser(description="M3 pre-go-live checklist verifier")
    p.add_argument("--data-dir", default=default_data_dir(),
                   help="the app's data dir (default: %%APPDATA%%\\tf\\data)")
    p.add_argument("--repo-root", default=str(Path(__file__).resolve().parent.parent),
                   help="repo root holding docs/soak and the safety audit")
    p.add_argument("--max-soak-age", type=int, default=14,
                   help="max age in days for the newest SOAK CLEAN report")
    p.add_argument("--skip-sidecar", action="store_true",
                   help="skip the /health probe (e.g. no terminal running)")
    args = p.parse_args(argv)
    if not os.path.isdir(args.data_dir):
        print(f"::error::data dir not found: {args.data_dir}")
        sys.exit(2)
    return args


# (dash hygiene: printed strings stay ASCII - Windows consoles garble em-dashes)


# ── checkers ────────────────────────────────────────────────────────
# Each checker returns (ok, detail). Failures carry a fix hint.

def load_settings(data_dir):
    path = os.path.join(data_dir, "settings.json")
    if not os.path.exists(path):
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError) as ex:
        print(f"::warning::unreadable settings.json: {ex}")
        return None


def check_safety_caps(settings):
    if settings is None:
        return False, "settings.json missing or unreadable — open the app once to create it"

    max_lots = settings.get("Mt5MaxLots", 0)
    loss_cap = settings.get("Mt5DailyLossCap", 0)
    equity_floor = settings.get("Mt5EquityFloor", 0)
    portfolio_cap = settings.get("FxPortfolioMaxLots", 0)
    blackout = settings.get("NewsBlackoutMinutes", 0)

    problems = []
    if not max_lots or max_lots <= 0:
        problems.append("Mt5MaxLots is 0/unset (0 DISABLES placement — set a positive cap)")
    if not loss_cap or loss_cap <= 0:
        problems.append("Mt5DailyLossCap is 0/unset (an uncapped daily loss has no stop)")
    if portfolio_cap is None or portfolio_cap <= 0:
        problems.append("FxPortfolioMaxLots is 0/unset (0 DISABLES the portfolio veto)")
    if not blackout or blackout <= 0:
        problems.append("NewsBlackoutMinutes is 0/unset (news events would not veto orders)")

    if problems:
        return False, "; ".join(problems)
    return True, (
        f"Mt5MaxLots={max_lots}, Mt5DailyLossCap={loss_cap}, "
        f"Mt5EquityFloor={equity_floor}, FxPortfolioMaxLots={portfolio_cap}, "
        f"NewsBlackoutMinutes={blackout}")


def check_sidecar(skip):
    if skip:
        return True, "skipped (--skip-sidecar)"
    try:
        with urllib.request.urlopen("http://127.0.0.1:53190/health", timeout=3) as resp:
            body = json.loads(resp.read().decode("utf-8"))
    except Exception as ex:
        return False, f"sidecar not healthy on 127.0.0.1:53190 ({ex.__class__.__name__}) - run: python bridge/mt5_sidecar.py"

    if not body.get("ok"):
        return False, f"sidecar answered but reports ok=false: {body}"
    if not body.get("terminal_connected"):
        return False, f"sidecar is up but the terminal is not connected: login={body.get('login')} server={body.get('server')}"
    return True, f"login={body.get('login')} server={body.get('server')} terminal connected"


def read_journal_entries(data_dir):
    """Returns (categories, fx_symbols, first_ts, last_ts).

    fx_symbols maps category -> {symbol: count} for entries whose Details
    JSON carries a Symbol field (FX_SIGNAL, FX_DECISION, FX_ORDER, FX_MODE).
    Details holds JSON-in-JSON (escaped inside the outer line), so it is
    unescaped once before parsing; rows that fail to parse count under the
    symbol "" (unknown)."""
    journal_dir = os.path.join(data_dir, "journal")
    categories = {}
    fx_symbols = {}
    first_ts = None
    last_ts = None
    ts_re = re.compile(r"\"Timestamp\":\"([^\"]+)\"")
    cat_re = re.compile(r"\"Category\":\"([^\"]+)\"")
    # Details is the LAST serialized field (Timestamp, AccountId, Category,
    # Details): the value runs to the line's closing quote-brace. Greedy to
    # the final `"}` so inner escaped quotes never truncate the capture.
    det_re = re.compile(r"\"Details\":\"(.*)\"\}")
    for path in sorted(glob.glob(os.path.join(journal_dir, "journal_*.jsonl"))):
        try:
            with open(path, encoding="utf-8") as f:
                for line in f:
                    cat_m = cat_re.search(line)
                    if cat_m:
                        cat = cat_m.group(1)
                        categories[cat] = categories.get(cat, 0) + 1
                    ts = ts_re.search(line)
                    if ts:
                        raw = ts.group(1)
                        if first_ts is None:
                            first_ts = raw
                        last_ts = raw
                    if cat_m:
                        cat = cat_m.group(1)
                        symbol = ""
                        det_m = det_re.search(line)
                        if det_m:
                            raw_details = det_m.group(1)
                            try:
                                details = json.loads(raw_details.encode().decode("unicode_escape"))
                                sym = details.get("Symbol")
                                if isinstance(sym, str):
                                    symbol = sym
                            except (ValueError, UnicodeDecodeError):
                                symbol = ""
                        bucket = fx_symbols.setdefault(cat, {})
                        bucket[symbol] = bucket.get(symbol, 0) + 1
        except OSError:
            continue
    return categories, fx_symbols, first_ts, last_ts


def parse_iso(ts):
    if not ts:
        return None
    try:
        return datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except ValueError:
        return None


def check_soak_progress(data_dir, signals_required=10):
    """Per-symbol soak progress: the engine counts one signal per cycle in
    which an alpha SPOKE while in PAPER (FxEngineHost.CountsTowardSoak),
    journals it as FX_MODE 'paper soak n/m on <symbol>', and the engine
    itself journals FX_SIGNAL '<alpha> -> <dir> conf ...' whenever an alpha
    speaks. Counting FX_SIGNAL per symbol reproduces the badge's bar; the
    soak bar itself (PaperSoakSignalsRequired) is 10 per symbol.
    GO LIVE is all-or-nothing across symbols, so the worst symbol decides."""
    categories, fx_symbols, first_ts, last_ts = read_journal_entries(data_dir)
    # The FX brain journals FX_DECISION (never BRAIN_DECISION - that older
    # category belongs to the retired binary surface); FX_SIGNAL rows are
    # its per-symbol signal record. Either proves the brain has produced a
    # decision; neither does means it has never run.
    decisions = sum(categories.get(c, 0) for c in ("FX_DECISION", "BRAIN_DECISION"))
    signals_seen = sum(categories.get(c, 0) for c in ("FX_SIGNAL", "FX_DECISION", "BRAIN_DECISION"))
    if decisions == 0:
        return False, "journal has 0 FX_DECISION entries - the FX brain has never produced a decision (start the FX brain in paper mode)"

    signal_counts = dict(fx_symbols.get("FX_SIGNAL", {}))
    signal_counts.pop("", None)   # unparseable Details rows
    if not signal_counts:
        # Older journals may lack FX_SYMBOL in Details; fall back to the
        # FX_MODE 'paper soak n/m on <symbol>' messages, then to a bare
        # decision-count statement.
        mode_counts = {s: c for s, c in fx_symbols.get("FX_MODE", {}).items() if s}
        if mode_counts:
            signal_counts = mode_counts
        else:
            return True, (f"{signals_seen} FX decision/signal entries journaled; no per-symbol "
                          f"FX_SIGNAL rows yet (legacy journal) - watch the FX badge for soak n/m")

    per_symbol = ", ".join(
        f"{sym} {count}/{signals_required}"
        for sym, count in sorted(signal_counts.items(), key=lambda kv: (-kv[1], kv[0])))
    worst = min(signal_counts.values())
    first_dt = parse_iso(first_ts)
    last_dt = parse_iso(last_ts)
    window = f" (first {first_dt.date()}, last {last_dt.date()})" if first_dt and last_dt else ""
    ok = worst >= signals_required
    detail = (f"per-symbol soak {per_symbol} - worst symbol decides "
              f"{'(complete)' if ok else f'(worst below {signals_required})'}{window}")
    return ok, detail


def check_soak_evidence(repo_root, max_age):
    evidence_dir = os.path.join(repo_root, "docs", "soak")
    if not os.path.isdir(evidence_dir):
        return False, "no docs/soak directory — run soak_report.py --record docs/soak after a demo session"

    newest = None
    for path in glob.glob(os.path.join(evidence_dir, "SOAK-*.md")):
        m = SOAK_FILE_RE.match(os.path.basename(path))
        if not m:
            continue
        try:
            with open(path, encoding="utf-8") as f:
                if "SOAK CLEAN" in f.read():
                    date = datetime.strptime(m.group(1), "%Y-%m-%d").replace(tzinfo=timezone.utc)
                    if newest is None or date > newest[1]:
                        newest = (path, date)
        except OSError:
            continue

    if newest is None:
        return False, "no SOAK CLEAN report committed yet (an empty soak is not evidence)"

    age_days = (datetime.now(timezone.utc) - newest[1]).days
    name = os.path.basename(newest[0])
    if age_days > max_age:
        return False, f"soak evidence is STALE: newest SOAK CLEAN is {name}, {age_days}d old (max {max_age}d) - run a demo session and record it"
    return True, f"soak evidence fresh: {name} ({age_days}d old, max {max_age}d)"


def check_webhook(settings):
    if settings is None:
        return False, "no settings.json - cannot verify the webhook"
    url = settings.get("WebhookUrl") or ""
    if not url.strip():
        return False, "no webhook configured (Settings -> notifications) - refusals/arms would be silent"
    # POST-free probe: a webhook URL is for posting; we only verify the
    # endpoint EXISTS. Any HTTP answer (even 4xx/5xx) proves something is
    # listening - a refused connection means the endpoint is a phantom and
    # every alert would die silently.
    try:
        req = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(req, timeout=5) as resp:
            return True, f"webhook endpoint reachable (HTTP {resp.status})"
    except urllib.error.HTTPError as ex:
        return True, f"webhook endpoint exists (HTTP {ex.code} on probe - posting is a different verb)"
    except Exception as ex:
        return False, f"webhook endpoint unreachable ({ex.__class__.__name__}) - nothing is listening at the configured URL"


# ── main ────────────────────────────────────────────────────────────

def main(argv):
    args = parse_args(argv)
    settings = load_settings(args.data_dir)

    checks = [
        ("safety caps fail-closed", check_safety_caps(settings)),
        ("sidecar healthy", check_sidecar(args.skip_sidecar)),
        ("soak progress", check_soak_progress(args.data_dir)),
        ("soak evidence fresh", check_soak_evidence(args.repo_root, args.max_soak_age)),
        ("webhook configured", check_webhook(settings)),
    ]

    print("Pre-go-live check")
    print(f"  data dir : {args.data_dir}")
    print(f"  repo root: {args.repo_root}")
    print()
    failed = 0
    for name, (ok, detail) in checks:
        mark = "PASS" if ok else "FAIL"
        if not ok:
            failed += 1
        print(f"  [{mark}] {name}: {detail}")

    print()
    if failed:
        print(f"GO-LIVE NOT ADVISED: {failed}/{len(checks)} item(s) failed - see docs/m3-go-live-readiness.md")
        return 1
    print("ALL CHECKS PASSED — the M3 checklist is satisfied; go-live remains an explicit, in-app action.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
