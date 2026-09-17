#!/usr/bin/env python3
"""Rail-trait coverage check: every test class that exercises real-money
gate state must carry [Trait("Category", "RealMoney")].

The gate drill (.github/workflows/gate-drill.yml) selects the safety rail's
own tests by that trait - not by name fragments (PR #61). A new gate-touching
test class without the trait would run in normal CI but SILENTLY MISS every
release drill, so rail coverage could regress on the exact runs that gate a
release. This guard fails the workflow-lint job the moment that happens.

What counts as "gate-touching" (precise signals, tuned to this repo's rail):
  - a typed reference to a rail type: ManualRealMoneyGate, RealMoneyGate,
    ManualMaxStake, UnlockPanel; or
  - a REAL_MONEY_* surface token - the journal/digest keys the gate itself
    emits (e.g. REAL_MONEY_UNLOCK_ARMED).
Arbitrary upper_snake identifiers (account ids, brain decisions, currency
codes) are deliberately NOT signals: the first version of this guard counted
them and flagged a quarter of the suite, all innocent.

A class is exempt when it carries the trait, or when the audit doc's test
class table marks it '(planned ...' or '[coverage: none]' (the doc is the
source of truth for acknowledged gaps); an inline '(planned ...' note in the
XML doc comment above the class works too.

The audit doc's "Test class coverage" table is cross-checked both ways:
classes listed in the doc must exist on disk, and classes carrying the
trait must be listed in the doc.

Exit code 1 on any violation. Run from CI's workflow-lint job, next to
check_safety_audit.py.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TESTS = ROOT / "tests"
DOC = ROOT / "docs" / "real-money-safety-audit.md"

# A trait belongs to the class declaration that FOLLOWS it (that is how C#
# attaches attributes), so multi-class files attribute correctly.
TRAIT = re.compile(r'\[Trait\(\s*"Category"\s*,\s*"RealMoney"\s*\)\]')

CLASS_DECL_RE = re.compile(
    r"^\s*(?:public|internal)\s+(?:sealed\s+|partial\s+|static\s+)*"
    r"class\s+(\w+)", re.MULTILINE)

# Markers inside a doc/test section or above a class that acknowledge a gap
# without carrying the trait.
PLANNED_MARKER = re.compile(r"\(planned\b")
NONE_MARKER = re.compile(r"\[\s*coverage\s*:\s*none\s*\]", re.IGNORECASE)

# Files that are test plumbing, never gate exercises themselves.
SKIP_FILES = {"AssemblyInfo.cs", "GlobalUsings.cs"}

# Typed rail-type references (weight 2 each, per distinct type). Order
# matters: longer alternations first so `ManualRealMoneyGate` is not eaten
# by a `RealMoneyGate` alternation.
RAIL_TYPE_RE = re.compile(
    r"\b(ManualRealMoneyGate|RealMoneyGate|ManualMaxStake|UnlockPanel)\b")

# The gate's own surface tokens (weight 1 each, distinct). Closed family:
# everything the rail emits with a REAL_MONEY_ prefix.
REAL_MONEY_TOKEN_RE = re.compile(r"\bREAL_MONEY_[A-Z0-9_]+\b")


def strip_comments(text: str) -> str:
    """Line and block comments out so doc-prose in headers never counts."""
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.DOTALL)
    return re.sub(r"//[^\n]*", " ", text)


def trait_owner_classes(code: str, decls: list[re.Match]) -> set[str]:
    """Class names owning a RealMoney trait: the next class declaration
    after each trait occurrence."""
    owners: set[str] = set()
    for tm in TRAIT.finditer(code):
        following = [d for d in decls if d.start() > tm.start()]
        if following:
            owners.add(following[0].group(1))
    return owners


def doc_comment_above(text: str, decl: re.Match, decls: list[re.Match]) -> str:
    """Raw text (comments included) of up to 10 lines above a class
    declaration, stopping at the previous class declaration - where an
    inline '(planned ...' note would live."""
    start_line = text.count("\n", 0, decl.start())
    prev_start = max((d.start() for d in decls
                      if d.start() < decl.start()), default=-1)
    prev_line = (text.count("\n", 0, prev_start) if prev_start >= 0 else -1)
    floor = max(start_line - 10, prev_line + 1, 0)
    return "\n".join(text.splitlines()[floor:start_line])


def doc_expected() -> dict[str, str]:
    """Class -> status ('covered' | 'planned' | 'none') parsed from the
    audit doc's "Test class coverage" table and prose sections."""
    text = DOC.read_text(encoding="utf-8", errors="replace")
    expected: dict[str, str] = {}
    header = None
    for line in text.splitlines():
        if line.startswith("## "):
            header = line
        for token in re.findall(r"\b\w+(?:ViewModel|Tests|Formatter|Service)\b",
                                line):
            # Table row or prose mention inside a class-coverage section.
            if header and "est class coverage" in header:
                status = ("planned" if PLANNED_MARKER.search(line)
                          else "none" if NONE_MARKER.search(line)
                          else "covered")
                expected[token] = status
    return expected


