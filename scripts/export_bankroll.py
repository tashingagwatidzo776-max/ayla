"""Export the app's growth-bankroll history for the trend page.

Reads the persistent trade store (%APPDATA%/tf/data/trades.json), reduces
each account's growth trades to a per-day closing bankroll (opening bankroll
of the day + that day's growth P/L, mirroring the GrowthSessionEngine's daily
StartBudget reset), and writes docs/growth-bankroll.csv:

    epoch_seconds,account,bankroll

The Pages deploy copies this file into the site and the trend chart plots it
on the money axis, next to nightly coverage. Run it on the machine where the
app trades (the store is local); commit the result so CI can publish it:

    python scripts/export_bankroll.py            # real store
    python scripts/export_bankroll.py -o /tmp/x.csv --store DIR
"""
import argparse
import json
import os
import sys
from datetime import datetime, timezone

DEFAULT_STORE = os.path.join(
    os.environ.get("APPDATA", os.path.expanduser("~")), "tf", "data", "trades.json")
DEFAULT_OUT = os.path.join(os.path.dirname(__file__), "..", "docs", "growth-bankroll.csv")


def settled_epoch(t: dict) -> int:
    """SettledAt as a unix epoch; the store serializes it as an ISO string."""
    v = t.get("SettledAt")
    if isinstance(v, (int, float)):
        return int(v)
    dt = datetime.fromisoformat(str(v))
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return int(dt.timestamp())


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--store", default=DEFAULT_STORE, help="path to trades.json")
    ap.add_argument("-o", "--out", default=DEFAULT_OUT, help="output CSV path")
    args = ap.parse_args()

    try:
        with open(args.store, encoding="utf-8") as f:
            trades = json.load(f)
    except FileNotFoundError:
        print(f"no trade store at {args.store} - nothing to export", file=sys.stderr)
        return 0  # not an error: the chart simply has no money axis yet
    except (json.JSONDecodeError, OSError) as e:
        print(f"cannot read trade store {args.store}: {e}", file=sys.stderr)
        return 1

    # Only settled growth trades count toward the bankroll curve; the runner
    # applies exactly these to its session engine.
    growth = [t for t in trades
              if (t.get("Source") == "Growth")
              and t.get("Outcome") in ("Won", "Lost", "Sold", "Cancelled")
              and t.get("AccountName")]

    per_day = {}  # (account, day) -> [profit sum, last settled epoch]
    for t in growth:
        epoch = settled_epoch(t)
        day = datetime.fromtimestamp(epoch, tz=timezone.utc).strftime("%Y-%m-%d")
        key = (t["AccountName"], day)
        entry = per_day.setdefault(key, [0.0, 0])
        entry[0] += float(t.get("Profit", 0))
        entry[1] = max(entry[1], epoch)

    # Daily StartBudget reset: each day opens at the session's opening
    # bankroll, so the daily delta is exactly that day's summed P/L. The
    # export records the closing bankroll per day, compounding across days
    # from a base of 0 (the store holds deltas, not absolute balances).
    closing = {}  # account -> (running bankroll, last epoch)
    rows = []
    for (acct, _day), (pnl, epoch) in sorted(
            per_day.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[1][1])):
        bank, last_epoch = closing.get(acct, (0.0, 0))
        bank = round(bank + pnl, 2)
        closing[acct] = (bank, max(last_epoch, epoch))
        rows.append((max(last_epoch, epoch), acct, bank))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    with open(args.out, "w", newline="", encoding="utf-8") as f:
        f.write("epoch_seconds,account,bankroll\n")
        for epoch, acct, bank in rows:
            f.write(f"{epoch},{acct},{bank}\n")
    print(f"wrote {len(rows)} bankroll points for {len(closing)} account(s) to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
