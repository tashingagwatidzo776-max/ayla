#!/usr/bin/env python3
"""Fast unit tests for scripts/export_bankroll.py's reduction.

The weekly bankroll drill exercises the backfill end-to-end in CI, but that
runs on Saturdays and only on windows-latest. These tests run anywhere with
plain python3 in milliseconds, so a drift between the Python reduction and
the C# exporter semantics is caught by any quick pre-push check, not just by
the drill.

Run: python scripts/test_export_bankroll.py   (exit 0 = all pass)
"""
import csv
import io
import json
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "export_bankroll.py"


def run_export(trades, extra_args=None):
    """Runs the script on a synthetic store; returns (rows, stdout, returncode)."""
    with tempfile.TemporaryDirectory() as tmp:
        store = Path(tmp) / "trades.json"
        out = Path(tmp) / "growth-bankroll.csv"
        store.write_text(json.dumps(trades), encoding="utf-8")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--store", str(store), "-o", str(out)]
            + (extra_args or []),
            capture_output=True, text=True)
        if proc.returncode != 0:
            return None, proc.stdout + proc.stderr, proc.returncode
        with open(out, newline="", encoding="utf-8") as f:
            return list(csv.DictReader(f)), proc.stdout, 0


def noon(days_ago, base=None):
    """Noon-UTC ISO stamp N days before base (default: now)."""
    now = int((base or datetime.now(timezone.utc)).timestamp())
    return datetime.fromtimestamp(
        (now // 86400 - days_ago) * 86400 + 12 * 3600, tz=timezone.utc
    ).isoformat()


def growth_trade(profit, days_ago, account="Alpha", outcome="Won", source="Growth"):
    return {
        "SettledAt": noon(days_ago), "Source": source, "Outcome": outcome,
        "AccountName": account, "Profit": profit,
    }


def test_single_account_compounds():
    rows, out, code = run_export([
        growth_trade(+0.90, 2),
        growth_trade(-1.00, 1, outcome="Lost"),
    ])
    assert code == 0, out
    assert len(rows) == 2
    assert rows[0]["account"] == "Alpha" and float(rows[0]["bankroll"]) == 0.90
    assert float(rows[1]["bankroll"]) == -0.10
    print("  ok: single account compounds across days")


def test_two_accounts_are_independent():
    rows, out, code = run_export([
        growth_trade(+0.90, 2, account="Alpha"),
        growth_trade(+1.44, 2, account="Beta"),
        growth_trade(-0.25, 1, account="Alpha", outcome="Sold"),
    ])
    assert code == 0, out
    alpha = [float(r["bankroll"]) for r in rows if r["account"] == "Alpha"]
    beta = [float(r["bankroll"]) for r in rows if r["account"] == "Beta"]
    assert alpha == [0.90, 0.65], alpha
    assert beta == [1.44], beta
    print("  ok: two accounts reduce independently")


def test_unsettleable_trades_excluded():
    rows, out, code = run_export([
        growth_trade(+0.90, 1),
        # Excluded: wrong source, in-flight, unknown outcome, no account.
        growth_trade(+5.00, 1, source="Manual"),
        growth_trade(+0.99, 1, outcome="Open"),
        growth_trade(+1.23, 1, outcome="Unknown"),
        {"SettledAt": noon(1), "Source": "Growth", "Outcome": "Won",
         "Profit": 0.70},  # AccountName missing
    ])
    assert code == 0, out
    assert len(rows) == 1
    assert float(rows[0]["bankroll"]) == 0.90
    print("  ok: unsettled/mis-attributed trades excluded")


def test_missing_store_is_not_an_error():
    with tempfile.TemporaryDirectory() as tmp:
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--store", str(Path(tmp) / "nope.json"),
             "-o", str(Path(tmp) / "out.csv")],
            capture_output=True, text=True)
    assert proc.returncode == 0, proc.stderr
    assert "nothing to export" in proc.stderr
    print("  ok: missing store exits 0 (no money axis yet, not a failure)")


def test_start_budget_seeds_first_row():
    rows, out, code = run_export(
        [growth_trade(+0.90, 1)], extra_args=["--start-budget", "5.00"])
    assert code == 0, out
    assert float(rows[0]["bankroll"]) == 5.90
    print("  ok: --start-budget seeds the opening bankroll")


def test_csv_schema():
    rows, out, code = run_export([growth_trade(+0.90, 1)])
    assert code == 0, out
    assert set(rows[0].keys()) == {"epoch_seconds", "account", "bankroll"}
    assert rows[0]["epoch_seconds"].isdigit()
    print("  ok: CSV schema is epoch_seconds,account,bankroll")


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    print(f"export_bankroll tests: {len(tests)} case(s)")
    for t in tests:
        t()
    print("all export_bankroll tests passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
