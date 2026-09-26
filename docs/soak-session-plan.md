# Demo soak session plan

The practical runbook for producing the evidence M3 demands: run the FX
brain in paper against the demo venue until every symbol's soak bar is
met, then execute the explicit go-live. Companion to
[`m3-go-live-readiness.md`](m3-go-live-readiness.md) (the readiness
review) and [`mt5-bridge.md`](mt5-bridge.md) (transport).

## 1. Before the first session — configure

1. **Settings → FX BRAIN** — set the symbols (`FxSymbols`), the fail-closed
   caps (`Mt5MaxLots`, `Mt5DailyLossCap`, `Mt5EquityFloor`,
   `FxPortfolioMaxLots`, `NewsBlackoutMinutes`), then **save** (this is
   what writes `settings.json`; the pre-go-live checker reads it).
2. **Start the sidecar** (`python bridge/mt5_sidecar.py`) and launch the
   app; the account bar must show your MT5 login/server — not
   `bridge offline`.
3. **Optional but recommended:** a Discord/Slack webhook so refusals and
   halts reach you out-of-band during long sessions.
4. **Sanity-check the machine-readable checklist:**
   `python scripts/pre_go_live_check.py --skip-sidecar` — soak progress and
   safety-caps legs should already report.

## 2. Run the soak

1. Press **FX BRAIN ON** on the Terminal account bar. The brain starts in
   PAPER — every decision is journaled, no order is placed.
2. **Cadence:** one engine cycle per symbol every ~60 s (`FxEngineHost`'s
   timer). A cycle only counts toward the soak when an *alpha actually
   speaks* (regime stand-downs and no-signal cycles do not count), so the
   wall-clock time to the bar is market-dependent, not fixed.
3. **The bar:** 10 signals per symbol, **all symbols** (all-or-nothing —
   one symbol at 3/10 holds the whole portfolio in paper). With 4 symbols
   and a few signals per hour, expect a handful of sessions across several
   days rather than one afternoon.
4. **Watch progress:** the account bar's soak pill shows every symbol's
   `sym n/m` (laggard first — that is the symbol holding GO LIVE back); the
   journal records `FX_MODE "paper soak n/m on <symbol>"` every time the
   counter advances; `python scripts/pre_go_live_check.py` reports the
   per-symbol n/m table and which symbol is the laggard.
5. **Leave it running:** supervisor halts (bridge down, loss cap, equity
   floor) return the engine to paper and latch until RE-ARM — treat those
   as findings, fix the cause, resume.

## 3. After each session — record the evidence

```bash
python scripts/soak_report.py --record docs/soak
git add docs/soak && git commit -m "soak evidence <date>"
git push
```

- The committed `SOAK-<date>.md` is what the weekly drill freshness-checks
  (14 days max age) — committing is part of the loop, not optional.
- The registered Monday `open-watcher` task records the report
  automatically after the week's first open; the `git commit` is yours.

## 4. When every symbol reads n/n — go live (demo)

1. `python scripts/pre_go_live_check.py` (no flags) must exit 0.
2. Press **GO LIVE** on the Terminal account bar. It is refused unless
   *every* symbol has soaked (`go-live refused — paper soak n/m signals`).
3. From here orders are real demo orders through the MT5 bridge: they hit
   every rail at execution time (kill switch, `Mt5MaxLots`, portfolio cap,
   news blackout, real-money gate — a demo account passes the gate, but the
   typed-phrase unlock rails stay armed for the real account).
4. **Settlements are the point:** `TRADE_SETTLEMENT` entries are the
   evidence the soak reports and the alpha scorecard consume. The first
   settled trade also unblocks the bankroll-drill issue (#44) — and the
   app announces it on the webhook (`🥇 First settled FX trade`, exactly
   once per installation), so monitoring sees the unblock without opening
   a journal.
5. Keep recording `soak_report.py --record docs/soak` per session — now
   with settled trades, the verdicts carry weight.

## 5. Only after demo evidence — the real-account question

The real-money path is deliberately separate: a real MT5 account flips the
venue verdict in the gate, which demands the typed session unlock
(`TRADE REAL MONEY`), which is watched for staleness (`ArmStalenessHours`).
Run the same checklist (`pre_go_live_check.py`), confirm every cap out
loud, and keep the webhook on — refusals and staleness alarms reach you
out-of-band or not at all. `docs/real-money-safety-audit.md` is the
rail-by-rail map of what stands between you and the order.
