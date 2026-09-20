#!/usr/bin/env python3
"""Weekend-expiry simulation: what a 3600s OAuth token does to the app's
connection layer over a market-closed weekend, vs a PAT (no expiry).

Replays AccountConnection/DerivClient's EXACT production state machine:
  - DerivClient reconnect backoff: delay = min(30, 2^min(attempt,5)) s,
    doubled per consecutive rapid (<15 s) drop, 300 s ceiling, ±20% jitter
    (src/DongGfx.Deriv/DerivClient.cs, ScheduleReconnect).
  - AccountConnection circuit breaker: 5 consecutive failures -> 10-minute
    open (src/DongGfx.App/Services/AccountConnection.cs).

Two scenarios per run: the legacy retry loop (pre-401-classification) and
the shipped behavior (a 401 discovery verdict is terminal -- one attempt,
then an actionable stop).

Dry-run only: no sockets, no calls to Deriv. Prints the comparison table
and the numbers quoted in docs/deriv-auth-oauth2.md.

Usage: python scripts/oauth_expiry_simulation.py
"""
from __future__ import annotations

import random

WINDOW_SECONDS = 62 * 3600  # Fri 22:00 UTC -> Mon 00:00 UTC (2026-09-19 records it as 62h)
TOKEN_TTL_SECONDS = 3600    # Deriv OAuth access token lifetime (docs: expires_in 3600)
BREAKER_THRESHOLD = 5
BREAKER_COOLDOWN = 600      # seconds


def simulate(retry_despite_auth_failure: bool, seed: int = 19) -> dict:
    rng = random.Random(seed)
    token_dies_at = float(TOKEN_TTL_SECONDS)  # signed in just before Friday close

    # DerivClient-level backoff state
    attempt = 0
    rapid_drop_streak = 0

    # AccountConnection breaker state
    consecutive_failures = 0
    circuit_open_until = 0.0
    degraded_time = 0.0

    connect_attempts = 0
    heartbeat_transitions = 0
    discoveries_401 = 0

    # Before expiry the token WORKS: the session sits connected, idle, and
    # makes no attempts at all. Jump straight to the moment the token dies.
    now = token_dies_at

    while now < WINDOW_SECONDS:
        if now < circuit_open_until:
            now = circuit_open_until
            continue

        connect_attempts += 1
        heartbeat_transitions += 2  # Connecting… -> Error (Not connected follows on retry)

        # Token expired? Discovery answers 401 before any socket opens.
        if now >= token_dies_at:
            discoveries_401 += 1
            if not retry_despite_auth_failure:
                # Shipped behavior: terminal — stop, surface "Token expired".
                return {
                    "connect_attempts": connect_attempts,
                    "heartbeat_transitions": heartbeat_transitions,
                    "discoveries_401": discoveries_401,
                    "breaker_open_cycles": int(degraded_time // BREAKER_COOLDOWN),
                    "degraded_time_h": degraded_time / 3600.0,
                }

        # A 401 discovery is a fast fail (<15 s lifetime) -> rapid-drop streak.
        rapid_drop_streak += 1
        attempt += 1
        delay = min(30.0, 2.0 ** min(attempt, 5))
        if rapid_drop_streak >= 2:
            delay = min(300.0, delay * 2.0 ** min(rapid_drop_streak, 4))
        delay *= 0.8 + rng.random() * 0.4

        consecutive_failures += 1
        if consecutive_failures >= BREAKER_THRESHOLD:
            circuit_open_until = now + BREAKER_COOLDOWN
            degraded_time += BREAKER_COOLDOWN
            consecutive_failures = 0  # breaker resets after its cooldown window
            heartbeat_transitions += 1  # Degraded status flip

        now += delay

    return {
        "connect_attempts": connect_attempts,
        "heartbeat_transitions": heartbeat_transitions,
        "discoveries_401": discoveries_401,
        "breaker_open_cycles": int(degraded_time // BREAKER_COOLDOWN),
        "degraded_time_h": degraded_time / 3600.0,
    }


def main() -> None:
    legacy = simulate(retry_despite_auth_failure=True)
    shipped = simulate(retry_despite_auth_failure=False)

    print(f"Weekend window: {WINDOW_SECONDS / 3600:.0f} h  |  OAuth token TTL: {TOKEN_TTL_SECONDS // 3600} h")
    print(f"{'metric':<24}{'legacy retry-forever':>22}{'shipped 401-terminal':>24}")
    rows = [
        ("connect attempts", "connect_attempts"),
        ("heartbeat transitions", "heartbeat_transitions"),
        ("401 discovery verdicts", "discoveries_401"),
        ("breaker open cycles", "breaker_open_cycles"),
        ("time degraded (h)", "degraded_time_h"),
    ]
    for label, key in rows:
        print(f"{label:<24}{legacy[key]:>22,.0f}{shipped[key]:>24,.0f}")

    print()
    print("PAT (no expiry) baseline: 0 attempts, 0 transitions, 0 degraded hours --")
    print("the session simply survives the weekend. See docs/deriv-auth-oauth2.md.")

    sanity = legacy["connect_attempts"] > 100 and shipped["connect_attempts"] == 1
    print(f"\nReality check (legacy >> 100 attempts, shipped == 1): {'PASS' if sanity else 'FAIL'}")
    raise SystemExit(0 if sanity else 1)


if __name__ == "__main__":
    main()
