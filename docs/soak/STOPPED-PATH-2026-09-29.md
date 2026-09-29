## Stopped-order path — PROVEN live (2026-09-29)

The 2026-09-29 fix ("one ruler per trade") attaches the sized stop to every
brain order. First production verification, 7 fills between 04:34Z and
06:03Z (XAUUSDmicro, USDJPY):

- 7/7 orders carried an SL in the FX_ORDER journal payload.
- Venue /positions shows the SL set (e.g. journal 4131.167957 -> venue 4131.17,
  i.e. rounded to the symbol's digits by the bridge, as designed).
- Fresh-trade R-units are sane: XAU #9820712902 filled @ 4133.61, SL 4131.17
  (risk distance 2.44); first FX_EXIT reading mfeR 2.42 == ~5.9 price units
  of favorable excursion — the exit brain's ruler now matches the sized stop.
- Legacy positions opened before the fix (e.g. EURUSD #9820161383) keep
  sl 0.0 at the venue; their ruler is the pip-floored fallback.

Verdict: sizing, stop attachment, venue acceptance and exit-brain R-units
all measure the same distance. Go-live checklist item "stopped-path
verified": GREEN (needs 14-day freshness like the rest of the evidence).
