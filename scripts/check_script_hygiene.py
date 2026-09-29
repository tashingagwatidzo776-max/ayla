#!/usr/bin/env python3
"""Script-hygiene linter: keep two mojibake bug classes extinct.

Windows consoles and PowerShell 5.1 default to legacy code pages, and this
repo's scripts pass emoji-rich text (AI narratives, alert markers) through
both. Two bugs have already shipped here:

  1. subprocess decode — `subprocess.run(..., text=True)` without
     `encoding=` decodes the child's UTF-8 as cp1252 (Windows default),
     raising UnicodeDecodeError or producing mojibake. soak_evening's
     analyst narrative hit exactly this (fixed 2026-09-26, c994e37).
  2. UTF-16 logs — PowerShell's Out-File / `>` / `*>>` default to UTF-16LE,
     which reads as mojibake in every editor and breaks downstream
     parsers expecting UTF-8 ASCII-ish text.

Rules enforced on scripts/*.py, bridge/*.py and scripts/*.ps1 (self,
test_*, and CI harnesses included — the same bug bites everywhere):

  PY-DECODE  subprocess.run/Popen/check_output/call/check_on with
             text=True (or capture_output=True, which implies decoding
             when text=True is present) must pass encoding="utf-8".
  PY-OPEN    text-mode open() must pass encoding= (binary "b" mode exempt).
  PS-ENC     Out-File/Add-Content/Set-Content/Tee-Object/Export-Csv and
             `>`/`>>` redirections must pin -Encoding utf8 (or ascii);
             PS 5.1 defaults are UTF-16LE (Out-File, >) or cp1252
             (Set-Content/Add-Content) — both wrong here.

Exit codes: 0 = clean, 1 = violations, 2 = --self-test failed.
Run from CI's integration job and scripts/ci-local.ps1 ('script-hygiene').
"""
from __future__ import annotations

import ast
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
PY_DIRS = [HERE, ROOT / "bridge"]
PS1_DIRS = [HERE]

SUBPROCESS_FUNCS = {"run", "Popen", "check_output", "call", "check_on"}
PS_OUT_CMDS = re.compile(
    r"\b(Out-File|Add-Content|Set-Content|Tee-Object|Export-Csv)\b")
PS_GOOD_ENC = re.compile(r"-Encoding\s+(utf8NoBOM|utf8BOM|utf8|ascii)\b", re.I)
# A real redirection operator is not glued to an identifier (">>" after a
# variable/letter or an arrow "->"/"=>" in a string), but stream redirects
# like 2> / 1> DO count (they share the UTF-16 default).
PS_REDIRECT = re.compile(r"(?<![A-Za-z_=\-])\*?>{1,2}(?!\s*\S*Encoding)")
# 2>&1 / 1>&2 etc.: stream-to-stream merges that change nothing on disk.
PS_STREAM_MERGE = re.compile(r"[12]>&[12]\b")
# Native-command stderr probes (`git ... 2> $file`): git writes its own
# bytes; PowerShell's redirection default only affects what it writes
# itself. The file is throwaway (exit code carries the signal), so these
# are exempt — name the variable *stderrFile* to opt in.
PS_NATIVE_STDERR_EXEMPT = re.compile(r"2>\s*\$\w*[sS]tderr\w*")
PS_ALL_STREAMS = re.compile(r"\*>")


def py_subprocess_findings(path: pathlib.Path) -> list[tuple[int, str]]:
    """PY-DECODE: any subprocess text-mode call without encoding=."""
    findings: list[tuple[int, str]] = []
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except SyntaxError as exc:
        return [(exc.lineno or 1, f"syntax error: {exc.msg}")]
    for node in ast.walk(tree):
        if not (isinstance(node, ast.Call)
                and isinstance(node.func, (ast.Name, ast.Attribute))):
            continue
        if isinstance(node.func, ast.Attribute):
            if (isinstance(node.func.value, ast.Name)
                    and node.func.value.id == "subprocess"
                    and node.func.attr in SUBPROCESS_FUNCS):
                pass
            else:
                continue
        elif node.func.id != "run" or any(
                isinstance(a, ast.Attribute) for a in node.args):
            continue
        kw = {k.arg for k in node.keywords if k.arg}
        has_text = "text" in kw
        # capture_output alone decodes bytes; without text=True it stays
        # bytes and the caller decodes — only flag when decoding happens.
        if not has_text:
            continue
        if "encoding" not in kw:
            findings.append((node.lineno,
                             "subprocess text=True without encoding= "
                             "(PY-DECODE: child output decodes as cp1252)"))
    return findings


def py_open_findings(path: pathlib.Path) -> list[tuple[int, str]]:
    """PY-OPEN: text-mode open() without encoding=."""
    findings: list[tuple[int, str]] = []
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except SyntaxError:
        return []  # already reported by PY-DECODE pass
    for node in ast.walk(tree):
        if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                and node.func.id == "open"):
            continue
        mode = ""
        if len(node.args) >= 2 and isinstance(node.args[1], ast.Constant):
            mode = str(node.args[1].value)
        if "b" in mode or "encoding" in {k.arg for k in node.keywords}:
            continue
        findings.append((node.lineno,
                         "text-mode open() without encoding= "
                         "(PY-OPEN: reads decode as cp1252)"))
    return findings


