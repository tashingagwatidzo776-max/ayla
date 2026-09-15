#!/usr/bin/env python3
"""Static lint for this repo's workflow/script automation.

Catches the bug classes that produced real failed runs here:

1. Invalid `gh --json` field names (gh rejects the whole invocation with
   "Unknown JSON field", which killed the first watchdog runs).
2. `workflow_dispatch` inputs referenced by a workflow but never declared,
   and inputs used in arithmetic without an integer-validation guard
   (a free-text input in $(( )) dies with a cryptic expansion error).
3. Bash scripts invoked by workflow steps whose required variables
   (`: "${VAR:?}"`) are not provided by the calling step's/job's env,
   unless the script itself defaults them (`:=`) — the missing
   DRILL_LABEL killed a live watchdog rehearsal.

Exempted: coverage-pages.yml (self-contained legacy trigger chain; see the
exemption note in its own header). Exit code 1 on any finding.
"""

import re
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
WF_DIR = ROOT / ".github" / "workflows"
SCRIPTS_DIR = ROOT / "scripts"
EXEMPT = {"coverage-pages.yml"}

# Fields accepted by `gh run list --json` (superset is safe: we only flag
# names gh rejects, e.g. the historical `runNumber` typo).
#
# Loaded from gh-fields.txt beside this script when present (one field per
# line, '#' comments allowed) so a gh CLI update that adds fields is a data
# edit rather than a code edit; the built-in set is the fallback and is
# always unioned in, so a stale file can only loosen, never break the lint.
GH_RUN_LIST_FIELDS = {
    "attempt", "conclusion", "createdAt", "completedAt", "databaseId",
    "displayTitle", "event", "headBranch", "headSha", "headCommit",
    "headRepository", "headRepositoryOwner", "jobs", "name", "number",
    "startedAt", "status", "updatedAt", "url", "workflowDatabaseId",
    "workflowName",
}


def _load_gh_fields() -> None:
    extra = Path(__file__).resolve().parent / "gh-fields.txt"
    if not extra.exists():
        return
    for line in extra.read_text(encoding="utf-8").splitlines():
        field = line.split("#", 1)[0].strip()
        if field:
            GH_RUN_LIST_FIELDS.add(field)

findings: list[str] = []


def err(fname: str, msg: str) -> None:
    findings.append(f"{fname}: {msg}")


def run_blocks(node) -> list[str]:
    """All run: script texts in a job dict."""
    out = []
    for step in node.get("steps", []) or []:
        run = step.get("run")
        if isinstance(run, str):
            out.append(run)
        elif isinstance(run, list):
            out.extend(str(x) for x in run)
    return out


def check_gh_json_fields(fname: str, script: str) -> None:
    for m in re.finditer(r"--json\s+([A-Za-z0-9_,\s]+?)(?:\s*(?:--|\||\\|$))", script):
        for field in re.split(r"[,\s]+", m.group(1).strip()):
            if field and field not in GH_RUN_LIST_FIELDS:
                err(fname, f"invalid gh --json field '{field}' "
                           f"(gh rejects unknown fields and the whole call fails)")


def declared_inputs(triggers: dict) -> set[str]:
    wd = triggers.get("workflow_dispatch") or {}
    if isinstance(wd, dict):
        return set((wd.get("inputs") or {}).keys())
    return set()


def input_uses(script: str, inp: str) -> list[tuple[str, str]]:
    """(kind, context) uses of an input: 'direct' ${{ inputs.x }}, or via a
    VAR=${{ inputs.x }} assignment later used arithmetically."""
    uses = []
    esc = re.escape(inp)
    # Direct comparison use: [ "${{ inputs.x }}" -gt 5 ]
    if re.search(r'\[\s*"\$\{\{\s*inputs\.' + esc + r'\s*\}\}"\s+-[gl][te]\b', script):
        uses.append(("arithmetic", inp))
    elif re.search(r"inputs\." + esc + r"\b", script):
        uses.append(("direct", inp))
    # Assignment then later arithmetic use of the assigned var
    for m in re.finditer(r"(\w+)=\$\{\{\s*inputs\." + esc +
                         r"\s*(?:\|\|\s*'[^']*')?\s*\}\}", script):
        var = m.group(1)
        pat_arith = r"\$\(\([^)]*\b" + var + r"\b[^)]*\)\)"
        pat_cmp = r'"\$\{' + var + r'[:}]?"\s+-[gl][te]\b'
        if re.search(pat_arith, script) or re.search(pat_cmp, script):
            uses.append(("arithmetic", var))
    return uses


