#!/usr/bin/env python3
"""Generate health.json + health-status.html for the Pages site.

Written by the Coverage Pages deploy from the run being published, so the
status page always describes exactly what the site shows. The README
"Pipeline health" section links to it: one glance answers "is CI healthy
right now?" — gate passing, no open drift issues, and the app's committed
money axis still advancing.

Healthy means ALL of:
  - the published run's combined line coverage is at or above the gate
  - there are no open ci-drift issues (failure alerts, no-shows)
  - the money axis is not stale: docs/growth-bankroll.csv has data and its
    newest row is younger than the threshold (a frozen axis is the app-side
    silent failure — the trade store lives on the trading machine, so CI
    can only see whether the committed CSV is still advancing; the pulse
    file next to it records the machine-side settled picture the watchdog
    compares against)

Read-only GitHub API call for the issue list; any failure there is fatal
(a status page that silently lies is worse than no status page).
"""

import csv
import json
import os
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

GATE = 60
SITE = Path("./site")
DOCS = Path("./docs")
# The badge goes red when the committed money axis is older than this. Same
# default as the watchdog's frozen-axis check, for the same reason: five days
# of silence means the trading app stopped advancing the CSV.
AXIS_STALE_DAYS = 5


def main() -> None:
    repo = os.environ["GITHUB_REPOSITORY"]
    run_id = os.environ.get("PAGES_RUN_ID", "")

    # --- coverage from the report this deploy is publishing ---
    line_pct = None
    summary = SITE / "Summary.txt"
    if summary.exists():
        for line in summary.read_text(encoding="utf-8", errors="replace").splitlines():
            if "Line coverage:" in line:
                try:
                    line_pct = float(line.split()[2].rstrip("%"))
                except (IndexError, ValueError):
                    pass
                break
    if line_pct is None:
        raise SystemExit("::error::could not read line coverage from site/Summary.txt")

    trend_points = 0
    trend_csv = SITE / "coverage-trend.csv"
    if trend_csv.exists():
        with trend_csv.open(newline="") as f:
            trend_points = sum(1 for _ in csv.DictReader(f))

    # --- open drift issues ---
    req = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/issues"
        "?state=open&labels=ci-drift&per_page=100",
        headers={
            "Authorization": f"Bearer {os.environ['GH_TOKEN']}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "ayla-pages-health",
        },
    )
    with urllib.request.urlopen(req, timeout=30) as r:
        issues = json.load(r)
    open_drift = [
        {
            "number": i["number"],
            "title": i["title"],
            "url": i["html_url"],
            "updated_at": i["updated_at"],
        }
        for i in issues
        if "pull_request" not in i  # the issues listing can include PRs
    ]

    gate_passing = line_pct >= GATE

    # --- money-axis staleness (the app's committed CSV must keep advancing)
    # The repo's committed CSV + pulse pair is the CI-visible slice of the
    # trading machine: pulse content changes only when a settled growth trade
    # lands (BankrollCsvFile), and the CSV's newest row is the axis tip.
    axis = {
        "stale_threshold_days": AXIS_STALE_DAYS,
        "has_data": False,
        "newest_row_epoch": None,
        "newest_row_age_days": None,
        "stale": None,  # None = no data yet: not evaluable, not unhealthy
        "pulse_last_settled_epoch": None,
    }
    now_epoch = int(datetime.now(timezone.utc).timestamp())
    committed_csv = DOCS / "growth-bankroll.csv"
    if committed_csv.exists():
        newest = 0
        with committed_csv.open(newline="", encoding="utf-8") as f:
            for row in csv.DictReader(f):
                try:
                    newest = max(newest, int(row["epoch_seconds"]))
                except (KeyError, TypeError, ValueError):
                    pass  # a malformed row is the trend page's problem, not the badge's
        if newest:
            age_days = (now_epoch - newest) / 86400
            axis.update(
                has_data=True,
                newest_row_epoch=newest,
                newest_row_age_days=round(age_days, 2),
                stale=age_days >= AXIS_STALE_DAYS,
            )
    pulse_path = DOCS / "growth-pulse.json"
    if pulse_path.exists():
        try:
            axis["pulse_last_settled_epoch"] = int(
                json.loads(pulse_path.read_text(encoding="utf-8")).get("last_settled_epoch") or 0
            ) or None
        except (json.JSONDecodeError, OSError, TypeError, ValueError):
            pass  # unreadable pulse: the watchdog owns that verdict, not the badge

    axis_ok = axis["stale"] is not True  # no data yet ⇒ badge stays on CI health
    healthy = bool(gate_passing and not open_drift and axis_ok)
    health = {
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "healthy": healthy,
        "coverage": {
            "line_pct": line_pct,
            "gate_pct": GATE,
            "gate_passing": gate_passing,
            "trend_points": trend_points,
        },
        "run": {
            "id": run_id,
            "url": f"https://github.com/{repo}/actions/runs/{run_id}" if run_id else None,
            "conclusion": os.environ.get("PAGES_RUN_CONCLUSION") or None,
        },
        "drift": {
            "open_issues": open_drift,
            "healthy": not open_drift,
        },
        # The app's money axis: present, advancing, and how stale the tip is.
        # stale=null means no data yet (the axis simply has not started).
        "money_axis": axis,
        # shields.io endpoint-badge fields, so the README can render a
        # dynamic healthy/unhealthy badge straight from health.json.
        "schemaVersion": 1,
        "label": "pipeline",
        "message": ("healthy" if healthy else "unhealthy"),
        "color": ("brightgreen" if healthy else "red"),
        "namedLogo": "githubactions",
    }

    # The human-readable verdict line (emoji + class) is derived from the
    # same boolean as the badge fields above, so the badge, the status page,
    # and health.json can never disagree.
    cls = "ok" if healthy else "bad"
    emoji = "\u2705" if healthy else "\u26a0\ufe0f"
    cov_cls = "ok" if gate_passing else "bad"
    drift_cls = "ok" if not open_drift else "bad"
    if axis["stale"] is True:
        axis_emoji, axis_text, axis_cls = "\U0001f6c4", "stale", "bad"  # \U0001f6c4 = stopwatch
    elif axis["has_data"]:
        axis_emoji, axis_text, axis_cls = "\u2705", f"{axis['newest_row_age_days']:g}d old", "ok"
    else:
        axis_emoji, axis_text, axis_cls = "", "no data yet", ""
    axis_chip = f" {axis_emoji}" if axis_emoji else ""
    rows = "".join(
        f'<li><a href="{d["url"]}">#{d["number"]}</a> {esc(d["title"])}</li>'
        for d in open_drift
    )
    html = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Pipeline health &mdash; ayla</title>
