#!/usr/bin/env python3
"""MainWindow binding inventory: every view-model member the shell exposes to
the user, grouped by the surface it binds against, flagged when nothing in the
desktop-free test suite references it.

The shell is the only place a user can reach most of the app, so a member no
test ever touches is a blind spot: it can be renamed, or its command can stop
being wired, and the unit gate stays green. This script walks MainWindow.xaml,
resolves each `{Binding ...}` path the same way the C# wiring guard does
(the nearest DataContext override wins, item templates are their own scope),
and prints a table.

Coverage is a NAME-REFERENCE heuristic, not real coverage: a member counts as
exercised when its leaf name appears as a whole word in a test source under
tests/DongGfx.App.Tests. It cannot see reflection, and a common name (Text,
When, Value) matches incidentally, so those rows are marked ambiguous. Treat a
flag as "worth a look", never as a verdict. MainWindowWiringTests.cs is skipped
on purpose: it asserts the wiring SHAPE, not behaviour, and would otherwise
"cover" every name it mentions in a contract string.

Usage:
  python scripts/xaml_binding_inventory.py                 # print the report
  python scripts/xaml_binding_inventory.py --write         # also write
                                                           # docs/xaml-binding-inventory.md
  python scripts/xaml_binding_inventory.py --summary       # one line: total=N flagged=M
  python scripts/xaml_binding_inventory.py --root DIR ...  # scan a different checkout
                                                           # (CI diffs against main's tree)

Exit codes: 0 = inventory produced, 2 = the XAML could not be read/parsed.
"""
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# The report carries ⚠ markers; a legacy Windows console (cp1252) cannot
# encode them, so pin stdout to UTF-8 where the runtime allows it.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]

HERE = Path(__file__).resolve().parent.parent

# The XAML wiring-shape contract: it names members in assertions, so counting
# it as coverage would let behaviour go untested while the inventory says green.
SKIP_TESTS = {"MainWindowWiringTests.cs"}

# DataContext override -> the view model its subtree binds against.
OVERRIDES = {
    "{Binding TerminalVm}": "TerminalViewModel",
    "{Binding MapsVm}": "MapsViewModel",
}

BINDING = re.compile(r"\{\s*Binding\s+([^}]*)\}")
# Leaves this short, or this generic, match incidentally: never a confident flag.
AMBIGUOUS = {"Text", "When", "Value", "Name", "Status", "Count", "Items"}
# A DataGrid column and a template both bind against the ROW item, not the
# surrounding view model, even though structurally they sit outside a
# DataTemplate.
ITEM_SCOPE = {
    "DataTemplate", "DataGridTemplateColumn", "DataGridTextColumn",
    "DataGridCheckBoxColumn", "DataGridComboBoxColumn", "DataGridHyperlinkColumn",
}
LOCAL = re.compile(r"\{[^}]*\}(\w+)")


def local(tag: str) -> str:
    match = LOCAL.match(tag)
    return match.group(1) if match else tag


def binding_paths(value: str) -> list[str]:
    """Every simple property path inside the attribute value (skips nested
    markup and converter-only bindings, which name no member)."""
    paths: list[str] = []
    for match in BINDING.finditer(value):
        inner = match.group(1).strip()
        first = inner.split(",")[0].strip()
        if first.lower().startswith("path="):
            first = first[len("path="):].strip()
        # {Binding} with no path, or a nested {Binding ...}: names nothing here.
        if not first or first.startswith("{") or first.startswith("RelativeSource"):
            continue
        paths.append(first)
    return paths


def scope_of(element: ET.Element, inherited: str) -> str:
    attr = element.get("DataContext")
    if attr is None:
        return inherited
    attr = attr.strip()
    if "PlacementTarget.DataContext" in attr:
        return inherited   # a popup shares its placement target's context
    if attr in OVERRIDES:
        return OVERRIDES[attr]
    return "unknown"       # a context this static walk cannot model


def leaf_of(path: str) -> str:
    segment = path.split(".")[-1].split("[")[0].strip()
    return segment.lstrip("!")


def exposes_member(path: str, attr: str) -> bool:
    """True when the binding names a view-model member the user reaches, not
    a scope declaration or a popup's link to its own row."""
    if attr == "DataContext":
        return False           # declares the DataContext, exposes no member
    if path.startswith("PlacementTarget."):
        return False           # a UI relationship (popup -> row), not a member
    return True


