#!/usr/bin/env python3
"""Test-trait coverage check: every test class must name a CI lane.

CI selects tests by trait, not by name or path:

    dotnet test ... --filter "Category=Unit"       # ci.yml `unit` job
    dotnet test ... --filter "Category=Uia"        # ci.yml `uia-smoke` job
    dotnet test ... --filter "Category=RealMoney"  # gate-drill.yml

xunit's filter matches nothing for a class carrying no Category trait. A test
class without one is therefore absent from all of them, and every job still
passes — a filter that matches nothing is not an error, so the loss is
invisible: no red build, no skipped-test warning, just silently missing
assertions.

That is not hypothetical. DashboardAndSettingsCoverageTests shipped with no
Category trait, so its assertions were absent from every PR's unit run; a
sweep of the same bug found a dozen more classes across both test projects
(DemoPaperExecutionTests, Tp1PlanGateConfigTests, FxExitBrainTests,
FxFamiliesTests, ...) — roughly ninety test methods no CI job ever executed.

This guard fails the workflow-lint job the moment a class with tests appears
without a lane trait.

What it enforces:
  - every top-level class containing [Fact]/[Theory] carries at least one
    [Trait("Category", "<lane>")] in its header (the run of attribute and
    comment lines immediately above the declaration — how C# attaches
    attributes);
  - the lane name is one this repo's workflows actually filter on (a typo
    like "unit" or "Unit " skips exactly as a missing trait does);
  - each lane's filter string must still appear in a workflow, so renaming
    a filter cannot quietly orphan the lane (this half is skipped when the
    scanned root carries no .github/workflows directory, e.g. a synthetic
    fixture tree).

A trait belongs to the declaration that FOLLOWS it, so a file with several
test classes is judged per class — a trait above one class does NOT cover its
neighbours. Private nested classes are plumbing, never test hosts, and are
ignored.

Exit codes: 0 = every test class names a live lane, 1 = violations,
2 = usage/root error.

Run from CI's workflow-lint job, next to check_rail_traits.py.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# Windows consoles default to a legacy code page; the violation text carries
# an em dash and would otherwise print as mojibake (and can raise on some
# hosts). Pin stdout to UTF-8 where the runtime allows it.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]

ROOT = Path(__file__).resolve().parent.parent

# Top-level class declarations only (column 0). Every test host in this repo
# is public/internal; nested fixture classes are private helpers, and
# requiring a visibility modifier keeps them out of the walk.
CLASS_DECL_RE = re.compile(
    r"^(?:public|internal)\s+"
    r"(?:sealed\s+|partial\s+|static\s+|abstract\s+)*"
    r"class\s+(\w+)",
    re.MULTILINE,
)

# A Category trait and the lane it names. Anything else in the attribute is
# left alone: this guard only cares about the lane selector.
TRAIT_RE = re.compile(r'\[Trait\(\s*"Category"\s*,\s*"([^"]*)"\s*\)\]')

# The markers that make a class a test host. Line-anchored so an attribute
# quoted inside a fixture string is not mistaken for one.
TEST_MARKER_RE = re.compile(r"^\s*\[(?:Fact|Theory)\b", re.MULTILINE)

# The header of a declaration is the run of attribute / comment / blank lines
# directly above it. A `[Trait(...)]` on a method earlier in the file is
# therefore never credited to the class that follows it.
# An attribute line, optionally followed by a trailing // comment — the
# repo writes [Collection("Uia")]   // why, and the walk must not stop there.
ATTRIBUTE_LINE_RE = re.compile(r"^\s*\[.*\]\s*(?://.*)?$")
COMMENT_LINE_RE = re.compile(r"^\s*(///|//|/\*|\*)")

# The lanes this repo filters on, and the workflow each is expected to read,
# and the filter string a lane must appear in the workflow as, so a renamed
# filter cannot quietly orphan the lane.
LANES = ("Unit", "Uia", "RealMoney")


def class_headers(text: str) -> list[tuple[str, str, str]]:
    """(class name, header text, class body) for every top-level class.

    The body runs to the next top-level declaration so a fact inside one
    class is never credited to another.
    """
    decls = list(CLASS_DECL_RE.finditer(text))
    out: list[tuple[str, str, str]] = []
    for i, decl in enumerate(decls):
        start_line = text.count("\n", 0, decl.start())
        lines = text.splitlines()
        top = start_line - 1
        while top >= 0:
            line = lines[top]
            if (not line.strip()
                    or COMMENT_LINE_RE.match(line)
                    or ATTRIBUTE_LINE_RE.match(line)):
                top -= 1
                continue
            break
        header = "\n".join(lines[top + 1:start_line])
        end = decls[i + 1].start() if i + 1 < len(decls) else len(text)
        out.append((decl.group(1), header, text[decl.start():end]))
    return out


def test_classes(root: Path) -> list[tuple[Path, str, str, list[str]]]:
    """(path, class name, header, lane traits) for every class holding tests."""
    found: list[tuple[Path, str, str, list[str]]] = []
    for path in sorted(root.rglob("*.cs")):
        if path.name in {"AssemblyInfo.cs", "GlobalUsings.cs"}:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for name, header, body in class_headers(text):
            if not TEST_MARKER_RE.search(body):
                continue
            found.append((path, name, header, TRAIT_RE.findall(header)))
    return found


def live_lanes(root: Path) -> dict[str, str]:
    """Lane -> the workflow that must still filter on it (empty when the
    scanned root has no workflows, e.g. a synthetic fixture tree)."""
    workflows = root / ".github" / "workflows"
    if not workflows.is_dir():
        return {}
    text = "\n".join(
        p.read_text(encoding="utf-8", errors="replace")
        for p in sorted(workflows.glob("*.yml"))
    )
    return {lane: text for lane in LANES}


def check(root: Path) -> tuple[list[str], int, int]:
    """(problems, test-class count, lane count)."""
    problems: list[str] = []
    classes = test_classes(root)
    workflows = live_lanes(root)

    for lane in LANES:
        text = workflows.get(lane)
        if text is not None and f"Category={lane}" not in text:
            problems.append(
                "::error::no workflow filters on "
                f'Category={lane} — the lane name this guard treats as a '
                "live target no longer exists"
            )

    for path, name, _header, lanes in classes:
        rel = path.relative_to(root).as_posix()
        if not lanes:
            problems.append(
                f"::error file={rel}::{name} contains [Fact]/[Theory] but "
                'carries no [Trait("Category", ...)] — CI selects tests by '
                "trait, so this class runs in NO CI job while every job stays "
                "green. Add the lane trait (usually "
                '[Trait("Category", "Unit")]).'
            )
            continue
        unknown = [lane for lane in lanes if lane not in LANES]
        if unknown:
            problems.append(
                f"::error file={rel}::{name} declares Category lane(s) "
                f"{', '.join(sorted(set(unknown)))} that no CI filter selects "
                f"(known lanes: {', '.join(LANES)}) — a misspelled lane skips "
                "the suite exactly like a missing trait."
            )

    return problems, len(classes), len(workflows)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=str(ROOT),
                        help="repo root to scan (default: this checkout)")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    tests = root / "tests"
    if not tests.is_dir():
        print(f"::error::no tests/ directory under {root} — run from the "
              "repo root")
        return 2

    problems, classes, lanes = check(root)
    if problems:
        print("Test-trait coverage violations — a test class with no "
              'Category trait runs in no CI job:')
        for problem in problems:
            print(problem)
        print("Fix: put the lane trait above the class declaration, e.g. "
              '[Trait("Category", "Unit")].')
        return 1

    lanes_note = (f"{lanes} lane(s) verified against the workflows"
                  if lanes else "workflow lane check skipped (no workflows)")
    print(f"Test-trait coverage OK: {classes} test class(es) name a live "
          f"Category lane ({lanes_note}).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))