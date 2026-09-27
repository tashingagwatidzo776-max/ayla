#!/usr/bin/env python3
"""Evening soak routine: analyst narrative + alpha rerun, into evidence.

Runs the two end-of-soak-day chores that until now were manual:

  --analyst  run the production analyst once (tools/AnalystSmoke against
             the live journal, local LLM qwen3 by default) and append the
             narrative to today's docs/soak/SOAK-<date>.md. A cold model
             is warmed first so the scheduled run records the LLM
             narrative, not the timeout fallback template.
  --alpha    re-run the alpha research loop over whatever the tick archive
             accumulated that day: propose -> ticks_to_bars per symbol ->
             backtest every proposal against every symbol's bars. Reports
             that finally clear the 5-trade bar are called out loudly —
             a human must still review and port anything promoted.

Both are append-only against the evidence doc and idempotent per day:
the analyst section is written once (rerun with --force to override),
alpha reruns only test (proposal, bars) pairs with no newer report.
Read-only everywhere else — this script never touches the app's data dir
except to READ the tick archive, and never posts anywhere itself (the
analyst inside AnalystSmoke does, through the configured webhook).
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
SOAK_DIR = REPO / "docs" / "soak"
ANALYST_EXE = REPO / "tools" / "AnalystSmoke" / "bin" / "lockfree3" / "AnalystSmoke.exe"
OLLAMA = pathlib.Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "Ollama" / "ollama.exe"
DEFAULT_TICK_DIR = pathlib.Path(os.environ.get("APPDATA", "")) / "tf" / "data" / "ticks" / "mt5"
NARRATIVE_TITLE = "AI analyst narrative"
NARRATIVE_MARK = f"## {NARRATIVE_TITLE}"
ALPHA_TITLE = "Alpha rerun"
ALPHA_MARK = f"## {ALPHA_TITLE}"


def today_soak_path(now: dt.datetime | None = None) -> pathlib.Path:
    return SOAK_DIR / f"SOAK-{(now or dt.datetime.now()).date():%Y-%m-%d}.md"


def append_section(path: pathlib.Path, title: str, body: str) -> bool:
    """Append `## title` + body if that section is not already present."""
    text = path.read_text(encoding="utf-8") if path.exists() else ""
    if f"\n## {title}\n" in f"\n{text}":
        return False
    with path.open("a", encoding="utf-8") as f:
        if text and not text.endswith("\n"):
            f.write("\n")
        f.write(f"\n## {title}\n\n{body.rstrip()}\n")
    return True


def replace_section(path: pathlib.Path, title: str, body: str) -> bool:
    """Rewrite the `## title` section in place, keeping everything before
    and after. Matches whole heading lines only — prose that merely
    MENTIONS the section title (evening notes do) must never match, and
    multi-line bodies move as a block."""
    text = path.read_text(encoding="utf-8")
    marker = f"\n## {title}\n"
    idx = text.find(marker)
    if idx < 0:
        return False
    head = text[:idx + 1]                       # keep the newline before '##'
    rest = text[idx + 1 + len(marker):]         # section body onward
    end = rest.find("\n## ")                    # next heading, if any
    tail = rest[end:] if end >= 0 else ""
    path.write_text(head + f"## {title}\n\n{body.rstrip()}\n" + tail,
                    encoding="utf-8")
    return True


# ── analyst ──────────────────────────────────────────────────────────

def warm_llm(model: str, timeout: int = 180) -> bool:
    """Load the model into memory so the 20s analyst budget is spent on
    narrating, not on a cold 522 MB load. Best effort."""
    if not OLLAMA.exists():
        return False
    try:
        r = subprocess.run([str(OLLAMA), "run", model, "warm"],
                           capture_output=True, timeout=timeout)
        return r.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def run_analyst(model: str) -> tuple[str | None, str]:
    """Returns (narrative or None, status note). Never raises."""
    if not ANALYST_EXE.exists():
        print("building AnalystSmoke (first run)...")
        build = subprocess.run(
            ["dotnet", "build", str(REPO / "tools" / "AnalystSmoke"),
             "-p:OutputPath=bin/lockfree3/", "-v", "q", "--nologo"],
            capture_output=True, text=True, timeout=600)
        if build.returncode != 0 or not ANALYST_EXE.exists():
            return None, "AnalystSmoke unavailable (build failed)"
    warm_llm(model)
    try:
        r = subprocess.run([str(ANALYST_EXE)], capture_output=True, text=True,
                           timeout=300, encoding="utf-8", errors="replace")
    except (OSError, subprocess.TimeoutExpired) as ex:
        return None, f"AnalystSmoke failed to run ({ex.__class__.__name__})"
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"=== NARRATIVE START ===\s*(.*?)\s*=== NARRATIVE END ===",
                  out, re.DOTALL)
    if not m or m.group(1).startswith("(nothing"):
        return None, "analyst had nothing to say (empty journal or toggle off)"
    return m.group(1), "ok"


def analyst_section(narrative: str) -> str:
    return (f"- captured: {dt.datetime.now():%Y-%m-%d %H:%M} local, "
            f"production JournalAnalystService (model from TF_LLM_MODEL, "
            f"default qwen3:0.6b)\n"
            f"- narrative (verbatim):\n"
            f"> {narrative}")


def alpha_title(now: dt.datetime | None = None) -> str:
    """Timestamped heading: every run appends its own section. Reruns are
    new information even on the same day (keyed backtest reports mean a
    rerun only measures data it has not seen), so a hard once-a-day block
    would silently skip the first real weekday validation."""
    return f"{ALPHA_TITLE} — {(now or dt.datetime.now()):%Y-%m-%d %H:%M} local"


# ── alpha rerun ──────────────────────────────────────────────────────

def convert_bars(symbol: str, tick_dir: pathlib.Path, out_dir: pathlib.Path) -> pathlib.Path | None:
    """Newest tick file for the symbol -> bar CSV. None when too thin."""
    matches = sorted(tick_dir.glob(f"{symbol}_*.jsonl"))
    if not matches:
        return None
    out = out_dir / f"{matches[-1].stem}.csv"
    try:
        r = subprocess.run(
            [sys.executable, str(REPO / "scripts" / "ai_alpha" / "ticks_to_bars.py"),
             symbol, "--tick-dir", str(tick_dir), "--out", str(out)],
            capture_output=True, text=True, timeout=300)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if r.returncode != 0 or not out.exists():
        return None
    return out


def bars_signature(csv: pathlib.Path) -> str:
    """Stem carries the tick file date: XAUUSDmicro_20260926."""
    return csv.stem.rsplit("_", 1)[-1]


def backtest(proposal: pathlib.Path, bars: pathlib.Path,
             results: pathlib.Path) -> dict | None:
    """One deterministic backtest; report keyed per (proposal, bars day)."""
    ai = REPO / "scripts" / "ai_alpha"
    try:
        r = subprocess.run(
            [sys.executable, str(ai / "backtest.py"), str(proposal),
             "--bars", str(bars)],
            capture_output=True, text=True, timeout=600)
    except (OSError, subprocess.TimeoutExpired):
        return None
    # backtest.py writes its plain report next to its own harness dir,
    # not the caller's results dir — read (and consume) it there.
    plain = (REPO / "scripts" / "ai_alpha" / "results") / proposal.name.replace(".json", ".report.json")
    if not plain.exists():
        return None
    report = json.loads(plain.read_text(encoding="utf-8"))
    report["symbol_bars"] = bars.stem
    keyed = results / f"{proposal.stem}.{bars_signature(bars)}.{bars.stem.split('_')[0]}.report.json"
    keyed.parent.mkdir(parents=True, exist_ok=True)
    keyed.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    plain.unlink(missing_ok=True)
    return report


def run_alpha(tick_dir: pathlib.Path) -> tuple[list[str], str]:
    """Returns (summary lines, finding-or-empty). Never raises."""
    ai = REPO / "scripts" / "ai_alpha"
    proposals_dir, bars_dir, results_dir = ai / "proposals", ai / "bars", ai / "results"
    lines: list[str] = []
    if not proposals_dir.exists():
        proposals_dir.mkdir(parents=True)
    before = {p.name for p in proposals_dir.glob("PROP-*.json")}
    r = None
    try:
        r = subprocess.run([sys.executable, str(ai / "propose.py")],
                           capture_output=True, text=True, timeout=600)
    except (OSError, subprocess.TimeoutExpired):
        pass  # missing script or hung daemon: proceed with what exists
    new = {p.name for p in proposals_dir.glob("PROP-*.json")} - before
    lines.append(f"- propose.py: {'+' + str(len(new)) + ' new proposal(s)' if new else 'no new proposal'}"
                 + ("" if r is not None and r.returncode == 0 else " (propose unavailable or failed)"))

    symbols = sorted({p.name.split("_")[0] for p in tick_dir.glob("*_*.jsonl")}) if tick_dir.exists() else []
    if not symbols:
        return lines + ["- no tick archives found - nothing to backtest"], ""
    proposals = sorted(proposals_dir.glob("PROP-*.json"))
    if not proposals:
        return lines + ["- no proposals to test"], ""

    tested, findings, skipped_thin = 0, [], 0
    for sym in symbols:
        bars = convert_bars(sym, tick_dir, bars_dir)
        if bars is None:
            skipped_thin += 1
            continue
        day = bars_signature(bars)
        for prop in proposals:
            keyed = results_dir / f"{prop.stem}.{day}.{sym}.report.json"
            if keyed.exists():
                # Already measured against these exact bars — but an open
                # positive finding from an earlier run still gets surfaced
                # (a crash after backtest, before the doc append, must not
                # bury a result that cleared the bar).
                try:
                    old = json.loads(keyed.read_text(encoding="utf-8"))
                    if str(old.get("expectancy_verdict", "")).startswith("positive expectancy"):
                        findings.append(f"{prop.stem} on {sym} ({old.get('trades')} trades, "
                                        f"{old.get('total_r', 0):+.1f}R, maxDD {old.get('max_drawdown_r', 0):.1f}R)")
                except (OSError, ValueError, TypeError):
                    pass
                continue
            report = backtest(prop, bars, results_dir)
            if report is None:
                continue
            tested += 1
            verdict = report["expectancy_verdict"]
            lines.append(f"- {prop.stem} on {sym} ({day}): {report['trades']} trades, "
                         f"{report['total_r']:+.1f}R, verdict: {verdict}")
            if verdict.startswith("positive expectancy"):
                findings.append(f"{prop.stem} on {sym} ({report['trades']} trades, "
                                f"{report['total_r']:+.1f}R, maxDD {report['max_drawdown_r']:.1f}R)")
    if skipped_thin:
        lines.append(f"- {skipped_thin} symbol(s) below the 50-bar floor (closed/static tape) - skipped")
    if not tested:
        lines.append("- nothing newly testable (all (proposal, bars) pairs already measured)")

    finding = ""
    if findings:
        finding = "; ".join(findings)
        lines.append(f"- **FINDING — human review needed: {finding}** "
                     "(a proposal cleared the 5-trade bar; porting is a reviewed PR, never automatic)")
    return lines, finding


# ── main ─────────────────────────────────────────────────────────────

def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--analyst", action="store_true", help="run the analyst and record the narrative")
    p.add_argument("--alpha", action="store_true", help="re-run the alpha harness over the tick archive")
    p.add_argument("--force", action="store_true", help="rewrite the analyst section even if present")
    p.add_argument("--model", default="qwen3:0.6b")
    p.add_argument("--tick-dir", type=pathlib.Path, default=DEFAULT_TICK_DIR)
    p.add_argument("--date", default=None, help="YYYY-MM-DD override for the evidence doc")
    args = p.parse_args(argv)
    if not args.analyst and not args.alpha:
        args.analyst = args.alpha = True

    soak = (SOAK_DIR / f"SOAK-{args.date}.md") if args.date else today_soak_path()
    soak.parent.mkdir(parents=True, exist_ok=True)
    analyst_status, finding = "skipped", ""

    if args.analyst:
        already = soak.exists() and NARRATIVE_MARK in soak.read_text(encoding="utf-8")
        if already and not args.force:
            analyst_status = "already recorded today"
        else:
            narrative, note = run_analyst(args.model)
            if narrative:
                if not already:
                    if append_section(soak, NARRATIVE_TITLE, analyst_section(narrative)):
                        analyst_status = "recorded"
                else:  # --force: replace today's section in place
                    analyst_status = ("replaced (--force)" if
                                      replace_section(soak, NARRATIVE_TITLE, analyst_section(narrative))
                                      else "recorded (section had vanished)")
            else:
                analyst_status = f"skipped ({note})"
        print(f"analyst: {analyst_status}")

    if args.alpha:
        lines, finding = run_alpha(args.tick_dir)
        append_section(soak, alpha_title(), "\n".join(lines))
        alpha_status = f"{len(lines)} line(s) recorded"
        print(f"alpha:   {alpha_status}")

    print(f"finding: {finding or 'none'}")
    print(f"evidence: {soak.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
