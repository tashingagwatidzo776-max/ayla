"""Generate the coverage-trend page for the GitHub Pages site.

Reads ./site/coverage-trend.csv (columns: epoch_seconds,run_id,line_coverage)
and — when present — ./site/growth-bankroll.csv (columns: epoch_seconds,account,
bankroll), writes ./site/trend.html — a self-contained Chart.js page showing
the trajectory of combined line coverage on main against the CI gate, with the
app's daily growth-bankroll deltas from the trade store plotted alongside.

Used by .github/workflows/coverage-pages.yml; can also be run locally.
"""

import csv
import datetime
import json
import os

GATE = float(os.environ.get("GATE", "60"))
MAX_POINTS = 30

rows = []
with open("./site/coverage-trend.csv", newline="") as f:
    for r in csv.DictReader(f):
        try:
            rows.append((int(r["epoch_seconds"]), r["run_id"], float(r["line_coverage"])))
        except (KeyError, TypeError, ValueError):
            continue

rows.sort()
rows = rows[-MAX_POINTS:]
pts = [{"x": ts * 1000, "y": pct, "run": rid} for ts, rid, pct in rows]
avg = round(sum(p for _, _, p in rows) / len(rows), 2) if rows else 0.0
latest = pts[-1] if pts else None
latest_class = "ok" if latest and latest["y"] >= GATE else "bad"
data = json.dumps(pts)

# Optional event annotations (docs/coverage-events.csv): vertical lines on
# the chart plus a list under it, so the trajectory tells the story of the
# codebase. Missing file simply means no annotations.
events = []
if os.path.exists("./docs/coverage-events.csv"):
    with open("./docs/coverage-events.csv", newline="") as f:
        for r in csv.DictReader(f):
            try:
                events.append((int(r["epoch_seconds"]), r["label"], r.get("detail", "")))
            except (KeyError, TypeError, ValueError):
                continue
    events.sort()
    if len(events) > MAX_POINTS:
        events = events[-MAX_POINTS:]
events_js = json.dumps(
    [{"x": ts * 1000, "label": lbl, "detail": det} for ts, lbl, det in events]
)

# Optional growth-bankroll trajectory (site/growth-bankroll.csv: one row per
# account per day, exported from the trade store by the Pages workflow) — the
# app's real money curve, plotted on its own axis so the two stories (code
# health, bankroll growth) are visible against each other. Missing file means
# no bankroll layer yet.
bank = []
if os.path.exists("./site/growth-bankroll.csv"):
    with open("./site/growth-bankroll.csv", newline="") as f:
        for r in csv.DictReader(f):
            try:
                bank.append((int(r["epoch_seconds"]), r["account"], float(r["bankroll"])))
            except (KeyError, TypeError, ValueError):
                continue

bank_by_acct = {}
for ts, acct, val in bank:
    bank_by_acct.setdefault(acct, []).append((ts, val))
for acct in bank_by_acct:
    bank_by_acct[acct].sort()
    if len(bank_by_acct[acct]) > MAX_POINTS:
        bank_by_acct[acct] = bank_by_acct[acct][-MAX_POINTS:]

# Total across accounts per timestamp (accounts share the daily export tick).
bank_total_pts = {}
for acct, pts_a in bank_by_acct.items():
    for ts, val in pts_a:
        bank_total_pts[ts] = round(bank_total_pts.get(ts, 0.0) + val, 2)
bank_total = sorted(bank_total_pts.items())[-MAX_POINTS:]

# Per-account drill-down: one dashed line per account on the money axis
# (reusing the CSV's per-account rows) plus a legend table beneath the chart
# so each line is attributable — name, latest bankroll, and how the account
# got there from its first exported day. Colors pair with the chart via a
# shared palette map emitted below.
BANK_COLORS = ["#1a7f37", "#8250df", # green, purple — gray family is CI coverage
               "#bf3989", "#d4a72c", "#0550ae", "#e16f24"]
bank_acct_color = {acct: BANK_COLORS[i % len(BANK_COLORS)]
                   for i, acct in enumerate(sorted(bank_by_acct))}
bank_rows = []
for acct in sorted(bank_by_acct):
    pts_a = bank_by_acct[acct]
    if not pts_a:
        continue
    latest_bankroll = pts_a[-1][1]
    opening_bankroll = pts_a[0][1]
    delta = latest_bankroll - opening_bankroll
    bank_rows.append({
        "account": acct,
        "color": bank_acct_color[acct],
        "latest": latest_bankroll,
        "delta": delta,
        "days": len(pts_a),
    })

