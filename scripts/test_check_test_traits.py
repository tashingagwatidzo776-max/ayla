#!/usr/bin/env python3
"""Unit tests for scripts/check_test_traits.py.

The trait guard protects CI's unit gate, so its verdicts must be exact:
an untraited test class fails, a traited one passes, and - the subtle cases
that make a naive per-file grep wrong - a trait above one class does not
cover a second class in the same file, a trait above a method does not cover
the class below it, a misspelled lane fails, and a private nested helper
carrying [Fact] is ignored. A final case runs the guard against this real
checkout so the repository itself is held to the rule.

Run: python scripts/test_check_test_traits.py   (exit 0 = all pass)
"""
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "check_test_traits.py"
ROOT = HERE.parent

TRAIT = '[Trait("Category", "Unit")]'


def run_check(files, with_workflows=True, root=None):
    """Runs the guard against a synthetic tree; returns (stdout+stderr, rc)."""
    if root is not None:
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(root)],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        return proc.stdout + proc.stderr, proc.returncode
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        tests = base / "tests"
        tests.mkdir(parents=True)
        for name, body in files.items():
            (tests / name).write_text(body, encoding="utf-8")
        if with_workflows:
            wf = base / ".github" / "workflows"
            wf.mkdir(parents=True)
            (wf / "ci.yml").write_text(
                'run: dotnet test --filter "Category=Unit"\n'
                'run: dotnet test --filter "Category=Uia"\n'
                'run: dotnet test --filter "Category=RealMoney"\n',
                encoding="utf-8")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(base)],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        return proc.stdout + proc.stderr, proc.returncode


def test_untraited_class_fails():
    out, rc = run_check({"FooTests.cs": (
        "public class FooTests\n{\n    [Fact]\n    public void A() { }\n}\n")})
    assert rc == 1, out
    assert "FooTests" in out and "no [Trait" in out


def test_traited_class_passes():
    out, rc = run_check({"FooTests.cs": (
        f"{TRAIT}\npublic class FooTests\n{{\n    [Fact]\n    public void A() {{ }}\n}}\n")})
    assert rc == 0, out


def test_second_class_in_a_file_is_not_covered_by_the_first():
    # The exact bug a per-file grep misses: FxBrainTests carries the trait,
    # FxRegimeTests (same file) does not.
    out, rc = run_check({"BrainTests.cs": (
        f"{TRAIT}\npublic class FxFeatureTests\n{{\n    [Fact]\n    public void A() {{ }}\n}}\n"
        "\npublic class FxRegimeTests\n{\n    [Fact]\n    public void B() { }\n}\n")})
    assert rc == 1, out
    assert "FxRegimeTests" in out
    assert "FxFeatureTests" not in out


def test_method_level_trait_does_not_cover_the_class():
    out, rc = run_check({"FooTests.cs": (
        "public class FooTests\n{\n    [Trait(\"Category\", \"Unit\")]\n"
        "    [Fact]\n    public void A() { }\n}\n")})
    assert rc == 1, out
    assert "FooTests" in out


def test_misspelled_lane_fails():
    out, rc = run_check({"FooTests.cs": (
        '[Trait("Category", "unit")]\n'
        "public class FooTests\n{\n    [Fact]\n    public void A() { }\n}\n")})
    assert rc == 1, out
    assert "unit" in out and "no CI filter selects" in out


def test_private_nested_helper_is_ignored():
    out, rc = run_check({"FooTests.cs": (
        f"{TRAIT}\npublic class FooTests\n{{\n"
        "    private sealed class Helper\n    {\n        [Fact]\n"
        "        public void NotATestHost() { }\n    }\n"
        "    [Fact]\n    public void A() { }\n}\n")})
    assert rc == 0, out


def test_missing_tests_dir_is_a_usage_error():
    with tempfile.TemporaryDirectory() as tmp:
        out, rc = run_check({}, root=Path(tmp))
    assert rc == 2, out
    assert "no tests/ directory" in out


def test_no_workflows_skips_the_lane_check():
    out, rc = run_check({"FooTests.cs": (
        f"{TRAIT}\npublic class FooTests\n{{\n    [Fact]\n    public void A() {{ }}\n}}\n")},
        with_workflows=False)
    assert rc == 0, out
    assert "workflow lane check skipped" in out


def test_renamed_lane_filter_fails():
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        (base / "tests").mkdir()
        (base / "tests" / "FooTests.cs").write_text(
            f"{TRAIT}\npublic class FooTests\n{{\n    [Fact]\n    public void A() {{ }}\n}}\n",
            encoding="utf-8")
        wf = base / ".github" / "workflows"
        wf.mkdir(parents=True)
        # The Unit filter was renamed away: the lane is now orphaned.
        (wf / "ci.yml").write_text(
            'run: dotnet test --filter "Category=Fast"\n', encoding="utf-8")
        out, rc = run_check({}, root=base)
    assert rc == 1, out
    assert "Category=Unit" in out


def test_real_repo_passes():
    out, rc = run_check({}, root=ROOT)
    assert rc == 0, out
    assert "Test-trait coverage OK" in out


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except AssertionError as e:
            failed += 1
            print(f"FAIL {t.__name__}: {e}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
