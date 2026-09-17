# Demo soak evidence

Reports recorded by `scripts/soak_report.py --record docs/soak` after a demo
session with the released app — one file per UTC day (`SOAK-YYYY-MM-DD.md`,
overwritten on re-runs; the accumulation **is** the trend).

The weekly drill's `soak-freshness` job enforces the habit: evidence may be
**absent** (nobody has soaked yet — informational), but never **stale**. The
newest report carrying a `SOAK CLEAN` verdict must be within 14 days, or the
drill fails and the watchdog files a triage issue.

After every real demo session:

```bash
python scripts/soak_report.py --record docs/soak
git add docs/soak && git commit -m "soak evidence <date>"
```

Exit codes: `0` clean · `1` findings (fix before real mode) · `3` NO DATA
(an empty soak is not evidence — connect a demo account and let it trade).
