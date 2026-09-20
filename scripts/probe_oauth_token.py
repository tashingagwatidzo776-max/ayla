#!/usr/bin/env python3
"""Probe: does this bearer token work with DongGfx's discovery/OTP flow?

Exercises the exact calls the app makes (src/DongGfx.Deriv/NewPlatformAuth.cs):

  1. Discovery   GET https://api.derivws.com/trading/v1/options/accounts
  2. OTP request POST https://api.derivws.com/trading/v1/options/accounts/{id}/otp
     (demo account preferred; the returned socket URL is NEVER connected to)

Token kinds (auto-detected):
  - OAuth 2.0 access token ("ory_at_..."): sent as a plain Bearer —
    per Deriv's docs, OAuth bearers must NOT carry the Deriv-App-ID header.
  - Personal Access Token (anything else): Bearer + Deriv-App-ID header
    (required; omitting it returns 401 "Deriv-App-ID header is required
    for PAT tokens").

Verdicts are printed per step; exit 0 only when discovery AND the OTP
exchange both succeed — i.e. the token is proven compatible with the app's
connect path end-to-end without opening a single trading socket.

Usage:
  python scripts/probe_oauth_token.py --token ory_at_...
  python scripts/probe_oauth_token.py --token <PAT> --app-id 1089
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request

API_BASE = "https://api.derivws.com"


def call(method: str, path: str, token: str, app_id: str | None, timeout: float = 20.0):
    url = API_BASE + path
    req = urllib.request.Request(url, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    if app_id:
        req.add_header("Deriv-App-ID", app_id)
    req.add_header("Accept", "application/json")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.status, json.loads(resp.read().decode("utf-8"))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--token", required=True, help="OAuth access token (ory_at_...) or PAT")
    ap.add_argument("--app-id", default=None, help="Registered app id (required for PATs)")
    args = ap.parse_args()

    token = args.token.strip()
    is_oauth = token.startswith("ory_at_")
    app_id = None if is_oauth else args.app_id

    print(f"token kind: {'OAuth 2.0 bearer (no Deriv-App-ID header sent)' if is_oauth else 'PAT (Deriv-App-ID required)'}")
    if not is_oauth and not app_id:
        print("FAIL: a PAT needs --app-id (the PAT app's registered id).")
        return 2

    # ── Step 1: discovery ────────────────────────────────────────────
    try:
        status, body = call("GET", "/trading/v1/options/accounts", token, app_id)
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")[:200]
        print(f"FAIL: discovery returned HTTP {e.code}: {detail}")
        if e.code == 401:
            print("      -> the platform rejected the bearer outright (bad/expired token"
                  + (", or a missing/mismatched Deriv-App-ID" if not is_oauth else "") + ").")
        return 1
    except Exception as e:  # noqa: BLE001 - probe must report, not crash
        print(f"FAIL: discovery could not complete: {e}")
        return 1

    accounts = body.get("data", []) if isinstance(body, dict) else []
    if not accounts:
        print("FAIL: discovery succeeded but listed no accounts.")
        return 1

    print(f"PASS: discovery listed {len(accounts)} account(s):")
    for a in accounts:
        print(f"      {a.get('account_id', '?')}  type={a.get('account_type', '?')}  "
              f"balance={a.get('balance', '?')} {a.get('currency', '')}  status={a.get('status', '?')}")

    # ── Step 2: OTP exchange (demo preferred; never connect the socket) ──
    demo = next((a for a in accounts if str(a.get("account_type", "")).lower() == "demo"), None)
    target = demo or accounts[0]
    acct_id = target.get("account_id", "")
    try:
        status, body = call("POST", f"/trading/v1/options/accounts/{acct_id}/otp", token, app_id)
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")[:200]
        print(f"FAIL: OTP request for {acct_id} returned HTTP {e.code}: {detail}")
        return 1
    except Exception as e:  # noqa: BLE001
        print(f"FAIL: OTP request could not complete: {e}")
        return 1

    ws_url = (body.get("data") or {}).get("url", "")
    if not ws_url:
        print("FAIL: OTP response carried no url.")
        return 1

    endpoint = "demo" if "/demo" in ws_url else ("real" if "/real" in ws_url else "unknown")
    print(f"PASS: OTP issued for {acct_id} -> authenticated WebSocket "
          f"(endpoint: {endpoint}; URL NOT connected, discarded).")

    print("\nVERDICT: token is compatible with the app's discovery/OTP connect path end-to-end.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
