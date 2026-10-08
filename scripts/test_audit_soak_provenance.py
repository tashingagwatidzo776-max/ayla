#!/usr/bin/env python3
"""Unit tests for scripts/audit_soak_provenance.py.

The audit decides whether paper-soak credits are real evidence for the
real-money go-live gate, so its verdicts must be exact and must fail closed:
an unparseable timestamp is contamination, a short identical-signal run on a
live tape is not, and absence of tick coverage is never silently read as
either.

Run: python scripts/test_audit_soak_provenance.py   (exit 0 = all pass)
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "audit_soak_provenance.py"

sys.path.insert(0, str(HERE))
import audit_soak_provenance as audit  # noqa: E402


# ── market_open: the calendar half of the verdict ──────────────────────────

def test_saturday_is_closed():
    assert audit.market_open("2026-10-03T12:00:00+00:00") == (False, "Saturday")


def test_sunday_before_open_is_closed():
    ok, why = audit.market_open("2026-10-04T20:59:00+00:00")
    assert not ok and "Sunday" in why


def test_sunday_at_open_is_open():
    assert audit.market_open("2026-10-04T21:00:00+00:00")[0] is True


def test_friday_after_close_is_closed():
    ok, why = audit.market_open("2026-10-02T22:00:00+00:00")
    assert not ok and "Friday" in why


def test_friday_just_before_close_is_open():
    assert audit.market_open("2026-10-02T21:59:00+00:00")[0] is True


def test_midweek_session_is_open():
    assert audit.market_open("2026-09-30T09:30:00+00:00")[0] is True


def test_unparseable_timestamp_fails_closed():
    ok, why = audit.market_open("not-a-time")
    assert not ok
    assert "unparseable" in why


def test_naive_timestamp_is_read_as_utc_not_local():
    # The journal writes an offset, but a naive stamp must not be re-read in
    # the machine's own zone: 00:30 naive-as-local is a Sunday afternoon in
    # UTC terms only if you get the conversion wrong. Pin both directions.
    naive_late_saturday = audit.market_open("2026-10-03T00:30:00")
    assert naive_late_saturday[0] is False, "naive Saturday must stay closed"
    assert audit.market_open("2026-09-30T09:30:00")[0] is True


# ── signal_run_at: the stall fingerprint ───────────────────────────────────

def _runs():
    # 1865-style index: symbol -> {timestamp: identical-run length}
    return {"XAUUSD": {
        "2026-10-03T11:31:54.3044829+00:00": 66,
        "2026-10-03T11:33:00.0000000+00:00": 1,
        "2026-10-03T11:40:00.0000000+00:00": 4,
    }}


def test_signal_run_matches_within_slop():
    # soak credit is written tens of ms after its signal
    assert audit.signal_run_at(_runs(), "2026-10-03T11:31:54.3383972+00:00",
                               "XAUUSD") == 66


def test_signal_run_zero_when_nothing_nearby():
    assert audit.signal_run_at(_runs(), "2026-10-03T12:31:54+00:00",
                               "XAUUSD") == 0


def test_signal_run_zero_for_unknown_symbol():
    assert audit.signal_run_at(_runs(), "2026-10-03T11:31:54+00:00",
                               "GBPUSD") == 0


# ── classify: the combined verdict ─────────────────────────────────────────

def _hours(prices, day="2026-09-30", sym="XAUUSD", hour="09"):
    return {(day, sym, hour): set(prices)}


def test_live_when_prices_move_in_the_hour():
    v, why = audit.classify("2026-09-30T09:15:00+00:00", "XAUUSD",
                            _hours([(1.1, 1.2), (1.1, 1.3)]))
    assert v == "live"
    assert "2 distinct" in why


def test_frozen_when_one_price_all_hour():
    v, why = audit.classify("2026-09-30T09:15:00+00:00", "XAUUSD",
                            _hours([(1.1, 1.2)]))
    assert v == "frozen"
    assert "one price" in why


def test_closed_beats_any_tick_evidence():
    # Saturday: the tick file may well hold one price, but the calendar is
    # the first and decisive word.
    v, why = audit.classify("2026-10-03T12:00:00+00:00", "XAUUSD", None)
    assert v == "closed"
    assert why == "Saturday"


def test_uncovered_without_fingerprint_is_not_judged():
    v, why = audit.classify("2026-09-30T09:15:00+00:00", "XAUUSD", {})
    assert v == "uncovered"
    assert "no coverage" in why


def test_uncovered_short_identical_run_stays_uncovered():
    # vol-breakout emits 4-5 identical conf-0.60 signals on a LIVE tape:
    # a short run must never be escalated into contamination.
    runs = {"XAUUSD": {"2026-09-30T09:15:00.0000000+00:00": 5}}
    v, why = audit.classify("2026-09-30T09:15:01+00:00", "XAUUSD", {}, runs)
    assert v == "uncovered"
    assert "run 5" in why


def test_uncovered_long_identical_run_is_frozen():
    runs = {"XAUUSD": {"2026-09-30T09:15:00.0000000+00:00": 66}}
    v, why = audit.classify("2026-09-30T09:15:01+00:00", "XAUUSD", {}, runs)
    assert v == "frozen"
    assert "66x" in why


def test_threshold_is_longer_than_a_busy_live_tape():
    # the fingerprint must not fire on the 4-5 signal vol-breakout bursts
    assert audit.STALL_RUN_LENGTH > 5


def test_naive_journal_timestamp_still_classifies():
    # a naive stamp must be read as UTC, so a Saturday stays closed even
    # without an offset (the local-machine zone must not decide evidence)
    assert audit.classify("2026-10-03T12:00:00", "XAUUSD", {})[0] == "closed"


# ── audit(): bucketing ─────────────────────────────────────────────────────

def test_audit_buckets_each_verdict():
    events = [
        ("2026-10-03T12:00:00+00:00", "XAUUSD", 1, 10),   # Saturday
        ("2026-09-30T09:15:00+00:00", "XAUUSD", 2, 10),   # live
        ("2026-09-30T09:16:00+00:00", "EURUSD", 3, 10),   # uncovered
    ]
    hours = {(("2026-09-30", "XAUUSD", "09")): {(1.1, 1.2), (1.1, 1.3)}}
    buckets = audit.audit(events, hours, {})
    assert len(buckets["closed"]) == 1
    assert len(buckets["live"]) == 1
    assert len(buckets["uncovered"]) == 1
    assert "frozen" not in buckets


# ── end to end, against a synthetic journal + tick archive ─────────────────

def _write_case(tmp: Path, credit_ts: str, symbol="XAUUSD",
                tick_prices=((1.1, 1.2), (1.1, 1.3)), with_ticks=True):
    """Builds a minimal data dir with one soak credit and returns its args."""
    journal = tmp / "journal"
    journal.mkdir(parents=True, exist_ok=True)
    day = credit_ts[:10].replace("-", "")
    rows = [
        {"Timestamp": credit_ts, "Category": "FX_MODE",
         "Details": f"paper soak 1/10 on {symbol}: "
                    f'{{"Symbol":"{symbol}","Seen":1,"Required":10}}'},
        {"Timestamp": credit_ts, "Category": "FX_SIGNAL",
         "Details": f"hurst-trend -> Sell conf 0.80 - Hurst 0.91: "
                    f'{{"Symbol":"{symbol}","Alpha":"hurst-trend",'
                    f'"Direction":1,"Confidence":0.8,"Reason":"Hurst 0.91"}}'},
    ]
    (journal / f"journal_{day}.jsonl").write_text(
        "\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")

    if with_ticks:
        ticks = tmp / "ticks" / "mt5"
        ticks.mkdir(parents=True, exist_ok=True)
        hour = credit_ts[11:13]
        epoch_ms = int(_epoch(credit_ts) * 1000)
        lines = [json.dumps({"b": b, "a": a, "t": epoch_ms + i * 1000})
                 for i, (b, a) in enumerate(tick_prices)]
        (ticks / f"{symbol}_{day}.jsonl").write_text(
            "\n".join(lines) + "\n", encoding="utf-8")
        assert hour  # hour is implied by the tick's own timestamp
    return ["--data", str(tmp)]


def _epoch(ts):
    from datetime import datetime, timezone
    dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.timestamp()


def _run(args):
    proc = subprocess.run([sys.executable, str(SCRIPT)] + args,
                          capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    return proc.stdout + proc.stderr, proc.returncode


def test_e2e_live_credit_passes():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(_write_case(Path(tmp), "2026-09-30T09:15:00+00:00"))
        assert rc == 0, out
        assert "OK" in out


def test_e2e_saturday_credit_fails():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(_write_case(Path(tmp), "2026-10-03T12:00:00+00:00"))
        assert rc == 1, out
        assert "Saturday" in out
        assert "CONTAMINATED" in out


def test_e2e_frozen_feed_fails():
    with tempfile.TemporaryDirectory() as tmp:
        args = _write_case(Path(tmp), "2026-09-30T09:15:00+00:00",
                           tick_prices=((1.1, 1.2),))
        out, rc = _run(args)
        assert rc == 1, out
        assert "frozen" in out


def test_e2e_no_journal_is_usage_error():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(["--data", str(Path(tmp) / "missing")])
        assert rc == 2, out


def test_e2e_empty_journal_is_ok():
    with tempfile.TemporaryDirectory() as tmp:
        (Path(tmp) / "journal").mkdir(parents=True)
        out, rc = _run(["--data", str(tmp)])
        assert rc == 0, out
        assert "nothing to audit" in out


def test_e2e_uncovered_does_not_fail():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(_write_case(Path(tmp), "2026-09-30T09:15:00+00:00",
                                   with_ticks=False))
        assert rc == 0, out
        assert "uncovered" in out


def test_e2e_since_excludes_older_contamination():
    # Saturday credit on 2026-10-03, but we only audit from the next day.
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(_write_case(Path(tmp), "2026-10-03T12:00:00+00:00")
                       + ["--since", "2026-10-04"])
        assert rc == 0, out
        assert "OK" in out


def test_e2e_since_includes_contamination():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(_write_case(Path(tmp), "2026-10-03T12:00:00+00:00")
                       + ["--since", "2026-10-03"])
        assert rc == 1, out
        assert "Saturday" in out


def test_e2e_since_must_be_iso_date():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = _run(_write_case(Path(tmp), "2026-09-30T09:15:00+00:00")
                       + ["--since", "not-a-date"])
        assert rc == 2, out
        assert "YYYY-MM-DD" in out


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except AssertionError as e:
            failed += 1
            print(f"FAIL {t.__name__}: {e}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
