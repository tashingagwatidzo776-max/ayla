"""Generate the coverage-trend page for the GitHub Pages site.

Reads ./site/coverage-trend.csv (columns: epoch_seconds,run_id,line_coverage),
writes ./site/trend.html — a self-contained Chart.js page showing the
trajectory of combined line coverage on main against the CI gate.

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
<p>Each point is one successful CI run on <code>main</code> (newest {len(pts)}).
The gate is enforced by the <code>coverage-report</code> job.
Full report: <a href="./index.html">index.html</a> &middot;
History data: <a href="./coverage-trend.csv">coverage-trend.csv</a></p>
<script src="{js_url}" crossorigin="anonymous"></script>
<script>
const points = {data};
new Chart(document.getElementById('trend'), {{
  type: 'line',
  data: {{ datasets: [{{
    label: 'Line coverage %',
    data: points.map(p => ({{x: p.x, y: p.y}})),
    borderWidth: 2,
    tension: 0.25,
    pointRadius: 3,
    borderColor: '#0969da',
    backgroundColor: '#0969da22'
  }}]}},
  options: {{
    parsing: false,
    scales: {{
      x: {{ type: 'time', adapters: {{date: {{locale: 'en-US'}}}}, time: {{unit: 'day', tooltipFormat: 'yyyy-MM-dd HH:mm'}}, title: {{display: true, text: 'CI run date'}} }},
      y: {{ beginAtZero: true, suggestedMax: 100, title: {{display: true, text: 'Line coverage %'}} }}
    }},
    plugins: {{
      tooltip: {{ callbacks: {{ label: c => c.parsed.y + '% (run ' + points[c.dataIndex].run + ')' }} }}
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
