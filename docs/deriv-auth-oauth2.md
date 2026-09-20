# Deriv OAuth 2.0 — research, in-app support, and the PAT recommendation

Recorded 2026-09-19 from the live developer docs (developers.deriv.com: OAuth 2.0,
Authentication, Complete Workflows). DongGfx supports OAuth 2.0 as a **second
sign-in path**; PATs remain the **default** — reasons below, with the numbers.

## The flow (Authorization Code + PKCE)

1. App generates `code_verifier` (43–128 chars, base64url), derives
   `code_challenge = BASE64URL(SHA256(verifier))`, and a random `state`.
2. App opens the browser at `https://auth.deriv.com/oauth2/auth` with
   `response_type=code`, `client_id`, `redirect_uri`, `scope` (space-separated:
   `trade`, `account_manage`, `application_read`, `payment`), `state`,
   `code_challenge`, `code_challenge_method=S256`. Sign-up adds
   `prompt=registration`; legacy-API apps may add `app_id`.
3. User signs in and consents **on Deriv's pages** — the password never touches
   the app.
4. Deriv redirects to the pre-registered `redirect_uri` with
   `?code=…&state=…` (or `?error=access_denied&…`). Verify `state` first (CSRF);
   the code is single-use and short-lived.
5. Backend/app POSTs form-encoded to `https://auth.deriv.com/oauth2/token`:
   `grant_type=authorization_code`, `client_id`, `code`, `code_verifier`,
   `redirect_uri`.
6. Response: `access_token` (`ory_at_…`), `token_type: Bearer`,
   `expires_in: 3600`; `refresh_token` is **optional** and must not be assumed.
7. Use `Authorization: Bearer …`. Unlike PATs, **no `Deriv-App-ID` header** is
   needed — the OAuth token already identifies the application.

## Endpoint reference

| Endpoint | URL |
|---|---|
| Authorization | `https://auth.deriv.com/oauth2/auth` |
| Token exchange | `https://auth.deriv.com/oauth2/token` |
| REST API base | `https://api.derivws.com` |
| OTP (WebSocket bootstrap) | `POST /trading/v1/options/accounts/{accountId}/otp` |
| Account discovery | `GET /trading/v1/options/accounts` |

Registration requirements: an **OAuth-type app** at developers.deriv.com
(client_id), and **every redirect URL whitelisted exactly** (subdirectories
included), HTTPS per the docs.

## Token lifetimes — the decisive fact

- Access token: **~1 hour** (`expires_in: 3600`).
- Refresh token: optional in the documented response; no documented silent
  refresh flow for desktop.
- PAT: no expiry (revocable in Deriv settings).

## In-app support (this repo)

- `src/DongGfx.Deriv/OAuthSignIn.cs` — PKCE (RFC 7636 S256; pinned to the RFC's
  appendix-B vector in tests), authorize-URL builder, token exchange, and the
  desktop loopback flow: free-port `HttpListener` on `http://localhost:{port}/`
  captures `/callback?code=…&state=…`, state verified, code exchanged
  immediately, listener closed. Browser open is virtual → tests run the whole
  flow headlessly.
- Accounts tab → "SIGN IN WITH DERIV (OAUTH 2.0)" band: paste the registered
  client id, sign in, and the token lands through the **same import path as a
  PAT** (discovery verifies; one row per token — see below).
- `AccountConnection` now classifies terminal auth failures (401-class): an
  expired bearer sets `AuthFailure` ("Token expired — sign in again (OAuth) or
  paste a fresh PAT") and stops, instead of grinding the circuit breaker
  forever. Transient failures keep breaker semantics.
- Tests: `OAuthSignInTests` (Core), `AccountsOAuthImportTests`,
  `AccountConnectionAuthFailureTests` (App).

## Desktop vs web — the tradeoff

Deriv's own guidance: OAuth 2.0 is "best fit: web-based applications"; choose
PAT "when browser redirects are not practical and manual token entry is
acceptable, **such as in desktop or native environments**."

| Concern | OAuth 2.0 | PAT |
|---|---|---|
| Token lifetime | ~1 h; refresh not guaranteed | No expiry |
| Weekend-idle → Monday-open session | Dies ~39 times over; every resumption needs a browser round-trip | Survives the weekend |
| Redirect capture | Needs a pre-registered callback; loopback `http://localhost:{port}` is the native-app convention but Deriv's docs advertise HTTPS only — validate with Deriv before relying on it | Not used |
| Onboarding other users | Excellent — no token pasting, consent is revocable | Each user mints/pastes a token |
| Password exposure | Never leaves Deriv | Never involved |

## The measured cost of an hourly token over a weekend

`scripts/oauth_expiry_simulation.py` replays the exact production state
machines — `DerivClient`'s reconnect backoff (2^n capped at 30 s, doubled per
rapid drop to a 300 s ceiling, ±20% jitter) and `AccountConnection`'s breaker
(5 failures → 10-minute open) — across a Fri 22:00 → Mon 00:00 UTC market
closure with a 1-hour token (signed in just before the close):

- Legacy retry-everything behavior: **615 failed connect attempts**, **1,353
  heartbeat transitions**, **123 breaker-open cycles**, **~20 h degraded** —
  the reconnect-storm pattern we just eliminated, back in force all weekend.
- With the 401-terminal classification shipped here: **1 attempt, 2
  transitions, 0 degraded time** — then a terminal, human-actionable state.
  No churn, but also **no trading Monday morning without a fresh sign-in**.

## Recommendation (and why PAT stays default)

For a single-user desktop tool holding sessions across weekends, the hourly
token is strictly worse: OAuth would reintroduce session churn during the
exact window the app trades, unless/until Deriv documents a silent refresh
flow for desktop. **PATs stay the default.** OAuth remains valuable for
future multi-user distribution (frictionless, revocable onboarding) and is
now a supported second path — the sign-in lands the OAuth bearer in the
ordinary account pipeline, so every downstream safety rail (discovery
demo/real verdict, real-money gate, governor) applies unchanged.

One architectural note discovered while building this: an OAuth consent
covers **all** of a user's accounts with one bearer, but this app keeps the
**one token per row** invariant (shared tokens churn sibling sessions — the
measured reconnect-storm root cause). The OAuth import therefore creates a
single row (demo preferred) per sign-in; simultaneous multi-account use goes
through per-account PATs.

## Probe: verify an OAuth token against our pipeline

`scripts/probe_oauth_token.py` takes an OAuth access token (or a PAT) and
exercises the exact calls the app makes — discovery
(`GET /trading/v1/options/accounts`), then an OTP request
(`POST /trading/v1/options/accounts/{id}/otp`, **never connecting to the
returned socket**) — printing per-call verdicts. Run it before cutting any
OAuth-first release:

```
python scripts/probe_oauth_token.py --token ory_at_... [--app-id <id>]
```

PATs require the app id (`--app-id`); OAuth bearers do not. Exit 0 = the
token works with our discovery/OTP flow end-to-end.
