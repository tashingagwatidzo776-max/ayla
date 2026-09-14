"""Generate the coverage-trend page for the GitHub Pages site.

Reads ./site/coverage-trend.csv (columns: epoch_seconds,run_id,line_coverage)
and — when present — ./site/growth-bankroll.csv (columns: epoch_seconds,account,
bankroll), writes ./site/trend.html — a self-contained Chart.js page showing
the trajectory of combined line coverage on main against the CI gate, with the
app's daily growth-bankroll deltas from the trade store plotted alongside.

Used by .github/workflows/coverage-pages.yml; can also be run locally.
"""

import csv
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

BANK_COLORS = ["#1a7f37", "#8250df", # green, purple — gray family is CI coverage
               "#bf3989", "#d4a72c", "#0550ae", "#e16f24"]
bank_datasets_js = json.dumps([
    {
        "label": acct,
        "data": [{"x": ts * 1000, "y": val} for ts, val in pts_a],
        "borderColor": BANK_COLORS[i % len(BANK_COLORS)],
        "backgroundColor": BANK_COLORS[i % len(BANK_COLORS)] + "22",
        "borderDash": [6, 4],
        "borderWidth": 1.5,
        "pointRadius": 2,
        "tension": 0.25,
        "yAxisID": "y1",
    }
    for i, (acct, pts_a) in enumerate(sorted(bank_by_acct.items()))
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
</style>
</head>
<body>
<h1>Coverage trend — combined line coverage on <code>main</code></h1>
<div class="cards">
  <div class="card">Latest<b class="{latest_class}">{latest['y'] if latest else '&mdash;'}%</b></div>
  <div class="card">Average (last {len(pts)})<b>{avg}%</b></div>
  <div class="card">Gate<b>{GATE:.0f}%</b></div>
</div>
<canvas id="trend" height="110"></canvas>
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
