#!/usr/bin/env python3
"""Safety-rail coverage check: every code path that can place a Deriv trade
must be documented in docs/real-money-safety-audit.md.

DerivClient.BuyAsync is the single choke point for order execution (it takes
a proposal id, so every strategy must pass GetProposalAsync first). This
script greps all `.BuyAsync(` call sites under src/ and fails when a file
involved in trading is not represented in the audit doc's coverage tables.

The doc documents each path with a pipeline line naming its source file, e.g.

    `TradesViewModel.PlaceDemoTrade → DerivClient` (single manual trade)

so the check maps call-site files to doc mentions (extensionless, like the
doc's own style). A known-unguardable site (the choke point itself) can be
listed in EXPECTED_INTRINSIC.

Exit code 1 on any uncovered call site. Run from CI's workflow-lint job.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
DOC = ROOT / "docs" / "real-money-safety-audit.md"

# The choke point defines BuyAsync; the definition itself is not a "path
# that places a trade" — it IS the rail.
EXPECTED_INTRINSIC = {"DerivClient"}

# Matches the doc's pipeline line style: `Foo.Bar → DerivClient`
PIPELINE_LINE = re.compile(r"`([A-Za-z0-9_.]+)")


def buy_async_sites():
    """Files under src/ containing a `.BuyAsync(` call, mapped to the
    extensionless file name the doc must mention."""
    sites = {}
    for path in sorted(SRC.rglob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if re.search(r"\.BuyAsync\(", text):
            sites[path.stem] = path.relative_to(ROOT).as_posix()
    return sites


def documented_names():
    """All code identifiers named in the audit doc's backticked pipeline
    lines. Compound identifiers (`TradesViewModel.PlaceDemoTrade`) count as
    naming every dot-separated component, so a path section that names its
    entry point covers that file."""
    text = DOC.read_text(encoding="utf-8", errors="replace")
    names = set()
    for compound in PIPELINE_LINE.findall(text):
        names.update(part for part in compound.split(".") if part)
    return names


def main():
    if not DOC.exists():
        print(f"::error::{DOC.relative_to(ROOT)} is missing — the safety-rail "
              "audit must exist and cover every BuyAsync call site")
        return 1

    sites = buy_async_sites()
    if not sites:
        print("::error::no BuyAsync call sites found under src/ — either the "
              "choke point moved or the grep is broken")
        return 1

    names = documented_names()
    missing = {name: rel for name, rel in sites.items()
               if name not in names and name not in EXPECTED_INTRINSIC}

    if missing:
        print("Uncovered BuyAsync call sites — every path that can place a "
              "Deriv trade must appear in docs/real-money-safety-audit.md "
              "with its safety rails:")
        for name, rel in sorted(missing.items()):
            print(f"::error file={rel}::{name} places trades but has no "
                  "coverage entry in the safety audit doc")
        print("Add a '## Path N — …' section to docs/real-money-safety-audit.md "
              "naming the file in a pipeline line and listing its rails "
              "(kill switch, governor, real-money gate, risk engine).")
        return 1

    print(f"Safety-rail coverage OK: {len(sites)} BuyAsync call site(s), "
          f"all documented ({', '.join(sorted(sites))}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