def has_int_validation(script: str, name: str) -> bool:
    pats = [
        r'case\s+"\$\{' + name + r'[:}]',
        r'\[\[?\s*"\$\{' + name + r'[:}]?\s*=~[^]]*0-9',
        r'"\$\{' + name + r'[:}]?"\s+-(?:gt|ge|lt|le|eq|ne)\b',
        # Direct-expression guard: [[ "${{ inputs.x }}" =~ ^[0-9]+$ ]]
        r'\[\[?\s*"\$\{\{\s*inputs\.' + name + r'\s*\}\}"?\s*=~[^]]*0-9',
    ]
    return any(re.search(p, script) for p in pats)


def check_dispatch_inputs(fname: str, triggers: dict, jobs: dict) -> None:
    declared = declared_inputs(triggers)
    all_scripts = "\n".join(s for j in jobs.values() for s in run_blocks(j))
    for m in set(re.findall(r"inputs\.(\w+)", all_scripts)):
        if m not in declared:
            err(fname, f"script references inputs.{m} but workflow_dispatch declares "
                       f"{sorted(declared) or 'no inputs'}")
    for inp in sorted(declared):
        for kind, ctx in input_uses(all_scripts, inp):
            if kind == "arithmetic" and not has_int_validation(all_scripts, ctx) \
               and not has_int_validation(all_scripts, inp):
                err(fname, f"dispatch input '{inp}' reaches arithmetic via '{ctx}' "
                           f"without integer validation (case *[!0-9]* or -gt guard)")


def script_required_vars(script_path: Path) -> tuple[set[str], set[str]]:
    """(required, defaulted) from : \"${VAR:?}\" / : \"${VAR:=...}\" lines."""
    required, defaulted = set(), set()
    if not script_path.exists():
        return required, defaulted
    text = script_path.read_text(encoding="utf-8")
    for m in re.finditer(r':\s*"\$\{(\w+):(\??=?)[^"]*"', text):
        if m.group(2) == "?":
            required.add(m.group(1))
        else:
            defaulted.add(m.group(1))
    return required, defaulted


def job_env(job: dict, scripts: list[str]) -> set[str]:
    """Env keys from YAML plus command-prefix assignments in the run scripts
    (`DRIFT_MODE=$mode bash ...` style, which the watchdog relies on)."""
    keys = set((job.get("env") or {}).keys())
    for step in job.get("steps", []) or []:
        keys |= set((step.get("env") or {}).keys())
    for script in scripts:
        keys |= set(re.findall(r"(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)=", script))
    return keys


def check_caller_env(fname: str, jobs: dict) -> None:
    for job_name, job in jobs.items():
        scripts = run_blocks(job)
        for script in scripts:
            for path in SCRIPTS_DIR.glob("*.sh"):
                if f"scripts/{path.name}" not in script:
                    continue
                required, defaulted = script_required_vars(path)
                have = job_env(job, scripts) | {
                    "GITHUB_SERVER_URL", "GITHUB_REPOSITORY", "GITHUB_REPOSITORY_OWNER",
                }
                missing = required - have
                if missing:
                    err(fname, f"job '{job_name}' calls scripts/{path.name} but does not "
                               f"provide required var(s): {sorted(missing)} "
                               f"(script has no default; env kills it under set -u)")


def main() -> int:
    _load_gh_fields()
    for wf in sorted(WF_DIR.glob("*.yml")) + sorted(WF_DIR.glob("*.yaml")):
        fname = wf.name
        if fname in EXEMPT:
            continue
        try:
            doc = yaml.safe_load(wf.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            err(fname, f"invalid YAML: {e}")
            continue
        triggers = doc.get(True, doc.get("on", {})) or {}  # PyYAML 1.1 parses bare `on:` as True
        jobs = doc.get("jobs") or {}
        for job in jobs.values():
            for s in run_blocks(job):
                check_gh_json_fields(fname, s)
        check_dispatch_inputs(fname, triggers, jobs)
        check_caller_env(fname, jobs)

    if findings:
        print(f"workflow-lint: {len(findings)} finding(s)")
        for f in findings:
            print(f"  - {f}")
        return 1
    print("workflow-lint: all clean "
          "(gh --json fields, dispatch input declarations/validation, script caller env)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