def main() -> int:
    if not TESTS.is_dir():
        print("::error::tests/ directory not found - run from the repo root")
        return 1

    problems: list[str] = []
    covered: list[str] = []
    traited: set[str] = set()

    for path in sorted(TESTS.rglob("*.cs")):
        if path.name in SKIP_FILES:
            continue
        rel = path.relative_to(ROOT).as_posix()
        text = path.read_text(encoding="utf-8", errors="replace")
        code = strip_comments(text)
        decls = list(CLASS_DECL_RE.finditer(code))
        if not decls:
            continue
        owners = trait_owner_classes(code, decls)
        traited |= owners

        for i, decl in enumerate(decls):
            cls = decl.group(1)
            end = decls[i + 1].start() if i + 1 < len(decls) else len(code)
            block = code[decl.start():end]
            types = sorted(set(RAIL_TYPE_RE.findall(block)))
            tokens = sorted(set(REAL_MONEY_TOKEN_RE.findall(block)))
            weight = 2 * len(types) + len(tokens)
            if weight == 0:
                # No gate signal: not this guard's business. A traited class
                # with no typed reference still counts as accounted for.
                if cls in owners:
                    covered.append(f"{cls} (trait)")
                continue

            has_trait = cls in owners
            doc_status = doc_expected().get(cls)
            planned = bool(
                PLANNED_MARKER.search(block)
                or PLANNED_MARKER.search(doc_comment_above(text, decl, decls)))
            none_marked = bool(NONE_MARKER.search(block))

            if has_trait:
                covered.append(f"{cls} (trait)")
                continue
            if doc_status == "planned" or planned:
                covered.append(f"{cls} (planned)")
                continue
            if doc_status == "none" or none_marked:
                covered.append(f"{cls} (none)")
                continue
            signals = types + tokens
            problems.append(
                f"::error file={rel}::{cls} exercises real-money gate "
                f"state ({', '.join(signals)}) but its test class carries "
                'no [Trait("Category", "RealMoney")] and no '
                "'(planned ...' note")

    # Doc-staleness half: every class the doc expects must still exist, and
    # every trait-carrying class must appear in the doc's table.
    expected = doc_expected()
    on_disk = set()
    for path in sorted(TESTS.rglob("*.cs")):
        if path.name in SKIP_FILES:
            continue
        on_disk.update(CLASS_DECL_RE.findall(
            strip_comments(path.read_text(encoding="utf-8", errors="replace"))))
    for name, status in sorted(expected.items()):
        if name not in on_disk:
            problems.append(
                f"::error file=docs/real-money-safety-audit.md::{name} is "
                f"listed as '{status}' in the audit doc's test-class table "
                "but no such class exists on disk - update the doc")
    for cls in sorted(traited):
        if cls not in expected:
            problems.append(
                "::error file=docs/real-money-safety-audit.md::test class "
                f"{cls} carries the RealMoney trait but is missing from the "
                "audit doc's test-class coverage table")

    if problems:
        print("Rail-trait coverage violations - the gate drill selects rail "
              'tests by [Trait("Category", "RealMoney")]; every '
              "gate-touching class must carry it (or note its status):")
        for p in problems:
            print(p)
        print("Fix: add the trait above the class declaration, or annotate "
              "the class/doc with '(planned ...' or '[coverage: none]'.")
        return 1

    print(f"Rail-trait coverage OK: {len(covered)} gate-relevant class(es) "
          f"accounted for ({', '.join(sorted(covered))}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