<style>
  body {{ font-family: 'Segoe UI', system-ui, sans-serif; margin: 2rem auto; max-width: 720px; padding: 0 1rem; color: #1f2328; }}
  .verdict {{ font-size: 1.6rem; margin: 1rem 0; }}
  .ok {{ color: #1a7f37; }} .bad {{ color: #cf222e; }}
  .cards {{ display: flex; gap: 1rem; flex-wrap: wrap; }}
  .card {{ border: 1px solid #d0d7de; border-radius: 8px; padding: .6rem 1rem; }}
  .card b {{ display: block; font-size: 1.3rem; }}
  a {{ color: #0969da; }}
</style>
</head>
<body>
<h1>Pipeline health</h1>
<p class="verdict {cls}">{emoji} {'Healthy' if health['healthy'] else 'Attention needed'}</p>
<div class="cards">
  <div class="card">Coverage gate<b class="{cov_cls}">{line_pct:g}% / {GATE}%</b></div>
  <div class="card">Open drift issues<b class="{drift_cls}">{len(open_drift)}</b></div>
  <div class="card">Money axis{axis_chip}<b class="{axis_cls}">{axis_text}</b></div>
  <div class="card">Trend points<b>{trend_points}</b></div>
</div>
{'<h2>Open drift issues</h2><ul>' + rows + '</ul>' if open_drift else ''}
<p>Regenerated by every Coverage Pages deploy &mdash; source run:
{'<a href="' + health["run"]["url"] + '">CI run ' + run_id + '</a>' if run_id else 'unknown'}.
Machine-readable: <a href="./health.json">health.json</a> &middot;
Coverage: <a href="./index.html">report</a> &middot; <a href="./trend.html">trend</a></p>
</body>
</html>
"""

    (SITE / "health.json").write_text(json.dumps(health, indent=2) + "\n", encoding="utf-8")
    (SITE / "health-status.html").write_text(html, encoding="utf-8")
    print(json.dumps(health, indent=2))


def esc(text: str) -> str:
    return (
        text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    )


if __name__ == "__main__":
    main()
