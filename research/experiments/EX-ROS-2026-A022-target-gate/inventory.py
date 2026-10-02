#!/usr/bin/env python3
"""EX-ROS-2026-A022 target-gate inventory (read-only, reproducible).

For each candidate repository checked out at its frozen SHA under CLONES,
copy it to a scratch directory (so nothing can be written into the clone)
and ask the Praxis CLI itself for the effective lifecycle state of every
backlog item (`work list --json`, which prefers the live in-flight state over
the backlog's own status field, docs/work-protocol.md "Local backlog") and for
the ready set (`work ready`). The gate count is the number of `ready` items.

Usage:
    python3 inventory.py --clones DIR --praxis PATH/TO/praxis [--check FILE]

`--check FILE` recomputes and compares against a committed inventory.json,
ignoring only the `generatedAt` field.
"""
import argparse
import collections
import json
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

# Frozen candidate set: the EX-ROS-2026-A022 allowlist supplied with the
# selection request, followed by Signal, inspected afterwards at the owner's
# request. Order is the order of inspection, not a preference.
CANDIDATES = (
    ("kemiller2002/forma", "1564993990b3abd4313742ef2953802429d64259", "allowlist"),
    ("kemiller2002/forma-studio", "e8ea17b3c4be16128ac50e8ca76bb144c9412479", "allowlist"),
    ("kemiller2002/folio", "eefcebab9c265f36f7c11316571fdb33a2b5a849", "allowlist"),
    ("kemiller2002/limen", "303b7beaaae6bce99bfa32228e86b607505ab273", "allowlist"),
    ("kemiller2002/iter", "321767f48bc824c6730257abecd3768da980839b", "allowlist"),
    ("kemiller2002/dokimos", "e641048c52edd8755c2964b6bafa4ae860e34062", "allowlist"),
    ("kemiller2002/vigila", "bc02eddc2145a7e9bd4875eb1df738d2096129e0", "allowlist"),
    ("kemiller2002/chrona", "495db073db7a79add6de9897294ec59795665e1d", "allowlist"),
    ("kemiller2002/summa", "6290d36e056f90ca401483a70e684994999006ef", "allowlist"),
    ("kemiller2002/strata", "3512d3217e809c6725a38a0e4ef36f504fc673ea", "allowlist"),
    ("kemiller2002/conditor", "a925ca52f8435a948c946cf49bd7d4f9a0810adf", "allowlist"),
    ("kemiller2002/aegis", "e629468f504458534faabc1b4cc34ded0ae60dce", "allowlist"),
    ("kemiller2002/percepta", "6df2dbc48af3fd48f8da139e83aa32eca054ef82", "allowlist"),
    ("kemiller2002/signal", "637218a14179c9ac4f1ab1333c75d03f9df6d797", "added-after-initial-pass"),
)

GATE_PER_COHORT = 5
GATE_TOTAL = 2 * GATE_PER_COHORT


def run(args, cwd=None):
    return subprocess.run(args, cwd=cwd, capture_output=True, text=True)


def head_sha(clone: Path) -> str:
    return run(["git", "-C", str(clone), "rev-parse", "HEAD"]).stdout.strip()


def read_json(path: Path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return None


def pinned_versions(clone: Path) -> dict:
    ros = read_json(clone / "ros.json") or {}
    installed = read_json(clone / ".echelon" / "ros.json") or {}
    return {
        "rosJsonVersion": ros.get("rosVersion") or ros.get("praxisVersion"),
        "installedVersion": installed.get("installedVersion"),
    }


def effective_items(praxis: str, clone: Path) -> list:
    """Ask Praxis for effective item state, from a throwaway copy."""
    if not (clone / ".ros" / "work" / "queue.json").exists():
        return []
    with tempfile.TemporaryDirectory() as tmp:
        copy = Path(tmp) / "repo"
        shutil.copytree(clone, copy, symlinks=True)
        out = run([praxis, "--root", str(copy), "work", "list", "--json"])
        if out.returncode != 0:
            raise RuntimeError(f"work list failed for {clone}: {out.stderr.strip()}")
        return json.loads(out.stdout)


def summarize(praxis: str, clones: Path, candidate) -> dict:
    full_name, frozen_sha, origin = candidate
    clone = clones / full_name.split("/")[1]
    observed = head_sha(clone)
    items = effective_items(praxis, clone)
    states = collections.Counter(i["status"] for i in items)
    ready = sorted(i["id"] for i in items if i["status"] == "ready")
    unfinished = sorted(
        (i["id"], i["status"]) for i in items if i["status"] not in ("complete", "abandoned")
    )
    return {
        "repository": full_name,
        "candidateOrigin": origin,
        "frozenSha": frozen_sha,
        "observedSha": observed,
        "shaMatches": observed == frozen_sha,
        "hasPraxisBacklog": (clone / ".ros" / "work" / "queue.json").exists(),
        "versions": pinned_versions(clone),
        "itemCount": len(items),
        "stateCounts": dict(sorted(states.items())),
        "readyCount": len(ready),
        "readyIds": ready,
        "unfinished": [{"id": i, "state": s} for i, s in unfinished],
        "meetsReadyCountGate": len(ready) >= GATE_TOTAL,
    }


def inventory(praxis: str, clones: Path) -> dict:
    rows = [summarize(praxis, clones, c) for c in CANDIDATES]
    return {
        "experiment": "EX-ROS-2026-A022",
        "gate": {
            "readyPerCohort": GATE_PER_COHORT,
            "readyTotalMinimum": GATE_TOTAL,
            "rule": "same repository must supply >=5 genuine ready high-affinity and >=5 genuine ready low-affinity items",
        },
        "praxisCliVersion": run([praxis, "--version"]).stdout.strip(),
        "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "candidates": rows,
        "eligibleByReadyCount": [r["repository"] for r in rows if r["meetsReadyCountGate"]],
    }


def comparable(doc: dict) -> dict:
    return {k: v for k, v in doc.items() if k != "generatedAt"}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--clones", required=True, type=Path)
    parser.add_argument("--praxis", required=True)
    parser.add_argument("--check", type=Path)
    args = parser.parse_args()
    doc = inventory(args.praxis, args.clones)
    if args.check:
        same = comparable(doc) == comparable(json.loads(args.check.read_text()))
        print("inventory matches" if same else "inventory DIFFERS")
        return 0 if same else 1
    print(json.dumps(doc, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