bank_datasets_js = json.dumps([
    {
        "label": acct,
        "data": [{"x": ts * 1000, "y": val} for ts, val in pts_a],
        "borderColor": bank_acct_color[acct],
        "backgroundColor": bank_acct_color[acct] + "22",
        "borderDash": [6, 4],
        "borderWidth": 1.5,
        "pointRadius": 2,
        "tension": 0.25,
        "yAxisID": "y1",
    }
    for acct, pts_a in sorted(bank_by_acct.items())
])

# The money axis only exists when there is bankroll data — the page shape
# must not change (a bare right axis) on days with nothing to plot.
xy_scales = (
    "x: { type: 'time', adapters: {date: {locale: 'en-US'}}, "
    "time: {unit: 'day', tooltipFormat: 'yyyy-MM-dd HH:mm'}, "
    "title: {display: true, text: 'CI run date'} }, "
    "y: { beginAtZero: true, suggestedMax: 100, "
    "title: {display: true, text: 'Line coverage %'} }"
)
scales_js = xy_scales + (
    ", y1: { position: 'right', beginAtZero: true, "
    "grid: { drawOnChartArea: false }, "
    "title: {display: true, text: 'Bankroll ($)'} }"
    if bank_by_acct else ""
)
bank_note = "" if not bank_by_acct else (
    "<p>Dashed lines (right axis): the app's growth-bankroll trajectory from "
    "the trade store, one point per account per day — "
    + " · ".join(sorted(bank_by_acct))
    + ". Exported by the Pages deploy.</p>"
)