def walk(element: ET.Element, scope: str, rows: list[tuple[str, str, str]]) -> None:
    """Collect (scope, binding path, attribute) for the element and subtree."""
    tag = local(element.tag)
    if tag in ITEM_SCOPE:
        scope = "item"     # the row's own type, not a shell view model
    else:
        scope = scope_of(element, scope)

    for name, value in element.attrib.items():
        for path in binding_paths(value):
            rows.append((scope, path, name))

    for child in element:
        walk(child, scope, rows)


def test_sources(tests: Path) -> str:
    chunks: list[str] = []
    for path in sorted(tests.glob("*.cs")):
        if path.name in SKIP_TESTS:
            continue
        chunks.append(path.read_text(encoding="utf-8"))
    return "\n".join(chunks)


def referenced(leaf: str, sources: str) -> int:
    return len(re.findall(rf"\b{re.escape(leaf)}\b", sources))


Row = tuple[str, str, str, int, str]


def collect(root: Path) -> list[Row]:
    shell = root / "src" / "DongGfx.App" / "MainWindow.xaml"
    tests = root / "tests" / "DongGfx.App.Tests"
    tree = ET.parse(shell)                       # raises on malformed XAML
    rows: list[tuple[str, str, str]] = []
    walk(tree.getroot(), "MainViewModel", rows)

    sources = test_sources(tests)
    seen: dict[tuple[str, str], Row] = {}
    for scope, path, attr in rows:
        if not exposes_member(path, attr):
            continue
        leaf = leaf_of(path)
        key = (scope, path)
        if key in seen:
            continue
        count = 0 if scope == "item" else referenced(leaf, sources)
        if scope == "item":
            note = "row-scoped"
        elif len(leaf) <= 3 or leaf in AMBIGUOUS:
            note = "ambiguous name"
        elif count == 0:
            note = "NO TEST REFERENCE"
        else:
            note = "ok"
        seen[key] = (scope, path, attr, count, note)

    return list(seen.values())


def render(rows: list[Row]) -> str:
    flagged = [r for r in rows if r[4] == "NO TEST REFERENCE"]
    lines = [
        "# MainWindow binding inventory",
        "",
        "Every `{Binding ...}` path in `src/DongGfx.App/MainWindow.xaml`, the",
        "surface it resolves against, and whether the desktop-free test suite",
        "references the member's leaf name.",
        "",
        "Generated by `scripts/xaml_binding_inventory.py` — regenerate, don't",
        "hand-edit. Coverage is a **name-reference heuristic**: it counts a",
        "member as exercised when its leaf name appears as a whole word in",
        "`tests/DongGfx.App.Tests` (excluding the wiring-shape contract test).",
        "A common or short name can match incidentally, so those rows are",
        "marked *ambiguous*; a flag means *look closer*, not *broken*.",
        "",
        f"**{len(rows)} distinct bindings**, **{len(flagged)} flagged** with no",
        "test reference.",
        "",
        "| Scope | Binding | Used by | Test refs | Note |",
        "|---|---|---|---:|---|",
    ]
    order = {"NO TEST REFERENCE": 0, "ambiguous name": 1, "row-scoped": 2, "ok": 3}
    for scope, path, attr, count, note in sorted(rows, key=lambda r: (order[r[4]], r[0], r[1])):
        mark = {0: "⚠", 1: "?", 2: "–", 3: ""}[order[note]]
        lines.append(f"| {scope} | `{path}` | {attr} | {count} | {mark} {note} |")
    lines.append("")
    return "\n".join(lines)


def parse_args(argv: list[str]) -> tuple[Path, bool, bool]:
    root, summary, write = HERE, False, False
    i = 0
    while i < len(argv):
        if argv[i] == "--root" and i + 1 < len(argv):
            root = Path(argv[i + 1]).resolve()
            i += 2
            continue
        if argv[i] == "--summary":
            summary = True
        elif argv[i] == "--write":
            write = True
        i += 1
    return root, summary, write


def main() -> int:
    root, summary, write = parse_args(sys.argv[1:])
    shell = root / "src" / "DongGfx.App" / "MainWindow.xaml"
    try:
        rows = collect(root)
    except (OSError, ET.ParseError) as exc:
        print(f"binding inventory: cannot read {shell}: {exc}")
        return 2

    flagged = [r for r in rows if r[4] == "NO TEST REFERENCE"]
    if summary:
        print(f"total={len(rows)} flagged={len(flagged)}")
    else:
        print(render(rows))
    if write:
        report = root / "docs" / "xaml-binding-inventory.md"
        report.write_text(render(rows), encoding="utf-8")
        print(f"wrote {report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
