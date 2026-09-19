#!/usr/bin/env python3
"""XAML StaticResource guard: every {StaticResource Key} a window or control
references must be declared in that file, in App.xaml, or in the merged
theme dictionary (Theme/Dark.xaml) that App.xaml pulls in.

A missing key cannot fail the C# build - it throws XamlParseException the
first time the window loads, i.e. on the user's machine (PR #77 shipped
exactly that). This check runs in CI before anything ships.

Exit codes:
  0 = all references resolve
  1 = at least one undeclared StaticResource reference
  2 = a scanned XAML file could not be read/parsed
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP_DIR = ROOT / "src" / "DongGfx.App"

# Files whose x:Key declarations are visible to every window/control.
GLOBAL_FILES = [
    APP_DIR / "App.xaml",
    APP_DIR / "Theme" / "Dark.xaml",
]

# {StaticResource Key} inside attribute values. The identifier-only
# capture naturally skips type-key lookups ({StaticResource {x:Type T}}).
REF = re.compile(r"\{\s*StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)")
# x:Key="Key" declarations.
DECL = re.compile(r'x:Key\s*=\s*"([^"]+)"')

def declared_keys(text: str) -> set[str]:
    return set(DECL.findall(text))

def main() -> int:
    if not APP_DIR.is_dir():
        print(f"XAML guard: app directory not found: {APP_DIR}")
        return 2

    global_keys: set[str] = set()
    for path in GLOBAL_FILES:
        try:
            global_keys |= declared_keys(path.read_text(encoding="utf-8"))
        except OSError as exc:
            print(f"XAML guard: cannot read {path.relative_to(ROOT)}: {exc}")
            return 2

    failures: list[str] = []
    checked = 0
    for path in sorted(APP_DIR.rglob("*.xaml")):
        if path in GLOBAL_FILES:
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as exc:
            print(f"XAML guard: cannot read {path.relative_to(ROOT)}: {exc}")
            return 2
        checked += 1
        refs = set(REF.findall(text))
        local = declared_keys(text)
        missing = refs - local - global_keys
        for key in sorted(missing):
            rel = path.relative_to(ROOT)
            failures.append(f"{rel}: StaticResource '{key}' is not declared in this file, App.xaml, or Theme/Dark.xaml")

    print(f"XAML guard: {checked} file(s) scanned, "
          f"{len(global_keys)} global resource key(s) available")
    if failures:
        print("XAML guard: FAILED")
        for line in failures:
            print(f"  - {line}")
        return 1
    print("XAML guard: OK")
    return 0

if __name__ == "__main__":
    sys.exit(main())