# ── Artifact-pipeline health banner ─────────────────────────────────
# Same signals the metrics-digest workflow reports to the webhook: rows in
# the committed bankroll CSV vs settled trades in the committed pulse file.
# Trades settling without the CSV advancing is the freeze the watchdog
# alerts on — surfacing it here makes pipeline health publicly visible on
# every page deploy, not only in Discord.
csv_rows = len(bank)
pulse_trades = 0
pulse_accounts = 0
pulse_last = "never"
if os.path.exists("./docs/growth-pulse.json"):
    try:
        with open("./docs/growth-pulse.json", encoding="utf-8") as pf:
            pulse = json.load(pf)
        pulse_trades = int(pulse.get("settled_trades", 0))
        pulse_accounts = len(pulse.get("accounts", []))
        pulse_epoch = int(pulse.get("last_settled_epoch", 0))
        if pulse_epoch > 0:
            pulse_last = datetime.datetime.fromtimestamp(
                pulse_epoch, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    except (ValueError, TypeError, OSError):
        pass

if pulse_trades > 0 and csv_rows == 0:
    banner_class = "bad"
    banner = (f"⚠ Artifact pipeline: {pulse_trades} trade(s) settled but the "
              "bankroll CSV axis is empty — publish may be frozen")
elif csv_rows > 0:
    banner_class = "ok"
    banner = (f"✓ Artifact pipeline current: {csv_rows} bankroll row(s) · "
              f"{pulse_trades} settled trade(s) · {pulse_accounts} account(s) · "
              f"last settled {pulse_last}")
else:
    banner_class = "ok"
    banner = "Artifact pipeline: no settled growth trades yet (pre-trade state)"

# Per-account drill-down legend: one chip per bankroll line, color-matched to
# the chart, naming the account, its latest bankroll, and the change across
# the exported window.
bank_legend = "" if not bank_rows else (
    "<div class=\"bank-legend\">"
    + "".join(
        f"<span class=\"bank-chip\" style=\"border-color:{r['color']}\">"
        f"<span class=\"swatch\" style=\"background:{r['color']}\"></span>{r['account']}: "
        f"<b>${r['latest']:.2f}</b> "
        f"<span class=\"{'ok' if r['delta'] >= 0 else 'bad'}\">"
        f"{'+' if r['delta'] >= 0 else ''}{r['delta']:.2f} over {r['days']}d</span></span>"
        for r in bank_rows
    )
    + "</div>"
)

# Chart.js pinned to an exact version from the jsDelivr CDN — no build step.
js_url = "https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"

body = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Coverage trend — ayla</title>
<style>
  body {{ font-family: 'Segoe UI', system-ui, sans-serif; margin: 2rem auto; max-width: 960px; padding: 0 1rem; color: #1f2328; }}
  h1 {{ font-size: 1.4rem; }}
  .cards {{ display: flex; gap: 1rem; flex-wrap: wrap; margin-bottom: 1.5rem; }}
  .card {{ border: 1px solid #d0d7de; border-radius: 8px; padding: .75rem 1.25rem; }}
  .card b {{ font-size: 1.5rem; display: block; }}
  .ok {{ color: #1a7f37; }} .bad {{ color: #cf222e; }}
  a {{ color: #0969da; }}
  .bank-legend {{ display: flex; gap: .5rem; flex-wrap: wrap; margin: .75rem 0; }}
  .bank-chip {{ display: inline-flex; align-items: center; gap: .35rem; border: 1px solid #d0d7de;
    border-left-width: 4px; border-radius: 6px; padding: .15rem .6rem; font-size: .85rem; }}
  .bank-chip .swatch {{ display: inline-block; width: .7rem; height: .7rem; border-radius: 2px;
    opacity: .8; }}
  .banner {{ border: 1px solid #d0d7de; border-left-width: 4px; border-radius: 8px;
    padding: .5rem .9rem; margin-bottom: 1.25rem; font-size: .9rem; }}
  .banner.ok {{ border-left-color: #1a7f37; }}
  .banner.bad {{ border-left-color: #cf222e; }}
</style>
</head>
<body>
<h1>Coverage trend — combined line coverage on <code>main</code></h1>
<div class="banner {banner_class}">{banner}</div>
<div class="cards">
  <div class="card">Latest<b class="{latest_class}">{latest['y'] if latest else '&mdash;'}%</b></div>
  <div class="card">Average (last {len(pts)})<b>{avg}%</b></div>
  <div class="card">Gate<b>{GATE:.0f}%</b></div>
</div>
<canvas id="trend" height="110"></canvas>
{bank_legend}
{bank_note}
{'' if not events else '<h2>Timeline</h2><ul>' + ''.join(f'<li><b>{lbl}</b> &mdash; {det}</li>' for _, lbl, det in events) + '</ul>'}
<p>Each point is one successful CI run on <code>main</code> (newest {len(pts)}).
The gate is enforced by the <code>coverage-report</code> job.
Full report: <a href="./index.html">index.html</a> &middot;
History data: <a href="./coverage-trend.csv">coverage-trend.csv</a></p>
<script src="{js_url}" crossorigin="anonymous"></script>
<script>
const points = {data};
const events = {events_js};
const bankSets = {bank_datasets_js};
const vlines = {{
  id: 'vlines',
  afterDatasetsDraw(chart) {{
    const {{ctx, chartArea: {{top, bottom}}, scales: {{x}}}} = chart;
    events.forEach(e => {{
      const px = x.getPixelForValue(e.x);
      if (px < chart.chartArea.left || px > chart.chartArea.right) return;
      ctx.save();
      ctx.strokeStyle = '#d4a72c';
      ctx.setLineDash([4, 4]);
      ctx.beginPath();
      ctx.moveTo(px, top);
      ctx.lineTo(px, bottom);
      ctx.stroke();
      ctx.restore();
    }});
  }}
}};
new Chart(document.getElementById('trend'), {{
  plugins: [vlines],
  type: 'line',
  data: {{ datasets: [{{
    label: 'Line coverage %',
    data: points.map(p => ({{x: p.x, y: p.y}})),
    borderWidth: 2,
    tension: 0.25,
    pointRadius: 3,
    borderColor: '#0969da',
    backgroundColor: '#0969da22'
  }}, ...bankSets]}},
  options: {{
    parsing: false,
    scales: {{ {scales_js} }},
    plugins: {{
      tooltip: {{ callbacks: {{ label: c => c.dataset.yAxisID === 'y1'
        ? (c.dataset.label + ': $' + c.parsed.y.toFixed(2))
        : (c.parsed.y + '% (run ' + points[c.dataIndex].run + ')') }} }}
    }}
  }}
}});
</script>
</body>
</html>
"""

with open("./site/trend.html", "w", encoding="utf-8", newline="\n") as f:
    f.write(body)
print(f"trend.html written with {len(pts)} points (gate {GATE:.0f}%)")