def ps1_findings(path: pathlib.Path) -> list[tuple[int, str]]:
    """PS-ENC: un-pinned output cmdlets / file redirections (backtick-joined)."""
    text = path.read_text(encoding="utf-8")
    # Join backtick continuations so a cmdlet and its -Encoding on the
    # next physical line are judged as one logical line.
    lines: list[str] = []
    pending = ""
    for lineno, raw in enumerate(text.splitlines(), start=1):
        line = pending + raw
        if line.rstrip().endswith("`"):
            pending = line.rstrip()[:-1] + " "
            continue
        pending = ""
        lines.append((lineno, line))
    if pending:
        lines.append((len(lines) + 1, pending))

    findings: list[tuple[int, str]] = []
    for lineno, line in lines:
        stripped = line.strip()
        if stripped.startswith("#"):
            continue
        # A line that is entirely inside a single- or double-quoted string
        # literal (the '# <sha256> <relative path>' comment-template shape)
        # is data, not a redirection.
        quote = stripped[0] if stripped[:1] in ('"', "'") else None
        if quote and stripped.endswith(quote) and len(stripped) > 1:
            continue
        # 2>&1-style stream merges only reshape the pipeline; no file is
        # written, so no encoding applies. (File-bound 2> still flags.)
        merge_scan = PS_STREAM_MERGE.sub(" ", line)
        if (PS_ALL_STREAMS.search(merge_scan)
                and not PS_NATIVE_STDERR_EXEMPT.search(line)):
            findings.append((lineno,
                             "`*>` redirection (PS-ENC: UTF-16LE on Windows "
                             "PowerShell - pipe through Out-File -Encoding utf8)"))
            continue
        if PS_OUT_CMDS.search(line) and not PS_GOOD_ENC.search(line):
            findings.append((lineno,
                             "output cmdlet without -Encoding utf8/ascii "
                             "(PS-ENC: defaults are UTF-16LE or cp1252)"))
            continue
        if (PS_REDIRECT.search(merge_scan) and not PS_GOOD_ENC.search(line)
                and not PS_NATIVE_STDERR_EXEMPT.search(line)):
            findings.append((lineno,
                             "`>`/`>>` redirection (PS-ENC: UTF-16LE on "
                             "Windows PowerShell - use Out-File -Encoding utf8)"))
    return findings


def scan() -> list[tuple[str, int, str]]:
    findings: list[tuple[str, int, str]] = []
    for py_dir in PY_DIRS:
        for path in sorted(py_dir.rglob("*.py")):
            if "__pycache__" in path.parts:
                continue
            for lineno, msg in py_subprocess_findings(path):
                findings.append((path, lineno, msg))
            for lineno, msg in py_open_findings(path):
                findings.append((path, lineno, msg))
    for ps1_dir in PS1_DIRS:
        for path in sorted(ps1_dir.rglob("*.ps1")):
            for lineno, msg in ps1_findings(path):
                findings.append((path, lineno, msg))
    return findings


def self_test() -> int:
    """The linter must catch the original bugs and pass clean code."""
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        base = pathlib.Path(tmp)
        bad_py = base / "bad.py"
        bad_py.write_text(
            "import subprocess\n"
            "subprocess.run(['x'], capture_output=True, text=True)\n"
            "subprocess.run(['y'], capture_output=True, text=True, timeout=5)\n"
            "with open('f.txt', 'w') as fh:\n"
            "    fh.write('x')\n", encoding="utf-8")
        good_py = base / "good.py"
        good_py.write_text(
            "import subprocess\n"
            "subprocess.run(['x'], capture_output=True, text=True,\n"
            "               encoding='utf-8', errors='replace')\n"
            "with open('f.bin', 'wb') as fh:\n"
            "    fh.write(b'x')\n"
            "with open('f.txt', 'w', encoding='utf-8') as fh:\n"
            "    fh.write('x')\n", encoding="utf-8")
        bad_ps1 = base / "bad.ps1"
        bad_ps1.write_text(
            "do-thing *>> $log\n"
            "Get-Content x | Out-File $log -Append\n"
            "Set-Content $f $lines\n", encoding="utf-8")
        good_ps1 = base / "good.ps1"
        good_ps1.write_text(
            "Get-Content x | Out-File $log -Append -Encoding utf8\n"
            "Set-Content $f $lines -Encoding ascii\n", encoding="utf-8")

        problems: list[str] = []
        if len(py_subprocess_findings(bad_py)) != 2:
            problems.append("PY-DECODE did not flag both bad calls")
        if py_subprocess_findings(good_py):
            problems.append("PY-DECODE flagged a clean call")
        if len(py_open_findings(bad_py)) != 1:
            problems.append("PY-OPEN did not flag the encoding-less open")
        if py_open_findings(good_py):
            problems.append("PY-OPEN flagged clean opens")
        if len(ps1_findings(bad_ps1)) != 3:
            problems.append("PS-ENC did not flag all three bad lines")
        if ps1_findings(good_ps1):
            problems.append("PS-ENC flagged clean lines")
        if problems:
            for p in problems:
                print(f"self-test FAIL: {p}")
            return 2
    print("self-test: ok (all rules fire on the original bug shapes)")
    return 0


def main(argv: list[str]) -> int:
    if "--self-test" in argv:
        return self_test()
    findings = scan()
    if not findings:
        print(f"script-hygiene: clean (PY-DECODE / PY-OPEN / PS-ENC)")
        return 0
    for path, lineno, msg in findings:
        rel = path.relative_to(ROOT)
        print(f"::error::{rel}:{lineno}: {msg}")
    print(f"script-hygiene: {len(findings)} finding(s) - "
          "pin encoding='utf-8', errors='replace' (Python) or "
          "-Encoding utf8/ascii (PowerShell)")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
